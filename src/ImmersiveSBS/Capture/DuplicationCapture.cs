using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ImmersiveSBS.Capture;

/// <summary>
/// DXGI Desktop Duplication of one output. The device must be on the adapter that owns the
/// output. Frames are copied into a texture of our own so the duplication frame can be
/// released at once.
/// </summary>
public sealed class DuplicationCapture : ICaptureSource
{
    private const int RetryIntervalMs = 250;

    private readonly ID3D11Device device;
    private readonly Func<string?> resolveGdiName;
    private string? gdiName;
    private IDXGIOutputDuplication? duplication;
    private ID3D11Texture2D? texture;
    private byte[] shapeBuffer = new byte[64 * 64 * 4];
    private long nextRetry;
    private int lastOpenError;

    public DuplicationCapture(ID3D11Device device, Func<string?> resolveGdiName)
    {
        this.device = device;
        this.resolveGdiName = resolveGdiName;
        TryOpen();
    }

    public ID3D11Texture2D? Texture => texture;

    public PointerState Pointer { get; } = new();

    public CaptureUpdate Acquire(int timeoutMs)
    {
        if (duplication == null)
        {
            if (Environment.TickCount64 < nextRetry || !TryOpen())
            {
                Thread.Sleep(Math.Min(timeoutMs, RetryIntervalMs));
                return default;
            }
        }

        Result r = duplication!.AcquireNextFrame((uint)timeoutMs, out OutduplFrameInfo info, out IDXGIResource? resource);
        if (r.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return default;
        if (r.Failure)
        {
            // Access lost: mode change, secure desktop, full-screen app switch. Reopen.
            Log.Write($"Duplication: AcquireNextFrame failed 0x{r.Code:X8}; reopening.");
            Close();
            return default;
        }

        bool frame = false, pointer = false;
        long qpc = 0, frameQpc = 0;
        try
        {
            if (info.LastPresentTime != 0)
            {
                using ID3D11Texture2D desktop = resource!.QueryInterface<ID3D11Texture2D>();
                EnsureTexture(desktop.Description);
                device.ImmediateContext.CopyResource(texture!, desktop);
                frame = true;
                qpc = frameQpc = info.LastPresentTime;
            }

            if (info.LastMouseUpdateTime != 0)
            {
                Pointer.X = info.PointerPosition.Position.X;
                Pointer.Y = info.PointerPosition.Position.Y;
                Pointer.Visible = info.PointerPosition.Visible;
                pointer = true;
                qpc = Math.Max(qpc, info.LastMouseUpdateTime);
            }

            if (info.PointerShapeBufferSize != 0)
            {
                ReadShape(info.PointerShapeBufferSize);
                pointer = true;
            }
        }
        finally
        {
            resource?.Dispose();
            duplication.ReleaseFrame();
        }

        return new CaptureUpdate(frame, pointer, qpc, frameQpc);
    }

    private unsafe void ReadShape(uint size)
    {
        if (shapeBuffer.Length < size) shapeBuffer = new byte[size];

        fixed (byte* p = shapeBuffer)
        {
            Result r = duplication!.GetFramePointerShape((uint)shapeBuffer.Length, (IntPtr)p, out uint used,
                out OutduplPointerShapeInfo shape);
            if (r.Failure)
            {
                Log.Write($"Duplication: GetFramePointerShape failed 0x{r.Code:X8}.");
                return;
            }

            Pointer.SetShape(shape.Type, (int)shape.Width, (int)shape.Height, (int)shape.Pitch,
                new ReadOnlySpan<byte>(p, (int)used), shape.HotSpot.X, shape.HotSpot.Y);
        }
    }

    private void EnsureTexture(Texture2DDescription source)
    {
        if (texture != null)
        {
            var d = texture.Description;
            if (d.Width == source.Width && d.Height == source.Height && d.Format == source.Format) return;
            texture.Dispose();
        }

        texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = source.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        Log.Write($"Duplication: desktop texture {source.Width}×{source.Height} {source.Format}.");
    }

    private bool TryOpen()
    {
        nextRetry = Environment.TickCount64 + RetryIntervalMs;

        gdiName = resolveGdiName();
        var opened = Displays.DxgiOutputs.Open(gdiName);
        if (opened == null) return false;

        (IDXGIAdapter1 adapter, IDXGIOutput output) = opened.Value;
        try
        {
            using IDXGIOutput5 output5 = output.QueryInterface<IDXGIOutput5>();
            duplication = output5.DuplicateOutput1(device, [Format.B8G8R8A8_UNorm]);
            var desc = duplication.Description;
            Log.Write($"Duplication: opened {gdiName} on {adapter.Description1.Description}, " +
                      $"{desc.ModeDescription.Width}×{desc.ModeDescription.Height} " +
                      $"@ {desc.ModeDescription.RefreshRate.Numerator}/{desc.ModeDescription.RefreshRate.Denominator}, " +
                      $"rotation {desc.Rotation}, system memory {desc.DesktopImageInSystemMemory}.");
            lastOpenError = 0;
            return true;
        }
        catch (SharpGenException e)
        {
            // E_ACCESSDENIED while the secure desktop (UAC prompt, lock screen) is up: no capture
            // API can see it, so the last frame stays until it closes. DXGI_ERROR_UNSUPPORTED when
            // the device is on another adapter than the output. Logged once per error, not per retry.
            if (e.HResult != lastOpenError)
            {
                lastOpenError = e.HResult;
                Log.Write($"Duplication: DuplicateOutput1 on {gdiName} failed 0x{e.HResult:X8}" +
                          (e.HResult == unchecked((int)0x80070005) ? " (secure desktop); retrying until it closes." : "; retrying."));
            }
            duplication = null;
            return false;
        }
        finally
        {
            output.Dispose();
            adapter.Dispose();
        }
    }

    private void Close()
    {
        duplication?.Dispose();
        duplication = null;
    }

    public void Dispose()
    {
        Close();
        texture?.Dispose();
        texture = null;
    }
}
