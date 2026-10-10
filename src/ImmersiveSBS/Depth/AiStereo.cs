using System.Diagnostics;
using System.Drawing;
using ImmersiveSBS.Presentation;
using Vortice.Direct3D11;

namespace ImmersiveSBS.Depth;

/// <summary>What AI 3D converts.</summary>
public enum AiTarget
{
    /// <summary>Off: the virtual monitor is shown flat in both eyes.</summary>
    Off,

    /// <summary>The playing video only, found by <see cref="RegionTracker"/> (the plan's default).</summary>
    Video,

    /// <summary>The whole virtual monitor; text and menus shimmer slightly with depth.</summary>
    WholeScreen,
}

/// <param name="Target">What to convert.</param>
/// <param name="Strength">σ ∈ [0, 1], a fraction of the comfort-limited maximum.</param>
/// <param name="Convergence">c ∈ (0, 1): the nearness at the screen plane.</param>
/// <param name="FieldOfView">The glasses' diagonal field of view, degrees.</param>
/// <param name="Native3D">What to do with video that is already side-by-side or top-bottom.</param>
/// <param name="Stretch">Horizontal stretch of the video around its centre (1 = none), for content
/// in the wrong aspect ratio; works with AI 3D off too.</param>
/// <param name="BufferSeconds">How late the video is shown, in step with the delayed sound; 0 = live.</param>
/// <param name="Tuning">Fine controls; null = the defaults.</param>
/// <param name="Theater">The video fills both eyes, the desktop hidden; needs a found video, AI 3D or not.</param>
/// <param name="FindVideo">Look for a playing video even with nothing else on, to offer the theater button.</param>
public sealed record StereoSettings(AiTarget Target, double Strength, double Convergence, double FieldOfView,
    Native3DMode Native3D = Native3DMode.Auto, double Stretch = 1, double BufferSeconds = 0, StereoTuning? Tuning = null,
    bool Theater = false, bool FindVideo = false)
{
    public static readonly StereoSettings Off = new(AiTarget.Off, 0.6, 0.35, 50);

    public bool Stretches => Math.Abs(Stretch - 1) > 0.001;
}

/// <summary>
/// Where the video is, exactly, as the browser extension reports it (source px), and whether it
/// plays. It stands in for the tracker's box, which shifts when the player's controls show.
/// </summary>
public sealed record VideoHint(Rectangle Region, bool Playing);

/// <summary>
/// Per-session driver of the video features on the render thread: finds the region, stretches
/// it, recognises video that is already 3D, feeds the depth service and builds the eye images —
/// live, or a fixed delay late from <see cref="VideoDelayBuffer"/> while the sound is delayed
/// too (video-3d-plan.md §4.1, §4.4, P5). Eye images are rebuilt only when the frame, the depth
/// or the settings changed. The live picture never waits for depth.
/// </summary>
public sealed class AiStereo : IDisposable
{
    /// <summary>Floating window: crossed disparity is looked for within this many px of an edge (§7.3).</summary>
    private const int EdgeBand = 32;

    /// <summary>Floating window smoothing, seconds (§7.3: τ ≈ 0.3 s).</summary>
    private const double BandTimeConstant = 0.3;

    /// <summary>From choosing a frame to it being on the glasses: about one refresh plus scan-out.</summary>
    private static readonly long PresentLatencyTicks = Stopwatch.Frequency * 20 / 1000;

    /// <summary>
    /// Live path: when the picture has moved on this far past the frame its depth was computed
    /// on (at least <see cref="DepthStaleFrames"/> video frames and this long of the video's own
    /// time), that depth no longer fits and tears the edges; the video is shown flat until depth
    /// is back within <see cref="DepthFreshFrames"/> frames. A busy or memory-starved GPU once
    /// took 825 ms a frame. Counted in video frames, not wall time: after a still moment (a
    /// static shot, a pause) the first new frame is one frame on, not seconds; measuring wall
    /// time flipped the picture flat and back on every such moment.
    /// </summary>
    public const double DepthStaleMs = 250;

    public const int DepthStaleFrames = 6;

    public const int DepthFreshFrames = 3;

