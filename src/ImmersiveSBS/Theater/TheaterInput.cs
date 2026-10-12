using System.Drawing;
using System.Runtime.InteropServices;
using ImmersiveSBS.Interop;

namespace ImmersiveSBS.Theater;

/// <summary>
/// Clicks on the on-screen buttons. The buttons exist only in the glasses, so a click on one
/// lands on the virtual monitor underneath (usually the browser's video, which would pause). A
/// low-level mouse hook on its own thread (as <see cref="Input.GlobalKeyboardHook"/>) swallows
/// the press and release on a button and reports the action; every other click passes through.
/// </summary>
public sealed class TheaterInput : IDisposable
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
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
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

    private readonly TheaterButtons buttons;
    private readonly LowLevelMouseProc proc;
    private readonly Thread thread;
    private volatile uint threadId;
    private IntPtr hook;
    private TheaterAction? pressed;
    private Point pressedAt;

    /// <summary>Raised on the hook thread when a button is clicked, with where it was pressed (desktop px); marshal real work elsewhere.</summary>
    public event Action<TheaterAction, Point>? Clicked;

    public TheaterInput(TheaterButtons buttons)
    {
        this.buttons = buttons;
        proc = HookProc;
        using var started = new ManualResetEvent(false);
        thread = new Thread(() => Run(started)) { IsBackground = true, Name = "Theater input", Priority = ThreadPriority.Highest };
        thread.Start();
        started.WaitOne();
    }

    private void Run(ManualResetEvent started)
    {
        threadId = GetCurrentThreadId();
        hook = SetWindowsHookEx(WH_MOUSE_LL, proc, Native.GetModuleHandleW(null), 0);
        if (hook == IntPtr.Zero) Log.Write("TheaterInput: SetWindowsHookEx failed, error " + Marshal.GetLastPInvokeError());
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
        if (code >= 0)
        {
            try
            {
                int message = (int)wParam;
                if (message is WM_LBUTTONDOWN or WM_LBUTTONDBLCLK)
                {
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    if (buttons.Hit(data.pt) is TheaterAction action)
                    {
                        pressed = action;
                        pressedAt = data.pt;
                        return 1;
                    }
                }
                else if (message == WM_LBUTTONUP && pressed is TheaterAction action)
                {
                    // The release belongs to a swallowed press: swallow it too, then act.
                    pressed = null;
                    Clicked?.Invoke(action, pressedAt);
                    return 1;
                }
            }
            catch (Exception e)
            {
                Log.Write("TheaterInput: " + e.Message);
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
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
