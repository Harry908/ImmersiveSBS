using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ImmersiveSBS.Browser;
using ImmersiveSBS.Capture;
using ImmersiveSBS.Depth;
using ImmersiveSBS.Displays;
using ImmersiveSBS.Input;
using ImmersiveSBS.Interop;
using ImmersiveSBS.Theater;
using ImmersiveSBS.Tray;

namespace ImmersiveSBS;

/// <summary>
/// The tray application: watches the display layout and decides when the virtual monitor is
/// shown on the glasses. A session runs while the virtual monitor and the glasses are both on
/// the desktop, the glasses are in 3D mode (or 2D with <see cref="AppSettings.ShowIn2D"/>), and
/// the user has not paused. The 2D/3D switch itself is followed inside the running session.
/// UI thread only, except <see cref="OnKey"/>.
/// </summary>
public sealed class AppController : IDisposable
{
    /// <summary>A failing session is retried this many times before waiting for a change.</summary>
    private const int MaxRetries = 3;

    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer retryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private DisplayChangeMonitor? displayMonitor;
    private DepthService? depthService;
    private DepthModel? missingModel;
    private string? depthStatus;

    /// <summary>Field of view for glasses not in <see cref="KnownGlasses"/> (DesktopSbS default).</summary>
    private const double DefaultFieldOfView = 50;
    private GlobalKeyboardHook? keyboard;
    private TrayIcon? tray;
    private SettingsWindow? settingsWindow;
    private TuningWindow? tuningWindow;
    private volatile Dictionary<Shortcut, ShortcutCommand> shortcuts = [];

    private Session? session;
    private string? sessionKey;
    private CursorGuard? cursorGuard;
    private StrayWindowGuard? windowGuard;

    private string lastSignature = string.Empty;
    private bool reconciling;
    private bool reconcileAgain;
    private SessionOptions? pendingDesired;
    private int failures;
    private string? lastFailure;
    private bool notifiedNoVirtualMonitor;
    private bool shuttingDown;

    /// <summary>The user closed the virtual monitor window; it stays closed until asked for or resumed.</summary>
    private bool windowDismissed;

    public AppController()
    {
        Settings = AppSettings.Load();
        retryTimer.Tick += (_, _) =>
        {
            retryTimer.Stop();
            Evaluate("retry", force: true);
        };
    }

    public AppSettings Settings { get; private set; }

    public bool Paused { get; private set; }

    /// <summary>One line for the tray and the settings window.</summary>
    public string StatusText { get; private set; } = "Starting";

    /// <summary>The running session, for live statistics; null when nothing is shown.</summary>
    public Session? Session => session;

    /// <summary>The virtual monitor and the glasses as last resolved; null when not attached.</summary>
    public DisplayInfo? VirtualMonitor { get; private set; }

    public DisplayInfo? Glasses { get; private set; }

