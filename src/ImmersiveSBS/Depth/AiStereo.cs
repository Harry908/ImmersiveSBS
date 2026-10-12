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

    /// <summary>The video the browser extension reports, only (the plan's default); nothing without it.</summary>
    Video,

    /// <summary>The whole virtual monitor; text and menus shimmer slightly with depth.</summary>
    WholeScreen,
}

/// <summary>When the video is shown relative to the depth computed for it (improvement-plan.md §6).</summary>
public enum VideoTiming
{
    /// <summary>Matched while depth runs more than 60 ms behind the picture (large models, Quality sizes), else live.</summary>
    Auto,

    /// <summary>The newest picture with the newest depth: no delay, but moving edges tear with a slow model.</summary>
    Live,

    /// <summary>
    /// The picture delayed just enough that every frame has its own depth; beyond 90 ms only with
    /// the browser extension delaying the sound, else 90 ms with the sound live.
    /// </summary>
    Matched,
}

/// <param name="Target">What to convert.</param>
/// <param name="Strength">σ ∈ [0, 1], a fraction of the comfort-limited maximum.</param>
/// <param name="Convergence">c ∈ (0, 1): the nearness at the screen plane.</param>
/// <param name="FieldOfView">The glasses' diagonal field of view, degrees.</param>
/// <param name="Native3D">What to do with video that is already side-by-side or top-bottom.</param>
/// <param name="Stretch">Horizontal stretch of the video around its centre (1 = none), for content
/// in the wrong aspect ratio; works with AI 3D off too.</param>
/// <param name="BufferSeconds">How late the video is shown, in step with the sound the page delays; 0 = no buffer.
/// Only with the browser extension; without it, Auto timing.</param>
/// <param name="Tuning">Fine controls; null = the defaults.</param>
/// <param name="Theater">The video fills both eyes, the desktop hidden; needs the extension's video, AI 3D or not.</param>
/// <param name="OfferTheater">Offer the "3D theater" button over the extension's videos.</param>
/// <param name="Timing">Live or matched while there is no buffer.</param>
public sealed record StereoSettings(AiTarget Target, double Strength, double Convergence, double FieldOfView,
    Native3DMode Native3D = Native3DMode.Auto, double Stretch = 1, double BufferSeconds = 0, StereoTuning? Tuning = null,
    bool Theater = false, bool OfferTheater = false, VideoTiming Timing = VideoTiming.Auto)
{
    public static readonly StereoSettings Off = new(AiTarget.Off, 0.6, 0.35, 50);

    public bool Stretches => Math.Abs(Stretch - 1) > 0.001;
}

/// <summary>
/// Where the video is, exactly, as the browser extension reports it (source px), and whether it
/// plays: the only way a video is found. Guessing it from the picture (a tracker of changing
/// areas) took scrolling pages, animations and the desktop for videos, and its box shifted as
/// the player's controls came and went; it was removed on 2026-10-12.
/// </summary>
/// <param name="Overlays">Boxes the page draws over the picture (player controls, titles, logos), source px.</param>
/// <param name="Captions">The page's caption boxes, source px; null when it reports none (subtitles may be burned in).</param>
/// <param name="Page">Frame events and playback state as they arrive (protocol 2); null with protocol 1.</param>
public sealed record VideoHint(Rectangle Region, bool Playing)
{
    public IReadOnlyList<Rectangle> Overlays { get; init; } = [];

    public IReadOnlyList<Rectangle>? Captions { get; init; }

    public PageVideo? Page { get; init; }

    /// <summary>How many times the video has seeked (protocol 1 too); −1 unknown.</summary>
    public int Seeks { get; init; } = -1;
}

/// <summary>
/// Per-session driver of the video features on the render thread: finds the region, stretches
/// it, recognises video that is already 3D, feeds the depth service and builds the eye images:
/// live, matched (delayed just enough for every frame's own depth, improvement-plan.md §6), or a
/// fixed delay late from <see cref="VideoDelayBuffer"/> while the sound is delayed too
/// (video-3d-plan.md §4.1, §4.4, P5). Only frames whose picture changed in the region are handed
/// to the model (improvement-plan.md §9 step 0): a 24–30 fps video changes every second or third
/// loop, and the rest were repeats. Eye images are rebuilt only when the frame, the depth or the
/// settings changed. The live picture never waits for depth.
/// </summary>
public sealed class AiStereo : IDisposable
{
    /// <summary>Floating window: crossed disparity is looked for within this many px of an edge (§7.3).</summary>
    private const int EdgeBand = 32;

    /// <summary>Floating window smoothing, seconds (§7.3: τ ≈ 0.3 s).</summary>
    private const double BandTimeConstant = 0.3;

    /// <summary>From choosing a frame to it being on the glasses: about one refresh plus scan-out.</summary>
    private const double PresentLatencyMs = 20;

    private static readonly long PresentLatencyTicks = (long)(Stopwatch.Frequency * PresentLatencyMs / 1000);

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

    /// <summary>
    /// ...or within this much of the video's time. Coming back on frames alone never happened for
    /// a large model: at ~50 ms a frame its depth is always 100–170 ms (6–10 desktop frames in the
    /// theater) behind, so it stayed flat until the video was paused.
    /// </summary>
    public const double DepthFreshMs = 200;

