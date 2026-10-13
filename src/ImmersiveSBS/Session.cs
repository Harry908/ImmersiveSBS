using System.Diagnostics;
using System.Drawing;
using ImmersiveSBS.Capture;
using ImmersiveSBS.Depth;
using ImmersiveSBS.Displays;
using ImmersiveSBS.Interop;
using ImmersiveSBS.Presentation;
using ImmersiveSBS.Theater;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ImmersiveSBS;

/// <param name="Source">The virtual monitor.</param>
/// <param name="Destination">The glasses (unused when <paramref name="MirrorBounds"/> is set).</param>
/// <param name="MirrorBounds">Show the virtual monitor in a window with these bounds instead.</param>
public sealed record SessionOptions(DisplayIdentity Source, DisplayIdentity Destination, CaptureMethod Method,
    Rectangle? MirrorBounds = null);

/// <summary>One second of measurements. Latencies start at the capture's QPC time stamp.</summary>
public sealed record SessionStats(
    CaptureMethod Method,
    int SourceWidth, int SourceHeight,
    int DestinationWidth, int DestinationHeight, bool SideBySide,
    double CaptureFps, double PointerUpdatesPerSecond, double PresentFps, double RefreshHz,
    double PresentLatencyAvgMs, double PresentLatencyMaxMs,
    double DisplayLatencyAvgMs, double DisplayLatencyMaxMs,
    double VideoMemoryMb,
    string? Ai = null,
    double PrivateMemoryMb = 0, double ManagedMemoryMb = 0, double VideoMemoryBudgetMb = 0,
    string? Measures = null, string? Gpu = null)
{
    public override string ToString() =>
        $"Capture   {Method}, {SourceWidth}×{SourceHeight}, {CaptureFps:0.0} fps, cursor {PointerUpdatesPerSecond:0}/s\n" +
        $"Present   {DestinationWidth}×{DestinationHeight} {(SideBySide ? "side-by-side" : "2D")}, {PresentFps:0.0} fps, " +
        $"glasses {(RefreshHz > 0 ? RefreshHz.ToString("0.00") + " Hz" : "refresh unknown")}\n" +
        $"Capture → Present  avg {PresentLatencyAvgMs:0.0} ms, max {PresentLatencyMaxMs:0.0} ms\n" +
        $"Capture → vblank   {(DisplayLatencyAvgMs > 0 ? $"avg {DisplayLatencyAvgMs:0.0} ms, max {DisplayLatencyMaxMs:0.0} ms" : "no frame statistics")}\n" +
        $"VRAM (this process, render adapter)  {VideoMemoryMb:0} MB of {VideoMemoryBudgetMb:0} MB budget; memory {PrivateMemoryMb:0} MB ({ManagedMemoryMb:0} MB managed)" +
        (Ai != null ? "\n" + Ai : "") +
        (Measures != null ? "\n" + Measures : "") +
        (Gpu != null ? "\n" + Gpu : "");
}

/// <summary>
/// Captures the virtual monitor and presents it on the glasses, or in a window on a real monitor,
/// from one render thread, until stopped, closed or until something fails.
/// </summary>
public sealed class Session
{
    private readonly SessionOptions options;
    private readonly Thread thread;
    private volatile bool stopRequested;
    private volatile SessionStats? stats;
    private volatile StereoSettings stereo = StereoSettings.Off;
    private volatile DepthService? depth;
    private volatile int stereoVersion;
    private double soundDelay;
    private volatile VideoHint? browserVideo;
    private readonly TheaterUi theaterUi = new();
    private readonly object delayLock = new();
    private Rectangle delayRegion;
    private double delaySeconds;
    private readonly object boundsLock = new();
    private Rectangle? requestedBounds;

    /// <summary>Where the presenter window is; render thread only.</summary>
    private Rectangle bounds;

    public Session(SessionOptions options)
    {
        this.options = options;
        thread = new Thread(Run) { Name = "Render", IsBackground = true };
    }

    /// <summary>Raised on the render thread when the session ends; null reason means stopped by request.</summary>
    public event Action<string?>? Ended;

    public SessionStats? Stats => stats;

    /// <summary>AI 3D settings; any thread, applied from the next frame without a restart.</summary>
    public StereoSettings Stereo
    {
        get => stereo;
        set
        {
            stereo = value;
            Interlocked.Increment(ref stereoVersion);
        }
    }

