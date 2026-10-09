using System.Diagnostics;

namespace ImmersiveSBS.Depth;

public enum DepthServiceState
{
    /// <summary>Loading a prepared model (about 1–4 s).</summary>
    Loading,
    /// <summary>First use of this model and size on this GPU: building its engine (about 20 s).</summary>
    Preparing,
    Ready,
    Failed,
}

/// <summary>A post-processed nearness map at model resolution, 1 = nearest.</summary>
public sealed class DepthFrame(int width, int height)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public float[] Nearness { get; } = new float[width * height];

    /// <summary>The render thread's id of the video frame this depth belongs to.</summary>
    public long FrameId { get; set; }

    public double InferenceMs { get; set; }

    public bool SceneCut { get; set; }
}

/// <summary>
/// Runs the depth model on its own thread (video-3d-plan.md §4.1 stage 4–5). The render thread
/// submits the newest preprocessed frame and takes the newest result; a slow model drops frames
/// rather than queueing them. Owns the model across sessions, so switching between the glasses
/// and the window does not reload it.
/// </summary>
public sealed class DepthService : IDisposable
{
    private readonly Thread thread;
    private readonly AutoResetEvent wake = new(false);
    private readonly object gate = new();
    private volatile bool stopping;
    private volatile bool resetRequested;

    private float[]? pending;          // submitted, not yet taken by the worker
    private long pendingId;
    private double pendingTime;
    private float[]? spare;            // a free input buffer
    private DepthFrame? ready;         // newest result, not yet taken
    private DepthFrame? spareFrame;

    private DepthEstimator? estimator;
    private DepthPostProcessor? post;
    private double lastTime = double.NaN;
    private double inferenceSum;
    private int inferenceCount;