    public GlassesStatus? GlassesStatus { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="StatusText"/> may have changed.</summary>
    public event Action? StatusChanged;

    public void Start()
    {
        Log.Write($"App: start, version {typeof(AppController).Assembly.GetName().Version}, settings {AppSettings.FilePath}.");
        // Measured: unoptimised, the depth post-processing takes 5–7 times as long (55 ms against
        // 8 ms a frame with the edge steps), which no measurement from such a run would show.
        if (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<DebuggableAttribute>(typeof(AppController).Assembly)?.IsJITOptimizerDisabled == true)
            Log.Write("App: this is a Debug build, not optimised: depth post-processing runs several times slower. Use a Release build for runs in the glasses.");
        BuildShortcuts();
        tray = new TrayIcon(this);
        keyboard = new GlobalKeyboardHook { KeyPressed = OnKey };
        displayMonitor = new DisplayChangeMonitor();
        displayMonitor.Changed += reason => Evaluate(reason, force: false);
        StartBrowserLink();
        audioTimer.Tick += (_, _) =>
        {
            UpdateAudioDelay();
            UpdateTheater();
        };
        audioTimer.Start();
        Evaluate("start", force: true);
    }

    #region Delayed sound

    /// <summary>
    /// A video lost for less than this (the page reporting it again, the model reloading) keeps
    /// its delayed sound and picture running: stopping meant the sound jumped ahead and then went
    /// silent for the whole delay, and the picture froze as long, on every such moment.
    /// </summary>
    private const long DelayGraceMs = 5000;

    private readonly DispatcherTimer audioTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private long delayWantedAt = long.MinValue / 2;

    private BrowserLink? browserLink;
    private int browserCount;
    /// <summary>The browser video under the found region, followed from tick to tick.</summary>
    private BrowserVideo? browserVideo;
    /// <summary>The browser whose pages play their sound late now, or null.</summary>
    private BrowserConnection? browserDelaying;
    /// <summary>Media (connection, id) the extension cannot delay: no buffer for them, matched timing within lip sync.</summary>
    private readonly HashSet<(int Connection, string Id)> browserRefused = [];
    private long browserLogAt;
    private long followLogAt;
    /// <summary>When the followed video was last seen where it should be (Environment.TickCount64).</summary>
    private long browserSeenAt;
    /// <summary>When the user picked a video (the theater button, the pick shortcut): kept even while paused and another plays.</summary>
    private long browserPickedAt = long.MinValue / 2;

    /// <summary>A reported video smaller than this (a thumbnail, a preview) gets no theater button, px.</summary>
    private static readonly System.Drawing.Size MinTheaterVideo = new(160, 90);

    /// <summary>
    /// How long the followed video is kept after it stops being found, ms. In the first run, Edge
    /// lost it for one tick at a time (a popup over it, the player re-laying out), and each loss
    /// restarted the page's delayed sound and the video's buffer: an audible gap and a freeze.
    /// </summary>
    private const long BrowserGraceMs = 1500;

    /// <param name="Window">The browser's top-level window.</param>
    /// <param name="Screen">Where the picture is on the desktop, px.</param>
    private sealed record BrowserVideo(BrowserConnection Connection, BrowserMedia Media, IntPtr Window, Rectangle Screen);

    /// <summary>"Sound 1 s behind" while the page delays the sound; null otherwise.</summary>
    public string? AudioStatus => browserDelaying != null
        ? $"sound {Seconds(browserDelaying.RequestedDelay - Settings.AudioOffsetMs / 1000.0)} behind (in the browser)"
        : null;

    private static string Seconds(double s) => s < 0.95 ? $"{s * 1000:0} ms" : $"{s:0.#} s";

    /// <summary>Where the extension is, to load it unpacked: copied next to the app.</summary>
    public static string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension");

    public bool BrowserConnected => browserLink is { Connections.Count: > 0 };

    /// <summary>The browser extension's state, for the settings window.</summary>
    public string BrowserStatus =>
        browserLink == null ? "The browser extension cannot connect: another program uses its port (see the log)."
        : browserLink.Connections.Count == 0 ? "Browser extension not connected: the buffer is off, and matched timing stays within 90 ms with the sound live."
        : "Browser extension connected: " + string.Join(", ", browserLink.Connections.Select(c => c.ToString()))
          + (browserDelaying != null ? "; the sound is delayed in the page." : ".");

    private void StartBrowserLink()
    {
        var link = new BrowserLink();
        if (!link.Start())
        {
            link.Dispose();
            return;
        }
        browserLink = link;
        link.Changed += () => dispatcher.BeginInvoke(() =>
        {
            if (browserLink == null || browserLink.Connections.Count == browserCount) return;
            browserCount = browserLink.Connections.Count;
            browserRefused.RemoveWhere(r => browserLink.Connections.All(c => c.Number != r.Connection));
            Notify();
        });
    }

    /// <summary>
    /// Keeps the page's delayed sound in step with what the session asks for. Sound is delayed
    /// only inside the page, by the browser extension: the app never captures, replays or turns
    /// down any program's sound. The session learns whether the page can delay the followed
    /// video's sound (else no buffer, and matched timing within lip sync with the sound live);
    /// while it asks for a delay, the page plays the sound that late, and the session is told how
    /// late it plays now, so the picture follows exactly.
    /// </summary>
    private void UpdateAudioDelay()
    {
        Session? s = session;
        UpdateBrowserVideo(s);
        if (s != null) s.PageVideos = PageVideoRects();

        BrowserVideo? video = browserVideo;
        bool canDelay = video != null && !shuttingDown && BrowserCanDelay(video);
        if (s != null) s.PageCanDelay = canDelay;

        bool on = s is { Windowed: false } && VirtualMonitor != null && !shuttingDown;
        double wanted = on ? s!.DelaySeconds : 0;
        Rectangle region = on ? s!.DelayRegion : Rectangle.Empty;
        long now = Environment.TickCount64;
        if (wanted > 0 && !region.IsEmpty && canDelay)
        {
            delayWantedAt = now;
        }
        else if (wanted > 0 && browserDelaying != null && now - delayWantedAt < DelayGraceMs)
        {
            return; // lost for a moment: the page's delay runs on as it is
        }
        else
        {
            StopBrowserDelay();
            if (s != null) s.SoundDelay = 0;
            return;
        }

        double trim = Settings.AudioOffsetMs / 1000.0;
        StartBrowserDelay(video!.Connection, video.Media.Tab, Math.Round(wanted + trim, 3));
        // The picture follows the page's sound: how late it is now, without the trim (0 until the
        // video is taken into the delay, which happens once it plays).
        s!.SoundDelay = video.Media.Delay > 0 ? Math.Max(0.001, Math.Round(video.Media.Delay - trim, 3)) : 0;
        LogBrowserDelay(video);
    }

    /// <summary>
    /// Follows a video the extension reports, the only way a video is found (nothing is guessed
    /// from the picture): the same element from tick to tick while it stays visible and on top in
    /// its window, else the largest playing one on the virtual monitor. A followed paused video
    /// gives way to one that plays (the user started another), except in the theater or just
    /// after the user picked it. The session gets its exact place; a seek clears the depth history.
    /// </summary>
    private void UpdateBrowserVideo(Session? s)
    {
        BrowserVideo? found = null;
        if (browserLink is { Connections.Count: > 0 } link && s is { Windowed: false } && VirtualMonitor != null && !shuttingDown)
        {
            long now = Environment.TickCount64;
            bool hold = Theater || now - browserPickedAt < 2000;
            if (browserVideo is { } previous) found = FollowBrowserVideo(link, previous);
            if (found is { Media.Playing: false } && !hold && PlayingBrowserVideo() is { } playing && !Same(playing, found))
                found = playing;
            found ??= PlayingBrowserVideo();
            if (found != null)
            {
                browserSeenAt = now;
            }
            else if (browserVideo is { } kept && link.Connections.Contains(kept.Connection) && now - browserSeenAt < BrowserGraceMs)
            {
                // Missed for a moment: keep it as last seen rather than restart the sound and the buffer.
                found = kept with { Media = kept.Connection.Media.FirstOrDefault(m => m.Id == kept.Media.Id) ?? kept.Media };
            }
        }

        BrowserVideo? was = browserVideo;
        if (found?.Connection != was?.Connection || found?.Media.Id != was?.Media.Id)
        {
            Log.Write(found == null
                ? "Browser: no longer following a video in the browser."
                : $"Browser: following a {found.Media.VideoWidth}×{found.Media.VideoHeight} video in {found.Connection}, " +
                  $"at {found.Screen} on the desktop.");
        }
        else if (found != null && found.Media.Seeks != was!.Media.Seeks && Settings.UsePageData)
        {
            // A jump in the video: depth smoothed over the old frames would smear into the new.
            depthService?.ResetHistory();
        }
        browserVideo = found;

        // Protocol 2: the followed video reports its frames, state and the boxes over it; every
        // other browser stops reporting. With page data off, the video is still found (the page
        // delays its sound) but nothing the page tells reaches the picture.
        bool usePage = Settings.UsePageData;
        PageVideo? page = null;
        if (browserLink != null)
        {
            foreach (BrowserConnection c in browserLink.Connections)
                if ((c != found?.Connection || !usePage) && c.Followed != null) browserLink.Follow(c, null);
            if (found != null && usePage) page = browserLink.Follow(found.Connection, found.Media.Id);
        }

        if (s == null) return;
        if (found == null || VirtualMonitor is not { } monitor)
        {
            s.BrowserVideo = null;
            return;
        }
        Rectangle local = found.Screen;
        local.Offset(-monitor.Bounds.X, -monitor.Bounds.Y);
        if (!usePage)
        {
            // Its place only: that is how the video is found.
            s.BrowserVideo = new VideoHint(local, found.Media.Playing);
            return;
        }

        // The page's boxes, from its CSS px to the virtual monitor's px, split into captions and the rest.
        var overlays = new List<Rectangle>();
        List<Rectangle>? captions = page != null ? [] : null;
        if (page != null)
        {
            Rectangle client = Native.ClientBounds(found.Window);
            foreach (PageBox box in found.Connection.Overlays)
            {
                if (found.Media.ToScreen(box.Box, client) is not Rectangle r) continue;
                r.Offset(-monitor.Bounds.X, -monitor.Bounds.Y);
                (box.IsCaption ? captions! : overlays).Add(r);
            }
        }
        s.BrowserVideo = new VideoHint(local, found.Media.Playing)
        {
            Page = page,
            Seeks = found.Media.Seeks,
            Overlays = overlays,
            Captions = captions,
        };
        LogFollowed(found, page, overlays.Count, captions?.Count ?? 0);
    }

    /// <summary>Every 10 s while a video is followed with protocol 2: what the page tells (the evidence from a run).</summary>
    private void LogFollowed(BrowserVideo video, PageVideo? page, int overlays, int captions)
    {
        long now = Environment.TickCount64;
        if (page == null || now - followLogAt < 10000) return;
        followLogAt = now;
        bool fresh = page.Fresh(Stopwatch.GetTimestamp());
        Log.Write($"Browser: the followed video in {video.Connection}: " + (fresh
            ? $"{(page.Paused ? "paused" : "playing")}, {page.FrameRate:0.##} fps, clock within {page.ClockErrorMs:0.0#} ms, " +
              $"{overlays} boxes over it and {captions} captions."
            : "no frame or state messages for over a second (page data not used)."));
    }

    private static System.Drawing.Point Center(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    private static bool Same(BrowserVideo a, BrowserVideo b) => a.Connection == b.Connection && a.Media.Id == b.Media.Id;

    /// <summary>The followed video again, if it is still there, visible and on top where it was seen.</summary>
    private static BrowserVideo? FollowBrowserVideo(BrowserLink link, BrowserVideo previous)
    {
        if (!link.Connections.Contains(previous.Connection) || !Native.IsWindow(previous.Window) || Native.IsIconic(previous.Window))
            return null;
        BrowserMedia? media = previous.Connection.Media.FirstOrDefault(m => m.Id == previous.Media.Id);
        if (media is not { Visible: true } || media.ScreenRect(Native.ClientBounds(previous.Window)) is not Rectangle screen) return null;
        if (Native.RootWindowAt(Center(screen)) != previous.Window) return null;
        return previous with { Media = media, Screen = screen };
    }

    /// <summary>
    /// The videos the extension reports visible on the virtual monitor (its px), where the theater
    /// button is offered; null while no browser is connected. Not thumbnails or previews (smaller
    /// than <see cref="MinTheaterVideo"/>).
    /// </summary>
    private IReadOnlyList<Rectangle>? PageVideoRects()
    {
        if (browserLink is not { Connections.Count: > 0 } || VirtualMonitor is not { } vm || shuttingDown) return null;
        var rects = new List<Rectangle>();
        foreach (BrowserVideo v in PageVideosNear())
        {
            Rectangle r = Rectangle.Intersect(v.Screen, vm.Bounds);
            if (r.Width < MinTheaterVideo.Width || r.Height < MinTheaterVideo.Height) continue;
            r.Offset(-vm.Bounds.X, -vm.Bounds.Y);
            rects.Add(r);
        }
        return rects;
    }

    /// <summary>The reported video at a desktop point, big enough for the theater; the one the button was pressed on.</summary>
    private BrowserVideo? PageVideoAt(System.Drawing.Point desktop) =>
        PageVideosNear().FirstOrDefault(v => v.Screen.Contains(desktop)
                                             && v.Screen.Width >= MinTheaterVideo.Width && v.Screen.Height >= MinTheaterVideo.Height);

    /// <summary>
    /// The visible videos the extension reports in the browser window under the cursor, in the
    /// followed video's, and in those at <paramref name="also"/> (desktop points), placed on the
    /// screen; each only where its window is on top at its centre (a page's coordinates say
    /// nothing about the window, so another window's page cannot count).
    /// </summary>
    private IEnumerable<BrowserVideo> PageVideosNear(params System.Drawing.Point[] also)
    {
        if (browserLink is not { } link) yield break;
        var windows = new List<IntPtr>();
        Native.GetCursorPos(out System.Drawing.Point cursor);
        foreach (System.Drawing.Point p in also.Prepend(cursor))
        {
            IntPtr at = Native.RootWindowAt(p);
            if (at != IntPtr.Zero && !windows.Contains(at)) windows.Add(at);
        }
        if (browserVideo is { } followed && !windows.Contains(followed.Window)) windows.Add(followed.Window);
        foreach (IntPtr window in windows)
        {
            if (!Native.IsWindow(window) || Native.IsIconic(window)) continue;
            Rectangle client = Native.ClientBounds(window);
            if (client.IsEmpty) continue;
            foreach (BrowserConnection c in link.Connections)
                foreach (BrowserMedia m in c.Media)
                    if (m.IsVideo && m.Visible && m.ScreenRect(client) is Rectangle screen && Native.RootWindowAt(Center(screen)) == window)
                        yield return new BrowserVideo(c, m, window, screen);
        }
    }

    /// <summary>
    /// The largest playing video the extension reports on the virtual monitor (big enough for the
    /// theater), or null. The browser windows are looked for under the cursor and at nine points
    /// across the monitor (a page's coordinates need its window).
    /// </summary>
    private BrowserVideo? PlayingBrowserVideo()
    {
        if (VirtualMonitor is not { } vm) return null;
        Rectangle b = vm.Bounds;
        var points = new System.Drawing.Point[9];
        for (int i = 0; i < 9; i++)
            points[i] = new System.Drawing.Point(b.X + b.Width * (1 + 2 * (i % 3)) / 6, b.Y + b.Height * (1 + 2 * (i / 3)) / 6);
        return PageVideosNear(points)
            .Where(v => v.Media.Playing && Rectangle.Intersect(v.Screen, b) is { Width: >= 160, Height: >= 90 })
            .MaxBy(v => (long)v.Screen.Width * v.Screen.Height);
    }

    /// <summary>
    /// Whether the page can delay this video's sound and that of everything else audible in its
    /// tab. Media it cannot delay (protected, from another site without CORS, audio not allowed to
    /// start before a click) are remembered, so the choice does not flip back and forth.
    /// </summary>
    private bool BrowserCanDelay(BrowserVideo video)
    {
        foreach (BrowserMedia m in video.Connection.Media)
        {
            if (m.Tab != video.Media.Tab || (m.Id != video.Media.Id && !(m.Playing && m.Audible))) continue;
            var key = (video.Connection.Number, m.Id);
            if (!m.Delayable && browserRefused.Add(key))
                Log.Write($"Browser: {video.Connection} cannot delay a {m.Kind}'s sound in the page ({m.Reason}); no buffer for it, matched timing within 90 ms with the sound live.");
            if (browserRefused.Contains(key)) return false;
        }
        return true;
    }

    /// <summary>Has the browser play the followed video's tab <paramref name="seconds"/> late, every other tab live.</summary>
    private void StartBrowserDelay(BrowserConnection connection, int tab, double seconds)
    {
        if (browserDelaying != connection)
        {
            StopBrowserDelay();
            browserDelaying = connection;
            browserLogAt = 0;
            Notify();
        }
        browserLink!.SetDelay(connection, seconds, tab);
    }

    private void StopBrowserDelay()
    {
        if (browserDelaying == null) return;
        browserLink?.SetDelay(browserDelaying, 0);
        Log.Write($"Browser: {browserDelaying} plays the sound live again.");
        browserDelaying = null;
        if (session != null) session.SoundDelay = 0;
        Notify();
    }

    /// <summary>Every 10 s while the page delays the sound: the evidence from a run in the glasses.</summary>
    private void LogBrowserDelay(BrowserVideo video)
    {
        long now = Environment.TickCount64;
        if (now - browserLogAt < 10000) return;
        browserLogAt = now;
        IReadOnlyList<BrowserMedia> media = video.Connection.Media;
        int late = media.Count(m => m.Delay > 0);
        Log.Write($"Audio: {video.Connection} plays the page's sound {video.Media.Delay:0.00#} s late " +
                  $"(asked {video.Connection.RequestedDelay:0.00#} s; {late} of {media.Count} media elements delayed; " +
                  $"video {(video.Media.Playing ? "playing" : "paused")}; Web Audio output latency {video.Media.Latency?.ToString() ?? "-"} ms).");
    }

    #endregion

    #region Commands

    public void Execute(ShortcutCommand command)
    {
        if (shuttingDown) return;

        switch (command)
        {
            case ShortcutCommand.Pause:
                Paused = !Paused;
                Log.Write(Paused ? "App: paused." : "App: resumed.");
                Evaluate(Paused ? "pause" : "resume", force: true);
                break;

            case ShortcutCommand.Settings:
                if (settingsWindow == null)
                {
                    settingsWindow = new SettingsWindow(this);
                    settingsWindow.Closed += (_, _) => settingsWindow = null;
                    settingsWindow.Show();
                }
                settingsWindow.Activate();
                break;

            case ShortcutCommand.Exit:
                _ = ShutdownAsync();
                break;

            case ShortcutCommand.MoreDepth:
            case ShortcutCommand.LessDepth:
                Adjust(s => s.Ai3DStrength = Math.Round(Math.Clamp(s.Ai3DStrength + (command == ShortcutCommand.MoreDepth ? 0.1 : -0.1), 0, 1), 2),
                    s => $"Depth {s.Ai3DStrength:0.0}");
                break;

            case ShortcutCommand.PictureBack:
            case ShortcutCommand.PictureForward:
                Adjust(s => s.Ai3DConvergence = Math.Round(Math.Clamp(s.Ai3DConvergence + (command == ShortcutCommand.PictureForward ? 0.05 : -0.05), 0.1, 0.9), 2),
                    s => $"Screen plane {s.Ai3DConvergence:0.00}");
                break;

            case ShortcutCommand.PickVideo:
                // The video the extension reports under the cursor becomes the followed one.
                Native.GetCursorPos(out System.Drawing.Point cursor);
                if (PageVideoAt(cursor) is { } picked) Follow(picked, "the pick shortcut");
                else Log.Write("AI 3D: no video from the browser extension under the cursor to pick.");
                break;

            case ShortcutCommand.Tuning:
                if (tuningWindow == null)
                {
                    tuningWindow = new TuningWindow(this);
                    tuningWindow.Closed += (_, _) => tuningWindow = null;
                    tuningWindow.Show();
                }
                tuningWindow.Activate();
                break;

            case ShortcutCommand.ToggleAi3D:
                SetAi3D(Settings.Ai3D == AiTarget.Off ? Settings.Ai3DLastTarget : AiTarget.Off);
                break;
        }
    }

    /// <summary>Switches AI 3D to <paramref name="target"/> and remembers the last target that was on.</summary>
    public void SetAi3D(AiTarget target)
    {
        if (Settings.Ai3D == target) return;
        AppSettings s = Settings.Clone();
        if (s.Ai3D != AiTarget.Off) s.Ai3DLastTarget = s.Ai3D;
        if (target != AiTarget.Off) s.Ai3DLastTarget = target;
        s.Ai3D = target;
        Log.Write($"App: AI 3D {target}.");
        ApplySettings(s);
    }

    /// <summary>A hotkey change: applied and saved at once, and named in the status for the panel and tray.</summary>
    private void Adjust(Action<AppSettings> change, Func<AppSettings, string> describe)
    {
        AppSettings s = Settings.Clone();
        change(s);
        LastAdjustment = describe(s);
        Log.Write("App: " + LastAdjustment + ".");
        ApplySettings(s);
        tuningWindow?.Reload();
    }

    /// <summary>The last change made with a hotkey, e.g. "Depth 0.7"; shown in the tuning panel.</summary>
    public string? LastAdjustment { get; private set; }

    #region Theater

    private readonly TheaterButtons theaterButtons = new();
    private TheaterInput? theaterInput;
    private TheaterWindow? theaterWindow;
    private long theaterWithoutVideoSince;
    private bool cursorConfined;

    /// <summary>
    /// The timing choices, from no delay to the longest (the theater panel steps through them,
    /// the tray and the settings window list them): live, auto, matched (improvement-plan.md §6),
    /// then the buffer.
    /// </summary>
    public static readonly (string Label, VideoTiming Timing, double Buffer)[] TimingChoices =
    [
        ("Live", VideoTiming.Live, 0),
        ("Auto: matched when depth is slow", VideoTiming.Auto, 0),
        ("Matched: video just late enough for its depth", VideoTiming.Matched, 0),
        ("Buffer 0.5 s", VideoTiming.Auto, 0.5),
        ("Buffer 1 s", VideoTiming.Auto, 1),
        ("Buffer 2 s", VideoTiming.Auto, 2),
    ];

    /// <summary>
    /// The buffer delays the picture by seconds, so it needs the sound delayed with it: only the
    /// browser extension does that. Without a connected browser its choices are unavailable.
    /// </summary>
    public bool BufferAvailable => BrowserConnected;

    /// <summary>Whether choice <paramref name="index"/> can be used now (a buffer needs the extension).</summary>
    public bool TimingAvailable(int index) => TimingAvailable(index, BufferAvailable);

    public static bool TimingAvailable(int index, bool bufferAvailable) => TimingChoices[index].Buffer == 0 || bufferAvailable;

    /// <summary>Choice <paramref name="index"/>'s label, saying why when it cannot be used now.</summary>
    public string TimingLabel(int index) =>
        TimingChoices[index].Label + (TimingAvailable(index) ? "" : " (needs the browser extension)");

    /// <summary>
    /// The next choice from the settings' one in direction <paramref name="step"/> that can be used
    /// now, or the same one at the end (the theater panel's − and +).
    /// </summary>
    public int NextTiming(AppSettings s, int step) => NextTiming(s, step, BufferAvailable);

    public static int NextTiming(AppSettings s, int step, bool bufferAvailable)
    {
        int i = TimingIndex(s);
        for (int k = i + step; k >= 0 && k < TimingChoices.Length; k += step)
            if (TimingAvailable(k, bufferAvailable)) return k;
        return i;
    }

    /// <summary>Which of <see cref="TimingChoices"/> the settings make.</summary>
    public static int TimingIndex(AppSettings s)
    {
        if (s.BufferSeconds <= 0) return Array.FindIndex(TimingChoices, c => c.Buffer == 0 && c.Timing == s.Timing) is var i and >= 0 ? i : 1;
        int best = 3;
        for (int k = 3; k < TimingChoices.Length; k++)
            if (Math.Abs(TimingChoices[k].Buffer - s.BufferSeconds) < Math.Abs(TimingChoices[best].Buffer - s.BufferSeconds)) best = k;
        return best;
    }

    /// <summary>Applies one of <see cref="TimingChoices"/> to <paramref name="s"/>; a buffer keeps the timing chosen for without one.</summary>
    public static void SetTiming(AppSettings s, int index)
    {
        var choice = TimingChoices[Math.Clamp(index, 0, TimingChoices.Length - 1)];
        s.BufferSeconds = choice.Buffer;
        if (choice.Buffer == 0) s.Timing = choice.Timing;
    }

    /// <summary>The found video fills both eyes in the glasses, the desktop hidden.</summary>
    public bool Theater { get; private set; }

    /// <summary>Turns theater mode on or off (tray, on-screen buttons, the player window's keys).</summary>
    public void SetTheater(bool on)
    {
        if (Theater == on) return;
        if (on && browserVideo == null)
        {
            // Videos are found only by the extension.
            tray?.Notify(BrowserConnected
                ? "The 3D theater needs a playing video in the browser on the virtual monitor."
                : "The 3D theater needs the ImmersiveSBS browser extension, which finds the video.");
            return;
        }
        if (on && !OpenTheaterWindow()) return;
        Theater = on;
        if (!on) CloseTheaterWindow();
        theaterWithoutVideoSince = 0;
        Log.Write(on ? "App: theater on." : "App: theater off.");
        // The theater turns AI 3D on for its video (and the model loads), so re-evaluate fully.
        Evaluate(on ? "theater on" : "theater off", force: true);
    }

    /// <summary>
    /// Opens the theater's player window over the virtual monitor (<see cref="TheaterWindow"/>):
    /// it takes the clicks and keys the page under it used to get. Only while the glasses show
    /// the virtual monitor, and only when the capture can leave it out.
    /// </summary>
    private bool OpenTheaterWindow()
    {
        if (session is not { Windowed: false } || VirtualMonitor is not { } vm || shuttingDown) return false;
        var window = new TheaterWindow(vm.Bounds);
        window.Command += OnTheaterKey;
        window.Show();
        if (!window.Excluded)
        {
            window.CloseAndRestore();
            tray?.Notify("The 3D theater needs Windows 10 2004 or later (a window the capture leaves out).");
            return false;
        }
        theaterWindow = window;
        Native.SeeThroughWindow = window.Handle;
        return true;
    }

    private void CloseTheaterWindow()
    {
        TheaterWindow? window = theaterWindow;
        theaterWindow = null;
        Native.SeeThroughWindow = IntPtr.Zero;
        window?.CloseAndRestore();
    }

    /// <summary>A key or double click in the player window (UI thread).</summary>
    private void OnTheaterKey(TheaterKey key)
    {
        switch (key)
        {
            case TheaterKey.Exit: SetTheater(false); break;
            case TheaterKey.PlayPause: TogglePlay(); break;
            case TheaterKey.Back or TheaterKey.Forward:
                if (browserVideo is { } video) browserLink?.Seek(video.Connection, video.Media.Id, key == TheaterKey.Back ? -10 : 10);
                break;
        }
    }

    /// <summary>The followed video itself; else the media key, which the browser gives to the video it chooses.</summary>
    private void TogglePlay()
    {
        if (browserVideo is { } video && browserLink != null) browserLink.TogglePlay(video.Connection, video.Media.Id);
        else Native.PressMediaPlayPause();
    }

    /// <summary>Hook thread → UI thread: an on-screen button was clicked in the glasses, at <paramref name="at"/> (desktop px).</summary>
    private void OnTheaterClicked(TheaterAction action, System.Drawing.Point at) => dispatcher.BeginInvoke(() =>
    {
        switch (action)
        {
            case TheaterAction.EnterTheater:
                // The theater opens on the video whose button was pressed.
                if (PageVideoAt(at) is { } pick) Follow(pick, "the theater button");
                SetTheater(true);
                break;
            case TheaterAction.ExitTheater: SetTheater(false); break;
            case TheaterAction.PlayPause: TogglePlay(); break;
            case TheaterAction.SeekBack: OnTheaterKey(TheaterKey.Back); break;
            case TheaterAction.SeekForward: OnTheaterKey(TheaterKey.Forward); break;
            case TheaterAction.Mute:
                if (browserVideo is { } muted) browserLink?.ToggleMute(muted.Connection, muted.Media.Id);
                break;
            case TheaterAction.SeekBar:
                // Where on the bar it was pressed says where to: a share of the video's length.
                if (browserVideo is { } seeking && browserLink != null && seeking.Connection.Followed is { } page
                    && double.IsFinite(page.Duration) && theaterButtons.RectOf(TheaterAction.SeekBar) is Rectangle bar)
                {
                    // The click area reaches above and below the drawn bar; its width is the bar's.
                    browserLink.SeekTo(seeking.Connection, seeking.Media.Id, TheaterGeometry.SeekFraction(bar, at.X) * page.Duration);
                }
                break;
            case TheaterAction.Settings: session?.ToggleTheaterPanel(); break;
            case TheaterAction.DepthLess: Execute(ShortcutCommand.LessDepth); break;
            case TheaterAction.DepthMore: Execute(ShortcutCommand.MoreDepth); break;
            case TheaterAction.PlaneBack: Execute(ShortcutCommand.PictureBack); break;
            case TheaterAction.PlaneForward: Execute(ShortcutCommand.PictureForward); break;
            case TheaterAction.ModelPrevious or TheaterAction.ModelNext:
                Adjust(s => s.DepthTier = NextModel(s, action == TheaterAction.ModelNext ? 1 : -1), s => "Model " + DepthModel.Name(s.DepthTier));
                break;
            case TheaterAction.SizePrevious or TheaterAction.SizeNext:
                Adjust(s => s.DepthPreset = (DepthPreset)Math.Clamp((int)s.DepthPreset + (action == TheaterAction.SizeNext ? 1 : -1), 0,
                    Enum.GetValues<DepthPreset>().Length - 1), s => $"Resolution {s.DepthPreset}");
                break;
            case TheaterAction.BufferLess or TheaterAction.BufferMore:
                Adjust(s => SetTiming(s, NextTiming(s, action == TheaterAction.BufferMore ? 1 : -1)),
                    s => "Timing: " + TimingLabel(TimingIndex(s)));
                break;
            case TheaterAction.ToggleEdgeSnap:
                Adjust(s => s.Tuning = s.Tuning with { EdgeSnap = !s.Tuning.EdgeSnap }, s => "Edge snapping " + (s.Tuning.EdgeSnap ? "on" : "off"));
                break;
            case TheaterAction.ToggleCaptions:
                Adjust(s => s.Tuning = s.Tuning with { Captions = !s.Tuning.Captions }, s => "Flat subtitles " + (s.Tuning.Captions ? "on" : "off"));
                break;
        }
    });

    /// <summary>Follows <paramref name="pick"/> from now on: the user chose it (<paramref name="how"/>).</summary>
    private void Follow(BrowserVideo pick, string how)
    {
        if (browserVideo is not { } current || !Same(current, pick))
            Log.Write($"Browser: {how} picked a {pick.Media.VideoWidth}×{pick.Media.VideoHeight} video in {pick.Connection} at {pick.Screen}.");
        browserVideo = pick;
        browserSeenAt = browserPickedAt = Environment.TickCount64;
        UpdateBrowserVideo(session);
    }

    /// <summary>The next (or previous) model whose file exists at the current resolution, wrapping round.</summary>
    private static DepthTier NextModel(AppSettings s, int step)
    {
        DepthTier[] tiers = Enum.GetValues<DepthTier>();
        int i = Array.IndexOf(tiers, s.DepthTier);
        for (int k = 1; k <= tiers.Length; k++)
        {
            DepthTier t = tiers[((i + step * k) % tiers.Length + tiers.Length) % tiers.Length];
            if (new DepthModel(t, s.DepthPreset).Exists) return t;
        }
        return s.DepthTier;
    }

    /// <summary>
    /// The click hook runs only while the glasses show the virtual monitor; theater mode ends by
    /// itself when no video has been found for a few seconds (the video was closed).
    /// </summary>
    private void UpdateTheater()
    {
        bool glasses = session is { Windowed: false } && !shuttingDown;
        if (glasses && theaterInput == null)
        {
            theaterInput = new TheaterInput(theaterButtons);
            theaterInput.Clicked += OnTheaterClicked;
        }
        else if (!glasses && theaterInput != null)
        {
            theaterInput.Dispose();
            theaterInput = null;
            theaterButtons.Clear();
        }
        // The glasses left 3D or the virtual monitor: the player window would cover a screen nobody sees through.
        if (!glasses && Theater) SetTheater(false);

        // In the theater only the video is visible, so the cursor stays on it: outside it, it would
        // move over (and click) windows the user cannot see. Re-applied each tick, as Windows
        // drops a clip on some events (a secure desktop, another app's clip).
        Rectangle confine = Theater && session is { TheaterActive: true } s0 ? s0.TheaterDesktop : Rectangle.Empty;
        if (!confine.IsEmpty)
        {
            Native.ConfineCursor(confine);
            cursorConfined = true;
        }
        else if (cursorConfined)
        {
            Native.ConfineCursor(null);
            cursorConfined = false;
        }

        if (!Theater) return;
        if (session is { TheaterActive: true })
        {
            theaterWithoutVideoSince = 0;
        }
        else if (theaterWithoutVideoSince == 0)
        {
            theaterWithoutVideoSince = Environment.TickCount64;
        }
        else if (Environment.TickCount64 - theaterWithoutVideoSince > 4000)
        {
            Log.Write("App: theater ended: no video found.");
            SetTheater(false);
        }
    }

    #endregion

    /// <summary>Sets the video stretch (1 = none).</summary>
    public void SetStretch(double factor)
    {
        if (Math.Abs(Settings.VideoStretch - factor) < 0.001) return;
        AppSettings s = Settings.Clone();
        s.VideoStretch = factor;
        ApplySettings(s);
    }

    /// <summary>Sets the timing to one of <see cref="TimingChoices"/>.</summary>
    public void SetTiming(int index)
    {
        if (TimingIndex(Settings) == index) return;
        AppSettings s = Settings.Clone();
        SetTiming(s, index);
        ApplySettings(s);
    }

    /// <summary>The depth model's state: loading, preparing, ready or failed; null when there is none (file missing).</summary>
    public DepthServiceState? ModelState => depthService?.State;

    /// <summary>
    /// The depth model's state for the UI, AI 3D on or not, with the time spent so far while it
    /// loads, e.g. "Loading Distill-Any-Depth Base 518×294… 2 s" or "Distill-Any-Depth Base 518×294 ready".
    /// </summary>
    public string ModelStatus
    {
        get
        {
            if (depthService is not DepthService d) return "Depth model: " + (depthStatus ?? "none");
            double seconds = Stopwatch.GetElapsedTime(d.LoadStarted).TotalSeconds;
            return d.State switch
            {
                DepthServiceState.Loading => $"Loading {d.Model}… {seconds:0} s",
                DepthServiceState.Preparing => $"Preparing {d.Model} for this GPU (first use, about 20 s)… {seconds:0} s",
                DepthServiceState.Failed => $"{d.Model} failed: {d.Status}",
                _ => d.Status,
            };
        }
    }

    /// <summary>The depth model is loading or preparing.</summary>
    public bool ModelBusy => ModelState is DepthServiceState.Loading or DepthServiceState.Preparing;

    /// <summary>
    /// What AI 3D converts right now: the setting, except that the theater always shows its video
    /// in 3D (the saved setting is left as it is, so leaving the theater turns it back).
    /// </summary>
    private AiTarget EffectiveAi3D => Theater ? AiTarget.Video : Settings.Ai3D;

    /// <summary>True while the virtual monitor is shown in a window instead of on the glasses.</summary>
    public bool WindowShown => session is { Windowed: true };

    /// <summary>Tray: close the virtual monitor window, or bring it back.</summary>
    public void ToggleWindow()
    {
        if (WindowShown)
        {
            windowDismissed = true;
        }
        else
        {
            windowDismissed = false;
            if (!Settings.ShowInWindowWhenNotOnGlasses)
            {
                AppSettings s = Settings.Clone();
                s.ShowInWindowWhenNotOnGlasses = true;
                ApplySettings(s);
                return;
            }
        }
        Evaluate("window toggled", force: true);
    }

    public void SetCaptureMethod(CaptureMethod method)
    {
        if (Settings.CaptureMethod == method) return;
        AppSettings s = Settings.Clone();
        s.CaptureMethod = method;
        ApplySettings(s);
    }

    /// <summary>Saves new settings and applies them at once.</summary>
    public void ApplySettings(AppSettings settings) => ApplySettings(settings, save: true);

    /// <summary>
    /// Applies settings live without saving them (the tuning panel while sliders move); the saved
    /// file stays as it was until <see cref="ApplySettings(AppSettings)"/>.
    /// </summary>
    public void PreviewSettings(AppSettings settings)
    {
        // Only AI 3D, stretch and sound values change here: hand them to the running session and
        // the depth thread without re-evaluating the displays on every slider step.
        Settings = settings;
        if (session != null) ApplyAi(session);
        Notify();
    }

    private void ApplySettings(AppSettings settings, bool save)
    {
        Settings = settings;
        if (save) Settings.Save();
        BuildShortcuts();
        failures = 0;
        lastFailure = null;
        notifiedNoVirtualMonitor = false;
        Evaluate("settings changed", force: true);
    }

    public string ShortcutText(ShortcutCommand command) =>
        Settings.Shortcuts.TryGetValue(command, out string? text) && Shortcut.TryParse(text, out Shortcut s) ? s.ToString() : string.Empty;

    public void OpenLog()
    {
        if (File.Exists(Log.FilePath)) Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
    }

    private void BuildShortcuts()
    {
        var map = new Dictionary<Shortcut, ShortcutCommand>();
        foreach (var (command, text) in Settings.Shortcuts)
        {
            if (Shortcut.TryParse(text, out Shortcut s)) map[s] = command;
            else if (!string.IsNullOrWhiteSpace(text)) Log.Write($"App: shortcut \"{text}\" for {command} is not valid.");
        }
        shortcuts = map;
    }

    /// <summary>Keyboard hook thread: must answer at once.</summary>
    private bool OnKey(ModifierKeys modifiers, Key key)
    {
        if (!shortcuts.TryGetValue(new Shortcut(modifiers, key), out ShortcutCommand command)) return false;
        dispatcher.BeginInvoke(() => Execute(command));
        return true;
    }

    #endregion

    #region Deciding

    /// <summary>
    /// Re-reads the layout and starts, keeps or stops the session. Without
    /// <paramref name="force"/> nothing happens while the layout signature is unchanged.
    /// </summary>
    private void Evaluate(string reason, bool force)
    {
        if (shuttingDown) return;

        List<DisplayInfo> displays = DisplayConfigurationService.GetDesktopDisplays();
        string signature = DisplayConfigurationService.GetSignature(displays);
        bool changed = signature != lastSignature;
        if (!force && !changed) return;

        lastSignature = signature;
        if (changed)
        {
            Log.Write($"App: layout ({reason}): " + string.Join("; ", displays.Select(d =>
                $"{d.Identity} [{d.Identity.EdidKey}, {d.OutputTechnology}] {d.Mode} at {d.Bounds}")));
            if (!force)
            {
                // Windows may still be settling, and a new layout earns a fresh set of retries.
                displayMonitor?.RequestFollowUp();
                failures = 0;
            }
        }

        DisplayIdentity? sourceIdentity = Settings.ResolveVirtualMonitorIdentity(displays);
        DisplayIdentity? glassesIdentity = Settings.ResolveGlassesIdentity(displays);
        VirtualMonitor = DisplayConfigurationService.ResolveBestMatch(displays, sourceIdentity, out _);
        Glasses = DisplayConfigurationService.ResolveBestMatch(displays, glassesIdentity, out _);
        GlassesStatus = GlassesStateEvaluator.Evaluate(Glasses, Settings.ShowIn2D);

        string? blocker = null;
        if (Paused) blocker = "Paused";
        else if (VirtualMonitor == null) blocker = "Virtual monitor not found";
        else if (glassesIdentity == null) blocker = "No glasses chosen or recognised";
        else if (Glasses != null && Glasses.Identity.MatchAgainst(VirtualMonitor.Identity) >= DisplayMatchStrength.GdiDeviceName)
            blocker = "The virtual monitor and the glasses are the same display";
        else if (!GlassesStatus.ShouldPresent) blocker = GlassesStatus.Message ?? GlassesStatus.State.ToString();
        else if (failures > MaxRetries) blocker = "Stopped after repeated errors: " + lastFailure;
        else if (retryTimer.IsEnabled) blocker = "Retrying shortly after an error: " + lastFailure;

        if (VirtualMonitor == null && !notifiedNoVirtualMonitor && !Paused)
        {
            notifiedNoVirtualMonitor = true;
            tray?.Notify("The virtual monitor was not found. Check that the Virtual Display Driver is installed and " +
                         "enabled in Device Manager, and that the display is extended in Settings > System > Display.");
        }

        SessionOptions? desired = null;
        if (blocker == null)
        {
            desired = new SessionOptions(VirtualMonitor!.Identity, Glasses!.Identity, Settings.CaptureMethod);
            StatusText = $"On: {Glasses.Identity} in {(GlassesStatus.State == GlassesState.Mode3D ? "3D" : "2D")} " +
                         $"({Glasses.Mode.Width}×{Glasses.Mode.Height})";
        }
        else if (VirtualMonitor != null && Settings.ShowInWindowWhenNotOnGlasses && !windowDismissed
                 && failures <= MaxRetries && !retryTimer.IsEnabled
                 && WindowBoundsFor(displays) is Rectangle windowBounds)
        {
            // Not on the glasses: keep the virtual monitor in reach in a window on a real monitor.
            desired = new SessionOptions(VirtualMonitor.Identity, VirtualMonitor.Identity, Settings.CaptureMethod, windowBounds);
            StatusText = blocker + "; virtual monitor shown in a window";
        }
        else
        {
            StatusText = blocker;
        }

        if (EffectiveAi3D != AiTarget.Off && desired is { MirrorBounds: null })
            StatusText += Theater ? "; 3D theater" : "; AI 3D " + (Settings.Ai3D == AiTarget.Video ? "video" : "whole screen");

        UpdateDepthService();
        _ = ReconcileAsync(desired);
        // A kept session follows the settled layout (e.g. a new main display rebases every monitor).
        if (desired is { MirrorBounds: null }) session?.MoveTo(Glasses!.Bounds);
        if (session != null) ApplyAi(session);
        UpdateGuards();
        Notify();
    }

    /// <summary>
    /// Loads the depth model at start, AI 3D on or not, so turning 3D on (or entering the theater)
    /// is instant; swaps it when the tier or size changes. It idles while nothing asks for depth.
    /// Shared by all sessions.
    /// </summary>
    private void UpdateDepthService()
    {
        var wanted = new DepthModel(Settings.DepthTier, Settings.DepthPreset);
        // Compared by file: the fixed-size Large is the same model whatever the preset.
        if (depthService != null && depthService.Model.Stem == wanted.Stem) return;
        if (depthService == null && missingModel?.Stem == wanted.Stem && !wanted.Exists) return;

        depthService?.Dispose();
        depthService = null;
        depthStatus = null;
        missingModel = null;

        if (!wanted.Exists)
        {
            missingModel = wanted;
            depthStatus = $"the model file {wanted.Stem}.onnx is missing from {DepthModel.ModelsDirectory}";
            Log.Write("App: AI 3D: " + depthStatus);
            return;
        }

        var service = new DepthService(wanted);
        service.StatusChanged += () => dispatcher.BeginInvoke(() =>
        {
            if (depthService != service) return;
            depthStatus = service.Status;
            Notify();
        });
        depthService = service;
        depthStatus = service.Status;
    }

    /// <summary>Hands the current AI 3D settings and model to a session (applied live).</summary>
    private void ApplyAi(Session s)
    {
        double fov = KnownGlasses.FindByEdidKey(Glasses?.Identity.EdidKey)?.FieldOfView ?? DefaultFieldOfView;
        var stereo = new StereoSettings(EffectiveAi3D, Settings.Ai3DStrength, Settings.Ai3DConvergence, fov, Settings.Native3D,
            Settings.VideoStretch, Settings.BufferSeconds, Settings.Tuning, Theater, Settings.OfferTheater, Settings.Timing);
        if (s.Stereo != stereo) s.Stereo = stereo;
        if (s.Depth != depthService) s.Depth = depthService;
        s.Buttons = theaterButtons;
        if (depthService != null)
        {
            if (depthService.Tuning != Settings.Tuning) depthService.Tuning = Settings.Tuning;
            depthService.Convergence = Settings.Ai3DConvergence;
        }
    }

    /// <summary>What makes two sessions the same; the window's bounds only matter at creation.</summary>
    private static string KeyOf(SessionOptions o) =>
        (o.MirrorBounds != null ? "window|" : "glasses|") + o.Source.DevicePath + "|" + o.Destination.DevicePath + "|" + o.Method;

    /// <summary>
    /// The real monitor a window or an evacuated window goes to: the main display unless that is
    /// the virtual monitor, otherwise the largest. Never the glasses or the virtual monitor.
    /// </summary>
    private DisplayInfo? VisibleDisplay(IEnumerable<DisplayInfo> displays)
    {
        var visible = displays
            .Where(d => d.Identity.MatchAgainst(VirtualMonitor?.Identity) < DisplayMatchStrength.GdiDeviceName
                        && d.Identity.MatchAgainst(Glasses?.Identity) < DisplayMatchStrength.GdiDeviceName
                        && d.Identity.EdidKey != KnownGlasses.VirtualMonitorEdidKey)
            .ToList();
        return visible.FirstOrDefault(d => d.IsPrimary)
               ?? visible.OrderByDescending(d => (long)d.Bounds.Width * d.Bounds.Height).FirstOrDefault();
    }

    /// <summary>Half the width of the visible display's work area, centred, in the virtual monitor's shape.</summary>
    private Rectangle? WindowBoundsFor(IEnumerable<DisplayInfo> displays)
    {
        if (VirtualMonitor == null || VisibleDisplay(displays) is not DisplayInfo host) return null;

        Rectangle work = host.WorkingArea;
        int width = work.Width / 2;
        // Client area in the virtual monitor's shape plus roughly a title bar.
        int height = Math.Min(work.Height, width * VirtualMonitor.Bounds.Height / Math.Max(1, VirtualMonitor.Bounds.Width) + 40);
        return new Rectangle(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, width, height);
    }

    /// <summary>Brings the session in line with <paramref name="desired"/>, one change at a time.</summary>
    private async Task ReconcileAsync(SessionOptions? desired)
    {
        pendingDesired = desired;
        if (reconciling)
        {
            reconcileAgain = true;
            return;
        }

        reconciling = true;
        try
        {
            do
            {
                reconcileAgain = false;
                SessionOptions? target = pendingDesired;
                string? key = target == null ? null : KeyOf(target);

                if (session != null && key != sessionKey)
                {
                    Session old = session;
                    Log.Write("App: stopping the session.");
                    await old.StopAsync();
                    // OnSessionEnded may run before or after this; it only clears its own session.
                    session = null;
                    sessionKey = null;
                }

                if (target != null && session == null && !shuttingDown)
                {
                    // Back on the glasses: the next pause shows the window again.
                    if (target.MirrorBounds == null) windowDismissed = false;
                    var s = new Session(target);
                    s.Ended += failure => dispatcher.BeginInvoke(() => OnSessionEnded(s, failure));
                    ApplyAi(s);
                    session = s;
                    sessionKey = key;
                    s.Start();
                }

                UpdateGuards();
                Notify();
            }
            while (reconcileAgain);
        }
        finally
        {
            reconciling = false;
        }
    }

    private void OnSessionEnded(Session ended, string? failure)
    {
        if (session == ended)
        {
            session = null;
            sessionKey = null;
        }
        UpdateGuards();

        if (ended.ClosedByUser && !shuttingDown)
        {
            windowDismissed = true;
            Log.Write("App: the virtual monitor window was closed; not shown again until resumed or asked for.");
            Evaluate("window closed", force: true);
        }

        if (failure != null && !shuttingDown)
        {
            failures++;
            lastFailure = failure;
            Log.Write($"App: session failed ({failures}/{MaxRetries + 1}): {failure}");
            if (failures <= MaxRetries) retryTimer.Start();
            Evaluate("session failed", force: true);
        }
        Notify();
    }

    /// <summary>The Stage A guards run exactly while a session shows the virtual monitor on the glasses.</summary>
    private void UpdateGuards()
    {
        bool active = session is { Windowed: false } && Glasses != null && VirtualMonitor != null && !shuttingDown;

        if (active && Settings.KeepCursorOffGlasses)
        {
            if (cursorGuard == null) cursorGuard = new CursorGuard(Glasses!.Bounds);
            else cursorGuard.Blocked = Glasses!.Bounds;
        }
        else if (cursorGuard != null)
        {
            cursorGuard.Dispose();
            cursorGuard = null;
        }

        if (active && Settings.MoveWindowsOffGlasses)
        {
            if (windowGuard == null) windowGuard = new StrayWindowGuard(Glasses!.Bounds, VirtualMonitor!.WorkingArea);
            else windowGuard.Update(Glasses!.Bounds, VirtualMonitor!.WorkingArea);
        }
        else if (windowGuard != null)
        {
            windowGuard.Dispose();
            windowGuard = null;
        }
    }

    private void Notify()
    {
        tray?.Refresh();
        StatusChanged?.Invoke();
    }

    #endregion

    public async Task ShutdownAsync()
    {
        if (shuttingDown) return;

        List<DisplayInfo> displays = DisplayConfigurationService.GetDesktopDisplays(retryWhileTargetUnavailable: false);
        DisplayInfo? host = VisibleDisplay(displays);
        if (VirtualMonitor is { IsPrimary: true })
        {
            // The taskbar, Start and new windows stay on the main display, which nothing will show.
            MessageBoxResult answer = MessageBox.Show(
                "The virtual monitor is your main display. After ImmersiveSBS exits, the taskbar, the Start menu " +
                "and new windows stay on it, where nothing shows them." +
                (Settings.MoveWindowsOffVirtualMonitorOnExit && host != null ? $"\n\nOpen windows will be moved to {host.Identity}." : "") +
                "\n\nTo get the taskbar back, make another display the main one in Settings > System > Display, " +
                "or press Win+P and choose \"PC screen only\".\n\nExit anyway?",
                "ImmersiveSBS", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        shuttingDown = true;
        Log.Write("App: exit.");

        settingsWindow?.Close();
        tuningWindow?.Close();
        retryTimer.Stop();
        audioTimer.Stop();
        CloseTheaterWindow();
        StopBrowserDelay();
        if (session != null)
        {
            // A render thread stuck in the driver must not hold the exit; process end frees it.
            Task stop = session.StopAsync();
            if (await Task.WhenAny(stop, Task.Delay(3000)) != stop)
                Log.Write("App: the session did not stop within 3 s; exiting anyway.");
        }
        session = null;

        // Nothing will show the virtual monitor after exit: bring its windows into sight.
        if (Settings.MoveWindowsOffVirtualMonitorOnExit && VirtualMonitor != null && host != null)
        {
            Log.Write($"App: moving windows from the virtual monitor to {host.Identity}.");
            StrayWindowGuard.Evacuate(VirtualMonitor.Bounds, host.WorkingArea);
        }

        Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>Releases the hooks, the depth model, the browser link and the tray icon; safe to call twice.</summary>
    public void Dispose()
    {
        if (cursorConfined)
        {
            Native.ConfineCursor(null);
            cursorConfined = false;
        }
        theaterInput?.Dispose();
        theaterInput = null;
        cursorGuard?.Dispose();
        cursorGuard = null;
        windowGuard?.Dispose();
        windowGuard = null;
        displayMonitor?.Dispose();
        displayMonitor = null;
        keyboard?.Dispose();
        browserLink?.Dispose();
        browserLink = null;
        depthService?.Dispose();
        depthService = null;
        keyboard = null;
        tray?.Dispose();
        tray = null;
    }
}
