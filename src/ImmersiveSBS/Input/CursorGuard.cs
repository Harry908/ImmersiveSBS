using System.Drawing;
using System.Runtime.InteropServices;
using ImmersiveSBS.Interop;

namespace ImmersiveSBS.Input;

/// <summary>
/// Keeps the mouse cursor off the glasses while they show the virtual monitor (plan D2
/// Stage A). A low-level mouse hook on its own thread (same reasoning as
/// <see cref="GlobalKeyboardHook"/>) drops any move that would land on the glasses and puts
/// the cursor at the nearest point just outside them instead, so it stops at the edge.
/// </summary>
public sealed class CursorGuard : IDisposable
{
    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public Point pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const uint WM_QUIT = 0x0012;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr hInstance, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Native.MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private readonly LowLevelMouseProc proc;
    private readonly Thread thread;
    private volatile uint threadId;
    private IntPtr hook;
    private readonly object blockedLock = new();
    private Rectangle blocked;

    /// <param name="blocked">The glasses' desktop bounds, in physical pixels.</param>
    public CursorGuard(Rectangle blocked)
    {
        this.blocked = blocked;
        proc = HookProc;
        using var started = new ManualResetEvent(false);
        thread = new Thread(() => Run(started))
        {
            IsBackground = true,
            Name = "Cursor guard",
            Priority = ThreadPriority.Highest,
        };
        thread.Start();
        started.WaitOne();

        // The cursor may already be on the glasses.
        if (Native.GetCursorPos(out Point p) && blocked.Contains(p) && NearestOutside(p) is Point q)
            Native.SetCursorPos(q.X, q.Y);
    }

    /// <summary>The glasses moved or changed size (2D/3D switch).</summary>
    public Rectangle Blocked
    {
        get { lock (blockedLock) return blocked; }
        set { lock (blockedLock) blocked = value; }
    }

    private void Run(ManualResetEvent started)
    {
        threadId = GetCurrentThreadId();
        hook = SetWindowsHookEx(WH_MOUSE_LL, proc, Native.GetModuleHandleW(null), 0);
        if (hook == IntPtr.Zero)
            Log.Write("CursorGuard: SetWindowsHookEx failed, error " + Marshal.GetLastPInvokeError());
        started.Set();

        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
        {
        }

        if (hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam == WM_MOUSEMOVE)
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                Rectangle r = Blocked;
                if (r.Contains(data.pt))
                {
                    // SetCursorPos raises one more (injected) move, which lands outside.
                    if (NearestOutside(data.pt) is Point q) Native.SetCursorPos(q.X, q.Y);
                    return 1;
                }
            }
            catch (Exception e)
            {
                Log.Write("CursorGuard: " + e.Message);
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    /// <summary>
    /// The point just outside the blocked rectangle closest to <paramref name="p"/> that lies on
    /// another monitor, or null when no edge of the glasses touches one.
    /// </summary>
    private Point? NearestOutside(Point p)
    {
        Rectangle r = Blocked;
        Point[] candidates =
        [
            new(r.Left - 1, p.Y),
            new(r.Right, p.Y),
            new(p.X, r.Top - 1),
            new(p.X, r.Bottom),
        ];

        Point? best = null;
        long bestDistance = long.MaxValue;
        foreach (Point c in candidates)
        {
            if (Native.MonitorFromPoint(c, Native.MONITOR_DEFAULTTONULL) == IntPtr.Zero) continue;
            long dx = c.X - p.X, dy = c.Y - p.Y;
            long d = dx * dx + dy * dy;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
            }
        }
        return best;
    }

    public void Dispose()
    {
        if (threadId != 0)
        {
            PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            threadId = 0;
        }
    }
}