    private readonly StereoSynth synth;
    private readonly VideoDelayBuffer delayed;
    private readonly Native3DDetector native = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private RegionTracker? tracker;
    private Size gridSource;
    private Size gridCells;
    private byte[] grid = [];
    private float[] input = [];
    private DepthFrame? depth;
    private DepthFrame? smoothed;
    private bool haveNearness;
    private Rectangle region;
    private long frameId;
    private long regionStartId;
    private double lastGridTime;
    private double gridDt = 1.0 / 30;
    private double lastBandTime;
    private double bandLeft, bandRight, bandLeftTarget, bandRightTarget;
    private long shownDelayedId = -1;
    private Rectangle theaterRegion;
    /// <summary>When each recent frame id was handed to the model, to know how old its depth is.</summary>
    private readonly long[] submitTicks = new long[64];
    private bool depthStale;
    private bool floatingWindow = true;
    private bool gridTracks;
    /// <summary>The model input queued on the GPU this frame, handed to the service after the present.</summary>
    private QueuedInput? queuedInput;

    private readonly record struct QueuedInput(DepthService Service, long FrameId, double Time);
    private StereoOverlay? overlay;
    private object? overlayKey;

    public AiStereo(ID3D11Device device)
    {
        synth = new StereoSynth(device);
        delayed = new VideoDelayBuffer(device);
    }

    /// <summary>One line for the statistics.</summary>
    public string Status { get; private set; } = "AI 3D off";

    /// <summary>
    /// The video region (source px) for which the sound should be delayed, or empty: a buffer is
    /// set, AI 3D follows a video, and it is not native 3D. The controller starts the delayed
    /// sound for the app under it and then passes <c>audioDelayed</c>.
    /// </summary>
    public Rectangle DelayRegion { get; private set; }

    /// <summary>The video found this frame (source px), or empty; for the on-screen theater button.</summary>
    public Rectangle FoundRegion { get; private set; }