    /// <summary>
    /// Set by the controller: how late the page plays the sound of the video under
    /// <see cref="DelayRegion"/> now, seconds (0: live). The picture follows it, so the two can
    /// never drift apart. Any thread.
    /// </summary>
    public double SoundDelay
    {
        get => Volatile.Read(ref soundDelay);
        set => Volatile.Write(ref soundDelay, value);
    }

    /// <summary>
    /// Set by the controller: the browser extension follows the video and can delay its sound in
    /// the page. Only then are delays longer than lip sync allows (the buffer, matched timing over
    /// 90 ms) used; the app never delays sound itself. Any thread.
    /// </summary>
    public bool PageCanDelay
    {
        get => Volatile.Read(ref pageCanDelay);
        set => Volatile.Write(ref pageCanDelay, value);
    }

    private bool pageCanDelay;

    /// <summary>
    /// The video region (virtual monitor px) whose sound should be delayed, or empty; read by the
    /// controller to find the app playing it. Any thread.
    /// </summary>
    public Rectangle DelayRegion
    {
        get { lock (delayLock) return delayRegion; }
    }

    /// <summary>
    /// How late that sound should be, seconds; kept while the region is lost for a moment, 0 when
    /// no delay is wanted (stop at once). Any thread.
    /// </summary>
    public double DelaySeconds
    {
        get { lock (delayLock) return delaySeconds; }
    }

    private void SetDelay(Rectangle r, double seconds)
    {
        lock (delayLock)
        {
            delayRegion = r;
            delaySeconds = seconds;
        }
    }

    /// <summary>
    /// The video's exact place from the browser extension (virtual monitor px), or null: the only
    /// way AI 3D and the theater find a video. Any thread.
    /// </summary>
    public VideoHint? BrowserVideo
    {
        get => browserVideo;
        set => browserVideo = value;
    }

    /// <summary>
    /// What the theater's 3D settings panel shows (the extension draws it): the settings now and
    /// the loaded model. Any thread.
    /// </summary>
    public TheaterPanel Panel()
    {
        StereoSettings stereo = Stereo;
        DepthModel? m = Depth?.Model;
        StereoTuning tuning = stereo.Tuning ?? StereoTuning.Default;
        string size = m == null ? "-" : $"{m.Preset} ({m.Width}×{m.Height})";
        string timing = stereo.BufferSeconds > 0
            ? PageCanDelay ? $"buffer {stereo.BufferSeconds:0.#} s" : "auto (buffer needs the extension)"
            : stereo.Timing switch
        {
            VideoTiming.Live => "live",
            VideoTiming.Matched => "matched",
            _ => "auto",
        };
        return new TheaterPanel(m == null ? "none" : DepthModel.Name(m.Tier), size, stereo.Strength, stereo.Convergence,
            timing, tuning.EdgeSnap, tuning.Captions);
    }

    /// <summary>The shared depth model, owned by the controller; null while AI 3D is off.</summary>
    public DepthService? Depth
    {
        get => depth;
        set
        {
            depth = value;
            Interlocked.Increment(ref stereoVersion);
        }
    }

    /// <summary>True when the user closed the mirror window, which ended the session.</summary>
    public bool ClosedByUser { get; private set; }

    /// <summary>True when this session shows a window rather than the glasses.</summary>
    public bool Windowed => options.MirrorBounds != null;

    public void Start() => thread.Start();

    public Task StopAsync()
    {
        stopRequested = true;
        return Task.Run(thread.Join);
    }

    /// <summary>
    /// Moves the presentation to the glasses' current desktop bounds; any thread. Applied on the
    /// render thread, and only when they differ from where it already is.
    /// </summary>
    public void MoveTo(Rectangle glassesBounds)
    {
        lock (boundsLock) requestedBounds = glassesBounds;
    }