    /// <summary>Matched timing plays the sound live up to this picture delay (ITU-R BT.1359: acceptable to +90 ms).</summary>
    public const double LiveSoundMaxSeconds = 0.090;

    /// <summary>A page that says paused while the picture keeps changing this long is not believed.</summary>
    private const double PausedButChangingSeconds = 0.5;

    /// <summary>The dirty rectangles missed a change this many times within <see cref="MissWindowSeconds"/>: stop trusting them.</summary>
    private const int MaxMissedChanges = 3;

    private const double MissWindowSeconds = 10;

    private readonly StereoSynth synth;
    private readonly VideoDelayBuffer delayed;
    private readonly Native3DDetector native = new();
    private readonly MatchedDelay matched = new(PresentLatencyMs);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private Size gridSource;
    private Size gridCells;
    private byte[] grid = [];
    private byte[] previousGrid = [];
    private bool previousGridValid;
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
    /// <summary>The model input queued on the GPU this frame, handed to the service after the present.</summary>
    private QueuedInput? queuedInput;

    /// <param name="Forced">Handed over whatever the luma grid says (a new region, model or settings).</param>
    private readonly record struct QueuedInput(DepthService Service, long FrameId, double Time, bool Forced);
    private StereoOverlay? overlay;
    private object? overlayKey;

    // Handing over only changed frames.
    private bool forceHandOver = true;
    private DepthService? lastService;
    private StereoSettings? lastSettings;
    private bool dirtyRectsTrusted = true;
    private int missedChanges;
    private double missWindowStart;
    /// <summary>The region of the frame whose luma grid is queued, and whether that frame was skipped as unchanged.</summary>
    private Rectangle checkRegion;
    private bool checkSkipped;
    private long lastDepthFrameId = -1;
    private int segment;
    private int seenSeeks = -1;
    private double wantedDelaySeconds;

    // Measures (improvement-plan.md §0), per statistics window.
    private readonly List<float> ageSamples = [];
    private float[] ageScratch = [];
    private int handOvers, repeatsSkipped, depthFramesTaken;
    private string changeSource = "-";
    private double holeFraction = double.NaN;
    private double lastHoleQueue;
    private string timingShown = "live";
    private VideoHint? boxesHint;
    private Rectangle boxesRegion;
    private double pausedChangingSince = double.NaN;
    /// <summary>A frame was handed over by <see cref="HandOverEarly"/> since the last <see cref="Process"/>: its picture changed.</summary>
    private bool earlyChanged;
    private bool liveLast;
    private bool pagePausedLast;

    public AiStereo(ID3D11Device device)
    {
        synth = new StereoSynth(device);
        delayed = new VideoDelayBuffer(device);
    }

    /// <summary>One line for the statistics.</summary>
    public string Status { get; private set; } = "AI 3D off";

    /// <summary>Times the GPU passes when set (improvement-plan.md M5).</summary>
    public GpuTimer? Timer
    {
        get => synth.Timer;
        set => synth.Timer = value;
    }

    /// <summary>
    /// The video region (source px) whose sound should be delayed by <see cref="DelaySeconds"/>,
    /// or empty: a buffer or matched timing is on, AI 3D follows a video, it is not native 3D,
    /// and the page can delay its sound. The controller has the page delay it and then reports
    /// how late the sound is through <c>soundDelay</c>.
    /// </summary>
    public Rectangle DelayRegion { get; private set; }

    /// <summary>
    /// How late the sound under <see cref="DelayRegion"/> should be, seconds; kept while the
    /// region is lost for a moment, 0 when no delay is wanted.
    /// </summary>
    public double DelaySeconds { get; private set; }

    /// <summary>The video this frame (source px), or empty; for the on-screen layer.</summary>
    public Rectangle FoundRegion { get; private set; }

