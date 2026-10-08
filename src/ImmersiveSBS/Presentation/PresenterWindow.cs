using System.Drawing;
using System.Runtime.InteropServices;
using ImmersiveSBS.Interop;

namespace ImmersiveSBS.Presentation;

/// <summary>
/// The borderless window that covers the glasses (plan D2 Stage A). Topmost, never activated,
/// no taskbar button, and it hides the real cursor if it strays onto the glasses. Created and
/// pumped on the render thread, so the swap chain and its window share one thread.
/// </summary>
public static unsafe class PresenterWindow
{
    private const string ClassName = "ImmersiveSBS.Presenter";
    private static bool registered;
    private static int displayChanges;

    /// <summary>Counts WM_DISPLAYCHANGE (2D/3D switch, layout change) since start.</summary>
    public static int DisplayChanges => Volatile.Read(ref displayChanges);

    public static IntPtr Create(Rectangle target)
    {
        if (!registered)
        {
            fixed (char* name = ClassName)
            {
                var wc = new Native.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = Native.GetModuleHandleW(null),
                    lpszClassName = name,
                };
                if (Native.RegisterClassExW(wc) == 0)
                    throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");
            }
            registered = true;
        }

        IntPtr hwnd = Native.CreateWindowExW(
            Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
            ClassName, "ImmersiveSBS", Native.WS_POPUP,
            target.Left, target.Top, target.Width, target.Height,
            IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");

        Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);
        return hwnd;
    }

    public static void Move(IntPtr hwnd, Rectangle target) =>
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, target.Left, target.Top, target.Width, target.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

    /// <summary>Dispatches every queued message of the calling thread.</summary>
    public static void Pump()
    {
        while (Native.PeekMessageW(out Native.MSG msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
        {
            Native.TranslateMessage(msg);
            Native.DispatchMessageW(msg);
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_DISPLAYCHANGE:
                Interlocked.Increment(ref displayChanges);
                break;
            case Native.WM_MOUSEACTIVATE:
                return Native.MA_NOACTIVATE;
            case Native.WM_SETCURSOR:
                Native.SetCursor(IntPtr.Zero);
                return 1;
        }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
