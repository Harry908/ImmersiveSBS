using Vortice.DXGI;

namespace ImmersiveSBS.Displays;

/// <summary>Finds the DXGI output, and its adapter, behind a GDI device name.</summary>
public static class DxgiOutputs
{
    /// <summary>
    /// Opens the output for <paramref name="gdiName"/> (e.g. \\.\DISPLAY19), or null. GDI names
    /// change between sessions, so callers resolve the name from a saved identity first.
    /// </summary>
    public static (IDXGIAdapter1 Adapter, IDXGIOutput Output)? Open(string? gdiName)
    {
        if (string.IsNullOrEmpty(gdiName)) return null;

        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
        {
            for (uint o = 0; adapter!.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
            {
                OutputDescription od = output!.Description;
                if (od.AttachedToDesktop && string.Equals(od.DeviceName, gdiName, StringComparison.OrdinalIgnoreCase))
                    return (adapter, output);
                output.Dispose();
            }
            adapter.Dispose();
        }

        return null;
    }
}
