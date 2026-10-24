using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;
using Windows.Management.Deployment;

namespace ImmersiveSBS.Depth;

/// <summary>
/// The TensorRT for RTX execution provider that Windows ML installs as a package
/// (<see cref="PackageName"/>). It is found through the package manager
/// for the current user, its provider DLL registered with ONNX Runtime once per process. A fresh
/// PC has no such package until some app asks Windows ML for it, so when none is found the app
/// asks (once per run; Windows Update downloads it). Only Windows ML's catalog is used: sessions
/// still run on the app's own ONNX Runtime.
/// DirectML measured ~65× slower on this machine (video-3d-plan.md §0), so there is no fallback.
/// </summary>
public static class TensorRtRtx
{
    public const string ProviderName = "NvTensorRTRTXExecutionProvider";
    /// <summary>
    /// In the package's name: MicrosoftCorporationII.WinML.NVIDIA.TRT-RTX.EP.2 on the old laptop,
    /// Microsoft.WinML.NVIDIA.TRT-RTX.EP.Framework.2 as Windows ML installed it in 2026-10.
    /// </summary>
    private const string PackageName = "WinML.NVIDIA.TRT-RTX.EP";
    private const string ProviderDll = @"ExecutionProvider\onnxruntime_providers_nv_tensorrt_rtx.dll";

    private static readonly object Gate = new();
    private static bool registered;
    private static bool installTried;

    /// <summary>The installed provider's package version, e.g. 2.30.49.0; set once registered.</summary>
    public static string? Version { get; private set; }

    /// <summary>
    /// Registers the provider and returns its devices (one per NVIDIA RTX GPU), first asking
    /// Windows ML to install it if it is missing (<paramref name="progress"/> says so while it
    /// downloads). Throws with a readable message when it is not installed or has no device.
    /// </summary>
    public static IReadOnlyList<OrtEpDevice> Devices(Action<string>? progress = null)
    {
        lock (Gate)
        {
            OrtEnv env = OrtEnv.Instance();
            if (!registered)
            {
                (string path, string version) = Locate() ?? Install(progress)
                    ?? throw new InvalidOperationException(
                        "TensorRT for RTX is not installed, and Windows ML could not install it (the log says why; restart the app to try again). It needs an NVIDIA RTX GPU and Windows 11 24H2 or later.");
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
                .Where(p => p.Id.Name.Contains(PackageName, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Asks Windows ML to install the provider through Windows Update and returns its DLL and
    /// version, or null (logged) when Windows ML offers none for this PC or the download fails.
    /// Tried once per process: a failed request takes several seconds.
    /// </summary>
    private static (string Path, string Version)? Install(Action<string>? progress)
    {
        if (installTried) return null;
        installTried = true;
        try
        {
            ExecutionProvider? provider = ExecutionProviderCatalog.GetDefault().FindAllProviders()
                .FirstOrDefault(p => p.Name == ProviderName);
            if (provider == null)
            {
                Log.Write("Depth: Windows ML offers no TensorRT for RTX for this PC.");
                return null;
            }
            if (provider.ReadyState != ExecutionProviderReadyState.Ready)
            {
                Log.Write($"Depth: TensorRT for RTX is {provider.ReadyState}; asking Windows ML to install it.");
                progress?.Invoke("Installing TensorRT for RTX through Windows Update (first use)…");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                ExecutionProviderReadyResult result = provider.EnsureReadyAsync().AsTask().GetAwaiter().GetResult();
                if (result.Status != ExecutionProviderReadyResultState.Success)
                {
                    Log.Write($"Depth: Windows ML could not install TensorRT for RTX ({result.Status}, " +
                        $"0x{result.ExtendedError?.HResult:X8}: {result.DiagnosticText}) after {watch.Elapsed.TotalSeconds:F0} s.");
                    return null;
                }
                Log.Write($"Depth: Windows ML installed TensorRT for RTX in {watch.Elapsed.TotalSeconds:F0} s.");
            }
            var v = provider.PackageId.Version;
            return (provider.LibraryPath, new Version(v.Major, v.Minor, v.Build, v.Revision).ToString());
        }
        catch (Exception e)
        {
            Log.Write("Depth: Windows ML could not install TensorRT for RTX: " + e.Message);
            return null;
        }
    }
}
