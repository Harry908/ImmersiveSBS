using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ImmersiveSBS.Audio;
using ImmersiveSBS.Capture;
using ImmersiveSBS.Depth;
using ImmersiveSBS.Displays;
using ImmersiveSBS.Input;
using ImmersiveSBS.Interop;
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
        audioTimer.Tick += (_, _) => UpdateAudioDelay();
        audioTimer.Start();
        Evaluate("start", force: true);
    }

    #region Delayed sound

    private readonly DispatcherTimer audioTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private AudioDelay? audioDelay;
    private bool audioStarting;
    private uint audioFailedProcess;

    /// <summary>"Sound 1 s behind" while the delayed sound runs; null otherwise.</summary>
    public string? AudioStatus => audioDelay == null ? null : $"sound {audioDelay.Delay.TotalSeconds:0.#} s behind";

    /// <summary>
    /// Keeps the delayed sound in step with what the session asks for: while it reports a video
    /// region to delay, the app under that region has its sound delayed, and only once that runs is
    /// the session told to delay the picture too.
    /// </summary>
    private void UpdateAudioDelay()
    {
        Session? s = session;
        Rectangle region = s?.DelayRegion ?? Rectangle.Empty;
        uint process = 0;
        if (s is { Windowed: false } && Settings.BufferSeconds > 0 && !region.IsEmpty && VirtualMonitor != null && !shuttingDown)
        {
            var center = new System.Drawing.Point(VirtualMonitor.Bounds.X + region.X + region.Width / 2,
                                   VirtualMonitor.Bounds.Y + region.Y + region.Height / 2);
            process = Native.ProcessAt(center);
            if (process == (uint)Environment.ProcessId) process = 0;
        }

        if (process == 0)
        {
            audioFailedProcess = 0;
            StopAudioDelay();
            return;
        }

        var delay = TimeSpan.FromSeconds(Settings.BufferSeconds);
        if (audioDelay != null && audioDelay.ProcessId == process && audioDelay.Delay == delay)
        {
            audioDelay.Offset = TimeSpan.FromMilliseconds(Settings.AudioOffsetMs);
            s!.AudioDelayed = true;
            return;
        }
        if (audioStarting || process == audioFailedProcess) return;

        StopAudioDelay();
        _ = StartAudioDelayAsync(process, delay);
    }

    private async Task StartAudioDelayAsync(uint process, TimeSpan delay)
    {
        audioStarting = true;
        try
        {
            AudioDelay started = await AudioDelay.StartAsync(process, delay);
            if (shuttingDown)
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
            // Not retried for this app until the video moves to another one or the buffer changes.
            audioFailedProcess = process;
            Log.Write($"Audio: could not delay process {process}'s sound: {e.Message}");
        }
        finally
        {
            audioStarting = false;
        }
    }

    private void StopAudioDelay()
    {
        if (session != null) session.AudioDelayed = false;
        if (audioDelay == null) return;
        audioDelay.Dispose();
        audioDelay = null;
        Notify();
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

    /// <summary>The depth model's state for the UI, or null while AI 3D is off.</summary>
    public string? DepthStatus => Settings.Ai3D == AiTarget.Off ? null : depthStatus;

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

        if (Settings.Ai3D != AiTarget.Off && desired is { MirrorBounds: null })
            StatusText += "; AI 3D " + (Settings.Ai3D == AiTarget.Video ? "video" : "whole screen");

        UpdateDepthService();
        _ = ReconcileAsync(desired);
        // A kept session follows the settled layout (e.g. a new main display rebases every monitor).
        if (desired is { MirrorBounds: null }) session?.MoveTo(Glasses!.Bounds);
        if (session != null) ApplyAi(session);
        UpdateGuards();
        Notify();
    }

    /// <summary>
    /// Loads the depth model while AI 3D is on (and swaps it when the tier or size changes),
    /// and frees its GPU memory when AI 3D is off. Shared by all sessions.
    /// </summary>
    private void UpdateDepthService()
    {
        DepthModel? wanted = Settings.Ai3D == AiTarget.Off ? null : new DepthModel(Settings.DepthTier, Settings.DepthPreset);
        if (depthService != null && depthService.Model == wanted) return;

        depthService?.Dispose();
        depthService = null;
        depthStatus = null;
        if (wanted == null) return;

        if (!wanted.Exists)
        {
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
        var stereo = new StereoSettings(Settings.Ai3D, Settings.Ai3DStrength, Settings.Ai3DConvergence, fov, Settings.Native3D,
            Settings.VideoStretch, Settings.BufferSeconds, Settings.Tuning);
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
        StopAudioDelay();
        if (session != null) await session.StopAsync();
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

    public void Dispose()
    {
        cursorGuard?.Dispose();
        cursorGuard = null;
        windowGuard?.Dispose();
        windowGuard = null;
        displayMonitor?.Dispose();
        displayMonitor = null;
        keyboard?.Dispose();
        audioDelay?.Dispose();
        audioDelay = null;
        depthService?.Dispose();
        depthService = null;
        keyboard = null;
        tray?.Dispose();
        tray = null;
    }
}