    /// <summary>
    /// Runs the video steps for this frame and returns the eye images to draw over the region, or
    /// null to show the frame flat.
    /// </summary>
    /// <param name="newFrame">The capture delivered a new desktop image.</param>
    /// <param name="eyeSize">One eye's size on the glasses, px.</param>
    /// <param name="scale">Eye px per source px (1 when the virtual monitor matches the eye).</param>
    /// <param name="soundDelay">How late the page plays the sound under <see cref="DelayRegion"/> now, seconds; 0 = live.</param>
    /// <param name="pageCanDelay">The browser extension follows this video and can delay its sound in the page.</param>
    /// <param name="hint">The video's exact place from the browser extension, if it reports one.</param>
    /// <param name="changed">Whether an area of the frame changed since the last call (false only when the capture knows it did not; null when it cannot tell).</param>
    /// <param name="captureTicks">When the newest desktop image was made, Stopwatch ticks; 0 = now.</param>
    public StereoOverlay? Process(ID3D11Texture2D source, bool newFrame, StereoSettings settings, DepthService? service,
        Size eyeSize, double scale, double soundDelay, bool pageCanDelay, VideoHint? hint = null, Func<Rectangle, bool?>? changed = null,
        long captureTicks = 0)
    {
        DelayRegion = Rectangle.Empty;
        FoundRegion = Rectangle.Empty;
        liveLast = false;
        if (settings.Target == AiTarget.Off && !settings.Stretches && !settings.Theater)
        {
            Forget();
            DelaySeconds = wantedDelaySeconds = 0;
            Status = "AI 3D off";
            return null;
        }
        // While the video is looked for, a delay already running is kept (the controller allows a grace time).
        DelaySeconds = wantedDelaySeconds;

        var d = source.Description;
        var sourceSize = new Size((int)d.Width, (int)d.Height);
        bool track = settings.Target != AiTarget.WholeScreen;
        if (newFrame || grid.Length == 0) QueueGrid(source, sourceSize);
        checkRegion = Rectangle.Empty;
        checkSkipped = false;

        // The video: only where the browser extension says it is. The theater keeps its last box
        // while the page's is missed for a moment (the controller's grace keeps it otherwise).
        Rectangle exact = Rectangle.Empty;
        double seconds = clock.Elapsed.TotalSeconds;
        if (track && hint is { } h)
        {
            Rectangle r = Rectangle.Intersect(h.Region, new Rectangle(Point.Empty, sourceSize));
            if (r.Width >= 16 && r.Height >= 16) exact = r;
        }
        if (!settings.Theater) theaterRegion = Rectangle.Empty;
        else if (!exact.IsEmpty) theaterRegion = exact;

        // The region: the whole frame, or the extension's video.
        Rectangle target;
        if (!track)
        {
            target = new Rectangle(Point.Empty, sourceSize);
        }
        else if (!exact.IsEmpty || !theaterRegion.IsEmpty)
        {
            target = exact.IsEmpty ? theaterRegion : exact;
        }
        else
        {
            Status = (settings.Target == AiTarget.Off ? "Stretch" : "AI 3D") + ": no video from the browser extension (it finds the video; without it, only Whole screen)";
            SetRegion(Rectangle.Empty, service);
            return null;
        }
        SetRegion(target, service);
        FoundRegion = region;
        // In the theater every new frame counts; a video the page says is playing counts too,
        // even in a still shot.
        bool regionMoving = !track || settings.Theater || hint is { Playing: true };
        Rectangle drawn = Stretched(region, settings.Stretch);
        string stretch = (track ? ", from the browser" : "") + (settings.Stretches ? $", stretched ×{settings.Stretch:0.##}" : "");

        // Stretch or theater with AI 3D off: the region flat in both eyes.
        if (settings.Target == AiTarget.Off)
        {
            DelaySeconds = wantedDelaySeconds = 0;
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
            // No delay region: the page's delayed sound runs on through the controller's grace
            // time rather than restart (silent for its length) if this was a passing false find.
            // The 2026-10-12 run flipped to "shown as is" every few seconds.
            if (settings.Native3D != Native3DMode.Auto) DelaySeconds = wantedDelaySeconds = 0;
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

        // Another model, or other settings (the depth thread applies them to the next frame it
        // gets): hand over the next frame even if the picture stands still.
        if (!ReferenceEquals(service, lastService))
        {
            if (lastService != null) matched.Reset();
            lastService = service;
            forceHandOver = true;
        }
        if (!ReferenceEquals(settings, lastSettings))
        {
            lastSettings = settings;
            forceHandOver = true;
        }

        // A jump in the video (the page's seek): nothing is blended or smoothed across it.
        PageVideo? page = hint?.Page;
        long now = Stopwatch.GetTimestamp();
        bool pageFresh = page?.Fresh(now) == true;
        int seeks = pageFresh ? page!.Seeks : !exact.IsEmpty ? hint!.Seeks : -1;
        if (seeks >= 0 && seeks != seenSeeks)
        {
            if (seenSeeks >= 0)
            {
                segment++;
                service.ResetHistory();
                forceHandOver = true;
            }
            seenSeeks = seeks;
        }

        // What the page draws over the picture: its controls held flat, its captions instead of
        // the subtitle detection (improvement-plan.md §7.4); only for the video it reports.
        if (!ReferenceEquals(hint, boxesHint) || region != boxesRegion)
        {
            boxesHint = hint;
            boxesRegion = region;
            service.PageBoxes = !exact.IsEmpty && pageFresh && hint is { } hb
                ? PageBoxes.ToModel(hb.Overlays, hb.Captions ?? [], region, new Size(service.Model.Width, service.Model.Height))
                : null;
        }

        // Which frames go to the model: those whose picture changed in the region. The capture's
        // dirty rectangles say so at once; otherwise the luma grid decides after the present.
        // While the page says the video is paused, none (its controls fading in and out are
        // redrawn, not handed over).
        bool? known = forceHandOver ? true : dirtyRectsTrusted ? changed?.Invoke(region) : null;
        // A frame handed over early changed the picture too; what changed since is the rest.
        bool pictureChanged = newFrame && (known ?? regionMoving) || earlyChanged;
        bool pagePaused = pageFresh && page!.Paused && !page.Seeking;
        // ...unless the picture keeps changing anyway (another element plays, or the page is
        // wrong): then the capture decides (§3.5 rule 3).
        if (!pagePaused || (newFrame && !pictureChanged)) pausedChangingSince = double.NaN;
        else if (pictureChanged && double.IsNaN(pausedChangingSince)) pausedChangingSince = seconds;
        if (pagePaused && seconds - pausedChangingSince >= PausedButChangingSeconds) pagePaused = false;
        bool handOver = pictureChanged && (forceHandOver || !pagePaused);
        changeSource = forceHandOver ? changeSource : known.HasValue ? "dirty rectangles" : dirtyRectsTrusted ? "luma grid" : "luma grid (dirty rectangles missed changes)";
        if (newFrame)
        {
            checkRegion = region;
            checkSkipped = known == false && !earlyChanged;
            if (regionMoving && !handOver && !earlyChanged) repeatsSkipped++;
        }
        earlyChanged = false;
        pagePausedLast = pagePaused;
        double mediaTime = double.NaN;
        if (pageFresh && page!.TryFrameAt(captureTicks > 0 ? captureTicks : now, Stopwatch.Frequency / 120, out PageVideo.FrameEvent fe))
            mediaTime = fe.MediaTime;

        StereoTuning tuning = settings.Tuning ?? StereoTuning.Default;
        synth.DilateRadius = tuning.DilateRadius;
        synth.EdgeSnap = tuning.EdgeSnap;
        synth.Lookup = tuning.Warp == WarpMode.Lookup;
        floatingWindow = tuning.FloatingWindow;
        if (!floatingWindow) bandLeftTarget = bandRightTarget = 0;

        double budget = StereoMath.ComfortBudget(settings.FieldOfView, eyeSize.Width, eyeSize.Height);
        // A stretch widens disparity as much as the picture; keep the total within the budget.
        float strength = (float)(StereoMath.Strength(budget, settings.Convergence, settings.Strength) / scale / settings.Stretch);

        // Soft edges (improvement-plan.md §7.1): enough passes for a typical step to become a ramp
        // the warp can stretch over; the guided upsample's colour term softens with them, or it
        // would sharpen the ramp again where it crosses a colour edge (§7.3).
        int passes = tuning.SoftEdges >= 0 ? Math.Min(tuning.SoftEdges, 4) : SoftEdges.AutoPasses(strength, region.Width, service.Model.Width);
        service.SoftEdgePasses = passes;
        synth.RangeSigma = (float)(0.1 + 0.05 * passes);

        // Timing: the buffer, matched, or live. Sound is only ever delayed by the page (the browser
        // extension): without it there is no buffer, and matched timing stays within the 90 ms
        // lip sync allows with the sound live.
        matched.FrameRate = pageFresh ? page!.FrameRate : 0;
        matched.Update(clock.Elapsed.TotalSeconds);
        bool bufferWanted = settings.BufferSeconds > 0 && track;
        bool buffer = bufferWanted && pageCanDelay;
        string note = bufferWanted && !pageCanDelay ? ", buffer needs the browser extension" : "";
        // A buffer that cannot run behaves as Auto.
        VideoTiming timing = bufferWanted ? VideoTiming.Auto : settings.Timing;
        bool wantMatched = !buffer && track && timing switch
        {
            VideoTiming.Matched => true,
            VideoTiming.Auto => matched.AutoMatched,
            _ => false,
        };
        if (buffer)
        {
            DelayRegion = region;
            DelaySeconds = wantedDelaySeconds = settings.BufferSeconds;
            timingShown = "buffer";
            // Until the page has delayed the sound, as late as lip sync allows with it live.
            double towards = soundDelay > 0 ? soundDelay : LiveSoundMaxSeconds;
            return Delayed(source, pictureChanged, handOver, known.HasValue, settings, service, strength, drawn, stretch,
                       mediaTime, ShownDelay(towards)) ?? FlatInTheater();
        }
        if (wantMatched && !double.IsNaN(matched.DelayMs))
        {
            double delay = matched.DelayMs / 1000;
            if (!pageCanDelay && delay > LiveSoundMaxSeconds)
            {
                note += $", matched {delay * 1000:0} ms needs the browser extension: {LiveSoundMaxSeconds * 1000:0} ms with the sound live";
                delay = LiveSoundMaxSeconds;
            }
            DelayRegion = pageCanDelay ? region : Rectangle.Empty;
            DelaySeconds = wantedDelaySeconds = pageCanDelay ? delay : 0;
            // The picture runs as late as the page plays the sound; until the page has delayed it
            // (or without the page) as late as lip sync allows with the sound live.
            double towards = soundDelay > 0 ? soundDelay : Math.Min(delay, LiveSoundMaxSeconds);
            service.LookAheadFrames = 0;
            timingShown = "matched";
            return Matched(source, pictureChanged, handOver, known.HasValue, settings, service, strength, drawn, stretch + note,
                       mediaTime, ShownDelay(towards), soundDelay > 0) ?? FlatInTheater();
        }

        DelaySeconds = wantedDelaySeconds = 0;
        if (delayed.Count > 0)
        {
            delayed.Clear();
            shownDelayedId = -1;
            overlay = null;
        }
        ShownDelay(0);
        service.LookAheadFrames = 0;
        timingShown = "live";
        liveLast = true;
        return Live(source, pictureChanged, handOver, known.HasValue, regionMoving, settings, service, strength, scale, drawn, stretch + note)
               ?? FlatInTheater();
    }

    /// <summary>
    /// How late the delayed picture is shown, following <paramref name="target"/> (the sound's
    /// delay): a longer delay is reached by holding the picture (time passes, the frame shown does
    /// not change), never by jumping back, because the page's sound reaches it the same way: a new
    /// delay line is silent for its delay, a short change glides. A shorter one is taken at once.
    /// Before, the buffer emptied itself whenever the two differed, and froze for its whole
    /// length each time (the 2026-10-11 run).
    /// </summary>
    private double ShownDelay(double target)
    {
        double now = clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - shownDelayAt, 0, 0.25);
        shownDelayAt = now;
        shownDelay = target > shownDelay ? Math.Min(target, shownDelay + dt) : Math.Max(target, 0);
        return shownDelay;
    }

