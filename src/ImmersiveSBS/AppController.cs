using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ImmersiveSBS.Audio;
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
        BuildShortcuts();
        tray = new TrayIcon(this);
        keyboard = new GlobalKeyboardHook { KeyPressed = OnKey };
        displayMonitor = new DisplayChangeMonitor();
        displayMonitor.Changed += reason => Evaluate(reason, force: false);
        // A previous run that crashed while delaying sound left the app's sessions turned down.
        SessionAttenuator.RecoverAfterCrash();
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
    /// A video lost for less than this (the tracker finding it again, the model reloading) keeps
    /// its delayed sound and picture running: stopping meant the sound jumped ahead and then went
    /// silent for the whole delay, and the picture froze as long, on every such moment.
    /// </summary>
    private const long DelayGraceMs = 5000;

    private readonly DispatcherTimer audioTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private AudioDelay? audioDelay;
    private bool audioStarting;
    private uint audioFailedProcess;
    private long audioFailedAt;
    private long delayWantedAt = long.MinValue / 2;

    private BrowserLink? browserLink;
    private int browserCount;
    /// <summary>The browser video under the found region, followed from tick to tick.</summary>
    private BrowserVideo? browserVideo;
    /// <summary>The browser whose pages play their sound late now, or null.</summary>
    private BrowserConnection? browserDelaying;
    /// <summary>Media (connection, id) the extension cannot delay; their sound is captured instead.</summary>
    private readonly HashSet<(int Connection, string Id)> browserRefused = [];
    private long browserLogAt;

    /// <param name="Window">The browser's top-level window.</param>
    /// <param name="Screen">Where the picture is on the desktop, px.</param>
    private sealed record BrowserVideo(BrowserConnection Connection, BrowserMedia Media, IntPtr Window, Rectangle Screen);

    /// <summary>"Sound 1 s behind" while the delayed sound runs; null otherwise.</summary>
    public string? AudioStatus => browserDelaying != null
        ? $"sound {Settings.BufferSeconds:0.#} s behind (in the browser)"
        : audioDelay == null ? null : $"sound {audioDelay.Delay.TotalSeconds:0.#} s behind";

    /// <summary>Where the extension is, to load it unpacked: copied next to the app.</summary>
    public static string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension");

    public bool BrowserConnected => browserLink is { Connections.Count: > 0 };

    /// <summary>The browser extension's state, for the settings window.</summary>
    public string BrowserStatus =>
        browserLink == null ? "The browser extension cannot connect: another program uses its port (see the log)."
        : browserLink.Connections.Count == 0 ? "Browser extension not connected. It delays a video's sound inside the page, at full quality."
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
    /// Keeps the delayed sound in step with what the session asks for: while it reports a video
    /// region to delay, that video's sound is played late, and only once it is does the session
    /// delay the picture too. A browser with the extension delays the sound inside the page; any
    /// other app (or media the page cannot delay) has its sound captured and played late here.
    /// </summary>
    private void UpdateAudioDelay()
    {
        Session? s = session;
        UpdateBrowserVideo(s);

        bool on = s is { Windowed: false } && Settings.BufferSeconds > 0 && VirtualMonitor != null && !shuttingDown;
        Rectangle region = on ? s!.DelayRegion : Rectangle.Empty;
        long now = Environment.TickCount64;
        if (!region.IsEmpty)
        {
            delayWantedAt = now;
        }
        else if (on && (audioDelay != null || browserDelaying != null) && now - delayWantedAt < DelayGraceMs)
        {
            return; // lost for a moment: everything runs on as it is
        }
        else
        {
            audioFailedProcess = 0;
            StopBrowserDelay();
            StopAudioDelay();
            if (s != null) s.AudioDelayed = false;
            return;
        }

        if (browserVideo is { } video && BrowserCanDelay(video))
        {
            StopAudioDelay();
            double seconds = Math.Round(Settings.BufferSeconds + Settings.AudioOffsetMs / 1000.0, 3);
            StartBrowserDelay(video.Connection, seconds);
            // The picture waits until the page says this video's sound is that late (it is taken
            // into the delay once it plays).
            s!.AudioDelayed = Math.Abs(video.Media.Delay - seconds) < 0.002;
            LogBrowserDelay(video);
            return;
        }
        StopBrowserDelay();

        var center = new System.Drawing.Point(VirtualMonitor!.Bounds.X + region.X + region.Width / 2,
                               VirtualMonitor.Bounds.Y + region.Y + region.Height / 2);
        uint process = Native.ProcessAt(center);
        if (process == (uint)Environment.ProcessId) process = 0;
        if (process == 0)
        {
            audioFailedProcess = 0;
            StopAudioDelay();
            s!.AudioDelayed = false;
            return;
        }

        var delay = TimeSpan.FromSeconds(Settings.BufferSeconds);
        if (audioDelay != null && audioDelay.ProcessId == process && audioDelay.Delay == delay && !audioDelay.NeedsRestart)
        {
            audioDelay.Offset = TimeSpan.FromMilliseconds(Settings.AudioOffsetMs);
            s!.AudioDelayed = true;
            return;
        }
        if (audioStarting || (process == audioFailedProcess && Environment.TickCount64 - audioFailedAt < 5000)) return;

        StopAudioDelay();
        s!.AudioDelayed = false;
        _ = StartAudioDelayAsync(process, delay);
    }

    private async Task StartAudioDelayAsync(uint process, TimeSpan delay)
    {
        audioStarting = true;
        try
        {
            AudioDelay started = await AudioDelay.StartAsync(process, delay);
            // The page took over meanwhile: two delays would play the sound twice.
            if (shuttingDown || browserDelaying != null)
            {
                started.Dispose();
                return;
            }
            started.Offset = TimeSpan.FromMilliseconds(Settings.AudioOffsetMs);
            audioDelay = started;
            Notify();
        }
        catch (Exception e)
        {
            // Retried for this app after a few seconds (an output may be coming back).
            audioFailedProcess = process;
            audioFailedAt = Environment.TickCount64;
            Log.Write($"Audio: could not delay process {process}'s sound: {e.Message}");
        }
        finally
        {
            audioStarting = false;
        }
    }

    private void StopAudioDelay()
    {
        if (audioDelay == null) return;
        if (session != null) session.AudioDelayed = false;
        audioDelay.Dispose();
        audioDelay = null;
        Notify();
    }

    /// <summary>
    /// Follows the video the extension reports under the session's found region: the same element
    /// from tick to tick while it stays on top in its window, else the one whose picture best
    /// covers the region. The session gets its exact place; a seek clears the depth history.
    /// </summary>
    private void UpdateBrowserVideo(Session? s)
    {
        BrowserVideo? found = null;
        Rectangle region = Rectangle.Empty;
        if (browserLink is { Connections.Count: > 0 } link && s is { Windowed: false } && VirtualMonitor is { } vm && !shuttingDown)
        {
            region = s.VideoRegion;
            if (!region.IsEmpty) region.Offset(vm.Bounds.Location);
            if (browserVideo is { } previous) found = FollowBrowserVideo(link, previous, region);
            if (found == null && !region.IsEmpty) found = FindBrowserVideo(link, region);
        }

        BrowserVideo? was = browserVideo;
        if (found?.Connection != was?.Connection || found?.Media.Id != was?.Media.Id)
        {
            Log.Write(found == null
                ? "Browser: no longer following a video in the browser."
                : $"Browser: following a {found.Media.VideoWidth}×{found.Media.VideoHeight} video in {found.Connection}, " +
                  $"at {found.Screen} on the desktop (found at {region}).");
        }
        else if (found != null && found.Media.Seeks != was!.Media.Seeks)
        {
            // A jump in the video: depth smoothed over the old frames would smear into the new.
            depthService?.ResetHistory();
        }
        browserVideo = found;

        if (s == null) return;
        if (found == null || VirtualMonitor is not { } monitor)
        {
            s.BrowserVideo = null;
            return;
        }
        Rectangle local = found.Screen;
        local.Offset(-monitor.Bounds.X, -monitor.Bounds.Y);
        s.BrowserVideo = new VideoHint(local, found.Media.Playing);
    }

    private static System.Drawing.Point Center(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    /// <summary>The followed video again, if it is still there, visible and on top where it was seen.</summary>
    private static BrowserVideo? FollowBrowserVideo(BrowserLink link, BrowserVideo previous, Rectangle region)
    {
        if (!link.Connections.Contains(previous.Connection) || !Native.IsWindow(previous.Window) || Native.IsIconic(previous.Window))
            return null;
        BrowserMedia? media = previous.Connection.Media.FirstOrDefault(m => m.Id == previous.Media.Id);
        if (media is not { Visible: true } || media.ScreenRect(Native.ClientBounds(previous.Window)) is not Rectangle screen) return null;
        if (Native.RootWindowAt(Center(screen)) != previous.Window) return null;
        // The tracker found something else: the user moved on to another video.
        if (!region.IsEmpty && BrowserMedia.Overlap(screen, region) < 0.3) return null;
        return previous with { Media = media, Screen = screen };
    }

    /// <summary>The reported video whose picture covers <paramref name="region"/> (desktop px) in the window there.</summary>
    private static BrowserVideo? FindBrowserVideo(BrowserLink link, Rectangle region)
    {
        IntPtr window = Native.RootWindowAt(Center(region));
        if (window == IntPtr.Zero) return null;
        Rectangle client = Native.ClientBounds(window);
        if (client.IsEmpty) return null;
        var media = link.Connections.SelectMany(c => c.Media.Select(m => (c, m)));
        return BrowserMedia.Match(media, client, region) is var (connection, m, screen)
            ? new BrowserVideo(connection, m, window, screen)
            : null;
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
                Log.Write($"Browser: {video.Connection} cannot delay a {m.Kind}'s sound in the page ({m.Reason}); capturing the browser's sound instead.");
            if (browserRefused.Contains(key)) return false;
        }
        return true;
    }

    private void StartBrowserDelay(BrowserConnection connection, double seconds)
    {
        if (browserDelaying != connection)
        {
            StopBrowserDelay();
            browserDelaying = connection;
            browserLogAt = 0;
            Notify();
        }
        browserLink!.SetDelay(connection, seconds);
    }

    private void StopBrowserDelay()
    {
        if (browserDelaying == null) return;
        browserLink?.SetDelay(browserDelaying, 0);
        Log.Write($"Browser: {browserDelaying} plays the sound live again.");
        browserDelaying = null;
        if (session != null) session.AudioDelayed = false;
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
                // The cursor is on the virtual monitor while the user works there; elsewhere this
                // only releases a lock.
                if (session is { Windowed: false } s && VirtualMonitor != null)
                {
                    Native.GetCursorPos(out System.Drawing.Point cursor);
                    Rectangle vm = VirtualMonitor.Bounds;
                    s.RequestPick(vm.Contains(cursor) ? new System.Drawing.Point(cursor.X - vm.X, cursor.Y - vm.Y) : null);
                }
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
    private long theaterWithoutVideoSince;
    private bool cursorConfined;

    /// <summary>The buffer steps the theater panel goes through, seconds.</summary>
    private static readonly double[] BufferSteps = [0, 0.5, 1, 2];

    /// <summary>The found video fills both eyes in the glasses, the desktop hidden.</summary>
    public bool Theater { get; private set; }

    /// <summary>Turns theater mode on or off (tray, on-screen buttons).</summary>
    public void SetTheater(bool on)
    {
        if (Theater == on) return;
        Theater = on;
        theaterWithoutVideoSince = 0;
        Log.Write(on ? "App: theater on." : "App: theater off.");
        // The theater turns AI 3D on for its video (and the model loads), so re-evaluate fully.
        Evaluate(on ? "theater on" : "theater off", force: true);
    }

    /// <summary>Hook thread → UI thread: an on-screen button was clicked in the glasses.</summary>
    private void OnTheaterClicked(TheaterAction action) => dispatcher.BeginInvoke(() =>
    {
        switch (action)
        {
            case TheaterAction.EnterTheater: SetTheater(true); break;
            case TheaterAction.ExitTheater: SetTheater(false); break;
            case TheaterAction.PlayPause:
                // The followed video itself; else the media key, which the browser gives to the video it chooses.
                if (browserVideo is { } video) browserLink!.TogglePlay(video.Connection, video.Media.Id);
                else Native.PressMediaPlayPause();
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
                Adjust(s =>
                {
                    int i = Array.FindIndex(BufferSteps, v => v >= s.BufferSeconds - 0.01);
                    i = Math.Clamp((i < 0 ? BufferSteps.Length - 1 : i) + (action == TheaterAction.BufferMore ? 1 : -1), 0, BufferSteps.Length - 1);
                    s.BufferSeconds = BufferSteps[i];
                }, s => s.BufferSeconds > 0 ? $"Buffer {s.BufferSeconds:0.#} s" : "Buffer off");
                break;
            case TheaterAction.ToggleEdgeSnap:
                Adjust(s => s.Tuning = s.Tuning with { EdgeSnap = !s.Tuning.EdgeSnap }, s => "Edge snapping " + (s.Tuning.EdgeSnap ? "on" : "off"));
                break;
            case TheaterAction.ToggleCaptions:
                Adjust(s => s.Tuning = s.Tuning with { Captions = !s.Tuning.Captions }, s => "Flat subtitles " + (s.Tuning.Captions ? "on" : "off"));
                break;
        }
    });

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

    /// <summary>Sets the video buffer, seconds (0 = live).</summary>
    public void SetBuffer(double seconds)
    {
        if (Math.Abs(Settings.BufferSeconds - seconds) < 0.001) return;
        AppSettings s = Settings.Clone();
        s.BufferSeconds = seconds;
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
            Settings.VideoStretch, Settings.BufferSeconds, Settings.Tuning, Theater, Settings.OfferTheater);
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
        StopBrowserDelay();
        StopAudioDelay();
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

    /// <summary>Releases the hooks, the depth model, the delayed sound and the tray icon; safe to call twice.</summary>
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
        audioDelay?.Dispose();
        audioDelay = null;
        browserLink?.Dispose();
        browserLink = null;
        depthService?.Dispose();
        depthService = null;
        keyboard = null;
        tray?.Dispose();
        tray = null;
    }
}