    /// <summary>
    /// Runs the video steps for this frame and returns the eye images to draw over the region, or
    /// null to show the frame flat.
    /// </summary>
    /// <param name="newFrame">The capture delivered a new desktop image.</param>
    /// <param name="eyeSize">One eye's size on the glasses, px.</param>
    /// <param name="scale">Eye px per source px (1 when the virtual monitor matches the eye).</param>
    /// <param name="audioDelayed">The app's sound is being played <see cref="StereoSettings.BufferSeconds"/> late.</param>
    /// <param name="hint">The video's exact place from the browser extension, if it reports one.</param>
    public StereoOverlay? Process(ID3D11Texture2D source, bool newFrame, StereoSettings settings, DepthService? service,
        Size eyeSize, double scale, bool audioDelayed, VideoHint? hint = null)
    {
        DelayRegion = Rectangle.Empty;
        FoundRegion = Rectangle.Empty;
        if (settings.Target == AiTarget.Off && !settings.Stretches && !settings.Theater && !settings.FindVideo)
        {
            Forget();
            Status = "AI 3D off";
            return null;
        }

        var d = source.Description;
        var sourceSize = new Size((int)d.Width, (int)d.Height);
        bool track = settings.Target != AiTarget.WholeScreen;
        if (newFrame || grid.Length == 0) QueueGrid(source, sourceSize, track);

        // The extension's box, when the tracker sees the same video (or nothing) and the user has
        // not picked one: exact, and steady while the player's controls come and go.
        Rectangle exact = Rectangle.Empty;
        if (track && hint is { } h && tracker?.Locked != true)
        {
            Rectangle r = Rectangle.Intersect(h.Region, new Rectangle(Point.Empty, sourceSize));
            bool agrees = tracker is not { State: not RegionState.None } || Browser.BrowserMedia.Overlap(r, tracker.Region) >= 0.5;
            if (r.Width >= 16 && r.Height >= 16 && agrees) exact = r;
        }

        // The theater keeps the area it was entered on. Following the tracker there made it jump
        // or drop back to the desktop whenever the player's controls appeared or the video paused.
        if (!settings.Theater) theaterRegion = Rectangle.Empty;
        else if (theaterRegion.IsEmpty && track && !exact.IsEmpty) theaterRegion = exact;
        else if (theaterRegion.IsEmpty && track && tracker is { State: not RegionState.None }) theaterRegion = tracker.Region;
        bool theaterLocked = settings.Theater && !theaterRegion.IsEmpty;

        // The region: the whole frame, the theater's, or the tracked video.
        Rectangle target;
        if (!track)
        {
            target = new Rectangle(Point.Empty, sourceSize);
        }
        else if (theaterLocked)
        {
            target = theaterRegion;
        }
        else if (!exact.IsEmpty)
        {
            target = exact;
        }
        else if (tracker == null || tracker.State == RegionState.None)
        {
            Status = (settings.Target == AiTarget.Off ? "Stretch" : "AI 3D") + ": looking for a playing video";
            SetRegion(Rectangle.Empty, service);
            return null;
        }
        else
        {
            target = tracker.Region;
        }
        SetRegion(target, service);
        FoundRegion = region;
        // In the theater every new frame counts (the tracker may be watching something else); a
        // video the browser says is playing counts too, even in a still shot.
        bool regionMoving = !track || theaterLocked || tracker?.State == RegionState.Tracking || (!exact.IsEmpty && hint!.Playing);
        Rectangle drawn = Stretched(region, settings.Stretch);
        string stretch = (tracker?.Locked == true ? ", locked" : "") + (!exact.IsEmpty && !theaterLocked ? ", exact from the browser" : "")
                         + (settings.Stretches ? $", stretched ×{settings.Stretch:0.##}" : "");

        // Nothing to draw: only finding the video, to offer the theater button.
        if (settings.Target == AiTarget.Off && !settings.Stretches && !settings.Theater)
        {
            Status = $"Video {region.Width}×{region.Height} found{stretch}";
            overlay = null;
            return null;
        }

        // Stretch or theater with AI 3D off: the region flat in both eyes.
        if (settings.Target == AiTarget.Off)
        {
            Status = $"Video {region.Width}×{region.Height}{stretch}";
            return Cached((Native3DLayout.None, region, drawn), newFrame, () => synth.SplitNative(source, region, Native3DLayout.None), drawn);
        }

        // Video that is already 3D goes straight to the eyes (§7.4).
        if (newFrame && regionMoving) native.Update(grid, gridCells.Width, 8, region, gridDt);
        Native3DLayout layout = settings.Native3D switch
        {
            Native3DMode.Off => Native3DLayout.None,
            Native3DMode.ForceSideBySide => Native3DLayout.SideBySide,
            Native3DMode.ForceTopBottom => Native3DLayout.TopBottom,
            _ => native.Layout,
        };
        if (layout != Native3DLayout.None)
        {
            Status = $"AI 3D: {(layout == Native3DLayout.SideBySide ? "side-by-side" : "top-bottom")} 3D video, shown as is{stretch}";
            return Cached((layout, region, drawn), newFrame, () => synth.SplitNative(source, region, layout), drawn);
        }

        // The theater shows its video flat until depth is there (the model loading, the buffer
        // filling), rather than dropping back to the desktop.
        StereoOverlay? FlatInTheater() => settings.Theater
            ? Cached((Native3DLayout.None, region, drawn), newFrame, () => synth.SplitNative(source, region, Native3DLayout.None), drawn)
            : null;

        if (service == null)
        {
            Status = "AI 3D: the depth model is not available";
            return FlatInTheater();
        }
        if (service.State != DepthServiceState.Ready)
        {
            Status = "AI 3D: " + service.Status;
            return FlatInTheater();
        }

        StereoTuning tuning = settings.Tuning ?? StereoTuning.Default;
        synth.DilateRadius = tuning.DilateRadius;
        synth.RangeSigma = (float)tuning.EdgeSoftness;
        synth.EdgeSnap = tuning.EdgeSnap;
        floatingWindow = tuning.FloatingWindow;
        if (!floatingWindow) bandLeftTarget = bandRightTarget = 0;

        double budget = StereoMath.ComfortBudget(settings.FieldOfView, eyeSize.Width, eyeSize.Height);
        // A stretch widens disparity as much as the picture; keep the total within the budget.
        float strength = (float)(StereoMath.Strength(budget, settings.Convergence, settings.Strength) / scale / settings.Stretch);

        if (settings.BufferSeconds > 0 && track) DelayRegion = region;
        if (DelayRegion != Rectangle.Empty && audioDelayed)
            return Delayed(source, newFrame && regionMoving, settings, service, strength, drawn, stretch) ?? FlatInTheater();

        if (delayed.Count > 0)
        {
            delayed.Clear();
            shownDelayedId = -1;
            overlay = null;
        }
        return Live(source, newFrame && regionMoving, regionMoving, settings, service, strength, scale, drawn, stretch) ?? FlatInTheater();
    }