    private double shownDelay, shownDelayAt;

    /// <summary>
    /// Render thread, as soon as a new desktop image arrives, before waiting for the swap chain:
    /// in live timing, queues the video's model input from it when the capture says its picture
    /// changed, so the depth model gets it up to a refresh sooner (improvement-plan.md §9 step 2);
    /// <see cref="SubmitReadyInput"/> hands it over once the GPU has copied it. Uses the region,
    /// model and settings of the last <see cref="Process"/>, which then knows this frame is done.
    /// </summary>
    /// <param name="resetChanges">Starts the capture's change record afresh, so the next call sees only what changed after this frame.</param>
    public void HandOverEarly(ID3D11Texture2D source, DepthService? service, Func<Rectangle, bool?> changed, Action resetChanges)
    {
        if (!liveLast || forceHandOver || region.IsEmpty || service == null || !ReferenceEquals(service, lastService)
            || service.State != DepthServiceState.Ready || pagePausedLast || !dirtyRectsTrusted || changed(region) != true)
            return;
        Submit(source, service, decided: true);
        synth.Flush();
        resetChanges();
        earlyChanged = true;
    }

    /// <summary>Hands a queued model input to the service if the GPU has already copied it; never waits.</summary>
    public void SubmitReadyInput(DepthService? current)
    {
        if (queuedInput is not { Forced: true } q || !synth.QueuedModelInputReady) return;
        queuedInput = null;
        int length = synth.QueuedModelInputLength;
        if (input.Length != length) input = new float[length];
        if (synth.ReadQueuedModelInput(input) && ReferenceEquals(q.Service, current))
        {
            q.Service.Submit(input, q.FrameId, q.Time);
            handOvers++;
        }
    }

