using System.Collections.Concurrent;
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

/// <summary>What runs the depth model: <see cref="DepthEstimator"/>, or a stand-in in tests.</summary>
public interface IDepthRunner : IDisposable
{
    /// <summary>Floats in one output (model width × height).</summary>
    int OutputLength { get; }

    /// <summary>One frame: planar normalised RGB in, relative inverse depth out.</summary>
    void Run(float[] input, float[] output);
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
/// rather than queueing them. Post-processing runs on a second thread, so the model works on the
/// next frame meanwhile (improvement-plan.md §9 step 1): a depth frame every max(model, post)
/// rather than every model + post, and each frame still takes model, then post. Owns the model
/// across sessions, so switching between the glasses and the window does not reload it.
/// </summary>
public sealed class DepthService : IDisposable
{
    private readonly Thread thread;
    private readonly AutoResetEvent wake = new(false);
    private readonly object gate = new();
    private readonly CancellationTokenSource stopSource = new();
    /// <summary>Model outputs free to fill (more are made while look-ahead holds some), and the one waiting for the post-processing.</summary>
    private readonly ConcurrentBag<PostJob> freeJobs = [];
    private readonly BlockingCollection<PostJob> postJobs = new(1);
    /// <summary>Post thread: model outputs held for the look-ahead, oldest first.</summary>
    private readonly List<PostJob> held = [];
    private float[] lastHeldLuma = [];
    private Thread? postThread;
    private volatile bool stopping;
    private volatile bool resetRequested;

    private float[]? pending;          // submitted, not yet taken by the worker
    private long pendingId;
    private double pendingTime;
    private float[]? spare;            // a free input buffer
    private DepthFrame? ready;         // newest result, not yet taken
    private DepthFrame? spareFrame;

    private readonly Func<DepthModel, Action<string>, IDepthRunner> load;
    private IDepthRunner? estimator;
    private DepthPostProcessor? post;
    private double lastTime = double.NaN;
    private double inferenceSum, postSum, idleSum, flickerSum;
    private int inferenceCount, flickerCount;

    /// <summary>One model output on its way to the post-processing.</summary>
    private sealed class PostJob(int length)
    {
        public readonly float[] Output = new float[length];
        public readonly float[] Luma = new float[length];
        public long Id;
        public double Time, InferenceMs, IdleMs;
        /// <summary>Its own depth range, and whether the picture cut to a new scene just before it.</summary>
        public double P2, P98;
        public bool CutBefore;
    }

    /// <summary>
    /// Look-ahead (improvement-plan.md §10): each frame is post-processed only once this many
    /// frames after it have come, and normalised toward the depth range of all of them, so the
    /// range opens before a near object enters instead of after. For the buffer, which shows a
    /// frame long after its depth is ready; 0 otherwise. Any thread, from the next frame.
    /// </summary>
    public int LookAheadFrames
    {
        get => lookAhead;
        set => lookAhead = Math.Clamp(value, 0, MaxLookAhead);
    }

    private volatile int lookAhead;

    public const int MaxLookAhead = 30;

    /// <summary>Held frames are let go when nothing new came for this long (a paused video).</summary>
    private static readonly TimeSpan LookAheadIdle = TimeSpan.FromMilliseconds(200);

    /// <param name="load">Loads the model (on the worker thread, with a progress callback); the TensorRT estimator by default.</param>
    public DepthService(DepthModel model, Func<DepthModel, Action<string>, IDepthRunner>? load = null)
    {
        Model = model;
        this.load = load ?? ((m, progress) => DepthEstimator.Load(m, progress));
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

    /// <summary>
    /// Soft-edge passes across (improvement-plan.md §7.1), from the depth strength and the
    /// video's size on the render thread; any thread, used from the next frame.
    /// </summary>
    public int SoftEdgePasses
    {
        get => softEdgePasses;
        set => softEdgePasses = Math.Clamp(value, 0, 4);
    }

    private volatile int softEdgePasses;

    /// <summary>What the page draws over the video, model px (improvement-plan.md §7.4); any thread, used from the next frame.</summary>
    public PageBoxes? PageBoxes
    {
        get => pageBoxes;
        set => pageBoxes = value;
    }

    private volatile PageBoxes? pageBoxes;

    /// <summary>Mean flicker over the last second (improvement-plan.md M4), NaN before any.</summary>
    public double AverageFlicker { get; private set; } = double.NaN;

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
        if (stopping || State != DepthServiceState.Ready) return;
        lock (gate)
        {
            float[] buffer = pending ?? spare ?? new float[input.Length];
            spare = null;
            input.CopyTo(buffer);
            pending = buffer;
            pendingId = frameId;
            pendingTime = timeSeconds;
        }
        try
        {
            wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // Swapped out and disposed meanwhile (the user picked another model).
        }
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
            estimator = load(Model, s => SetStatus(DepthServiceState.Preparing, s));
            post = new DepthPostProcessor(Model.Width, Model.Height);
            SetStatus(DepthServiceState.Ready, $"{Model} ready");
        }
        catch (Exception e)
        {
            Log.Write("Depth: " + e);
            SetStatus(DepthServiceState.Failed, e.Message);
            return;
        }

