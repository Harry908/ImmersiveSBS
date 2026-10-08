// Ported from DesktopSbS: DesktopSbS/Hook/GlobalKeyboardHook.cs
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace ImmersiveSBS.Input;

/// <summary>
/// System-wide keyboard shortcuts through a low-level keyboard hook.
///
/// The hook lives on its own thread, which does nothing but pump messages. Windows calls a
/// low-level hook through the installing thread's message loop and waits for it before the
/// keystroke reaches any application; on a busy UI thread that delayed every keystroke
/// system-wide, and past LowLevelHooksTimeout Windows removes the hook silently.
///
/// Modifiers are read with GetAsyncKeyState, which works whichever window has focus.
/// </summary>
public sealed class GlobalKeyboardHook : IDisposable
{
    #region Win32

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr hInstance, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    #endregion

    /// <summary>
    /// Called on the hook thread for every key press, with the modifiers held at that moment.
    /// Return true to consume the keystroke. Must return quickly: marshal real work elsewhere.
    /// </summary>
    public Func<ModifierKeys, Key, bool>? KeyPressed;

    private readonly LowLevelKeyboardProc proc;
    private readonly Thread thread;
    private volatile uint threadId;
    private IntPtr hook;

    /// <summary>
    /// Virtual key whose press was consumed; its release is consumed too, so the application
    /// never sees a key-up without the key-down.
    /// </summary>
    private int swallowedKey = -1;

    public GlobalKeyboardHook()
    {
        proc = HookProc; // kept alive for the lifetime of the hook
        using var started = new ManualResetEvent(false);
        thread = new Thread(() => Run(started))
        {
            IsBackground = true,
            Name = "Keyboard hook",
            // Every keystroke on the machine waits for this thread.
            Priority = ThreadPriority.Highest,
        };
        thread.Start();
        started.WaitOne();
    }

    private void Run(ManualResetEvent started)
    {
        threadId = GetCurrentThreadId();
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
            Log.Write("GlobalKeyboardHook: SetWindowsHookEx failed, error " + Marshal.GetLastPInvokeError());
        started.Set();

        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
        {
            // Nothing to dispatch: the loop only lets Windows call the hook.
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
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    Func<ModifierKeys, Key, bool>? handler = KeyPressed;
                    if (handler != null && handler(CurrentModifiers(), KeyInterop.KeyFromVirtualKey(data.vkCode)))
                    {
                        swallowedKey = data.vkCode;
                        return 1;
                    }
                }
                else if (message is WM_KEYUP or WM_SYSKEYUP && data.vkCode == swallowedKey)
                {
                    swallowedKey = -1;
                    return 1;
                }
            }
            catch (Exception e)
            {
                // Never let an exception escape into the input system.
                Log.Write("GlobalKeyboardHook: " + e.Message);
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    private static ModifierKeys CurrentModifiers()
    {
        var modifiers = ModifierKeys.None;
        if (IsDown(VK_CONTROL)) modifiers |= ModifierKeys.Control;
        if (IsDown(VK_MENU)) modifiers |= ModifierKeys.Alt;
        if (IsDown(VK_SHIFT)) modifiers |= ModifierKeys.Shift;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) modifiers |= ModifierKeys.Windows;
        return modifiers;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (threadId != 0)
        {
            PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            threadId = 0;
        }
    }
}
