using System.Diagnostics;
using System.Drawing;
using System.Windows.Threading;
using ImmersiveSBS.Interop;

namespace ImmersiveSBS.Input;

/// <summary>
/// Moves windows that end up on the glasses onto the virtual monitor while the glasses show it
/// (plan D2 Stage A): new windows Windows places there, windows moved there with Win+Shift+Arrow,
/// and windows Windows parks there after a layout change. Out-of-context WinEvent hooks on the
/// UI thread report shows and moves; a full sweep runs on start and on every layout change.
/// </summary>
public sealed class StrayWindowGuard : IDisposable
{
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const int OBJID_WINDOW = 0;

    /// <summary>A window that keeps coming back is left alone after this many moves a minute.</summary>
    private const int MaxMovesPerMinute = 3;

    /// <summary>
    /// Shell surfaces that exist once per monitor or belong where Windows put them: taskbars,
    /// the desktop, Task View (XamlExplorerHostIslandWindow, one per monitor), the Display
    /// settings "identify" numbers. Moving them breaks them.
    /// </summary>
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "ImmersiveSBS.Presenter",
        "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "ForegroundStaging",
        "MonitorNumberWindow", "Windows.UI.Core.CoreWindow", "TaskListThumbnailWnd",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    /// <summary>
    /// Shell processes whose windows are overlays (Start, search, notifications, Task View).
    /// Explorer also owns File Explorer windows, which are ordinary and do get moved.
    /// </summary>
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
        "TextInputHost", "LockApp", "ShellHost",
    };

    private const string FileExplorerClass = "CabinetWClass";

    private readonly Dictionary<uint, string> processNames = [];

    private readonly Dispatcher dispatcher;
    private readonly Native.WinEventProc proc;
    private readonly List<IntPtr> hooks = [];
    private readonly HashSet<IntPtr> pending = [];
    private readonly Dictionary<IntPtr, (int Count, long Since)> moves = [];
    private readonly uint ownProcess = (uint)Environment.ProcessId;
    private Rectangle glasses;
    private Rectangle target;

    /// <param name="glasses">The glasses' desktop bounds.</param>
    /// <param name="target">The work area windows are moved into (the virtual monitor's).</param>
    /// <param name="watch">False for a single sweep without hooks.</param>
    public StrayWindowGuard(Rectangle glasses, Rectangle target, bool watch = true)
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        this.glasses = glasses;
        this.target = target;
        proc = OnWinEvent;

        foreach ((uint min, uint max) in watch ? new[]
                 {
                     (EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND),
                     (EVENT_SYSTEM_MINIMIZEEND, EVENT_SYSTEM_MINIMIZEEND),
                     (EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW),
                     (EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE),
                 } : [])
        {
            IntPtr h = Native.SetWinEventHook(min, max, IntPtr.Zero, proc, 0, 0,
                Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            if (h == IntPtr.Zero) Log.Write($"StrayWindowGuard: SetWinEventHook 0x{min:X4} failed.");
            else hooks.Add(h);
        }

        Sweep();
    }

    /// <summary>
    /// Moves every window from one display to another work area once, e.g. off the virtual
    /// monitor when the app exits and nothing will show it any more.
    /// </summary>
    public static void Evacuate(Rectangle from, Rectangle toWorkArea)
    {
        using var sweep = new StrayWindowGuard(from, toWorkArea, watch: false);
    }

    /// <summary>Updates the geometry after a layout change and sweeps again.</summary>
    public void Update(Rectangle glasses, Rectangle target)
    {
        this.glasses = glasses;
        this.target = target;
        Sweep();
    }

    /// <summary>Checks every top-level window.</summary>
    public void Sweep()
    {
        var windows = new List<IntPtr>();
        Native.EnumWindows((hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        foreach (IntPtr hwnd in windows) Check(hwnd);
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;

        // Mid-drag the user is still placing the window; MOVESIZEEND follows.
        if (evt == EVENT_OBJECT_LOCATIONCHANGE && Native.GetAsyncKeyState(Native.VK_LBUTTON) < 0) return;

        if (pending.Add(hwnd))
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                pending.Remove(hwnd);
                Check(hwnd);
            });
        }
    }

    private void Check(IntPtr hwnd)
    {
        if (!IsCandidate(hwnd) || !Native.GetWindowRect(hwnd, out Native.RECT r)) return;

        var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        if (!glasses.Contains(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2)) return;

        long now = Stopwatch.GetTimestamp();
        if (moves.TryGetValue(hwnd, out var m) && now - m.Since < 60 * Stopwatch.Frequency)
        {
            if (m.Count >= MaxMovesPerMinute) return;
            moves[hwnd] = (m.Count + 1, m.Since);
        }
        else
        {
            moves[hwnd] = (1, now);
        }

        Move(hwnd, rect);
    }

    private bool IsCandidate(IntPtr hwnd)
    {
        if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return false;
        if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return false;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == ownProcess) return false;

        // Cloaked: on another virtual desktop, or a suspended UWP frame.
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        string cls = Native.GetClassName(hwnd);
        if (ExcludedClasses.Contains(cls)) return false;
        return cls == FileExplorerClass || !ShellProcesses.Contains(ProcessName(pid));
    }

    private string ProcessName(uint pid)
    {
        if (!processNames.TryGetValue(pid, out string? name))
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                name = p.ProcessName;
            }
            catch (Exception)
            {
                name = string.Empty;
            }
            // Process ids are reused, but a stale entry only costs one wrong decision per window.
            if (processNames.Count > 512) processNames.Clear();
            processNames[pid] = name;
        }
        return name;
    }

    private void Move(IntPtr hwnd, Rectangle rect)
    {
        bool maximized = Native.IsZoomed(hwnd);
        if (maximized)
        {
            // A maximized window moves by restoring, moving and maximizing again on the target.
            Native.ShowWindow(hwnd, Native.SW_RESTORE);
            if (Native.GetWindowRect(hwnd, out Native.RECT r)) rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        // Same offset from the top-left as on the glasses, clamped into the target.
        int w = Math.Min(rect.Width, target.Width);
        int h = Math.Min(rect.Height, target.Height);
        int x = Math.Clamp(target.Left + (rect.Left - glasses.Left), target.Left, target.Right - w);
        int y = Math.Clamp(target.Top + (rect.Top - glasses.Top), target.Top, target.Bottom - h);

        bool ok = Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        if (maximized) Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);

        // Moving an elevated program's window fails from a normal process (UIPI). The program and
        // window class only: a window's title can name what the user watches or reads.
        Log.Write($"StrayWindowGuard: {(ok ? "moved" : "could not move")} a {Native.ProcessName(hwnd)} window " +
                  $"({Native.GetClassName(hwnd)}) from {rect} to {x},{y}{(maximized ? ", maximized" : "")}.");
    }

    public void Dispose()
    {
        foreach (IntPtr h in hooks) Native.UnhookWinEvent(h);
        hooks.Clear();
    }
}