    private void Run()
    {
        string? failure = null;
        ID3D11Device? device = null;
        IDXGIAdapter3? adapter = null;
        ICaptureSource? capture = null;
        AiStereo? ai = null;
        GpuTimer? gpuTimer = null;
        Renderer? renderer = null;
        IntPtr hwnd = IntPtr.Zero;

        try
        {
            Log.Write($"Session: start, {options.Method}, {options.Source} → {options.Destination}.");

            // Desktop Duplication needs the device on the adapter that owns the virtual monitor.
            var source = DxgiOutputs.Open(SourceGdiName())
                ?? throw new InvalidOperationException($"The virtual monitor ({options.Source}) is not on the desktop.");
            source.Output.Dispose();
            using (source.Adapter)
            {
                adapter = source.Adapter.QueryInterface<IDXGIAdapter3>();
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out device).CheckError();
                Log.Write($"Session: device on {adapter.Description1.Description}, feature level {device!.FeatureLevel}.");
            }

            if (options.MirrorBounds is Rectangle mirror)
            {
                // Windowed on a physical monitor: never side-by-side, whatever its shape.
                hwnd = MirrorWindow.Create(mirror);
                Size client = MirrorWindow.ClientSize(hwnd);
                renderer = new Renderer(device, hwnd, client.Width, client.Height) { AllowSideBySide = false };
                Log.Write($"Session: showing the virtual monitor in a window at {mirror}.");
            }
            else
            {
                DisplayInfo destination = DisplayConfigurationService.Resolve(options.Destination)
                    ?? throw new InvalidOperationException($"The glasses ({options.Destination}) are not on the desktop.");
                Log.Write($"Session: destination {destination} at {destination.Bounds}.");

                hwnd = PresenterWindow.Create(destination.Bounds);
                bounds = destination.Bounds;
                renderer = new Renderer(device, hwnd, destination.Bounds.Width, destination.Bounds.Height);
            }
            capture = options.Method == CaptureMethod.DesktopDuplication
                ? new DuplicationCapture(device, SourceGdiName)
                : new GraphicsCapture(device, SourceGdiName);

            gpuTimer = new GpuTimer(device);
            ai = new AiStereo(device) { Timer = gpuTimer };
            Loop(adapter, capture, renderer, hwnd, ai, gpuTimer);
        }
        catch (Exception e)
        {
            failure = e.Message;
            Log.Write($"Session: failed: {e}");
        }
        finally
        {
            ai?.Dispose();
            gpuTimer?.Dispose();
            capture?.Dispose();
            renderer?.Dispose();
            if (hwnd != IntPtr.Zero) Native.DestroyWindow(hwnd);
            device?.Dispose();
            adapter?.Dispose();
            stats = null;
            Log.Write("Session: ended.");
            Ended?.Invoke(failure);
        }
    }

    /// <summary>The virtual monitor's current GDI name; it changes between sessions.</summary>
    private string? SourceGdiName() =>
        DisplayConfigurationService.Resolve(options.Source, retryWhileTargetUnavailable: false)?.Identity.GdiDeviceName;

    private void Loop(IDXGIAdapter3 adapter, ICaptureSource capture, Renderer renderer, IntPtr hwnd, AiStereo ai, GpuTimer gpuTimer)
    {
        void Place(Rectangle target, string why)
        {
            Log.Write($"Session: {why}; glasses at {target}.");
            PresenterWindow.Move(hwnd, target);
            renderer.Resize(target.Width, target.Height);
            bounds = target;
        }

        var meter = new StatsMeter(options.Method);
        bool windowed = options.MirrorBounds != null;
        int seenDisplayChanges = PresenterWindow.DisplayChanges;
        int seenResizes = MirrorWindow.Resizes;
        int seenCloseRequests = MirrorWindow.CloseRequests;
        bool redraw = true;
        int seenStereoVersion = stereoVersion;
        DepthServiceState? seenModelState = null;
        string? aiStatus = null;
        (string?, string?) Measures(double seconds) => (ai.TakeMeasures(seconds, Depth, PageData()), gpuTimer.TakeSummary());

        while (!stopRequested)
        {
            PresenterWindow.Pump();

            if (windowed)
            {
                if (seenCloseRequests != MirrorWindow.CloseRequests)
                {
                    Log.Write("Session: the window was closed.");
                    ClosedByUser = true;
                    break;
                }
                if (seenResizes != MirrorWindow.Resizes)
                {
                    seenResizes = MirrorWindow.Resizes;
                    Size client = MirrorWindow.ClientSize(hwnd);
                    renderer.Resize(client.Width, client.Height);
                    redraw = true;
                }
            }
            else if (seenDisplayChanges != PresenterWindow.DisplayChanges)
            {
                // 2D/3D switch or layout change: follow the glasses by identity at once. Windows
                // may still be rebasing coordinates (a new main display moves every monitor), so
                // the controller sends the settled bounds again through MoveTo.
                seenDisplayChanges = PresenterWindow.DisplayChanges;
                DisplayInfo? d = DisplayConfigurationService.Resolve(options.Destination);
                if (d != null) Place(d.Bounds, "display change");
                else Log.Write("Session: display change; glasses not on the desktop.");
                redraw = true;
            }

            Rectangle? requested;
            lock (boundsLock)
            {
                requested = requestedBounds;
                requestedBounds = null;
            }
            if (!windowed && requested is Rectangle r && r != bounds)
            {
                Place(r, "layout settled");
                redraw = true;
            }

            // The delayed picture (the buffer, matched timing) shows its next held frame on time,
            // whether or not the desktop changes meanwhile.
            long due = renderer.SideBySide ? ai.NextDueTicks : long.MaxValue;
            int waitMs = redraw ? 0 : 16;
            if (due != long.MaxValue)
                waitMs = (int)Math.Clamp(Math.Ceiling((due - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency), 0, waitMs);
            CaptureUpdate update = capture.Acquire(waitMs);
            meter.OnCapture(update);
            // The depth model gets the new frame now, not after waiting for the swap chain.
            if (update.Frame && renderer.SideBySide && capture.Texture is { } arrived) ai.HandOverEarly(arrived, Depth, update.FrameQpc);

            // New depth or new AI settings change the picture even when the desktop did not.
            // The model's state shows as a badge in the glasses, and its loading ends a flat picture.
            DepthServiceState? modelState = Depth?.State;
            bool aiChanged = Depth?.HasResult == true || stereoVersion != seenStereoVersion || theaterUi.NeedsRedraw
                             || modelState != seenModelState || Stopwatch.GetTimestamp() >= due;
            seenStereoVersion = stereoVersion;
            seenModelState = modelState;
            if (!update.Any && !redraw && !aiChanged)
            {
                meter.Publish(this, adapter, renderer, capture, aiStatus, Measures);
                continue;
            }

            // Wait until the swap chain takes a frame, then fold in anything captured meanwhile.
            Native.WaitForSingleObjectEx(renderer.Waitable, 100, true);
            CaptureUpdate late = capture.Acquire(0);
            meter.OnCapture(late);
            ai.SubmitReadyInput(Depth);
            long content = Math.Max(update.ContentQpc, late.ContentQpc);
            // When the desktop image now in the texture was made (not the cursor's time).
            long frameQpc = late.Frame ? late.FrameQpc : update.FrameQpc;

            // AI 3D only exists in side-by-side; in 2D and in the window the frame stays flat.
            StereoOverlay? overlay = null;
            UiLayer? ui = null;
            IReadOnlyList<Rectangle>? live = null;
            StereoSettings stereo = Stereo;
            gpuTimer.BeginFrame();
            if (renderer.SideBySide && capture.Texture is { } frame)
            {
                var fd = frame.Description;
                var eye = new Size(renderer.Width / 2, renderer.Height);
                double scale = Math.Min((double)eye.Width / fd.Width, (double)eye.Height / fd.Height);
                if (stats is { VideoMemoryBudgetMb: > 0 } st) ai.VideoMemoryBudget = (long)(st.VideoMemoryBudgetMb * 1024 * 1024);
                VideoHint? hint = BrowserVideo;
                overlay = ai.Process(frame, update.Frame || late.Frame, stereo, Depth, eye, scale, SoundDelay, PageCanDelay, hint, frameQpc);
                if (overlay != null) live = ai.LiveControls(hint);
                aiStatus = ai.Status;
                ui = theaterUi.Build(overlay, ai.FoundRegion, eye, new Size((int)fd.Width, (int)fd.Height), stereo.Target, Depth?.State);
                SetDelay(ai.DelayRegion, ai.DelaySeconds);
            }
            else
            {
                aiStatus = stereo.Target == AiTarget.Off ? null : "AI 3D: works when the glasses are in 3D mode";
                SetDelay(Rectangle.Empty, 0);
                theaterUi.Hide();
            }

            renderer.Render(capture.Texture, options.Method == CaptureMethod.DesktopDuplication ? capture.Pointer : null, overlay, ui, live);
            gpuTimer.Mark("draw");
            gpuTimer.EndFrame();
            long presentQpc = Stopwatch.GetTimestamp();
            uint presentCount = renderer.Present();
            ai.CollectReadbacks(Depth);
            gpuTimer.Collect();
            meter.OnPresent(presentCount, content, presentQpc);
            if (renderer.TryGetFrameStatistics(out FrameStatistics fs)) meter.OnFrameStatistics(fs);
            redraw = false;

            meter.Publish(this, adapter, renderer, capture, aiStatus, Measures);
        }
    }

    /// <summary>What the browser extension tells about the video, for the measures (M7).</summary>
    private string PageData()
    {
        if (BrowserVideo is not { } hint) return "none (no theater)";
        if (hint.Page is not { } page || !page.Fresh(Stopwatch.GetTimestamp())) return "the theater's place, no frame reports for over a second";
        double fps = page.FrameRate, error = page.ClockErrorMs;
        return "the theater (" + (fps > 0 ? $"{fps:0.#} fps drawn" : "no frames yet") +
               (double.IsFinite(error) ? $", clock within {error:0.0} ms" : "") +
               (hint.Captions != null ? $", {hint.Captions.Count} caption boxes" : "") + $", {hint.Overlays.Count} overlay boxes)";
    }

    /// <summary>Accumulates one second of measurements on the render thread.</summary>
    private sealed class StatsMeter(CaptureMethod method)
    {
        private const int Ring = 64;
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        private readonly uint[] ringCount = new uint[Ring];
        private readonly long[] ringContent = new long[Ring];
        private long windowStart = Stopwatch.GetTimestamp();
        private long nextLog = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
        private int frames, pointerUpdates, presents;
        private double presentSum, presentMax;
        private int displayCount;
        private double displaySum, displayMax;
        private uint lastStatsPresent;
        private uint firstRefresh, lastRefresh;
        private long firstSync, lastSync;

        public void OnCapture(CaptureUpdate u)
        {
            if (u.Frame) frames++;
            if (u.Pointer) pointerUpdates++;
        }

        public void OnPresent(uint presentCount, long contentQpc, long presentQpc)
        {
            presents++;
            ringCount[presentCount % Ring] = presentCount;
            ringContent[presentCount % Ring] = contentQpc;
            if (contentQpc <= 0) return;

            double ms = (presentQpc - contentQpc) * MsPerTick;
            presentSum += ms;
            presentMax = Math.Max(presentMax, ms);
        }

        public void OnFrameStatistics(FrameStatistics fs)
        {
            if (fs.SyncQPCTime == 0) return;

            if (firstSync == 0)
            {
                firstSync = fs.SyncQPCTime;
                firstRefresh = fs.SyncRefreshCount;
            }
            lastSync = fs.SyncQPCTime;
            lastRefresh = fs.SyncRefreshCount;

            if (fs.PresentCount == lastStatsPresent) return;
            lastStatsPresent = fs.PresentCount;

            int slot = (int)(fs.PresentCount % Ring);
            if (ringCount[slot] != fs.PresentCount || ringContent[slot] <= 0) return;

            double ms = (fs.SyncQPCTime - ringContent[slot]) * MsPerTick;
            if (ms <= 0 || ms > 1000) return;
            displayCount++;
            displaySum += ms;
            displayMax = Math.Max(displayMax, ms);
        }

        public void Publish(Session session, IDXGIAdapter3 adapter, Renderer renderer, ICaptureSource capture, string? ai,
            Func<double, (string? Measures, string? Gpu)> measures)
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = (now - windowStart) / (double)Stopwatch.Frequency;
            if (seconds < 1) return;

            double refreshHz = lastRefresh > firstRefresh && lastSync > firstSync
                ? (lastRefresh - firstRefresh) * (double)Stopwatch.Frequency / (lastSync - firstSync)
                : 0;
            double vram = 0, budget = 0;
            try
            {
                var info = adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                vram = info.CurrentUsage / (1024.0 * 1024.0);
                // What Windows lets this process keep in VRAM now; above it, memory pages out and
                // everything on the GPU slows down (the depth model first).
                budget = info.Budget / (1024.0 * 1024.0);
            }
            catch { }

            var src = capture.Texture?.Description;
            int measured = presents > 0 ? presents : 1;
            (string? aiMeasures, string? gpu) = measures(seconds);
            var s = new SessionStats(method,
                (int)(src?.Width ?? 0), (int)(src?.Height ?? 0),
                renderer.Width, renderer.Height, renderer.SideBySide,
                frames / seconds, pointerUpdates / seconds, presents / seconds, refreshHz,
                presentSum / measured, presentMax,
                displayCount > 0 ? displaySum / displayCount : 0, displayMax,
                vram, ai,
                // A steady climb here over minutes is a leak (Windows flagged one once, at a model load).
                System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / (1024.0 * 1024.0),
                GC.GetTotalMemory(false) / (1024.0 * 1024.0), budget, ai != null ? aiMeasures : null, gpu);
            session.stats = s;

            if (now >= nextLog)
            {
                Log.Write("Stats: " + s.ToString().Replace("\n", " | "));
                nextLog = now + 10 * Stopwatch.Frequency;
            }

            windowStart = now;
            frames = pointerUpdates = presents = displayCount = 0;
            presentSum = presentMax = displaySum = displayMax = 0;
            firstSync = 0;
        }
    }
}
