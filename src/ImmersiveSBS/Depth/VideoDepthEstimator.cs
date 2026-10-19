using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ImmersiveSBS.Depth;

/// <summary>
/// Video Depth Anything Small in its streaming mode (improvement-plan.md §10.2) on TensorRT for
/// RTX. Each frame goes through the image encoder alone; the head's temporal attention also
/// reads the hidden states the frames before it left (8 tensors a frame, 4 motion modules × 2
/// attention blocks, flattened into one state: 956,160 floats at 518×294). The files are the
/// authors' model exported as two static graphs: the first frame (image → depth, state) and the
/// step (image and 31 past states → depth, state).
///
/// The states never leave the GPU: 31 of them a call would be 118 MB over the bus. They live in
/// a ring of device tensors bound to the step's inputs in the order the window reads them, and
/// each call writes its state into a free slot. The window is the authors' (their
/// infer_video_depth_one): it starts as 32 copies of the first frame's state, then keeps that
/// first frame for good and the 41 newest, and each call reads the first two and the last 29.
/// <see cref="Reset"/> starts it over at the next frame (a seek, a scene cut). Not thread-safe:
/// one caller.
/// </summary>
public sealed class VideoDepthEstimator : IDepthRunner
{
    private const int Past = 31;        // states each step reads
    private const int Window = 32;      // the authors' INFER_LEN
    private const int Kept = 42;        // first frame + 41 newest
    private const int Slots = Kept + 1; // and the one being written

    private readonly InferenceSession first;
    private readonly InferenceSession step;
    private readonly OrtAllocator allocator;
    private readonly OrtValue[] ring = new OrtValue[Slots];
    private readonly RunOptions runOptions = new();
    private readonly long[] inputShape, outputShape;
    private readonly string[] pastNames = [.. Enumerable.Range(0, Past).Select(k => $"past{k}")];

    /// <summary>The ring slot of each entry of the window, oldest first; empty before the first frame.</summary>
    private readonly List<int> window = [];
    private readonly bool[] used = new bool[Slots];
    private int frames;

    private VideoDepthEstimator(DepthModel model, InferenceSession first, InferenceSession step, OrtEpDevice device)
    {
        Model = model;
        this.first = first;
        this.step = step;
        inputShape = [1, 3, model.Height, model.Width];
        outputShape = [1, model.Height, model.Width];
        long stateLength = step.InputMetadata["past0"].Dimensions[0];
        StateBytes = stateLength * sizeof(float);
        allocator = new OrtAllocator(step, device.GetMemoryInfo(OrtDeviceMemoryType.DEFAULT));
        for (int i = 0; i < Slots; i++) ring[i] = OrtValue.CreateAllocatedTensorValue(allocator, TensorElementType.Float, [stateLength]);
    }

    public DepthModel Model { get; }

    public int InputLength => 3 * Model.Width * Model.Height;

    public int OutputLength => Model.Width * Model.Height;

    /// <summary>One frame's state on the GPU, bytes.</summary>
    public long StateBytes { get; }

    public static VideoDepthEstimator Load(DepthModel model, Action<string>? progress = null)
    {
        if (!model.Exists)
            throw new System.IO.FileNotFoundException($"The depth model {model.Stem}.onnx or {model.FirstStem}.onnx is missing from {DepthModel.ModelsDirectory}.");
        InferenceSession firstSession = DepthEstimator.Open(model + " (first frame)", model.FirstStem, model.FirstFilePath, progress, out _);
        InferenceSession stepSession = DepthEstimator.Open(model.ToString(), model.Stem, model.FilePath, progress, out OrtEpDevice device);
        return new VideoDepthEstimator(model, firstSession, stepSession, device);
    }

    /// <summary>Forgets the frames before: the next one starts a new window, as the stream's first.</summary>
    public void Reset()
    {
        window.Clear();
        Array.Clear(used);
        frames = 0;
    }

    public void Run(float[] input, float[] output)
    {
        if (input.Length != InputLength || output.Length != OutputLength)
            throw new ArgumentException("Buffer sizes do not match the model.");

        int slot = Array.IndexOf(used, false);
        bool start = window.Count == 0;
        InferenceSession session = start ? first : step;
        using OrtValue image = OrtValue.CreateTensorValueFromMemory(input, inputShape);
        using OrtValue depth = OrtValue.CreateTensorValueFromMemory(output, outputShape);
        using OrtIoBinding binding = session.CreateIoBinding();
        binding.BindInput("image", image);
        if (!start)
        {
            for (int k = 0; k < Past; k++)
                binding.BindInput(pastNames[k], ring[window[k < 2 ? k : window.Count - (Past - k)]]);
        }
        binding.BindOutput("depth", depth);
        binding.BindOutput("state", ring[slot]);
        session.RunWithBinding(runOptions, binding);
        binding.SynchronizeBoundOutputs();

        used[slot] = true;
        if (start)
            for (int i = 0; i < Window; i++) window.Add(slot);
        else
            window.Add(slot);
        // From the 11th frame on the window is full: the oldest after the first frame goes.
        if (frames++ + Window > Kept)
        {
            int gone = window[1];
            window.RemoveAt(1);
            if (!window.Contains(gone)) used[gone] = false;
        }
    }

    public void Dispose()
    {
        foreach (OrtValue state in ring) state?.Dispose();
        allocator.Dispose();
        runOptions.Dispose();
        step.Dispose();
        first.Dispose();
    }
}
