using System.IO;
using Microsoft.ML.OnnxRuntime;
using Windows.Management.Deployment;

namespace ImmersiveSBS.Depth;

/// <summary>
/// The TensorRT for RTX execution provider that Windows ML installs as a package
/// (MicrosoftCorporationII.WinML.NVIDIA.TRT-RTX.EP.*). It is found through the package manager
/// for the current user, its provider DLL registered with ONNX Runtime once per process.
/// DirectML measured ~65× slower on this machine (video-3d-plan.md §0), so there is no fallback.
/// </summary>
public static class TensorRtRtx
{
    public const string ProviderName = "NvTensorRTRTXExecutionProvider";
    private const string PackagePrefix = "MicrosoftCorporationII.WinML.NVIDIA.TRT-RTX.EP";
    private const string ProviderDll = @"ExecutionProvider\onnxruntime_providers_nv_tensorrt_rtx.dll";

    private static readonly object Gate = new();
    private static bool registered;

    /// <summary>The installed provider's package version, e.g. 2.30.49.0; set once registered.</summary>
    public static string? Version { get; private set; }

    /// <summary>
    /// Registers the provider and returns its devices (one per NVIDIA RTX GPU). Throws with a
    /// readable message when it is not installed or has no device.
    /// </summary>
    public static IReadOnlyList<OrtEpDevice> Devices()
    {
        lock (Gate)
        {
            OrtEnv env = OrtEnv.Instance();
            if (!registered)
            {
                (string path, string version) = Locate()
                    ?? throw new InvalidOperationException(
                        "TensorRT for RTX is not installed. Windows ML installs it on PCs with an NVIDIA RTX GPU (Windows 11 24H2 or later).");
                env.RegisterExecutionProviderLibrary(ProviderName, path);
                registered = true;
                Version = version;
                Log.Write($"Depth: registered {ProviderName} {version} from {path}.");
            }

            var devices = env.GetEpDevices().Where(d => d.EpName == ProviderName).ToList();
            if (devices.Count == 0) throw new InvalidOperationException("TensorRT for RTX found no supported NVIDIA GPU.");
            return devices;
        }
    }

    /// <summary>The newest installed provider package's DLL and version, or null.</summary>
    private static (string Path, string Version)? Locate()
    {
        try
        {
            var best = new PackageManager().FindPackagesForUser(string.Empty)
                .Where(p => p.Id.Name.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(p => (Package: p, Version: new Version(p.Id.Version.Major, p.Id.Version.Minor, p.Id.Version.Build, p.Id.Version.Revision)))
                .OrderByDescending(x => x.Version)
                .FirstOrDefault(x => File.Exists(System.IO.Path.Combine(x.Package.InstalledPath, ProviderDll)));
            return best.Package == null
                ? null
                : (System.IO.Path.Combine(best.Package.InstalledPath, ProviderDll), best.Version.ToString());
        }
        catch (Exception e)
        {
            Log.Write("Depth: could not query packages for TensorRT for RTX: " + e.Message);
            return null;
        }
    }
}
