using System.Diagnostics;
using System.Drawing;
using ImmersiveSBS.Capture;
using ImmersiveSBS.Displays;
using ImmersiveSBS.Interop;
using ImmersiveSBS.Presentation;
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
    double VideoMemoryMb)
{
    public override string ToString() =>
        $"Capture   {Method}, {SourceWidth}×{SourceHeight}, {CaptureFps:0.0} fps, cursor {PointerUpdatesPerSecond:0}/s\n" +
        $"Present   {DestinationWidth}×{DestinationHeight} {(SideBySide ? "side-by-side" : "2D")}, {PresentFps:0.0} fps, " +
        $"glasses {(RefreshHz > 0 ? RefreshHz.ToString("0.00") + " Hz" : "refresh unknown")}\n" +
        $"Capture → Present  avg {PresentLatencyAvgMs:0.0} ms, max {PresentLatencyMaxMs:0.0} ms\n" +
        $"Capture → vblank   {(DisplayLatencyAvgMs > 0 ? $"avg {DisplayLatencyAvgMs:0.0} ms, max {DisplayLatencyMaxMs:0.0} ms" : "no frame statistics")}\n" +
        $"VRAM (this process, render adapter)  {VideoMemoryMb:0} MB";
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

            Loop(adapter, capture, renderer, hwnd);
        }
        catch (Exception e)
        {
            failure = e.Message;
            Log.Write($"Session: failed: {e}");
        }
        finally
        {
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

    private void Loop(IDXGIAdapter3 adapter, ICaptureSource capture, Renderer renderer, IntPtr hwnd)
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

            CaptureUpdate update = capture.Acquire(redraw ? 0 : 16);
            meter.OnCapture(update);
            if (!update.Any && !redraw)
            {
                meter.Publish(this, adapter, renderer, capture);
                continue;
            }

            // Wait until the swap chain takes a frame, then fold in anything captured meanwhile.
            Native.WaitForSingleObjectEx(renderer.Waitable, 100, true);
            CaptureUpdate late = capture.Acquire(0);
            meter.OnCapture(late);
            long content = Math.Max(update.ContentQpc, late.ContentQpc);

            renderer.Render(capture.Texture, options.Method == CaptureMethod.DesktopDuplication ? capture.Pointer : null);
            long presentQpc = Stopwatch.GetTimestamp();
            uint presentCount = renderer.Present();
            meter.OnPresent(presentCount, content, presentQpc);
            if (renderer.TryGetFrameStatistics(out FrameStatistics fs)) meter.OnFrameStatistics(fs);
            redraw = false;

            meter.Publish(this, adapter, renderer, capture);
        }
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

        public void Publish(Session session, IDXGIAdapter3 adapter, Renderer renderer, ICaptureSource capture)
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = (now - windowStart) / (double)Stopwatch.Frequency;
            if (seconds < 1) return;

            double refreshHz = lastRefresh > firstRefresh && lastSync > firstSync
                ? (lastRefresh - firstRefresh) * (double)Stopwatch.Frequency / (lastSync - firstSync)
                : 0;
            double vram = 0;
            try { vram = adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage / (1024.0 * 1024.0); }
            catch { }

            var src = capture.Texture?.Description;
            int measured = presents > 0 ? presents : 1;
            var s = new SessionStats(method,
                (int)(src?.Width ?? 0), (int)(src?.Height ?? 0),
                renderer.Width, renderer.Height, renderer.SideBySide,
                frames / seconds, pointerUpdates / seconds, presents / seconds, refreshHz,
                presentSum / measured, presentMax,
                displayCount > 0 ? displaySum / displayCount : 0, displayMax,
                vram);
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
