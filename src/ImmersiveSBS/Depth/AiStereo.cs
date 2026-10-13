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

    /// <summary>The extension's theater video: set while the theater is open, not a setting.</summary>
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
/// <param name="Native3D">What the video is: 2D (converted) or side-by-side or top-bottom 3D (shown as is).</param>
/// <param name="BufferSeconds">How late the video is shown, in step with the sound the page delays; 0 = no buffer.
/// Only where the page can delay the sound; else Auto timing.</param>
/// <param name="Tuning">Fine controls; null = the defaults.</param>
/// <param name="Theater">The extension's player shows the video: it fills both eyes, the desktop hidden, AI 3D or not.</param>
/// <param name="Timing">Live or matched while there is no buffer.</param>
public sealed record StereoSettings(AiTarget Target, double Strength, double Convergence, double FieldOfView,
    Native3DMode Native3D = Native3DMode.Off, double BufferSeconds = 0, StereoTuning? Tuning = null,
    bool Theater = false, VideoTiming Timing = VideoTiming.Auto)
{
    public static readonly StereoSettings Off = new(AiTarget.Off, 0.6, 0.35, 50);
}

/// <summary>
/// Where the extension's theater shows its video (source px, the player full screen on the
/// virtual monitor), and whether it plays: the only way browser video reaches AI 3D. Guessing a
/// video from the picture took scrolling pages, animations and the desktop for videos, and
/// following one in place in a page needed its window and layout guessed too; both were removed
/// on 2026-10-12.
/// </summary>
/// <param name="Overlays">The player's controls over the picture, source px.</param>
/// <param name="Captions">The player's caption boxes, source px (subtitles may also be burned in).</param>
/// <param name="Page">The theater's frame events and playback state as they arrive.</param>
public sealed record VideoHint(Rectangle Region, bool Playing)
{
    public IReadOnlyList<Rectangle> Overlays { get; init; } = [];

    public IReadOnlyList<Rectangle>? Captions { get; init; }

    public PageVideo? Page { get; init; }
}