    /// <summary>The live path: the newest frame with the newest depth there is.</summary>
    private StereoOverlay? Live(ID3D11Texture2D source, bool pictureChanged, bool handOver, bool decided, bool regionMoving,
        StereoSettings settings, DepthService service, float strength, double scale, Rectangle drawn, string stretch)
    {
        if (handOver) Submit(source, service, decided);

        // Depth computed for an earlier region does not fit this one.
        bool newDepth = service.TryTake(ref depth) && depth!.FrameId > regionStartId;
        if (newDepth)
        {
            OnDepth(depth!);
            synth.UploadNearness(depth!);
            haveNearness = true;
            UpdateBandTargets(depth!.Nearness, depth.Width, depth.Height, strength, settings.Convergence);
        }

        string what = settings.Target == AiTarget.WholeScreen ? "whole screen" : $"video {region.Width}×{region.Height}";
        Status = $"AI 3D: {what}{(regionMoving ? "" : ", paused")}{stretch}; {service.Status}";
        if (!haveNearness) return null;

        // How far the picture has moved on past the depth's frame, in frames and in video time.
        (long frames, double ms) = DepthLag();
        bool stale = depthStale ? frames > DepthFreshFrames && ms > DepthFreshMs : frames >= DepthStaleFrames && ms > DepthStaleMs;
        if (stale)
        {
            if (!depthStale) Log.Write($"AI 3D: depth is {frames} frames ({ms:0} ms) behind the picture; showing the video flat until it catches up.");
            depthStale = true;
            Status = $"AI 3D: {what}: depth too slow ({ms:0} ms behind), shown flat{stretch}; {service.Status}";
            return Cached((Native3DLayout.None, region, drawn), pictureChanged, () => synth.SplitNative(source, region, Native3DLayout.None), drawn);
        }
        if (depthStale) Log.Write($"AI 3D: depth caught up ({frames} frames behind).");
        depthStale = false;
        if (double.IsFinite(ms)) ageSamples.Add((float)ms);

        bool bandsMoved = SmoothBands();
        object key = (settings, scale, region);
        if (pictureChanged || newDepth || bandsMoved || overlay == null || !key.Equals(overlayKey))
        {
            overlay = new StereoOverlay(
                synth.Synthesize(source, region, strength, (float)settings.Convergence, (float)bandLeft, (float)bandRight), drawn, region);
            overlayKey = key;
            QueueHoleCountEverySecond();
        }
        return overlay;
    }