    /// <summary>The live path: the newest frame with the newest depth there is.</summary>
    private StereoOverlay? Live(ID3D11Texture2D source, bool infer, bool regionMoving, StereoSettings settings, DepthService service,
        float strength, double scale, Rectangle drawn, string stretch)
    {
        if (infer) Submit(source, service);

        // Depth computed for an earlier region does not fit this one.
        bool newDepth = service.TryTake(ref depth) && depth!.FrameId > regionStartId;
        if (newDepth)
        {
            synth.UploadNearness(depth!);
            haveNearness = true;
            UpdateBandTargets(depth!.Nearness, depth.Width, depth.Height, strength, settings.Convergence);
        }

        string what = settings.Target == AiTarget.WholeScreen ? "whole screen" : $"video {region.Width}×{region.Height}";
        Status = $"AI 3D: {what}{(regionMoving ? "" : ", paused")}{stretch}; {service.Status}";
        if (!haveNearness) return null;

        // How far the picture has moved on past the depth's frame, in frames and in video time.
        (long frames, double ms) = DepthLag();
        bool stale = depthStale ? frames > DepthFreshFrames : frames >= DepthStaleFrames && ms > DepthStaleMs;
        if (stale)
        {
            if (!depthStale) Log.Write($"AI 3D: depth is {frames} frames ({ms:0} ms) behind the picture; showing the video flat until it catches up.");
            depthStale = true;
            Status = $"AI 3D: {what}: depth too slow ({ms:0} ms behind), shown flat{stretch}; {service.Status}";
            return Cached((Native3DLayout.None, region, drawn), infer, () => synth.SplitNative(source, region, Native3DLayout.None), drawn);
        }
        if (depthStale) Log.Write($"AI 3D: depth caught up ({frames} frames behind).");
        depthStale = false;

        bool bandsMoved = SmoothBands();
        object key = (settings, scale, region);
        if (infer || newDepth || bandsMoved || overlay == null || !key.Equals(overlayKey))
        {
            overlay = new StereoOverlay(
                synth.Synthesize(source, region, strength, (float)settings.Convergence, (float)bandLeft, (float)bandRight), drawn, region);
            overlayKey = key;
        }
        return overlay;
    }