        postThread = new Thread(PostLoop) { Name = "Depth post", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        postThread.Start();
        CancellationToken stop = stopSource.Token;

        while (!stopping)
        {
            long idleStart = Stopwatch.GetTimestamp();
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
            double idle = Stopwatch.GetElapsedTime(idleStart).TotalMilliseconds;

            try
            {
                if (!freeJobs.TryTake(out PostJob? job)) job = new PostJob(estimator.OutputLength);
                long t0 = Stopwatch.GetTimestamp();
                estimator.Run(input, job.Output);
                job.InferenceMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                Luma(input, job.Luma, Model.Width * Model.Height);
                job.Id = id;
                job.Time = time;
                job.IdleMs = idle;
                lock (gate) spare = input;
                // Waits only while the frame before is still being post-processed and another waits.
                postJobs.Add(job, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Write("Depth: inference failed: " + e);
                SetStatus(DepthServiceState.Failed, "Inference failed: " + e.Message);
                return;
            }
        }
    }

    /// <summary>
    /// The post-processing thread: the model's outputs in order, into nearness maps for the
    /// render thread; with look-ahead, each once the frames after it are in.
    /// </summary>
    private void PostLoop()
    {
        CancellationToken stop = stopSource.Token;
        long windowStart = Stopwatch.GetTimestamp();
        while (true)
        {
            PostJob? job = null;
            try
            {
                // While frames are held, wait only so long: a paused video sends no more.
                if (held.Count == 0) job = postJobs.Take(stop);
                else postJobs.TryTake(out job, (int)LookAheadIdle.TotalMilliseconds, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (resetRequested)
                {
                    // Held frames belong to what came before (another region, a seek): let them go as they are.
                    while (held.Count > 0)
                    {
                        PostJob old = Release();
                        PostProcess(old, (old.P2, old.P98), ref windowStart);
                    }
                    resetRequested = false;
                    post!.Reset();
                    lastTime = double.NaN;
                }
                if (job != null)
                {
                    (job.P2, job.P98) = post!.MeasureRange(job.Output);
                    // Only looking ahead needs to know where the cuts are before it gets there.
                    job.CutBefore = LookAheadFrames > 0 && lastHeldLuma.Length == job.Luma.Length
                                    && MeanChange(lastHeldLuma, job.Luma) > Tuning.SceneCut;
                    if (LookAheadFrames > 0)
                    {
                        if (lastHeldLuma.Length != job.Luma.Length) lastHeldLuma = new float[job.Luma.Length];
                        job.Luma.CopyTo(lastHeldLuma, 0);
                    }
                    else
                    {
                        lastHeldLuma = [];
                    }
                    held.Add(job);
                }
                int keep = job == null ? 0 : LookAheadFrames;
                while (held.Count > keep)
                {
                    // The range over this frame and those after it, up to the next scene cut.
                    double p2 = held[0].P2, p98 = held[0].P98;
                    for (int k = 1; k < held.Count && !held[k].CutBefore; k++)
                    {
                        p2 = Math.Min(p2, held[k].P2);
                        p98 = Math.Max(p98, held[k].P98);
                    }
                    PostProcess(Release(), (p2, p98), ref windowStart);
                }
            }
            catch (Exception e)
            {
                Log.Write("Depth: post-processing failed: " + e);
                SetStatus(DepthServiceState.Failed, "Post-processing failed: " + e.Message);
                stopping = true;
                stopSource.Cancel();
                return;
            }
        }
    }

    private PostJob Release()
    {
        PostJob first = held[0];
        held.RemoveAt(0);
        return first;
    }

    private static double MeanChange(float[] a, float[] b)
    {
        var sums = new double[RowBands.RangeCount(a.Length)];
        RowBands.ForRange(a.Length, (part, from, to) =>
        {
            double sum = 0;
            for (int i = from; i < to; i++) sum += Math.Abs(a[i] - b[i]);
            sums[part] = sum;
        });
        return sums.Sum() / a.Length;
    }

    /// <summary>One model output into a nearness map for the render thread; the job goes back to the free ones.</summary>
    private void PostProcess(PostJob job, (double, double) range, ref long windowStart)
    {
        try
        {
            long p0 = Stopwatch.GetTimestamp();
            double dt = double.IsNaN(lastTime) ? 1.0 / 30 : job.Time - lastTime;
            lastTime = job.Time;

            DepthFrame frame;
            lock (gate) { frame = spareFrame ?? new DepthFrame(Model.Width, Model.Height); spareFrame = null; }
            StereoTuning tuning = Tuning;
            post!.AlphaMin = tuning.Smoothing;
            post.SceneCutThreshold = tuning.SceneCut;
            post.StaticOverlays = tuning.StaticOverlays;
            post.Captions = tuning.Captions;
            post.StaticNearness = Convergence;
            post.AntiAlias = tuning.AntiAlias switch
            {
                AutoSwitch.On => true,
                AutoSwitch.Off => false,
                _ => Model.SteppedEdges,
            };
            post.Foreground = tuning.Foreground;
            int passes = SoftEdgePasses;
            post.PageBoxes = PageBoxes;
            post.SoftEdgePassesX = passes;
            post.SoftEdgePassesY = Math.Min(passes, 1);
            frame.SceneCut = post.Process(job.Output, job.Luma, dt, frame.Nearness, range);
            if (double.IsFinite(post.Flicker))
            {
                flickerSum += post.Flicker;
                flickerCount++;
            }
            frame.FrameId = job.Id;
            frame.InferenceMs = job.InferenceMs;

            lock (gate)
            {
                spareFrame ??= ready;   // an untaken result is dropped for the newer one
                ready = frame;
            }

            inferenceSum += job.InferenceMs;
            idleSum += job.IdleMs;
            postSum += Stopwatch.GetElapsedTime(p0).TotalMilliseconds;
            inferenceCount++;
            if (Stopwatch.GetElapsedTime(windowStart).TotalSeconds >= 1)
            {
                AverageInferenceMs = inferenceSum / inferenceCount;
                AverageFlicker = flickerCount > 0 ? flickerSum / flickerCount : double.NaN;
                flickerSum = 0;
                flickerCount = 0;
                // Model, post-processing (alongside the next frame's model) and the model
                // waiting for a frame: where a depth frame's time goes (improvement-plan.md M6).
                int ahead = LookAheadFrames;
                SetStatus(DepthServiceState.Ready, $"{Model}: {AverageInferenceMs:0.0} ms + post {postSum / inferenceCount:0.0} ms alongside, " +
                                                   $"waiting {idleSum / inferenceCount:0.0} ms; {inferenceCount} depth frames/s" +
                                                   (ahead > 0 ? $", looking {ahead} frames ahead" : ""));
                inferenceSum = postSum = idleSum = 0;
                inferenceCount = 0;
                windowStart = Stopwatch.GetTimestamp();
            }
        }
        finally
        {
            freeJobs.Add(job);
        }
    }

    /// <summary>
    /// Luma in [0, 1] from the ImageNet-normalised planar RGB input: on the model's thread between
    /// two runs, so SIMD and in parts on several cores. Y = Σ k_c·(x_c·σ_c + μ_c), folded into
    /// one multiply-add per plane.
    /// </summary>
    public static void Luma(float[] input, float[] luma, int plane)
    {
        const float r = 0.299f * 0.229f, g = 0.587f * 0.224f, b = 0.114f * 0.225f;
        const float offset = 0.299f * 0.485f + 0.587f * 0.456f + 0.114f * 0.406f;
        int v = System.Numerics.Vector<float>.Count;
        RowBands.ForRange(plane, (_, from, to) =>
        {
            int i = from;
            for (; i + v <= to; i += v)
            {
                var y = new System.Numerics.Vector<float>(input, i) * r
                        + new System.Numerics.Vector<float>(input, plane + i) * g
                        + new System.Numerics.Vector<float>(input, 2 * plane + i) * b
                        + new System.Numerics.Vector<float>(offset);
                y.CopyTo(luma, i);
            }
            for (; i < to; i++) luma[i] = input[i] * r + input[plane + i] * g + input[2 * plane + i] * b + offset;
        });
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
        stopSource.Cancel();
        wake.Set();
        postThread?.Join(1000);
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
