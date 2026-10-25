using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ImmersiveSBS.Tests;

/// <summary>
/// The D3D11 device of the GPU tests: on the high-performance GPU, which is where the app's runs
/// (the virtual monitor is on it), else on the default adapter, else WARP. The default adapter is
/// the one driving the main display: on a laptop with the built-in screen as the main display,
/// the integrated GPU, where the eyes took 6.2 ms against 0.9 ms on the RTX 3060.
/// </summary>
internal static class TestDevice
{
    public static ID3D11Device Create()
    {
        try
        {
            using IDXGIFactory6 factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
            if (factory.EnumAdapterByGpuPreference(0, GpuPreference.HighPerformance, out IDXGIAdapter1? adapter).Success && adapter != null)
            {
                using (adapter)
                {
                    if (D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                            [FeatureLevel.Level_11_0], out ID3D11Device? fast).Success)
                        return fast!;
                }
            }
        }
        catch (SharpGenException)
        {
            // No DXGI 1.6 (or no such adapter): the default one below.
        }

        if (D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out ID3D11Device? d).Failure)
        {
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out d).CheckError();
        }
        return d!;
    }

    /// <summary>The GPU <paramref name="device"/> runs on, for the timing tests' output.</summary>
    public static string AdapterName(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgi.GetAdapter();
        return adapter.Description.Description;
    }

    /// <summary>A BGRA picture the shaders can read, from <paramref name="pixels"/> (<paramref name="w"/>×<paramref name="h"/>×4 bytes, rows tight).</summary>
    public static unsafe ID3D11Texture2D CreateBgra(ID3D11Device device, int w, int h, byte[] pixels)
    {
        fixed (byte* p = pixels)
        {
            return device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            }, [new Vortice.DataRectangle((IntPtr)p, (uint)(w * 4))]);
        }
    }

    /// <summary>Reads an eyes texture, both eyes of <paramref name="w"/>×<paramref name="h"/> side by side, left first: BGRA bytes, rows tight.</summary>
    public static unsafe byte[] ReadEyes(ID3D11Device device, ID3D11ShaderResourceView view, int w, int h)
    {
        using ID3D11Resource resource = view.Resource;
        using ID3D11Texture2D eyes = resource.QueryInterface<ID3D11Texture2D>();
        var d = eyes.Description;
        d.Usage = ResourceUsage.Staging;
        d.BindFlags = BindFlags.None;
        d.CPUAccessFlags = CpuAccessFlags.Read;
        using ID3D11Texture2D staging = device.CreateTexture2D(d);
        device.ImmediateContext.CopyResource(staging, eyes);
        var m = device.ImmediateContext.Map(staging, 0, MapMode.Read);
        var result = new byte[2 * w * h * 4];
        for (int y = 0; y < h; y++)
            new ReadOnlySpan<byte>((byte*)m.DataPointer + y * m.RowPitch, 2 * w * 4).CopyTo(result.AsSpan(y * 2 * w * 4));
        device.ImmediateContext.Unmap(staging, 0);
        return result;
    }
}
