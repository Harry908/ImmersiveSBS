using System.Diagnostics;
using System.Runtime.InteropServices;
using ImmersiveSBS.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ImmersiveSBS.Capture;

/// <summary>
/// Windows.Graphics.Capture of one monitor, the fallback of plan D3. Windows draws the cursor
/// into the frame, so <see cref="Pointer"/> stays hidden. The yellow capture border is not
/// switched off (that API needs Windows 11); it is drawn on the virtual monitor, which the user
/// only sees through the capture, which excludes it.
/// </summary>
public sealed class GraphicsCapture : ICaptureSource
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    private readonly ID3D11Device device;
    private readonly Func<string?> resolveGdiName;
    private string? gdiName;
    private readonly AutoResetEvent frameArrived = new(false);
    private IDirect3DDevice? winrtDevice;
    private GraphicsCaptureItem? item;
    private Direct3D11CaptureFramePool? pool;
    private GraphicsCaptureSession? session;
    private Windows.Graphics.SizeInt32 poolSize;
    private ID3D11Texture2D? texture;
    private volatile bool closed;
    private long nextRetry;

    public GraphicsCapture(ID3D11Device device, Func<string?> resolveGdiName)
    {
        this.device = device;
        this.resolveGdiName = resolveGdiName;
        using (ID3D11Multithread mt = device.ImmediateContext.QueryInterface<ID3D11Multithread>())
        {
            // The free-threaded frame pool touches the device from its own thread.
            mt.SetMultithreadProtected(true);
        }
        TryOpen();
    }

    public ID3D11Texture2D? Texture => texture;

    public PointerState Pointer { get; } = new();

    public CaptureUpdate Acquire(int timeoutMs)
    {
        if (session == null || closed)
        {
            if (closed) Close();
            if (Environment.TickCount64 < nextRetry || !TryOpen())
            {
                Thread.Sleep(Math.Min(timeoutMs, 250));
                return default;
            }
        }

        if (!frameArrived.WaitOne(timeoutMs)) return default;

        // Only the newest frame matters.
        Direct3D11CaptureFrame? latest = null;
        for (Direct3D11CaptureFrame? f; (f = pool!.TryGetNextFrame()) != null;)
        {
            latest?.Dispose();
            latest = f;
        }
        if (latest == null) return default;

        using (latest)
        {
            var size = latest.ContentSize;
            if (size.Width != poolSize.Width || size.Height != poolSize.Height)
            {
                Log.Write($"GraphicsCapture: content size now {size.Width}×{size.Height}; recreating the pool.");
                poolSize = size;
                pool.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            }

            using ID3D11Texture2D frame = TextureOf(latest.Surface);
            uint w = (uint)Math.Min(size.Width, (int)frame.Description.Width);
            uint h = (uint)Math.Min(size.Height, (int)frame.Description.Height);
            EnsureTexture(w, h);
            device.ImmediateContext.CopySubresourceRegion(texture!, 0, 0, 0, 0, frame, 0, new Box(0, 0, 0, (int)w, (int)h, 1));

            // SystemRelativeTime is QPC time in 100 ns units.
            long qpc = (long)(latest.SystemRelativeTime.Ticks * (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond);
            return new CaptureUpdate(true, false, qpc, qpc);
        }
    }

    private bool TryOpen()
    {
        nextRetry = Environment.TickCount64 + 250;

        gdiName = resolveGdiName();
        var opened = Displays.DxgiOutputs.Open(gdiName);
        if (opened == null) return false;
        IntPtr monitor = opened.Value.Output.Description.Monitor;
        opened.Value.Output.Dispose();
        opened.Value.Adapter.Dispose();

        try
        {
            winrtDevice ??= CreateWinRTDevice(device);
            item = CreateItemForMonitor(monitor);
            item.Closed += (_, _) => closed = true;
            poolSize = item.Size;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
            pool.FrameArrived += (_, _) => frameArrived.Set();
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = true;
            session.StartCapture();
            closed = false;
            Log.Write($"GraphicsCapture: capturing {gdiName}, {poolSize.Width}×{poolSize.Height}.");
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"GraphicsCapture: start on {gdiName} failed: {e.Message}");
            Close();
            return false;
        }
    }

    private void EnsureTexture(uint width, uint height)
    {
        if (texture != null)
        {
            var d = texture.Description;
            if (d.Width == width && d.Height == height) return;
            texture.Dispose();
        }

        texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
    }

    private static IDirect3DDevice CreateWinRTDevice(ID3D11Device device)
    {
        using IDXGIDevice dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr raw));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(raw);
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(Native.WindowsCreateString(className, (uint)className.Length, out IntPtr hstring));
        IntPtr interop = IntPtr.Zero, raw = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(Native.RoGetActivationFactory(hstring, IID_IGraphicsCaptureItemInterop, out interop));
            // IGraphicsCaptureItemInterop: slot 3 CreateForWindow, slot 4 CreateForMonitor.
            Marshal.ThrowExceptionForHR(Native.CallSlot(interop, 4, monitor, IID_IGraphicsCaptureItem, out raw));
            return GraphicsCaptureItem.FromAbi(raw);
        }
        finally
        {
            if (raw != IntPtr.Zero) Marshal.Release(raw);
            if (interop != IntPtr.Zero) Marshal.Release(interop);
            Native.WindowsDeleteString(hstring);
        }
    }

    private static ID3D11Texture2D TextureOf(IDirect3DSurface surface)
    {
        IntPtr raw = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(raw, IID_IDirect3DDxgiInterfaceAccess, out IntPtr access));
            try
            {
                // IDirect3DDxgiInterfaceAccess::GetInterface is slot 3.
                Marshal.ThrowExceptionForHR(Native.CallSlot(access, 3, typeof(ID3D11Texture2D).GUID, out IntPtr tex));
                return new ID3D11Texture2D(tex);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    private void Close()
    {
        session?.Dispose();
        session = null;
        pool?.Dispose();
        pool = null;
        item = null;
    }

    public void Dispose()
    {
        Close();
        (winrtDevice as IDisposable)?.Dispose();
        winrtDevice = null;
        texture?.Dispose();
        texture = null;
        frameArrived.Dispose();
    }
}