    /// <summary>
    /// The buffer: frames go into it and to the model as they arrive; the frame from
    /// <paramref name="shownSeconds"/> ago (reaching <see cref="StereoSettings.BufferSeconds"/>
    /// once the page plays the sound that late) is shown, with depth averaged over its neighbours.
    /// </summary>
    private StereoOverlay? Delayed(ID3D11Texture2D source, bool pictureChanged, bool handOver, bool decided, StereoSettings settings,
        DepthService service, float strength, Rectangle drawn, string stretch, double mediaTime, double shownSeconds)
    {
        double bufferSeconds = settings.BufferSeconds;
        // The buffer shows a frame long after its depth is ready: the depth thread looks as many
        // frames ahead as fit in that time, less a margin (improvement-plan.md §10).
        double period = matched.PeriodMs;
        service.LookAheadFrames = double.IsFinite(period) && period > 0 ? (int)Math.Floor((shownSeconds - 0.3) * 1000 / period) : 0;
        Push(source, pictureChanged, handOver, decided, service, bufferSeconds, mediaTime);
        bool newDepth = TakeDepthIntoBuffer(service);

        string waiting = shownSeconds < bufferSeconds - 0.002 ? $", reaching {bufferSeconds:0.#} s with the page's sound" : "";
        Status = $"AI 3D: video {region.Width}×{region.Height}, {shownSeconds:0.##} s behind ({delayed.Count} frames){waiting}{stretch}; {service.Status}";
        int index = SelectDelayed(shownSeconds);
        if (index < 0) return null;

        // Each frame is shown where it was on the screen when it was taken.
        VideoDelayBuffer.Frame frame = delayed[index];
        bool bandsMoved = SmoothBands();
        object key = (frame.Id, settings, frame.Region, "buffer");
        if (frame.Id == shownDelayedId && !newDepth && !bandsMoved && overlay != null && key.Equals(overlayKey)) return overlay;

        EnsureSmoothed(service.Model);
        bool have = delayed.SmoothedNearness(index, smoothed!.Nearness, out long nearestTicks);
        if (have) ageSamples.Add((float)(nearestTicks * 1000.0 / Stopwatch.Frequency));
        else
        {
            // While the delay is still short, the neighbours may have no depth yet: the nearest frame's.
            VideoDelayBuffer.DepthMatch match = delayed.InterpolatedNearness(index, smoothed.Nearness, out long ageTicks);
            have = match != VideoDelayBuffer.DepthMatch.None;
            if (have) ageSamples.Add((float)(ageTicks * 1000.0 / Stopwatch.Frequency));
        }
        return ShowDelayed(frame, have, key, settings, strength, drawn);
    }

    /// <summary>
    /// Matched timing (improvement-plan.md §6): the picture <paramref name="delaySeconds"/> late,
    /// each frame with its own depth or depth interpolated between the frames on either side.
    /// </summary>
    /// <param name="soundDelayed">The page plays the sound late too; otherwise it is live.</param>
    private StereoOverlay? Matched(ID3D11Texture2D source, bool pictureChanged, bool handOver, bool decided, StereoSettings settings,
        DepthService service, float strength, Rectangle drawn, string stretch, double mediaTime, double delaySeconds, bool soundDelayed)
    {
        // Frames for the delay being reached as well as the one shown now.
        Push(source, pictureChanged, handOver, decided, service, Math.Max(delaySeconds, wantedDelaySeconds), mediaTime);
        bool newDepth = TakeDepthIntoBuffer(service);

        string sound = soundDelayed ? ", sound delayed in the page" : ", sound live";
        Status = $"AI 3D: video {region.Width}×{region.Height}, matched {delaySeconds * 1000:0} ms (depth needs {matched.NeedMs:0} ms){sound}{stretch}; {service.Status}";
        int index = SelectDelayed(delaySeconds);
        if (index < 0) return null;

        VideoDelayBuffer.Frame frame = delayed[index];
        bool bandsMoved = SmoothBands();
        object key = (frame.Id, settings, frame.Region, "matched");
        // New depth can turn a held frame into an interpolated one: rebuild then too.
        if (frame.Id == shownDelayedId && !newDepth && !bandsMoved && overlay != null && key.Equals(overlayKey)) return overlay;

        EnsureSmoothed(service.Model);
        VideoDelayBuffer.DepthMatch match = delayed.InterpolatedNearness(index, smoothed!.Nearness, out long ageTicks);
        if (match != VideoDelayBuffer.DepthMatch.None) ageSamples.Add((float)(ageTicks * 1000.0 / Stopwatch.Frequency));
        return ShowDelayed(frame, match != VideoDelayBuffer.DepthMatch.None, key, settings, strength, drawn);
    }

    /// <summary>
    /// Copies a changed frame into the buffer, and hands it to the model when it should be. The
    /// first frame of a region goes in (and to the model) even when the picture stands still: a
    /// paused video would otherwise have nothing to show.
    /// </summary>
    private void Push(ID3D11Texture2D source, bool pictureChanged, bool handOver, bool decided, DepthService service, double delaySeconds,
        double mediaTime)
    {
        bool first = delayed.Count == 0 || delayed[delayed.Count - 1].Region != region;
        if (!pictureChanged && !first) return;
        if (delayed.Push(source, region, Stopwatch.GetTimestamp(), frameId + 1, TimeSpan.FromSeconds(delaySeconds), Stopwatch.Frequency,
                mediaTime, segment))
        {
            Timer?.Mark("buffer");
            if (handOver || first) Submit(source, service, decided || first);
            else frameId++;   // its own id, without depth: shown with its neighbours'
        }
    }