/// <summary>
/// Per-session driver of the video features on the render thread: takes the region (the
/// theater's video, or the whole screen), shows video the user says is already 3D as it is,
/// feeds the depth service and builds the eye images: live, matched (delayed just enough for
/// every frame's own depth, improvement-plan.md §6), or a fixed delay late from
/// <see cref="VideoDelayBuffer"/> while the sound is delayed too (video-3d-plan.md §4.1, §4.4,
/// P5). Only new pictures go to the model (improvement-plan.md §9 step 0): in the theater the
/// frames its player reports drawing, so a 24–30 fps video hands over 24–30 a second and a
/// paused one none; the whole screen, each new capture. Eye images are rebuilt only when the
/// frame, the depth or the settings changed. The live picture never waits for depth.
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

    private readonly StereoSynth synth;
    private readonly VideoDelayBuffer delayed;
    private readonly MatchedDelay matched = new(PresentLatencyMs);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private float[] input = [];
    private DepthFrame? depth;
    private DepthFrame? smoothed;
    private bool haveNearness;
    private Rectangle region;
    private long frameId;
    private long regionStartId;
    private double lastBandTime;
    private double bandLeft, bandRight, bandLeftTarget, bandRightTarget;
    private long shownDelayedId = -1;
    /// <summary>When each recent frame id was handed to the model, to know how old its depth is.</summary>
    private readonly long[] submitTicks = new long[64];
    private bool depthStale;
    private bool floatingWindow = true;
    /// <summary>The model input queued on the GPU this frame, handed to the service after the present.</summary>
    private QueuedInput? queuedInput;

    private readonly record struct QueuedInput(DepthService Service, long FrameId, double Time);
    private StereoOverlay? overlay;
    private object? overlayKey;

    // Handing over only changed frames.
    private bool forceHandOver = true;
    private DepthService? lastService;
    private StereoSettings? lastSettings;
    /// <summary>The theater's last frame handed to the model (its count of frames drawn), −1 for none.</summary>
    private long lastPresented = -1;
    /// <summary>The theater's page data at the last <see cref="Process"/>, for <see cref="HandOverEarly"/>; null outside the theater.</summary>
    private PageVideo? lastPage;
    private long lastDepthFrameId = -1;
    private int segment;
    private int seenSeeks = -1;
    private double wantedDelaySeconds;
    /// <summary>When the frame being processed was captured, Stopwatch ticks (now when unknown).</summary>
    private long frameTicks;
    /// <summary>The ids of the frames whose depth arrived this <see cref="Process"/>, for <see cref="ShownDepthChanged"/>.</summary>
    private readonly List<long> attachedIds = [];
    /// <summary>The frames whose depth the shown held frame's nearness came from (<see cref="VideoDelayBuffer.LastSources"/>).</summary>
    private (long From, long To) shownSources = (long.MinValue, long.MaxValue);
    private int lookAheadFrames;

    // Measures (improvement-plan.md §0), per statistics window.
    private int gen2Seen = GC.CollectionCount(2);
    private readonly List<float> ageSamples = [];
    private float[] ageScratch = [];
    private int handOvers, depthFramesTaken;
    private string changeSource = "-";
    private double holeFraction = double.NaN;
    private double lastHoleQueue;
    private string timingShown = "live";
    /// <summary>A frame was handed over by <see cref="HandOverEarly"/> since the last <see cref="Process"/>: its picture changed.</summary>
    private bool earlyChanged;
    private bool liveLast;

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
    /// The player's own controls and 3D panel (source px) to draw from the live capture, flat in
    /// both eyes, over the 3D picture: those on the screen now, and those in the held frame shown
    /// (gone from the screen meanwhile). The page draws them over its video, so they came with
    /// the picture: through the buffer seconds late and bent by the depth, while the pointer
    /// moved live. Empty outside the theater.
    /// </summary>
    public IReadOnlyList<Rectangle> LiveControls(VideoHint? hint)
    {
        liveControls.Clear();
        long now = Stopwatch.GetTimestamp();
        if (FoundRegion.IsEmpty || hint?.Page is not { } page || !page.Fresh(now)) return liveControls;
        PageVideo.BoxSet? current = page.BoxesAt(now);
        if (current != null) liveControls.AddRange(current.Overlays);
        if (shownTicks > 0 && page.BoxesAt(shownTicks) is { } shown && !ReferenceEquals(shown, current))
            liveControls.AddRange(shown.Overlays);
        return liveControls;
    }

    private readonly List<Rectangle> liveControls = [];

    /// <summary>When the held frame shown was captured, Stopwatch ticks; 0 when the picture is live.</summary>
    private long shownTicks;

    /// <summary>
    /// When the delayed picture (the buffer, matched timing) next shows another held frame,
    /// Stopwatch ticks; <see cref="long.MaxValue"/> when nothing is due. The session wakes for it:
    /// the held frames are shown on time, not only when a capture, the pointer or depth happens to
    /// cause a redraw (a page that stopped repainting froze the delayed tail while its sound played on).
    /// </summary>
    public long NextDueTicks { get; private set; } = long.MaxValue;

    /// <summary>
    /// The GPU memory Windows lets this process use, bytes (0: unknown); the held frames get an
    /// eighth of it, 128–600 MB, rather than 600 MB on every GPU.
    /// </summary>
    public long VideoMemoryBudget
    {
        set
        {
            long bytes = value > 0
                ? Math.Clamp(value / 8, 128L * 1024 * 1024, VideoDelayBuffer.DefaultMaxBytes)
                : VideoDelayBuffer.DefaultMaxBytes;
            // Only a real change: a smaller cap in use drops held frames.
            if (Math.Abs(bytes - delayed.MaxBytes) > 64L * 1024 * 1024) delayed.MaxBytes = bytes;
        }
    }

    /// <summary>
    /// Runs the video steps for this frame and returns the eye images to draw over the region, or
    /// null to show the frame flat.
    /// </summary>
    /// <param name="newFrame">The capture delivered a new desktop image.</param>
    /// <param name="eyeSize">One eye's size on the glasses, px.</param>
    /// <param name="scale">Eye px per source px (1 when the virtual monitor matches the eye).</param>
    /// <param name="soundDelay">How late the page plays the sound under <see cref="DelayRegion"/> now, seconds; 0 = live.</param>
    /// <param name="pageCanDelay">The page can delay this video's sound (not protected or cross-site media).</param>
    /// <param name="hint">The theater's video from the browser extension; null without one.</param>
    /// <param name="captureTicks">When the newest desktop image was made, Stopwatch ticks; 0 = now.</param>
    public StereoOverlay? Process(ID3D11Texture2D source, bool newFrame, StereoSettings settings, DepthService? service,
        Size eyeSize, double scale, double soundDelay, bool pageCanDelay, VideoHint? hint = null, long captureTicks = 0)
    {
        DelayRegion = Rectangle.Empty;
        FoundRegion = Rectangle.Empty;
        NextDueTicks = long.MaxValue;
        shownTicks = 0;
        liveLast = false;
        lastPage = null;
        if (settings.Target == AiTarget.Off)
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

        // The region: the whole frame, or where the theater shows its video.
        Rectangle target = new(Point.Empty, sourceSize);
        if (track)
        {
            target = hint is { } h ? Rectangle.Intersect(h.Region, target) : Rectangle.Empty;
            if (target.Width < 16 || target.Height < 16)
            {
                Status = "AI 3D: waiting for the theater's video (full screen on the virtual monitor)";
                SetRegion(Rectangle.Empty, service);
                return null;
            }
        }
        SetRegion(target, service);
        FoundRegion = region;
        string from = track ? ", in the theater" : "";

        // Video that is already 3D goes straight to the eyes, as the user says it is (§7.4).
        Native3DLayout layout = settings.Native3D switch
        {
            Native3DMode.ForceSideBySide => Native3DLayout.SideBySide,
            Native3DMode.ForceTopBottom => Native3DLayout.TopBottom,
            _ => Native3DLayout.None,
        };
        if (layout != Native3DLayout.None)
        {
            DelaySeconds = wantedDelaySeconds = 0;
            Status = $"AI 3D: {(layout == Native3DLayout.SideBySide ? "side-by-side" : "top-bottom")} 3D video, shown as is{from}";
            return Cached((layout, region), newFrame, () => synth.SplitNative(source, region, layout));
        }

        // The theater shows its video flat until depth is there (the model loading, the buffer
        // filling), rather than dropping back to the page.
        StereoOverlay? FlatInTheater() => track
            ? Cached((Native3DLayout.None, region), newFrame, () => synth.SplitNative(source, region, Native3DLayout.None))
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
        // Held frames and the model's frame times are stamped when the frame was captured, not
        // when this runs: that is later by the swap chain's wait, which varied with what else had
        // been presented and made the buffer skip frames that were on time.
        frameTicks = captureTicks > 0 && captureTicks <= now ? captureTicks : now;
        bool pageFresh = page?.Fresh(now) == true;
        int seeks = pageFresh ? page!.Seeks : -1;
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

        // What the player draws over the picture: its controls held flat, its captions instead of
        // the subtitle detection (improvement-plan.md §7.4).
        // The depth thread looks them up for each frame by its capture time, as the page reports them.
        PageVideo? boxesPage = track && pageFresh ? page : null;
        var modelSize = new Size(service.Model.Width, service.Model.Height);
        if (service.PageBoxes is not { } boxSource
            ? boxesPage != null
            : !ReferenceEquals(boxSource.Page, boxesPage) || boxSource.Region != region || boxSource.Model != modelSize)
        {
            service.PageBoxes = boxesPage == null ? null
                : new PageBoxSource(boxesPage, region, modelSize, Stopwatch.GetTimestamp() - clock.ElapsedTicks);
        }

        // Which frames go to the model. In the theater, those the player drew since the last one
        // handed over: it reports each frame it draws, so a paused video gives none and a seek
        // while paused gives one. Without its reports yet (the clock still syncing), each new
        // capture while the page says it plays. The whole screen: each new capture.
        PageVideo.FrameEvent shownFrame = default;
        bool haveFrame = track && pageFresh && page!.TryFrameAt(captureTicks > 0 ? captureTicks : now, Stopwatch.Frequency / 120, out shownFrame);
        bool newPicture;
        if (!track) newPicture = newFrame;
        else if (haveFrame) newPicture = newFrame && NewerThanHandedOver(shownFrame.Presented);
        else newPicture = newFrame && !(pageFresh && page!.Paused && !page.Seeking);
        if (forceHandOver && newFrame) newPicture = true;
        if (newPicture && haveFrame) lastPresented = shownFrame.Presented;
        // A frame handed over early changed the picture too; it is not handed over again.
        bool pictureChanged = newPicture || earlyChanged;
        bool handOver = newPicture;
        changeSource = !track ? "captured frames" : haveFrame ? "the theater's frames" : "captured frames (no frame reports yet)";
        earlyChanged = false;
        lastPage = track && pageFresh ? page : null;
        double mediaTime = haveFrame ? shownFrame.MediaTime : double.NaN;
        bool regionMoving = !track || !(pageFresh && page!.Paused);

        StereoTuning tuning = settings.Tuning ?? StereoTuning.Default;
        synth.DilateRadius = tuning.DilateRadius;
        synth.EdgeSnap = tuning.EdgeSnap;
        synth.Lookup = tuning.Warp == WarpMode.Lookup;
        floatingWindow = tuning.FloatingWindow;
        if (!floatingWindow) bandLeftTarget = bandRightTarget = 0;

        double budget = StereoMath.ComfortBudget(settings.FieldOfView, eyeSize.Width, eyeSize.Height);
        float strength = (float)(StereoMath.Strength(budget, settings.Convergence, settings.Strength) / scale);

        // Soft edges (improvement-plan.md §7.1): enough passes for a typical step to become a ramp
        // the warp can stretch over; the guided upsample's colour term softens with them, or it
        // would sharpen the ramp again where it crosses a colour edge (§7.3).
        int passes = tuning.SoftEdges >= 0 ? Math.Min(tuning.SoftEdges, 4) : SoftEdges.AutoPasses(strength, region.Width, service.Model.Width);
        service.SoftEdgePasses = passes;
        synth.RangeSigma = (float)(0.1 + 0.05 * passes);

        // Timing: the buffer, matched, or live. Sound is only ever delayed by the page (the browser
        // extension); for media it cannot delay there is no buffer, and matched timing stays
        // within the 90 ms lip sync allows with the sound live.
        matched.FrameRate = pageFresh ? page!.FrameRate : 0;
        matched.Update(clock.Elapsed.TotalSeconds);
        bool bufferWanted = settings.BufferSeconds > 0 && track;
        bool buffer = bufferWanted && pageCanDelay;
        string note = bufferWanted && !pageCanDelay ? ", no buffer: the page cannot delay this video's sound" : "";
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
            return Delayed(source, pictureChanged, handOver, settings, service, strength, from + note,
                       mediaTime, ShownDelay(towards)) ?? FlatInTheater();
        }
        if (wantMatched && !double.IsNaN(matched.DelayMs))
        {
            double delay = matched.DelayMs / 1000;
            if (!pageCanDelay && delay > LiveSoundMaxSeconds)
            {
                note += $", matched {delay * 1000:0} ms needs the sound delayed: {LiveSoundMaxSeconds * 1000:0} ms with the sound live";
                delay = LiveSoundMaxSeconds;
            }
            DelayRegion = pageCanDelay ? region : Rectangle.Empty;
            DelaySeconds = wantedDelaySeconds = pageCanDelay ? delay : 0;
            // The picture runs as late as the page plays the sound; until the page has delayed it
            // (or without the page) as late as lip sync allows with the sound live.
            double towards = soundDelay > 0 ? soundDelay : Math.Min(delay, LiveSoundMaxSeconds);
            service.LookAheadFrames = lookAheadFrames = 0;
            service.KeepAll = true;
            timingShown = "matched";
            return Matched(source, pictureChanged, handOver, settings, service, strength, from + note,
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
        service.LookAheadFrames = lookAheadFrames = 0;
        service.KeepAll = false;
        timingShown = "live";
        liveLast = true;
        return Live(source, pictureChanged, handOver, regionMoving, settings, service, strength, scale, from + note)
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
    /// A frame after the last one handed over. A frame counted on ahead of the page's reports
    /// may be followed by the report of an earlier one, which is no new picture; a count far
    /// behind is the page counting again (a new video).
    /// </summary>
    private bool NewerThanHandedOver(long presented) =>
        presented > lastPresented || presented + 4 * PageVideo.MaxFramesAhead < lastPresented;

    /// <summary>
    /// Render thread, as soon as a new desktop image arrives, before waiting for the swap chain:
    /// in live timing in the theater, queues the video's model input from it when the player
    /// reports a new frame shown by then, so the depth model gets it up to a refresh sooner
    /// (improvement-plan.md §9 step 2); <see cref="SubmitReadyInput"/> hands it over once the GPU
    /// has copied it. Uses the region, model and settings of the last <see cref="Process"/>,
    /// which then knows this frame is done.
    /// </summary>
    /// <param name="captureTicks">When the image was made, Stopwatch ticks.</param>
    public void HandOverEarly(ID3D11Texture2D source, DepthService? service, long captureTicks)
    {
        if (!liveLast || forceHandOver || region.IsEmpty || service == null || !ReferenceEquals(service, lastService)
            || service.State != DepthServiceState.Ready || lastPage is not { } page || page.Paused
            || !page.TryFrameAt(captureTicks, Stopwatch.Frequency / 120, out PageVideo.FrameEvent shown) || !NewerThanHandedOver(shown.Presented))
            return;
        lastPresented = shown.Presented;
        Submit(source, service, captureTicks);
        synth.Flush();
        earlyChanged = true;
    }

    /// <summary>Hands a queued model input to the service if the GPU has already copied it; never waits.</summary>
    public void SubmitReadyInput(DepthService? current)
    {
        if (queuedInput is not { } q || !synth.QueuedModelInputReady) return;
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
    private StereoOverlay? Live(ID3D11Texture2D source, bool pictureChanged, bool handOver, bool regionMoving,
        StereoSettings settings, DepthService service, float strength, double scale, string from)
    {
        if (handOver) Submit(source, service, frameTicks);

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
        Status = $"AI 3D: {what}{(regionMoving ? "" : ", paused")}{from}; {service.Status}";
        if (!haveNearness) return null;

        // How far the picture has moved on past the depth's frame, in frames and in video time.
        (long frames, double ms) = DepthLag();
        bool stale = depthStale ? frames > DepthFreshFrames && ms > DepthFreshMs : frames >= DepthStaleFrames && ms > DepthStaleMs;
        if (stale)
        {
            if (!depthStale) Log.Write($"AI 3D: depth is {frames} frames ({ms:0} ms) behind the picture; showing the video flat until it catches up.");
            depthStale = true;
            Status = $"AI 3D: {what}: depth too slow ({ms:0} ms behind), shown flat{from}; {service.Status}";
            return Cached((Native3DLayout.None, region), pictureChanged, () => synth.SplitNative(source, region, Native3DLayout.None));
        }
        if (depthStale) Log.Write($"AI 3D: depth caught up ({frames} frames behind).");
        depthStale = false;
        if (double.IsFinite(ms)) ageSamples.Add((float)ms);

        bool bandsMoved = SmoothBands();
        object key = (settings, scale, region);
        if (pictureChanged || newDepth || bandsMoved || overlay == null || !key.Equals(overlayKey))
        {
            overlay = new StereoOverlay(
                synth.Synthesize(source, region, strength, (float)settings.Convergence, (float)bandLeft, (float)bandRight), region);
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
    private StereoOverlay? Delayed(ID3D11Texture2D source, bool pictureChanged, bool handOver, StereoSettings settings,
        DepthService service, float strength, string from, double mediaTime, double shownSeconds)
    {
        double bufferSeconds = settings.BufferSeconds;
        // The buffer shows a frame long after its depth is ready: the depth thread looks as many
        // frames ahead as fit in that time, less a margin (improvement-plan.md §10). It goes down
        // only when more than a frame too long: on a whole number of frames (0.2 s at 30 fps) a
        // period wandering by a fraction of a millisecond moved it back and forth.
        double period = matched.PeriodMs;
        int lookAhead = double.IsFinite(period) && period > 0 ? Math.Max(0, (int)Math.Floor((shownSeconds - 0.3) * 1000 / period)) : 0;
        if (lookAhead > lookAheadFrames || lookAhead < lookAheadFrames - 1) lookAheadFrames = lookAhead;
        service.LookAheadFrames = lookAheadFrames;
        service.KeepAll = true;
        Push(source, pictureChanged, handOver, service, bufferSeconds, mediaTime);
        TakeDepthIntoBuffer(service);

        string waiting = shownSeconds < bufferSeconds - 0.002 ? $", reaching {bufferSeconds:0.#} s with the page's sound" : "";
        Status = $"AI 3D: video {region.Width}×{region.Height}, {shownSeconds:0.##} s behind ({delayed.Count} frames){waiting}{from}; {service.Status}";
        int index = SelectDelayed(shownSeconds);
        if (index < 0) return null;

        // Each frame is shown where it was on the screen when it was taken.
        VideoDelayBuffer.Frame frame = delayed[index];
        bool bandsMoved = SmoothBands();
        object key = (frame.Id, settings, frame.Region, "buffer");
        // Depth for frames shown later (all of it, once the delay is longer than the depth takes)
        // does not change this one: rebuilding for it doubled the stereo work.
        if (frame.Id == shownDelayedId && !ShownDepthChanged() && !bandsMoved && overlay != null && key.Equals(overlayKey)) return overlay;

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
        shownSources = delayed.LastSources;
        return ShowDelayed(frame, have, key, settings, strength);
    }

    /// <summary>
    /// Matched timing (improvement-plan.md §6): the picture <paramref name="delaySeconds"/> late,
    /// each frame with its own depth or depth interpolated between the frames on either side.
    /// </summary>
    /// <param name="soundDelayed">The page plays the sound late too; otherwise it is live.</param>
    private StereoOverlay? Matched(ID3D11Texture2D source, bool pictureChanged, bool handOver, StereoSettings settings,
        DepthService service, float strength, string from, double mediaTime, double delaySeconds, bool soundDelayed)
    {
        // Frames for the delay being reached as well as the one shown now.
        Push(source, pictureChanged, handOver, service, Math.Max(delaySeconds, wantedDelaySeconds), mediaTime);
        TakeDepthIntoBuffer(service);

        string sound = soundDelayed ? ", sound delayed in the page" : ", sound live";
        Status = $"AI 3D: video {region.Width}×{region.Height}, matched {delaySeconds * 1000:0} ms (depth needs {matched.NeedMs:0} ms){sound}{from}; {service.Status}";
        int index = SelectDelayed(delaySeconds);
        if (index < 0) return null;

        VideoDelayBuffer.Frame frame = delayed[index];
        bool bandsMoved = SmoothBands();
        object key = (frame.Id, settings, frame.Region, "matched");
        // New depth between the frames this one's came from can turn a held frame into an
        // interpolated one: rebuild then, not for depth of frames further on.
        if (frame.Id == shownDelayedId && !ShownDepthChanged() && !bandsMoved && overlay != null && key.Equals(overlayKey)) return overlay;

        EnsureSmoothed(service.Model);
        VideoDelayBuffer.DepthMatch match = delayed.InterpolatedNearness(index, smoothed!.Nearness, out long ageTicks);
        if (match != VideoDelayBuffer.DepthMatch.None) ageSamples.Add((float)(ageTicks * 1000.0 / Stopwatch.Frequency));
        shownSources = delayed.LastSources;
        return ShowDelayed(frame, match != VideoDelayBuffer.DepthMatch.None, key, settings, strength);
    }

    /// <summary>
    /// Copies a changed frame into the buffer, and hands it to the model when it should be. The
    /// first frame of a region goes in (and to the model) even when the picture stands still: a
    /// paused video would otherwise have nothing to show.
    /// </summary>
    private void Push(ID3D11Texture2D source, bool pictureChanged, bool handOver, DepthService service, double delaySeconds,
        double mediaTime)
    {
        bool first = delayed.Count == 0 || delayed[delayed.Count - 1].Region != region;
        if (!pictureChanged && !first) return;
        if (delayed.Push(source, region, frameTicks, frameId + 1, TimeSpan.FromSeconds(delaySeconds), Stopwatch.Frequency,
                mediaTime, segment, matched.FrameRate))
        {
            Timer?.Mark("buffer");
            if (handOver || first) Submit(source, service, frameTicks);
            else frameId++;   // its own id, without depth: shown with its neighbours'
        }
    }

    /// <summary>Files every depth frame that has arrived with its held frame; their ids go to <see cref="attachedIds"/>.</summary>
    private void TakeDepthIntoBuffer(DepthService service)
    {
        attachedIds.Clear();
        while (service.TryTake(ref depth))
        {
            OnDepth(depth!);
            if (delayed.Attach(depth!)) attachedIds.Add(depth!.FrameId);
        }
    }

    /// <summary>Depth arrived this time for a frame the shown one's nearness came from (or would).</summary>
    private bool ShownDepthChanged()
    {
        foreach (long id in attachedIds)
            if (id >= shownSources.From && id <= shownSources.To) return true;
        return false;
    }

    /// <summary>
    /// The held frame to show now, chosen <see cref="PresentLatencyTicks"/> ahead of being seen;
    /// sets <see cref="NextDueTicks"/> to when the one after it is.
    /// </summary>
    private int SelectDelayed(double delaySeconds)
    {
        long delayTicks = (long)(delaySeconds * Stopwatch.Frequency);
        int index = delayed.Select(Stopwatch.GetTimestamp() + PresentLatencyTicks - delayTicks);
        shownTicks = index >= 0 ? delayed[index].Time : 0;
        long next = delayed.NextTime(index);
        NextDueTicks = next == long.MaxValue ? long.MaxValue : next + delayTicks - PresentLatencyTicks;
        return index;
    }

    /// <summary>Stopwatch ticks as seconds of <see cref="clock"/>, the frames' clock for the depth thread.</summary>
    private double ClockSeconds(long ticks) => (ticks - (Stopwatch.GetTimestamp() - clock.ElapsedTicks)) / (double)Stopwatch.Frequency;

    private void EnsureSmoothed(DepthModel model)
    {
        if (smoothed == null || smoothed.Width != model.Width || smoothed.Height != model.Height)
            smoothed = new DepthFrame(model.Width, model.Height);
    }

    /// <summary>Builds the eyes of a held frame from the nearness in <see cref="smoothed"/>, or shows it flat without.</summary>
    private StereoOverlay ShowDelayed(VideoDelayBuffer.Frame frame, bool haveDepth, object key, StereoSettings settings, float strength)
    {
        var local = new Rectangle(Point.Empty, frame.Region.Size);
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
        var shown = new StereoOverlay(eyes, frame.Region);
        overlay = shown;
        overlayKey = key;
        return shown;
    }

    /// <summary>Queues this frame's model input; <see cref="CollectReadbacks"/> hands it to the service.</summary>
    /// <param name="captureTicks">When the frame was captured: the depth thread looks up the
    /// player's boxes and times its smoothing by it (the hand-over came up to a refresh later).</param>
    private void Submit(ID3D11Texture2D source, DepthService service, long captureTicks)
    {
        DepthModel model = service.Model;
        synth.QueueModelInput(source, region, new Size(model.Width, model.Height));
        queuedInput = new QueuedInput(service, ++frameId, ClockSeconds(captureTicks));
        submitTicks[frameId % submitTicks.Length] = Stopwatch.GetTimestamp();
        forceHandOver = false;
    }

    /// <summary>A depth frame reached the render thread: how old it is and how far after the previous one, for matched timing.</summary>
    private void OnDepth(DepthFrame frame)
    {
        depthFramesTaken++;
        long now = Stopwatch.GetTimestamp();
        if (frameId - frame.FrameId >= submitTicks.Length) return;
        // Without the time the buffer's look-ahead held it: matched timing needs the depth's own
        // latency. With it, a buffer left matched timing at 300 ms and Auto on matched for seconds.
        double ageMs = (now - submitTicks[frame.FrameId % submitTicks.Length]) * 1000.0 / Stopwatch.Frequency - frame.HeldMs;
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
    /// Render thread, after the present: reads what <see cref="Process"/> queued on the GPU and
    /// hands the model input to the depth service. By now the GPU has made the copies, so this
    /// rarely waits, and when it does, it is not in front of a present.
    /// </summary>
    /// <param name="current">The session's depth service now: input queued for one that was
    /// swapped out meanwhile (another model or size) is dropped; it has been disposed.</param>
    public void CollectReadbacks(DepthService? current)
    {
        if (queuedInput is { } q)
        {
            queuedInput = null;
            int length = synth.QueuedModelInputLength;
            if (input.Length != length) input = new float[length];
            if (synth.ReadQueuedModelInput(input) && ReferenceEquals(q.Service, current))
            {
                q.Service.Submit(input, q.FrameId, q.Time);
                handOvers++;
            }
        }

        if (synth.ReadQueuedHoles(out double holes)) holeFraction = holes;
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
        // Frames the held picture's memory cap left out, and full collections (large depth maps
        // made and dropped per frame showed up as these).
        int skipped = delayed.TakeSkipped();
        int gen2 = GC.CollectionCount(2);
        string held = timingShown == "live" ? "" : $"; held frames skipped {skipped}";
        string text = $"Measures: depth age {age} (mean/p95, {timingShown}); holes {(double.IsNaN(holeFraction) ? "-" : $"{holeFraction * 100:0.00} %")}; " +
                      $"flicker {flicker}; hand-overs {handOvers / s:0}/s ({changeSource}); " +
                      $"model busy {busy * 100:0} %{held}; full GCs {gen2 - gen2Seen}; page data: {pageData}";
        gen2Seen = gen2;
        ageSamples.Clear();
        handOvers = depthFramesTaken = 0;
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
    private StereoOverlay? Cached(object key, bool newFrame, Func<ID3D11ShaderResourceView> build)
    {
        if (newFrame || overlay == null || !key.Equals(overlayKey))
        {
            overlay = new StereoOverlay(build(), region);
            overlayKey = key;
        }
        return overlay;
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
        lastPresented = -1;
        service?.ResetHistory();
        forceHandOver = true;
    }

    private void Forget()
    {
        region = Rectangle.Empty;
        haveNearness = false;
        overlay = null;
        lastPage = null;
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