    public DepthService(DepthModel model)
    {
        Model = model;
        Status = $"Loading {model}…";
        thread = new Thread(Run) { Name = "Depth", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    public DepthModel Model { get; }

    /// <summary>Smoothing and scene-cut sensitivity of the post-processing; any thread, used from the next frame.</summary>
    public StereoTuning Tuning
    {
        get => tuning;
        set => tuning = value;
    }

    private volatile StereoTuning tuning = StereoTuning.Default;

    /// <summary>The screen-plane nearness c, where static overlays are put; any thread.</summary>
    public double Convergence { get; set; } = 0.35;

    /// <summary>A result is waiting to be taken.</summary>
    public bool HasResult
    {
        get { lock (gate) return ready != null; }
    }

    public DepthServiceState State { get; private set; } = DepthServiceState.Loading;

    /// <summary>What the service is doing, for the UI: preparing, an error, or timings.</summary>
    public string Status { get; private set; }

    /// <summary>When the model started loading, for the elapsed time while it loads.</summary>
    public long LoadStarted { get; } = Stopwatch.GetTimestamp();

    /// <summary>Raised on the depth thread when <see cref="State"/> or <see cref="Status"/> changes.</summary>
    public event Action? StatusChanged;

    /// <summary>Average inference time over the last second, ms; 0 before any.</summary>
    public double AverageInferenceMs { get; private set; }

    /// <summary>
    /// Hands the newest model input to the worker (copied). An input not yet taken is replaced.
    /// <paramref name="timeSeconds"/> is the frame's capture time, for temporal filtering.
    /// </summary>
    public void Submit(ReadOnlySpan<float> input, long frameId, double timeSeconds)
    {
        if (State != DepthServiceState.Ready) return;
        lock (gate)
        {
            float[] buffer = pending ?? spare ?? new float[input.Length];
            spare = null;
            input.CopyTo(buffer);
            pending = buffer;
            pendingId = frameId;
            pendingTime = timeSeconds;
        }
        wake.Set();
    }

    /// <summary>
    /// Takes the newest result, if there is one, by swapping it with <paramref name="frame"/>
    /// (the caller's previous frame becomes the service's spare).
    /// </summary>
    public bool TryTake(ref DepthFrame? frame)
    {
        lock (gate)
        {
            if (ready == null) return false;
            // A frame from an earlier model (another size) is not reused.
            if (frame != null && frame.Width == Model.Width && frame.Height == Model.Height) spareFrame = frame;
            frame = ready;
            ready = null;
            return true;
        }
    }

    private void Run()
    {
        try
        {
            estimator = DepthEstimator.Load(Model, s => SetStatus(DepthServiceState.Preparing, s));
            post = new DepthPostProcessor(Model.Width, Model.Height);
            SetStatus(DepthServiceState.Ready, $"{Model} ready");
        }
        catch (Exception e)
        {
            Log.Write("Depth: " + e);
            SetStatus(DepthServiceState.Failed, e.Message);
            return;
        }

        var output = new float[estimator.OutputLength];
        var luma = new float[estimator.OutputLength];
        long windowStart = Stopwatch.GetTimestamp();

        while (!stopping)
        {
            wake.WaitOne(500);
            float[]? input;
            long id;
            double time;
            lock (gate)
            {
                input = pending;
                id = pendingId;
                time = pendingTime;
                pending = null;
            }
            if (input == null) continue;

            try
            {
                if (resetRequested)
                {
                    resetRequested = false;
                    post!.Reset();
                    lastTime = double.NaN;
                }

                long t0 = Stopwatch.GetTimestamp();
                estimator.Run(input, output);
                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

                Luma(input, luma, Model.Width * Model.Height);
                double dt = double.IsNaN(lastTime) ? 1.0 / 30 : time - lastTime;
                lastTime = time;

                DepthFrame frame;
                lock (gate) { frame = spareFrame ?? new DepthFrame(Model.Width, Model.Height); spareFrame = null; }
                StereoTuning tuning = Tuning;
                post!.AlphaMin = tuning.Smoothing;
                post.SceneCutThreshold = tuning.SceneCut;
                post.StaticOverlays = tuning.StaticOverlays;
                post.Captions = tuning.Captions;
                post.StaticNearness = Convergence;
                frame.SceneCut = post.Process(output, luma, dt, frame.Nearness);
                frame.FrameId = id;
                frame.InferenceMs = ms;

                lock (gate)
                {
                    spareFrame ??= ready;   // an untaken result is dropped for the newer one
                    ready = frame;
                    spare = input;
                }

                inferenceSum += ms;
                inferenceCount++;
                if (Stopwatch.GetElapsedTime(windowStart).TotalSeconds >= 1)
                {
                    AverageInferenceMs = inferenceSum / inferenceCount;
                    SetStatus(DepthServiceState.Ready, $"{Model}: {AverageInferenceMs:0.0} ms, {inferenceCount} depth frames/s");
                    inferenceSum = 0;
                    inferenceCount = 0;
                    windowStart = Stopwatch.GetTimestamp();
                }
            }
            catch (Exception e)
            {
                Log.Write("Depth: inference failed: " + e);
                SetStatus(DepthServiceState.Failed, "Inference failed: " + e.Message);
                return;
            }
        }
    }

    /// <summary>Luma in [0, 1] from the ImageNet-normalised planar RGB input.</summary>
    private static void Luma(float[] input, float[] luma, int plane)
    {
        for (int i = 0; i < plane; i++)
        {
            float r = input[i] * 0.229f + 0.485f;
            float g = input[plane + i] * 0.224f + 0.456f;
            float b = input[2 * plane + i] * 0.225f + 0.406f;
            luma[i] = 0.299f * r + 0.587f * g + 0.114f * b;
        }
    }

    /// <summary>Forgets temporal history, e.g. when the video region changed; any thread.</summary>
    public void ResetHistory() => resetRequested = true;

    private void SetStatus(DepthServiceState state, string status)
    {
        State = state;
        Status = status;
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Stops the worker and unloads the model (its GPU memory). A worker still busy after a few
    /// seconds (loading, or compiling a first-use engine) keeps its model: unloading it under a
    /// running inference would crash, and process exit frees it anyway.
    /// </summary>
    public void Dispose()
    {
        stopping = true;
        wake.Set();
        if (!thread.Join(3000))
        {
            Log.Write($"Depth: the worker is still busy ({State}); {Model} is left for the process to free.");
            return;
        }
        if (estimator != null)
        {
            estimator.Dispose();
            estimator = null;
            Log.Write($"Depth: unloaded {Model}.");
        }
        wake.Dispose();
    }
}