    /// <summary>Files every depth frame that has arrived with its held frame; true when any did.</summary>
    private bool TakeDepthIntoBuffer(DepthService service)
    {
        bool any = false;
        while (service.TryTake(ref depth))
        {
            OnDepth(depth!);
            delayed.Attach(depth!);
            any = true;
        }
        return any;
    }

    /// <summary>The held frame to show now, chosen <see cref="PresentLatencyTicks"/> ahead of being seen.</summary>
    private int SelectDelayed(double delaySeconds) =>
        delayed.Select(Stopwatch.GetTimestamp() + PresentLatencyTicks - (long)(delaySeconds * Stopwatch.Frequency));

    private void EnsureSmoothed(DepthModel model)
    {
        if (smoothed == null || smoothed.Width != model.Width || smoothed.Height != model.Height)
            smoothed = new DepthFrame(model.Width, model.Height);
    }

    /// <summary>Builds the eyes of a held frame from the nearness in <see cref="smoothed"/>, or shows it flat without.</summary>
    private StereoOverlay ShowDelayed(VideoDelayBuffer.Frame frame, bool haveDepth, object key, StereoSettings settings, float strength,
        Rectangle drawn)
    {
        var local = new Rectangle(Point.Empty, frame.Region.Size);
        Rectangle frameDrawn = frame.Region == region ? drawn : Stretched(frame.Region, settings.Stretch);
        ID3D11ShaderResourceView eyes;
        if (haveDepth)
        {
            synth.UploadNearness(smoothed!);
            UpdateBandTargets(smoothed!.Nearness, smoothed.Width, smoothed.Height, strength, settings.Convergence);
            eyes = synth.Synthesize(frame.TextureOrThrow, local, strength, (float)settings.Convergence, (float)bandLeft, (float)bandRight);
            QueueHoleCountEverySecond();
        }
        else
        {
            // No depth near this frame yet: show it flat rather than late or with the wrong depth.
            eyes = synth.SplitNative(frame.TextureOrThrow, local, Native3DLayout.None);
        }

        shownDelayedId = frame.Id;
        var shown = new StereoOverlay(eyes, frameDrawn, frame.Region);
        overlay = shown;
        overlayKey = key;
        return shown;
    }

    /// <summary>Queues this frame's model input; <see cref="CollectReadbacks"/> hands it to the service.</summary>
    private void Submit(ID3D11Texture2D source, DepthService service, bool decided)
    {
        DepthModel model = service.Model;
        synth.QueueModelInput(source, region, new Size(model.Width, model.Height));
        queuedInput = new QueuedInput(service, ++frameId, clock.Elapsed.TotalSeconds, forceHandOver || decided);
        submitTicks[frameId % submitTicks.Length] = Stopwatch.GetTimestamp();
        forceHandOver = false;
    }

    /// <summary>A depth frame reached the render thread: how old it is and how far after the previous one, for matched timing.</summary>
    private void OnDepth(DepthFrame frame)
    {
        depthFramesTaken++;
        long now = Stopwatch.GetTimestamp();
        if (frameId - frame.FrameId >= submitTicks.Length) return;
        double ageMs = (now - submitTicks[frame.FrameId % submitTicks.Length]) * 1000.0 / Stopwatch.Frequency;
        double periodMs = lastDepthFrameId >= 0 && frame.FrameId > lastDepthFrameId && frameId - lastDepthFrameId < submitTicks.Length
            ? (submitTicks[frame.FrameId % submitTicks.Length] - submitTicks[lastDepthFrameId % submitTicks.Length]) * 1000.0 / Stopwatch.Frequency
            : 0;
        lastDepthFrameId = frame.FrameId;
        matched.Add(ageMs, periodMs, clock.Elapsed.TotalSeconds);
    }

    private void QueueHoleCountEverySecond()
    {
        double now = clock.Elapsed.TotalSeconds;
        if (now - lastHoleQueue < 1) return;
        lastHoleQueue = now;
        synth.QueueHoleCount();
    }

    /// <summary>
    /// Render thread, after the present: reads what <see cref="Process"/> queued on the GPU. The
    /// luma grid says whether the frame changed in the region (and feeds the 3D video detector); the model input goes to the depth service
    /// unless it repeats the previous frame. By now the GPU has made the copies, so this rarely
    /// waits, and when it does, it is not in front of a present.
    /// </summary>
    /// <param name="current">The session's depth service now: input queued for one that was
    /// swapped out meanwhile (another model or size) is dropped; it has been disposed.</param>
    public void CollectReadbacks(DepthService? current)
    {
        bool gridRead = synth.ReadQueuedLumaGrid(grid);
        bool regionChanged = true;
        if (gridRead)
        {
            double now = clock.Elapsed.TotalSeconds;
            gridDt = Math.Clamp(now - lastGridTime, 1.0 / 240, 1.0);
            lastGridTime = now;
            if (!checkRegion.IsEmpty && previousGridValid) regionChanged = GridChanged(checkRegion);
            if (checkSkipped && regionChanged) OnMissedChange(now);
            if (previousGrid.Length != grid.Length) previousGrid = new byte[grid.Length];
            grid.CopyTo(previousGrid, 0);
            previousGridValid = true;
        }

        if (queuedInput is { } q)
        {
            queuedInput = null;
            if (!q.Forced && gridRead && !regionChanged)
            {
                // A repeat of the frame before: the model's answer would be the same.
                synth.DiscardQueuedModelInput();
                repeatsSkipped++;
            }
            else
            {
                int length = synth.QueuedModelInputLength;
                if (input.Length != length) input = new float[length];
                if (synth.ReadQueuedModelInput(input) && ReferenceEquals(q.Service, current))
                {
                    q.Service.Submit(input, q.FrameId, q.Time);
                    handOvers++;
                }
            }
        }

        if (synth.ReadQueuedHoles(out double holes)) holeFraction = holes;
    }