    /// <summary>
    /// The delayed path: frames go into the buffer and to the model as they arrive; the frame from
    /// <see cref="StereoSettings.BufferSeconds"/> ago is shown, with depth averaged over its neighbours.
    /// </summary>
    private StereoOverlay? Delayed(ID3D11Texture2D source, bool infer, StereoSettings settings, DepthService service,
        float strength, Rectangle drawn, string stretch)
    {
        var delay = TimeSpan.FromSeconds(settings.BufferSeconds);
        long now = Stopwatch.GetTimestamp();
        if (infer && delayed.Push(source, region, now, frameId + 1, delay, Stopwatch.Frequency)) Submit(source, service);

        // Depth belongs to the frame it was computed for, whatever the region is now.
        bool newDepth = false;
        while (service.TryTake(ref depth))
        {
            delayed.Attach(depth!);
            newDepth = true;
        }

        Status = $"AI 3D: video {region.Width}×{region.Height}, {settings.BufferSeconds:0.#} s behind ({delayed.Count} frames){stretch}; {service.Status}";
        int index = delayed.Select(now + PresentLatencyTicks - (long)(delay.TotalSeconds * Stopwatch.Frequency));
        if (index < 0) return null;

        // Each frame is shown where it was on the screen when it was taken.
        VideoDelayBuffer.Frame frame = delayed[index];
        var local = new Rectangle(Point.Empty, frame.Region.Size);
        Rectangle frameDrawn = frame.Region == region ? drawn : Stretched(frame.Region, settings.Stretch);
        bool bandsMoved = SmoothBands();
        object key = (frame.Id, settings, frame.Region);
        if (frame.Id == shownDelayedId && !newDepth && !bandsMoved && overlay != null && key.Equals(overlayKey)) return overlay;

        DepthModel model = service.Model;
        if (smoothed == null || smoothed.Width != model.Width || smoothed.Height != model.Height)
            smoothed = new DepthFrame(model.Width, model.Height);

        ID3D11ShaderResourceView eyes;
        if (delayed.SmoothedNearness(index, smoothed.Nearness))
        {
            synth.UploadNearness(smoothed);
            UpdateBandTargets(smoothed.Nearness, smoothed.Width, smoothed.Height, strength, settings.Convergence);
            eyes = synth.Synthesize(frame.TextureOrThrow, local, strength, (float)settings.Convergence, (float)bandLeft, (float)bandRight);
        }
        else
        {
            // No depth near this frame yet: show it flat rather than late or with the wrong depth.
            eyes = synth.SplitNative(frame.TextureOrThrow, local, Native3DLayout.None);
        }

        shownDelayedId = frame.Id;
        overlay = new StereoOverlay(eyes, frameDrawn, frame.Region);
        overlayKey = key;
        return overlay;
    }

