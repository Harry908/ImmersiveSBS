using System.Drawing;
using System.Runtime.InteropServices;
using ImmersiveSBS.Interop;

namespace ImmersiveSBS.Presentation;

/// <summary>
/// An ordinary, resizable window on a physical monitor that shows the virtual monitor while it
/// is not on the glasses, so nothing on it is ever out of reach. The cursor is drawn in it: the
/// user moves the real mouse onto the virtual monitor and watches this window. Closing it only
/// asks the session to end. Created and pumped on the render thread, like the glasses window.
/// </summary>
public static unsafe class MirrorWindow
{
    private const string ClassName = "ImmersiveSBS.Mirror";
    private static bool registered;
    private static int resizes;
    private static int closeRequests;

    /// <summary>Counts WM_SIZE since start; the swap chain follows the client size.</summary>
    public static int Resizes => Volatile.Read(ref resizes);

    /// <summary>Counts close requests (title bar X, Alt+F4) since start.</summary>
    public static int CloseRequests => Volatile.Read(ref closeRequests);

    public static IntPtr Create(Rectangle bounds)
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
                    hCursor = Native.LoadCursorW(IntPtr.Zero, Native.IDC_ARROW),
                    lpszClassName = name,
                };
                if (Native.RegisterClassExW(wc) == 0)
                    throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");
            }
            registered = true;
        }

        IntPtr hwnd = Native.CreateWindowExW(0, ClassName,
            "ImmersiveSBS: virtual monitor (move the mouse onto it to use it)", Native.WS_OVERLAPPEDWINDOW,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");

        // Not activated: it appears while the user is busy elsewhere (pause, glasses unplugged).
        Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);
        return hwnd;
    }

    public static Size ClientSize(IntPtr hwnd)
    {
        Native.GetClientRect(hwnd, out Native.RECT r);
        return new Size(Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_SIZE:
                Interlocked.Increment(ref resizes);
                break;
            case Native.WM_CLOSE:
                // The session destroys the window when it ends.
                Interlocked.Increment(ref closeRequests);
                return 0;
        }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
