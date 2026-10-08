using System.Runtime.InteropServices;

namespace ImmersiveSBS.Interop;

/// <summary>The Win32 calls the prototype needs. Physical pixels throughout (per-monitor v2).</summary>
internal static unsafe partial class Native
{
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_NOACTIVATE = 0x08000000;

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_HOTKEY = 0x0312;
    public const int MA_NOACTIVATE = 3;

    public const int SW_SHOWNOACTIVATE = 4;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint PM_REMOVE = 0x0001;
    public const uint WAIT_TIMEOUT = 0x102;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_F11 = 0x7A;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAY_DEVICEW
    {
        public int cb;
        public fixed char DeviceName[32];
        public fixed char DeviceString[128];
        public uint StateFlags;
        public fixed char DeviceID[128];
        public fixed char DeviceKey[128];
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial ushort RegisterClassExW(in WNDCLASSEXW wc);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in MSG msg);

    [LibraryImport("user32.dll")]
    public static partial IntPtr DispatchMessageW(in MSG msg);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetCursor(IntPtr cursor);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayDevicesW(string? device, uint index, ref DISPLAY_DEVICEW dd, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hwnd, int id);

    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_CLOSE = 0x0010;
    public static readonly IntPtr IDC_ARROW = new(32512);

    [LibraryImport("user32.dll")]
    public static partial IntPtr LoadCursorW(IntPtr instance, IntPtr name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr hwnd, out RECT rect);

    public const uint MONITOR_DEFAULTTONULL = 0;
    public const uint GA_ROOT = 2;
    public const int SW_MAXIMIZE = 3;
    public const int SW_RESTORE = 9;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const int VK_LBUTTON = 0x01;
    public const int DWMWA_CLOAKED = 14;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static partial IntPtr MonitorFromPointNative(POINT pt, uint flags);

    public static IntPtr MonitorFromPoint(System.Drawing.Point pt, uint flags) =>
        MonitorFromPointNative(new POINT { X = pt.X, Y = pt.Y }, flags);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPosNative(out POINT point);

    public static bool GetCursorPos(out System.Drawing.Point point)
    {
        bool ok = GetCursorPosNative(out POINT p);
        point = new System.Drawing.Point(p.X, p.Y);
        return ok;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static partial int GetClassNameW(IntPtr hwnd, char* buffer, int max);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static partial int GetWindowTextW(IntPtr hwnd, char* buffer, int max);

    [LibraryImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static partial IntPtr WindowFromPointNative(POINT point);

    /// <summary>The process owning the top-level window at a desktop point; 0 for none.</summary>
    public static uint ProcessAt(System.Drawing.Point point)
    {
        IntPtr hwnd = WindowFromPointNative(new POINT { X = point.X, Y = point.Y });
        if (hwnd == IntPtr.Zero) return 0;
        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        GetWindowThreadProcessId(root != IntPtr.Zero ? root : hwnd, out uint pid);
        return pid;
    }

    public static string GetClassName(IntPtr hwnd)
    {
        char* buffer = stackalloc char[256];
        int n = GetClassNameW(hwnd, buffer, 256);
        return n > 0 ? new string(buffer, 0, n) : string.Empty;
    }

    public static string GetWindowText(IntPtr hwnd)
    {
        char* buffer = stackalloc char[256];
        int n = GetWindowTextW(hwnd, buffer, 256);
        return n > 0 ? new string(buffer, 0, n) : string.Empty;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandleW(string? name);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObjectEx(IntPtr handle, uint milliseconds, [MarshalAs(UnmanagedType.Bool)] bool alertable);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("d3d11.dll")]
    public static partial int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int WindowsCreateString(string source, uint length, out IntPtr hstring);

    [LibraryImport("combase.dll")]
    public static partial int WindowsDeleteString(IntPtr hstring);

    [LibraryImport("combase.dll")]
    public static partial int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    /// <summary>Calls a COM method by vtable slot (IUnknown occupies slots 0-2).</summary>
    public static int CallSlot(IntPtr obj, int slot, IntPtr arg, in Guid iid, out IntPtr result)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(*(void***)obj + slot));
        IntPtr r;
        fixed (Guid* pIid = &iid)
        {
            int hr = fn(obj, arg, pIid, &r);
            result = r;
            return hr;
        }
    }

    /// <summary>Calls a COM method of shape HRESULT M(REFIID, void**) by vtable slot.</summary>
    public static int CallSlot(IntPtr obj, int slot, in Guid iid, out IntPtr result)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(*(void***)obj + slot));
        IntPtr r;
        fixed (Guid* pIid = &iid)
        {
            int hr = fn(obj, pIid, &r);
            result = r;
            return hr;
        }
    }
}