    /// <summary>Queues this frame's model input; <see cref="CollectReadbacks"/> hands it to the service.</summary>
    private void Submit(ID3D11Texture2D source, DepthService service)
    {
        DepthModel model = service.Model;
        synth.QueueModelInput(source, region, new Size(model.Width, model.Height));
        queuedInput = new QueuedInput(service, ++frameId, clock.Elapsed.TotalSeconds);
        submitTicks[frameId % submitTicks.Length] = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Render thread, after the present: reads what <see cref="Process"/> queued on the GPU. The
    /// luma grid feeds the tracker (so the region follows one frame late, which nobody sees) and
    /// the model input goes to the depth service. By now the GPU has made the copies, so this
    /// rarely waits, and when it does, it is not in front of a present.
    /// </summary>
    public void CollectReadbacks()
    {
        if (synth.ReadQueuedLumaGrid(grid))
        {
            double now = clock.Elapsed.TotalSeconds;
            gridDt = Math.Clamp(now - lastGridTime, 1.0 / 240, 1.0);
            lastGridTime = now;
            if (gridTracks)
            {
                tracker ??= new RegionTracker(gridCells.Width, gridCells.Height, 8, gridSource);
                tracker.Update(grid, gridDt);
            }
        }

        if (queuedInput is { } q)
        {
            queuedInput = null;
            int length = synth.QueuedModelInputLength;
            if (input.Length != length) input = new float[length];
            if (synth.ReadQueuedModelInput(input)) q.Service.Submit(input, q.FrameId, q.Time);
        }
    }

    /// <summary>
    /// Frames handed to the model since the one behind the current depth, and the time between
    /// the two hand-overs (the video's time, which stands still while the picture does).
    /// </summary>
    private (long Frames, double Ms) DepthLag()
    {
        if (depth == null) return (long.MaxValue, double.PositiveInfinity);
        long frames = frameId - depth.FrameId;
        if (frames >= submitTicks.Length) return (frames, double.PositiveInfinity);
        long ticks = submitTicks[frameId % submitTicks.Length] - submitTicks[depth.FrameId % submitTicks.Length];
        return (frames, ticks * 1000.0 / Stopwatch.Frequency);
    }

    /// <summary>Rebuilds a flat or native overlay only on a new frame or a new key.</summary>
    private StereoOverlay? Cached(object key, bool newFrame, Func<ID3D11ShaderResourceView> build, Rectangle drawn)
    {
        if (newFrame || overlay == null || !key.Equals(overlayKey))
        {
            overlay = new StereoOverlay(build(), drawn, region);
            overlayKey = key;
        }
        return overlay;
    }

    /// <summary>
    /// Releases a picked video, or picks and locks the changing area at <paramref name="point"/>
    /// (source px) when nothing is locked (§5.6).
    /// </summary>
    public void Pick(Point? point)
    {
        if (tracker == null) return;
        if (tracker.Locked)
        {
            tracker.Unlock();
            Log.Write("AI 3D: video lock released.");
        }
        else if (point is Point p)
        {
            bool picked = tracker.Pick(p);
            Log.Write(picked ? $"AI 3D: picked and locked the video at {tracker.Region}." : $"AI 3D: nothing changing at {p} to pick.");
        }
    }

    /// <summary>The region widened by <paramref name="factor"/> around its centre.</summary>
    public static Rectangle Stretched(Rectangle region, double factor)
    {
        if (Math.Abs(factor - 1) < 0.001) return region;
        int width = (int)Math.Round(region.Width * factor);
        return new Rectangle(region.Left + (region.Width - width) / 2, region.Top, width, region.Height);
    }

    /// <summary>Queues the luma grid; <see cref="CollectReadbacks"/> reads it and feeds the tracker when following a video.</summary>
    private void QueueGrid(ID3D11Texture2D source, Size sourceSize, bool track)
    {
        if (gridSource != sourceSize || grid.Length == 0)
        {
            gridSource = sourceSize;
            gridCells = new Size((sourceSize.Width + 7) / 8, (sourceSize.Height + 7) / 8);
            grid = new byte[gridCells.Width * gridCells.Height];
            tracker = null;
            lastGridTime = clock.Elapsed.TotalSeconds;
        }

        synth.QueueLumaGrid(source);
        gridTracks = track;
    }

    /// <summary>
    /// Floating window (§7.3 step 5): the widest crossed disparity S·(n − c) found within
    /// <see cref="EdgeBand"/> px of each side edge of the region becomes that edge's band.
    /// </summary>
    private void UpdateBandTargets(float[] nearness, int width, int height, float strength, double convergence)
    {
        int columns = Math.Clamp((int)Math.Ceiling((double)EdgeBand * width / Math.Max(1, region.Width)), 1, width / 2);
        float left = 0, right = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < columns; x++)
            {
                left = Math.Max(left, nearness[row + x]);
                right = Math.Max(right, nearness[row + width - 1 - x]);
            }
        }
        bandLeftTarget = floatingWindow ? Math.Max(0, strength * (left - convergence)) : 0;
        bandRightTarget = floatingWindow ? Math.Max(0, strength * (right - convergence)) : 0;
    }

    /// <summary>Moves the bands toward their targets; true when either moved by a visible amount.</summary>
    private bool SmoothBands()
    {
        double now = clock.Elapsed.TotalSeconds;
        double a = 1 - Math.Exp(-Math.Clamp(now - lastBandTime, 0, 1) / BandTimeConstant);
        lastBandTime = now;
        double l = bandLeft + (bandLeftTarget - bandLeft) * a;
        double r = bandRight + (bandRightTarget - bandRight) * a;
        bool moved = Math.Abs(l - bandLeft) > 0.1 || Math.Abs(r - bandRight) > 0.1;
        bandLeft = l;
        bandRight = r;
        return moved;
    }

    /// <summary>
    /// A different region invalidates the live depth. Held frames stay: each keeps its own crop
    /// and depth, so the delayed picture runs on through the change.
    /// </summary>
    private void SetRegion(Rectangle target, DepthService? service)
    {
        if (target == region) return;
        region = target;
        regionStartId = frameId;
        haveNearness = false;
        overlay = null;
        bandLeft = bandRight = bandLeftTarget = bandRightTarget = 0;
        native.Reset();
        service?.ResetHistory();
    }

    private void Forget()
    {
        tracker = null;
        grid = [];
        region = Rectangle.Empty;
        haveNearness = false;
        overlay = null;
        native.Reset();
        delayed.Clear();
        shownDelayedId = -1;
    }

    public void Dispose()
    {
        delayed.Dispose();
        synth.Dispose();
    }
}
