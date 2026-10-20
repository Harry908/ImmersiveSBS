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
        Log.Write($"App: start, version {Version}, settings {AppSettings.FilePath}.");
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
            UpdateStatus();
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
    /// <summary>The theater's video, followed from tick to tick; null while no theater is open.</summary>
    private BrowserVideo? browserVideo;
    /// <summary>The browser whose pages play their sound late now, or null.</summary>
    private BrowserConnection? browserDelaying;
    /// <summary>Media (connection, id) the extension cannot delay: no buffer for them, matched timing within lip sync.</summary>
    private readonly HashSet<(int Connection, string Id)> browserRefused = [];
    private long browserLogAt;
    private long followLogAt;
    /// <summary>Why the theater was last found not full screen on the virtual monitor (said once per theater and reason).</summary>
    private string? offScreenTheater;

    /// <param name="Theater">What its page reports about the theater.</param>
    /// <param name="Media">The video's entry in the media report, once it is there (for its sound).</param>
    private sealed record BrowserVideo(BrowserConnection Connection, TheaterReport Theater, BrowserMedia? Media);

    /// <summary>"Sound 1 s behind" while the page delays the sound; null otherwise.</summary>
    public string? AudioStatus => browserDelaying != null
        ? $"sound {Seconds(browserDelaying.RequestedDelay - browserTrim)} behind (in the browser)"
        : null;

    /// <summary>What the page's delay differs by from the picture's: the user's trim less the Web Audio output latency, seconds.</summary>
    private double browserTrim;

    /// <summary>The page's delay asked for now and the one before, each with its trim.</summary>
    private ((double Seconds, double Trim) Now, (double Seconds, double Trim) Before) delayRequests = ((double.NaN, 0), (double.NaN, 0));

    private static string Seconds(double s) => s < 0.95 ? $"{s * 1000:0} ms" : $"{s:0.#} s";

    /// <summary>Where the extension is, to load it unpacked: copied next to the app.</summary>
    public static string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension");

    /// <summary>The release number, from the project's Version (without the build's commit suffix).</summary>
    public static string Version { get; } =
        (typeof(AppController).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?")
        .Split('+')[0];

    public bool BrowserConnected => browserLink is { Connections.Count: > 0 };

    /// <summary>The browser extension's state, for the settings window.</summary>
    public string BrowserStatus =>
        browserLink == null ? "The browser extension cannot connect: another program uses its port (see the log)."
        : browserLink.Connections.Count == 0
            ? "Browser extension not connected. It is part of ImmersiveSBS: browser video is shown in 3D only in its theater, and the page delays the sound."
        : "Browser extension connected: " + string.Join(", ", browserLink.Connections.Select(c => c.ToString()))
          + (Theater ? "; the 3D theater is open" : "") + (browserDelaying != null ? "; the sound is delayed in the page." : ".");

    private void StartBrowserLink()
    {
        var link = new BrowserLink();
        if (!link.Start())
        {
            link.Dispose();
            return;
        }
        browserLink = link;
        link.Command += (_, name) => dispatcher.BeginInvoke(() => OnPageCommand(name));
        link.Refused += (browser, version) => dispatcher.BeginInvoke(() => tray?.Notify(
            $"{browser} has an older ImmersiveSBS extension ({version}). Reload it: open edge://extensions (or chrome://extensions), " +
            "press the reload button on ImmersiveSBS link, then reload the video's tab."));
        link.Changed += () => dispatcher.BeginInvoke(() =>
        {
            if (browserLink == null) return;
            // A theater opened or closed: at once, not at the next tick.
            UpdateBrowserVideo(session);
            if (browserLink.Connections.Count == browserCount) return;
            browserCount = browserLink.Connections.Count;
            browserRefused.RemoveWhere(r => browserLink.Connections.All(c => c.Number != r.Connection));
            Notify();
        });
    }

    /// <summary>
    /// Keeps the page's delayed sound in step with what the session asks for. Sound is delayed
    /// only inside the page, by the browser extension: the app never captures, replays or turns
    /// down any program's sound. The session learns whether the page can delay the theater
    /// video's sound (protected and cross-origin media cannot: no buffer then, and matched timing
    /// within lip sync with the sound live); while it asks for a delay, the page plays the sound
    /// that late, and the session is told how late it plays now, so the picture follows exactly.
    /// </summary>
    private void UpdateAudioDelay()
    {
        Session? s = session;
        UpdateBrowserVideo(s);

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

        // Through Web Audio the sound is heard its context's output latency later than the page's
        // own playback, which the picture keeps time with (50 ms in the 2026-10-12 run): the delay
        // asked for is that much shorter, so sound and picture are as late as each other before
        // the user's trim. In 5 ms steps, so a latency wandering by a millisecond moves nothing.
        double latency = Math.Round((video!.Media?.Latency ?? 0) / 5.0) * 5 / 1000.0;
        double trim = Settings.AudioOffsetMs / 1000.0 - latency;
        double requested = Math.Max(0.001, Math.Round(wanted + trim, 3));
        if (!(Math.Abs(requested - delayRequests.Now.Seconds) <= 0.0005)) delayRequests = ((requested, trim), delayRequests.Now);
        browserTrim = trim;
        // The theater shows the picture as late as asked, by its audio clock, which counts the
        // output's latency: so without the trim.
        StartBrowserDelay(video.Connection, video.Theater.Tab, requested, wanted);
        // How late it is heard now, without the trim (0 until the video is taken into the delay,
        // which happens once it plays): the depth model looks that far ahead. Until the page
        // reports the new delay, the one it reports went with the trim asked for before.
        double delay = video.Media?.Delay ?? 0;
        double applied = Math.Abs(delay - delayRequests.Before.Seconds) < 0.0005 && Math.Abs(delay - requested) >= 0.0005
            ? delayRequests.Before.Trim
            : trim;
        s!.SoundDelay = delay > 0 ? Math.Max(0.001, Math.Round(delay - applied, 3)) : 0;
        LogBrowserDelay(video);
    }

    /// <summary>
    /// Follows the extension's 3D theater, the only way browser video reaches AI 3D: its page
    /// reports which video, where its player shows it and whether that is the whole screen; the
    /// newest one counts, should two browsers have one open. The session gets its place on the
    /// virtual monitor, its frames, state and the boxes of its controls and captions.
    /// </summary>
    private void UpdateBrowserVideo(Session? s)
    {
        BrowserVideo? found = null;
        if (browserLink is { Connections.Count: > 0 } link && !shuttingDown
            && link.Connections.Where(c => c.Theater != null).MaxBy(c => c.Theater!.Since) is { } c)
        {
            TheaterReport t = c.Theater!;
            found = new BrowserVideo(c, t, c.Media.FirstOrDefault(m => m.Id == t.MediaId));
        }

        BrowserVideo? was = browserVideo;
        if (found?.Connection != was?.Connection || found?.Theater.MediaId != was?.Theater.MediaId)
        {
            Log.Write(found == null
                ? "Browser: no theater video."
                : $"Browser: the theater shows a {found.Media?.VideoWidth ?? 0}×{found.Media?.VideoHeight ?? 0} video in {found.Connection}.");
            offScreenTheater = null;
        }
        browserVideo = found;
        SetTheater(found != null);

        if (s == null) return;
        if (found == null || s.Windowed || VirtualMonitor is not { } vm || found.Theater.Picture is not { } picture)
        {
            s.BrowserVideo = null;
            return;
        }
        // Its place on the screen is known only while its viewport is the screen, and the screen
        // must be the virtual monitor (the glasses show nothing else).
        string? notShown = !found.Theater.OnScreen
            ? $"its page says its viewport is not the screen ({found.Theater.View})"
            : FullScreenOn(vm.Bounds, out string window) ? null
            : $"no window covers the virtual monitor {vm.Bounds.Width}×{vm.Bounds.Height} at ({vm.Bounds.X}, {vm.Bounds.Y}): {window}";
        if (notShown != null)
        {
            string key = found.Theater.MediaId + "|" + notShown;
            if (offScreenTheater != key)
            {
                offScreenTheater = key;
                Log.Write($"Browser: the theater is not full screen on the virtual monitor yet ({notShown}); its video is shown once it is.");
            }
            s.BrowserVideo = null;
            return;
        }
        if (offScreenTheater != null)
        {
            offScreenTheater = null;
            Log.Write($"Browser: the theater is full screen on the virtual monitor ({found.Theater.View}).");
        }

        PageVideo? page = found.Connection.Followed;
        Rectangle local = found.Theater.ToScreen(picture);
        var overlays = new List<Rectangle>();
        var captions = new List<Rectangle>();
        foreach (PageBox box in found.Connection.Overlays)
            (box.IsCaption ? captions : overlays).Add(found.Theater.ToScreen(box.Box));
        s.BrowserVideo = new VideoHint(local, page?.Paused != true)
        {
            Page = page,
            Overlays = overlays,
            Captions = captions,
        };
        LogFollowed(found, page, overlays.Count, captions.Count);
    }

    /// <summary>Whether a window fills <paramref name="monitor"/> (the browser, full screen there), not this app's.</summary>
    /// <param name="found">For the log: the window at the monitor's centre (its program and place).</param>
    private static bool FullScreenOn(Rectangle monitor, out string found)
    {
        IntPtr window = Native.RootWindowAt(new System.Drawing.Point(monitor.X + monitor.Width / 2, monitor.Y + monitor.Height / 2));
        if (window == IntPtr.Zero || !Native.GetWindowRect(window, out Native.RECT r))
        {
            found = "no window at its centre";
            return false;
        }
        found = $"{Native.ProcessName(window)} at ({r.Left}, {r.Top}) to ({r.Right}, {r.Bottom}) at its centre";
        return r.Left <= monitor.Left && r.Top <= monitor.Top && r.Right >= monitor.Right && r.Bottom >= monitor.Bottom;
    }

    /// <summary>Every 10 s while the theater is open: what the page tells (the evidence from a run).</summary>
    private void LogFollowed(BrowserVideo video, PageVideo? page, int overlays, int captions)
    {
        long now = Environment.TickCount64;
        if (page == null || now - followLogAt < 10000) return;
        followLogAt = now;
        bool fresh = page.Fresh(Stopwatch.GetTimestamp());
        Log.Write($"Browser: the theater in {video.Connection}: " + (fresh
            ? $"{(page.Paused ? "paused" : "playing")}, {page.FrameRate:0.##} fps, clock within {page.ClockErrorMs:0.0#} ms, " +
              $"{overlays} boxes over it and {captions} captions."
            : "no frame or state messages for over a second."));
    }

    /// <summary>
    /// Whether the page can delay this video's sound and that of everything else audible in its
    /// tab. Media it can never delay (protected, from another site without CORS) are remembered,
    /// so the choice does not flip back and forth. Audio not allowed to start yet ("blocked", no
    /// click in the page) is not: it passes, and remembered it turned the buffer off for good
    /// when a resuming context was taken for it (2026-10-19).
    /// </summary>
    private bool BrowserCanDelay(BrowserVideo video)
    {
        foreach (BrowserMedia m in video.Connection.Media)
        {
            if (m.Tab != video.Theater.Tab || (m.Id != video.Theater.MediaId && !(m.Playing && m.Audible))) continue;
            var key = (video.Connection.Number, m.Id);
            if (!m.Delayable && m.Reason == "blocked") return false;
            if (!m.Delayable && browserRefused.Add(key))
                Log.Write($"Browser: {video.Connection} cannot delay a {m.Kind}'s sound in the page ({m.Reason}); no buffer for it, matched timing within 90 ms with the sound live.");
            if (browserRefused.Contains(key)) return false;
        }
        return true;
    }

    /// <summary>
    /// Has the browser play the theater video's tab <paramref name="seconds"/> late, every other
    /// tab live, and the theater show its picture <paramref name="picture"/> late.
    /// </summary>
    private void StartBrowserDelay(BrowserConnection connection, int tab, double seconds, double picture)
    {
        if (browserDelaying != connection)
        {
            StopBrowserDelay();
            browserDelaying = connection;
            browserLogAt = 0;
            Notify();
        }
        browserLink!.SetDelay(connection, seconds, tab, picture);
    }

    private void StopBrowserDelay()
    {
        if (browserDelaying == null) return;
        browserLink?.SetDelay(browserDelaying, 0, -1);
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
        Log.Write($"Audio: {video.Connection} plays the page's sound {video.Media?.Delay ?? 0:0.00#} s late " +
                  $"(asked {video.Connection.RequestedDelay:0.00#} s; {late} of {media.Count} media elements delayed; " +
                  $"video {(video.Media?.Playing == true ? "playing" : "paused")}; Web Audio output latency {video.Media?.Latency?.ToString() ?? "-"} ms).");
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
        Log.Write("App: " + describe(s) + ".");
        ApplySettings(s);
        tuningWindow?.Reload();
    }

    #region Theater

    /// <summary>What the browsers were last told (<see cref="UpdateStatus"/>).</summary>
    private (bool Glasses, TheaterPanel? Panel, double Stretch, Native3DMode Layout, System.Drawing.Size? Picture) sentStatus;

    /// <summary>
    /// The timing choices, from no delay to the longest (the theater panel steps through them by
    /// their short names, the settings window lists them): live, auto, matched
    /// (improvement-plan.md §6), then the buffer.
    /// </summary>
    public static readonly (string Label, string Short, VideoTiming Timing, double Buffer)[] TimingChoices =
    [
        ("Live", "Live", VideoTiming.Live, 0),
        ("Auto: matched when depth is slow", "Auto", VideoTiming.Auto, 0),
        ("Matched: video just late enough for its depth", "Matched", VideoTiming.Matched, 0),
        ("Buffer 0.5 s", "Buffer 0.5 s", VideoTiming.Auto, 0.5),
        ("Buffer 1 s", "Buffer 1 s", VideoTiming.Auto, 1),
        ("Buffer 2 s", "Buffer 2 s", VideoTiming.Auto, 2),
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
    public static int TimingIndex(AppSettings s) => TimingIndex(s.Timing, s.BufferSeconds);

    /// <summary>Which of <see cref="TimingChoices"/> a timing and buffer make (the nearest buffer).</summary>
    public static int TimingIndex(VideoTiming timing, double bufferSeconds)
    {
        if (bufferSeconds <= 0) return Array.FindIndex(TimingChoices, c => c.Buffer == 0 && c.Timing == timing) is var i and >= 0 ? i : 1;
        int best = 3;
        for (int k = 3; k < TimingChoices.Length; k++)
            if (Math.Abs(TimingChoices[k].Buffer - bufferSeconds) < Math.Abs(TimingChoices[best].Buffer - bufferSeconds)) best = k;
        return best;
    }

    /// <summary>Applies one of <see cref="TimingChoices"/> to <paramref name="s"/>; a buffer keeps the timing chosen for without one.</summary>
    public static void SetTiming(AppSettings s, int index)
    {
        var choice = TimingChoices[Math.Clamp(index, 0, TimingChoices.Length - 1)];
        s.BufferSeconds = choice.Buffer;
        if (choice.Buffer == 0) s.Timing = choice.Timing;
    }

    /// <summary>
    /// The extension's 3D theater is open: its player is full screen in the page
    /// (the extension draws the button, the player and its controls), and the app shows it in 3D
    /// filling both eyes, AI 3D on or not.
    /// </summary>
    public bool Theater { get; private set; }

    /// <summary>Follows the page's theater (<see cref="UpdateBrowserVideo"/>).</summary>
    private void SetTheater(bool on)
    {
        if (Theater == on) return;
        Theater = on;
        Log.Write(on ? "App: theater on (the extension's player)." : "App: theater off.");
        // The theater turns AI 3D on for its video (and the model loads), so re-evaluate fully.
        Evaluate(on ? "theater on" : "theater off", force: true);
    }

    /// <summary>
    /// Opens the extension's theater on the largest playing video of the tab in front, or closes
    /// it (the tray): without a click in the page it fills the browser window, which goes full screen.
    /// </summary>
    public void ToggleTheater()
    {
        if (!BrowserConnected)
        {
            tray?.Notify("The 3D theater is in the ImmersiveSBS browser extension, which is not connected: load it from the app's folder (Settings).");
            return;
        }
        browserLink!.RequestTheater(!Theater);
    }

    /// <summary>
    /// A button of the theater's 3D panel, in the page (UI thread). The new values go back to the
    /// page with the next status.
    /// </summary>
    private void OnPageCommand(string name)
    {
        switch (name)
        {
            case "depthLess": Execute(ShortcutCommand.LessDepth); break;
            case "depthMore": Execute(ShortcutCommand.MoreDepth); break;
            case "planeBack": Execute(ShortcutCommand.PictureBack); break;
            case "planeForward": Execute(ShortcutCommand.PictureForward); break;
            case "modelPrevious" or "modelNext":
                Adjust(s => s.DepthTier = NextModel(s, name == "modelNext" ? 1 : -1), s => "Model " + DepthModel.Name(s.DepthTier));
                break;
            case "sizePrevious" or "sizeNext":
                Adjust(s => s.DepthPreset = (DepthPreset)Math.Clamp((int)s.DepthPreset + (name == "sizeNext" ? 1 : -1), 0,
                    Enum.GetValues<DepthPreset>().Length - 1), s => $"Resolution {s.DepthPreset}");
                break;
            case "timingLess" or "timingMore":
                Adjust(s => SetTiming(s, NextTiming(s, name == "timingMore" ? 1 : -1)), s => "Timing: " + TimingLabel(TimingIndex(s)));
                break;
            case "stretchLess" or "stretchMore":
                // The player draws the stretch; 1 to 1.5 in steps of 0.05.
                Adjust(s => s.VideoStretch = Math.Round(Math.Clamp(s.VideoStretch + (name == "stretchMore" ? 0.05 : -0.05), 1, 1.5), 2),
                    s => $"Stretch ×{s.VideoStretch:0.00}");
                break;
            case "layoutNext":
                // What the video is: 2D (converted), side-by-side or top-bottom 3D (shown as is).
                Adjust(s => s.Native3D = s.Native3D switch
                {
                    Native3DMode.Off => Native3DMode.ForceSideBySide,
                    Native3DMode.ForceSideBySide => Native3DMode.ForceTopBottom,
                    _ => Native3DMode.Off,
                }, s => "Video " + s.Native3D);
                break;
            default:
                Log.Write($"Browser: unknown command from the theater: {name}.");
                break;
        }
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
    /// Tells the browsers what their theater needs (UI thread, every tick; sent only on a
    /// change): whether the glasses show the virtual monitor in 3D (the button shows only then),
    /// the 3D panel's values, and the depth model's input size, at which the theater sends its
    /// frames (none while the model is not ready, or the video is shown as the 3D it already is).
    /// </summary>
    private void UpdateStatus()
    {
        if (browserLink == null) return;
        bool glasses = session is { Windowed: false } && VirtualMonitor != null && !shuttingDown;
        TheaterPanel? panel = session?.Panel();
        System.Drawing.Size? picture = glasses && Settings.Native3D == Native3DMode.Off
                                       && session?.Depth is { State: DepthServiceState.Ready, Model: var model }
            ? new System.Drawing.Size(model.Width, model.Height)
            : null;
        var status = (glasses, panel, Settings.VideoStretch, Settings.Native3D, picture);
        if (status == sentStatus) return;
        sentStatus = status;
        object? settings = panel == null ? null : new
        {
            model = panel.Model,
            size = panel.Size,
            depth = Math.Round(panel.Depth, 2),
            plane = Math.Round(panel.Plane, 2),
            timing = panel.Timing,
            stretch = Math.Round(Settings.VideoStretch, 2),
            layout = Settings.Native3D switch
            {
                Native3DMode.ForceSideBySide => "sideBySide",
                Native3DMode.ForceTopBottom => "topBottom",
                _ => "2d",
            },
        };
        browserLink.SetStatus(glasses, settings, picture);
    }

    #endregion

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
        }
        Evaluate("window toggled", force: true);
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
        else if (VirtualMonitor != null && !windowDismissed
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
            StatusText += Theater ? "; 3D theater" : "; AI 3D whole screen";

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
            Settings.BufferSeconds, Settings.Tuning, Theater, Settings.Timing);
        if (s.Stereo != stereo) s.Stereo = stereo;
        if (s.Depth != depthService) s.Depth = depthService;
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

        if (active)
        {
            if (cursorGuard == null) cursorGuard = new CursorGuard(Glasses!.Bounds);
            else cursorGuard.Blocked = Glasses!.Bounds;
        }
        else if (cursorGuard != null)
        {
            cursorGuard.Dispose();
            cursorGuard = null;
        }

        if (active)
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
                (host != null ? $"\n\nOpen windows will be moved to {host.Identity}." : "") +
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
        if (VirtualMonitor != null && host != null)
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
