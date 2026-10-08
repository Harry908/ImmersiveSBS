// Ported from DesktopSbS: DesktopSbS/Model/DisplayChangeMonitor.cs
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ImmersiveSBS.Displays;

/// <summary>
/// Listens for everything that can change the display layout and raises one debounced
/// <see cref="Changed"/> event per burst, on the UI thread.
///
/// Owns a hidden top-level window: a message-only window (HWND_MESSAGE) does not receive
/// broadcasts such as WM_DISPLAYCHANGE.
///
/// Triggers are deliberately generous (DBT_DEVNODES_CHANGED fires for any USB device). That is
/// cheap because the consumer compares a display signature first.
/// </summary>
public sealed class DisplayChangeMonitor : IDisposable
{
    #region Win32

    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_DEVICECHANGE = 0x0219;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int WM_DPICHANGED = 0x02E0;

    private const int SPI_SETWORKAREA = 0x002F;
    private const int SPI_SETLOGICALDPI = 0x009E;

    private const int DBT_DEVNODES_CHANGED = 0x0007;
    private const int DBT_CONFIGCHANGED = 0x0018;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    private const int DBT_DEVTYP_DEVICEINTERFACE = 5;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>GUID_DEVINTERFACE_MONITOR: arrival/removal of a monitor.</summary>
    private static readonly Guid GUID_DEVINTERFACE_MONITOR = new("E6F07B5F-EE97-4A90-B076-33F57BF4EAA7");

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public short dbcc_name;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr notificationFilter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    #endregion

    /// <summary>Quiet period before a burst of notifications is reported.</summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Delay before a follow-up check. Windows can report a new layout in several steps (a path
    /// first, its target a little later), so a change is re-checked until two readings agree.
    /// </summary>
    private static readonly TimeSpan FollowUpDelay = TimeSpan.FromMilliseconds(1500);

    private const int MaxFollowUps = 3;

    /// <summary>Safety net: some changes reach no window of ours.</summary>
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(4);

    private readonly HwndSource source;
    private readonly DispatcherTimer debounceTimer;
    private readonly DispatcherTimer followUpTimer;
    private readonly DispatcherTimer pollTimer;
    private readonly List<string> pendingReasons = [];
    private IntPtr monitorNotification;
    private int followUpCount;

    /// <summary>Raised on the UI thread once a burst has settled; the argument says why.</summary>
    public event Action<string>? Changed;

    public DisplayChangeMonitor()
    {
        source = new HwndSource(new HwndSourceParameters("ImmersiveSBS.DisplayChangeMonitor")
        {
            Width = 0,
            Height = 0,
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
            ParentWindow = IntPtr.Zero,
        });
        source.AddHook(WndProc);

        debounceTimer = new DispatcherTimer { Interval = DebounceDelay };
        debounceTimer.Tick += (_, _) =>
        {
            debounceTimer.Stop();
            string reasons = string.Join(", ", pendingReasons);
            pendingReasons.Clear();
            Raise(reasons);
        };

        followUpTimer = new DispatcherTimer { Interval = FollowUpDelay };
        followUpTimer.Tick += (_, _) =>
        {
            followUpTimer.Stop();
            Raise("follow-up check " + followUpCount + "/" + MaxFollowUps);
        };

        RegisterForMonitorNotifications();

        pollTimer = new DispatcherTimer { Interval = PollDelay };
        pollTimer.Tick += (_, _) => Raise("periodic check");
        pollTimer.Start();
    }

    /// <summary>
    /// Asks for one more check shortly, because the layout just changed and Windows may not
    /// have settled. Bounded, so a layout that keeps reading differently cannot loop forever.
    /// </summary>
    public void RequestFollowUp()
    {
        if (followUpCount >= MaxFollowUps) return;

        followUpCount++;
        followUpTimer.Stop();
        followUpTimer.Start();
    }

    private void RegisterForMonitorNotifications()
    {
        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_classguid = GUID_DEVINTERFACE_MONITOR,
        };

        IntPtr buffer = Marshal.AllocHGlobal(filter.dbcc_size);
        try
        {
            Marshal.StructureToPtr(filter, buffer, false);
            monitorNotification = RegisterDeviceNotification(source.Handle, buffer, DEVICE_NOTIFY_WINDOW_HANDLE);
            if (monitorNotification == IntPtr.Zero)
            {
                // Not fatal: WM_DISPLAYCHANGE and DBT_DEVNODES_CHANGED still arrive.
                Log.Write("DisplayChangeMonitor: RegisterDeviceNotification failed, error " + Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_DISPLAYCHANGE:
                Trigger("WM_DISPLAYCHANGE " + LowWord(lParam) + "x" + HighWord(lParam));
                break;

            case WM_DEVICECHANGE:
                switch ((int)wParam)
                {
                    case DBT_DEVNODES_CHANGED: Trigger("DBT_DEVNODES_CHANGED"); break;
                    case DBT_CONFIGCHANGED: Trigger("DBT_CONFIGCHANGED"); break;
                    case DBT_DEVICEARRIVAL: Trigger("monitor arrival"); break;
                    case DBT_DEVICEREMOVECOMPLETE: Trigger("monitor removal"); break;
                }
                break;

            case WM_DPICHANGED:
                Trigger("WM_DPICHANGED " + LowWord(wParam) + " dpi");
                break;

            case WM_SETTINGCHANGE:
                // The taskbar moved or resized: working areas changed.
                if ((int)wParam == SPI_SETWORKAREA) Trigger("SPI_SETWORKAREA");
                else if ((int)wParam == SPI_SETLOGICALDPI) Trigger("SPI_SETLOGICALDPI");
                else if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "WindowMetrics") Trigger("WindowMetrics");
                break;

            case WM_POWERBROADCAST:
                int evt = (int)wParam;
                if (evt is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND) Trigger("resume from sleep");
                break;
        }

        return IntPtr.Zero;
    }

    private void Trigger(string reason)
    {
        if (!pendingReasons.Contains(reason)) pendingReasons.Add(reason);

        // A real OS event starts a new confirmation cycle.
        followUpCount = 0;
        followUpTimer.Stop();

        debounceTimer.Stop();
        debounceTimer.Start();
    }

    private void Raise(string reasons) => Changed?.Invoke(reasons);

    private static int LowWord(IntPtr value) => unchecked((int)(value.ToInt64() & 0xFFFF));

    private static int HighWord(IntPtr value) => unchecked((int)((value.ToInt64() >> 16) & 0xFFFF));

    public void Dispose()
    {
        debounceTimer.Stop();
        followUpTimer.Stop();
        pollTimer.Stop();

        if (monitorNotification != IntPtr.Zero)
        {
            UnregisterDeviceNotification(monitorNotification);
            monitorNotification = IntPtr.Zero;
        }

        source.RemoveHook(WndProc);
        source.Dispose();
    }
}