    /// <summary>Any 8×8 cell of <paramref name="area"/> whose mean luma moved since the last grid.</summary>
    private bool GridChanged(Rectangle area)
    {
        int x0 = Math.Max(0, area.Left / 8), y0 = Math.Max(0, area.Top / 8);
        int x1 = Math.Min(gridCells.Width, (area.Right + 7) / 8), y1 = Math.Min(gridCells.Height, (area.Bottom + 7) / 8);
        for (int y = y0; y < y1; y++)
        {
            int row = y * gridCells.Width;
            if (!grid.AsSpan(row + x0, Math.Max(0, x1 - x0)).SequenceEqual(previousGrid.AsSpan(row + x0, Math.Max(0, x1 - x0)))) return true;
        }
        return false;
    }

    /// <summary>The dirty rectangles said the region was unchanged, but its picture changed: after a few, they are not used again.</summary>
    private void OnMissedChange(double now)
    {
        if (now - missWindowStart > MissWindowSeconds)
        {
            missWindowStart = now;
            missedChanges = 0;
        }
        if (++missedChanges < MaxMissedChanges || !dirtyRectsTrusted) return;
        dirtyRectsTrusted = false;
        Log.Write($"AI 3D: the capture's dirty rectangles missed {missedChanges} changes of the video within {MissWindowSeconds:0} s; " +
                  "the luma grid decides which frames are new from now on.");
    }

    /// <summary>
    /// One line of the plan's measures since the last call (improvement-plan.md §0): M1 depth
    /// age, M2 holes, M4 flicker and M7 work share; then starts over.
    /// </summary>
    public string TakeMeasures(double seconds, DepthService? service, string pageData)
    {
        string age = "-";
        if (ageSamples.Count > 0)
        {
            if (ageScratch.Length < ageSamples.Count) ageScratch = new float[ageSamples.Count * 2];
            Span<float> a = ageScratch.AsSpan(0, ageSamples.Count);
            ageSamples.CopyTo(a);
            float mean = 0;
            foreach (float v in a) mean += v;
            mean /= a.Length;
            float p95 = Selection.Nth(a, (int)(0.95 * (a.Length - 1)));
            age = $"{mean:0}/{p95:0} ms";
        }
        double s = Math.Max(seconds, 1e-3);
        double busy = service is { AverageInferenceMs: > 0 } ? Math.Min(1, depthFramesTaken / s * service.AverageInferenceMs / 1000) : 0;
        string flicker = service != null && double.IsFinite(service.AverageFlicker) ? $"{service.AverageFlicker:0.0000}" : "-";
        string text = $"Measures: depth age {age} (mean/p95, {timingShown}); holes {(double.IsNaN(holeFraction) ? "-" : $"{holeFraction * 100:0.00} %")}; " +
                      $"flicker {flicker}; hand-overs {handOvers / s:0}/s, repeats skipped {repeatsSkipped / s:0}/s ({changeSource}); " +
                      $"model busy {busy * 100:0} %; page data: {pageData}";
        ageSamples.Clear();
        handOvers = repeatsSkipped = depthFramesTaken = 0;
        holeFraction = double.NaN;
        return text;
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

    /// <summary>The region widened by <paramref name="factor"/> around its centre.</summary>
    public static Rectangle Stretched(Rectangle region, double factor)
    {
        if (Math.Abs(factor - 1) < 0.001) return region;
        int width = (int)Math.Round(region.Width * factor);
        return new Rectangle(region.Left + (region.Width - width) / 2, region.Top, width, region.Height);
    }

    /// <summary>Queues the luma grid; <see cref="CollectReadbacks"/> reads it.</summary>
    private void QueueGrid(ID3D11Texture2D source, Size sourceSize)
    {
        if (gridSource != sourceSize || grid.Length == 0)
        {
            gridSource = sourceSize;
            gridCells = new Size((sourceSize.Width + 7) / 8, (sourceSize.Height + 7) / 8);
            grid = new byte[gridCells.Width * gridCells.Height];
            previousGridValid = false;
            lastGridTime = clock.Elapsed.TotalSeconds;
        }

        synth.QueueLumaGrid(source);
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
        forceHandOver = true;
    }

    private void Forget()
    {
        grid = [];
        previousGridValid = false;
        region = Rectangle.Empty;
        haveNearness = false;
        overlay = null;
        native.Reset();
        delayed.Clear();
        shownDelayedId = -1;
        forceHandOver = true;
    }

    public void Dispose()
    {
        delayed.Dispose();
        synth.Dispose();
    }
}
