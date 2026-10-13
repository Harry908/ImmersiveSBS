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
}
