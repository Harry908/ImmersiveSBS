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
public sealed class VideoDepthEstimator : ITemporalDepthRunner
{
    private readonly InferenceSession first;
    private readonly InferenceSession step;
    private readonly OrtAllocator allocator;
    private readonly OrtValue[] ring = new OrtValue[StateWindow.Slots];
    private readonly RunOptions runOptions = new();
    private readonly long[] inputShape, outputShape;
    private readonly string[] pastNames = [.. Enumerable.Range(0, StateWindow.Past).Select(k => $"past{k}")];
    private readonly StateWindow window = new();

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
        for (int i = 0; i < StateWindow.Slots; i++) ring[i] = OrtValue.CreateAllocatedTensorValue(allocator, TensorElementType.Float, [stateLength]);
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
    public void Reset() => window.Reset();

    public void Run(float[] input, float[] output)
    {
        if (input.Length != InputLength || output.Length != OutputLength)
            throw new ArgumentException("Buffer sizes do not match the model.");

        int slot = window.FreeSlot;
        bool start = window.IsEmpty;
        InferenceSession session = start ? first : step;
        using OrtValue image = OrtValue.CreateTensorValueFromMemory(input, inputShape);
        using OrtValue depth = OrtValue.CreateTensorValueFromMemory(output, outputShape);
        using OrtIoBinding binding = session.CreateIoBinding();
        binding.BindInput("image", image);
        if (!start)
        {
            for (int k = 0; k < StateWindow.Past; k++)
                binding.BindInput(pastNames[k], ring[window.Read(k)]);
        }
        binding.BindOutput("depth", depth);
        binding.BindOutput("state", ring[slot]);
        session.RunWithBinding(runOptions, binding);
        binding.SynchronizeBoundOutputs();
        window.Add(slot);
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

/// <summary>
/// Which ring slots a streaming step reads, as the authors' infer_video_depth_one keeps its
/// frame_cache_list: 32 copies of the first frame's state, then each new state appended and,
/// from the 11th frame (id + INFER_LEN &gt; gap + 1, gap 41), the entry after the first deleted;
/// each step reads entries [0:2] + [-29:]. A slot is free again once no entry holds it.
/// </summary>
public sealed class StateWindow
{
    /// <summary>States each step reads (the authors' INFER_LEN − 1).</summary>
    public const int Past = 31;

    private const int Window = 32;      // the authors' INFER_LEN
    private const int Kept = 42;        // first frame + 41 newest (their gap + 1)

    /// <summary>Ring slots: the most kept, and the one being written.</summary>
    public const int Slots = Kept + 1;

    /// <summary>The ring slot of each entry, oldest first; empty before the first frame.</summary>
    private readonly List<int> entries = [];
    private readonly bool[] used = new bool[Slots];
    private int frames;

    public bool IsEmpty => entries.Count == 0;

    /// <summary>A slot no entry holds, for the next state.</summary>
    public int FreeSlot => Array.IndexOf(used, false);

    /// <summary>The slot of the <paramref name="k"/>-th state a step reads (0 ≤ k &lt; <see cref="Past"/>).</summary>
    public int Read(int k) => entries[k < 2 ? k : entries.Count - (Past - k)];

    /// <summary>The frame's state, written to <paramref name="slot"/>, joins the window.</summary>
    public void Add(int slot)
    {
        used[slot] = true;
        if (entries.Count == 0)
            for (int i = 0; i < Window; i++) entries.Add(slot);
        else
            entries.Add(slot);
        if (frames++ + Window > Kept)
        {
            int gone = entries[1];
            entries.RemoveAt(1);
            if (!entries.Contains(gone)) used[gone] = false;
        }
    }

    /// <summary>Forgets every frame: the next one is the first.</summary>
    public void Reset()
    {
        entries.Clear();
        Array.Clear(used);
        frames = 0;
    }
}
