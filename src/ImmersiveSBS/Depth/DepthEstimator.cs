using System.Diagnostics;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using OrtSessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace ImmersiveSBS.Depth;

/// <summary>
/// One depth model on TensorRT for RTX. The first use compiles the model into an EPContext
/// model with the TensorRT engine embedded, cached per model, provider version and GPU under
/// %LOCALAPPDATA%\ImmersiveSBS\engines; later starts load that in about a second instead of
/// building the engine again (12–18 s, video-3d-plan.md §0). Not thread-safe: one caller.
/// </summary>
public sealed class DepthEstimator : IDisposable
{
    private readonly InferenceSession session;
    private readonly string inputName;
    private readonly string outputName;
    private readonly long[] inputShape;
    private readonly long[] outputShape;
    private readonly RunOptions runOptions = new();

    private DepthEstimator(DepthModel model, InferenceSession session)
    {
        Model = model;
        this.session = session;
        inputName = session.InputNames[0];
        outputName = session.OutputNames[0];
        inputShape = [1, 3, model.Height, model.Width];
        // Exports differ in rank ([1, H, W] or [1, 1, H, W]); the element count is H × W either way.
        outputShape = session.OutputMetadata[outputName].Dimensions.Select(d => d > 0 ? (long)d : 1).ToArray();
        if (outputShape.Aggregate(1L, (a, b) => a * b) != (long)model.Width * model.Height) outputShape = [1, model.Height, model.Width];
    }

    public static string EnginesDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImmersiveSBS", "engines");

    public DepthModel Model { get; }

    /// <summary>Elements of the input tensor: 3 × H × W, planar RGB, ImageNet-normalised.</summary>
    public int InputLength => 3 * Model.Width * Model.Height;

    /// <summary>Elements of the output: H × W relative inverse depth.</summary>
    public int OutputLength => Model.Width * Model.Height;

    /// <summary>
    /// Loads <paramref name="model"/>, compiling and caching its engine first if needed.
    /// <paramref name="progress"/> receives short status lines for the UI.
    /// </summary>
    public static DepthEstimator Load(DepthModel model, Action<string>? progress = null)
    {
        if (!model.Exists) throw new FileNotFoundException($"The depth model {model.Stem}.onnx is missing from {DepthModel.ModelsDirectory}.");

        IReadOnlyList<OrtEpDevice> devices = TensorRtRtx.Devices();
        OrtEpDevice device = devices[0];
        string gpu = $"{device.HardwareDevice.VendorId:x4}-{device.HardwareDevice.DeviceId:x4}";
        Directory.CreateDirectory(EnginesDirectory);
        string compiled = Path.Combine(EnginesDirectory, $"{model.Stem}.trtrtx-{TensorRtRtx.Version}.{gpu}.ctx.onnx");

        var watch = Stopwatch.StartNew();
        if (!File.Exists(compiled))
        {
            progress?.Invoke($"Preparing {model} for this GPU (first use, about 20 s)…");
            if (!TryCompile(model, device, compiled)) compiled = model.FilePath;
        }

        try
        {
            var estimator = new DepthEstimator(model, CreateSession(compiled, device));
            Log.Write($"Depth: loaded {model} from {Path.GetFileName(compiled)} in {watch.ElapsedMilliseconds} ms.");
            return estimator;
        }
        catch (OnnxRuntimeException e) when (compiled != model.FilePath)
        {
            // A stale engine (driver or provider update): rebuild once.
            Log.Write($"Depth: cached engine failed to load ({e.Message}); rebuilding.");
            File.Delete(compiled);
            progress?.Invoke($"Rebuilding {model} for this GPU…");
            string path = TryCompile(model, device, compiled) ? compiled : model.FilePath;
            return new DepthEstimator(model, CreateSession(path, device));
        }
    }

    private static InferenceSession CreateSession(string path, OrtEpDevice device)
    {
        using var options = NewOptions(device);
        return new InferenceSession(path, options);
    }

    private static OrtSessionOptions NewOptions(OrtEpDevice device)
    {
        var options = new OrtSessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        // The provider compiles kernels just in time when a session loads; its runtime cache keeps
        // them, so only the first load on a machine pays for it.
        options.AppendExecutionProvider(OrtEnv.Instance(), [device], new Dictionary<string, string>
        {
            ["nv_runtime_cache_path"] = Path.Combine(EnginesDirectory, "runtime-cache"),
        });
        return options;
    }

    /// <summary>Compiles to an EPContext model with the engine embedded; false keeps the plain model.</summary>
    private static bool TryCompile(DepthModel model, OrtEpDevice device, string output)
    {
        string temp = output + ".tmp";
        var watch = Stopwatch.StartNew();
        try
        {
            using OrtSessionOptions options = NewOptions(device);
            using var compile = new OrtModelCompilationOptions(options);
            compile.SetInputModelPath(model.FilePath);
            compile.SetOutputModelPath(temp);
            compile.SetEpContextEmbedMode(true);
            compile.CompileModel();
            File.Move(temp, output, overwrite: true);
            Log.Write($"Depth: compiled {model} in {watch.Elapsed.TotalSeconds:0.0} s to {Path.GetFileName(output)}.");
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"Depth: compiling {model} failed ({e.Message}); the engine will be built at each start.");
            try { File.Delete(temp); } catch { }
            return false;
        }
    }

    /// <summary>Runs the model: <paramref name="input"/> → <paramref name="output"/>, both caller-owned.</summary>
    public void Run(float[] input, float[] output)
    {
        if (input.Length != InputLength || output.Length != OutputLength)
            throw new ArgumentException("Buffer sizes do not match the model.");

        using OrtValue inValue = OrtValue.CreateTensorValueFromMemory(input, inputShape);
        using OrtValue outValue = OrtValue.CreateTensorValueFromMemory(output, outputShape);
        session.Run(runOptions, [inputName], [inValue], [outputName], [outValue]);
    }

    public void Dispose()
    {
        runOptions.Dispose();
        session.Dispose();
    }
}
