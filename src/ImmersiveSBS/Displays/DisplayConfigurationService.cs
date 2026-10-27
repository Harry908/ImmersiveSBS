// Ported from DesktopSbS: DesktopSbS/Model/DisplayConfigurationService.cs
using System.Drawing;
using System.Runtime.InteropServices;
using ImmersiveSBS.Interop;
using SWF = System.Windows.Forms;

namespace ImmersiveSBS.Displays;

/// <summary>
/// Enumerates the attached displays with a stable identity for each one.
///
/// Read-only by design. This class never calls SetDisplayConfig and never changes a display
/// mode: switching the glasses between 2D and 3D is the user's job, and the app only reports
/// what Windows currently says (the one change, the virtual monitor off the desktop while the
/// app is closed, is <see cref="VirtualMonitorSwitch"/>'s).
/// </summary>
public static class DisplayConfigurationService
{
    /// <summary>
    /// Windows can report an active path whose target is not available yet for a short window
    /// during a hot-plug, so enumeration is retried a few times before its answer is final.
    /// </summary>
    private const int DefaultRetryCount = 3;
    private const int RetryDelayMs = 120;

    /// <summary>
    /// Set on DISPLAYCONFIG_PATH_INFO.flags when the mode index fields are split into two
    /// 16-bit halves rather than being a single index.
    /// </summary>
    private const uint DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE = 0x00000008;

    private const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;
    private const ushort DISPLAYCONFIG_PATH_SPLIT_IDX_INVALID = 0xFFFF;

    /// <summary>
    /// Enumerates every active display: from the CCD API when it succeeds, otherwise from
    /// Screen + EnumDisplaySettings, so callers always get something usable.
    /// </summary>
    /// <param name="retryWhileTargetUnavailable">
    /// Retry while any path reports targetAvailable == false. Worth doing after a display
    /// change notification, not worth doing on a settings dialog.
    /// </param>
    public static List<DisplayInfo> GetDisplays(bool retryWhileTargetUnavailable = true)
    {
        int attempts = retryWhileTargetUnavailable ? DefaultRetryCount : 1;
        List<DisplayInfo>? result = null;

        for (int attempt = 0; attempt < attempts; ++attempt)
        {
            result = TryGetDisplaysFromDisplayConfig();
            if (result == null || result.Count == 0) break;
            if (result.All(d => d.TargetAvailable)) return result;
            if (attempt < attempts - 1) Thread.Sleep(RetryDelayMs);
        }

        return result is { Count: > 0 } ? result : GetDisplaysFromScreens();
    }

    /// <summary>Only displays that are part of the desktop and can host windows.</summary>
    public static List<DisplayInfo> GetDesktopDisplays(bool retryWhileTargetUnavailable = true) =>
        GetDisplays(retryWhileTargetUnavailable).Where(d => d.HasDesktopBounds).ToList();

    /// <summary>
    /// Picks the attached display that best matches a saved identity: device path first, then
    /// EDID, then GDI name, then geometry. Null when nothing matches at all.
    /// </summary>
    public static DisplayInfo? ResolveBestMatch(IEnumerable<DisplayInfo> displays, DisplayIdentity? saved,
        out DisplayMatchStrength strength)
    {
        strength = DisplayMatchStrength.None;
        if (saved == null) return null;

        DisplayInfo? best = null;
        foreach (DisplayInfo display in displays)
        {
            DisplayMatchStrength candidate = display.Identity.MatchAgainst(saved);
            if (candidate == DisplayMatchStrength.None
                && !saved.LastKnownBounds.IsEmpty
                && display.Bounds == saved.LastKnownBounds)
            {
                candidate = DisplayMatchStrength.Geometry;
            }

            if (candidate > strength)
            {
                strength = candidate;
                best = display;
            }
        }

        return best;
    }

    /// <summary>Resolves a saved identity against the current desktop displays.</summary>
    public static DisplayInfo? Resolve(DisplayIdentity? saved, bool retryWhileTargetUnavailable = true) =>
        ResolveBestMatch(GetDesktopDisplays(retryWhileTargetUnavailable), saved, out _);

    /// <summary>
    /// A string that changes exactly when the layout changes: which displays, their desktop
    /// mode, bounds, working area, DPI and availability. Refresh rate is left out on purpose.
    /// Order-independent.
    /// </summary>
    public static string GetSignature(IEnumerable<DisplayInfo> displays) =>
        string.Join(";", displays
            .Select(d => string.Join("|",
                d.Identity.DevicePath,
                d.Identity.GdiDeviceName,
                d.Mode.Width + "x" + d.Mode.Height,
                d.Bounds.ToString(),
                d.WorkingArea.ToString(),
                d.DpiX.ToString(),
                d.TargetAvailable ? "1" : "0"))
            .OrderBy(x => x, StringComparer.Ordinal));

    #region CCD enumeration

    private static List<DisplayInfo>? TryGetDisplaysFromDisplayConfig()
    {
        try
        {
            if (!TryQuery(DisplayConfig.QDC_ONLY_ACTIVE_PATHS, out var paths, out var modes)) return null;

            HashSet<string> hmdTargets = GetHeadMountedTargetKeys(paths);
            Dictionary<string, SWF.Screen> screensByDevice = GetScreensByDeviceName();

            var result = new List<DisplayInfo>(paths.Length);
            foreach (DisplayConfig.DISPLAYCONFIG_PATH_INFO path in paths)
            {
                result.Add(BuildDisplayInfo(path, modes, hmdTargets, screensByDevice));
            }
            return result;
        }
        catch (Exception e)
        {
            // A missing export or a bad struct layout must not take the app down; the
            // Screen-based fallback still produces usable geometry.
            Log.Write("DisplayConfigurationService: CCD query failed: " + e.Message);
            return null;
        }
    }

    internal static bool TryQuery(uint flags,
        out DisplayConfig.DISPLAYCONFIG_PATH_INFO[] paths,
        out DisplayConfig.DISPLAYCONFIG_MODE_INFO[] modes)
    {
        paths = [];
        modes = [];

        // The configuration can change between sizing and querying, in which case
        // QueryDisplayConfig returns ERROR_INSUFFICIENT_BUFFER and the pair has to be redone.
        for (int attempt = 0; attempt < 5; ++attempt)
        {
            if (DisplayConfig.GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount) != DisplayConfig.ERROR_SUCCESS)
                return false;
            if (pathCount == 0) return true;

            var pathBuffer = new DisplayConfig.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modeBuffer = new DisplayConfig.DISPLAYCONFIG_MODE_INFO[modeCount];

            int queryResult = DisplayConfig.QueryDisplayConfig(flags, ref pathCount, pathBuffer, ref modeCount, modeBuffer, IntPtr.Zero);
            if (queryResult == DisplayConfig.ERROR_SUCCESS)
            {
                // The counts come back as the number actually written.
                paths = pathBuffer.Take((int)pathCount).ToArray();
                modes = modeBuffer.Take((int)modeCount).ToArray();
                return true;
            }

            if (queryResult != DisplayConfig.ERROR_INSUFFICIENT_BUFFER) return false;
        }

        return false;
    }

    /// <summary>
    /// Targets that appear only when QDC_INCLUDE_HMD is set are head-mounted displays.
    /// Returns their adapter+target keys.
    /// </summary>
    private static HashSet<string> GetHeadMountedTargetKeys(DisplayConfig.DISPLAYCONFIG_PATH_INFO[] normalPaths)
    {
        var hmd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TryQuery(DisplayConfig.QDC_ONLY_ACTIVE_PATHS | DisplayConfig.QDC_INCLUDE_HMD, out var withHmd, out _))
            return hmd;

        var normal = new HashSet<string>(normalPaths.Select(p => TargetKey(p.targetInfo.adapterId, p.targetInfo.id)),
            StringComparer.OrdinalIgnoreCase);

        foreach (DisplayConfig.DISPLAYCONFIG_PATH_INFO path in withHmd)
        {
            string key = TargetKey(path.targetInfo.adapterId, path.targetInfo.id);
            if (!normal.Contains(key)) hmd.Add(key);
        }

        return hmd;
    }

    private static string TargetKey(DisplayConfig.LUID adapterId, uint targetId) => adapterId + ":" + targetId;

    private static DisplayInfo BuildDisplayInfo(
        DisplayConfig.DISPLAYCONFIG_PATH_INFO path,
        DisplayConfig.DISPLAYCONFIG_MODE_INFO[] modes,
        HashSet<string> hmdTargets,
        Dictionary<string, SWF.Screen> screensByDevice)
    {
        DisplayIdentity identity = TargetIdentity(path, out string outputTechnology);

        var sourceName = new DisplayConfig.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
        sourceName.header.type = DisplayConfig.DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_SOURCE_NAME;
        sourceName.header.size = (uint)Marshal.SizeOf<DisplayConfig.DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
        sourceName.header.adapterId = path.sourceInfo.adapterId;
        sourceName.header.id = path.sourceInfo.id;

        if (DisplayConfig.DisplayConfigGetDeviceInfo(ref sourceName) == DisplayConfig.ERROR_SUCCESS)
            identity.GdiDeviceName = Clean(sourceName.viewGdiDeviceName);

        if (string.IsNullOrEmpty(identity.FriendlyName))
            identity.FriendlyName = FallbackName(identity, path.targetInfo.outputTechnology);

        var info = new DisplayInfo
        {
            Identity = identity,
            Mode = BuildMode(path, modes),
            TargetAvailable = path.targetInfo.targetAvailable,
            IsHeadMounted = hmdTargets.Contains(TargetKey(path.targetInfo.adapterId, path.targetInfo.id)),
            OutputTechnology = outputTechnology,
            AdapterId = path.targetInfo.adapterId.ToString(),
            TargetId = path.targetInfo.id,
            SourceId = path.sourceInfo.id,
            FromDisplayConfig = true,
        };

        // Bounds and working area come from Screen: the coordinate space windows live in.
        if (!string.IsNullOrEmpty(identity.GdiDeviceName)
            && screensByDevice.TryGetValue(identity.GdiDeviceName, out SWF.Screen? screen))
        {
            ApplyScreen(info, screen);
        }

        return info;
    }

    /// <summary>The monitor at a path's target: its device path, EDID and name (no GDI name: that is the source's).</summary>
    internal static DisplayIdentity TargetIdentity(DisplayConfig.DISPLAYCONFIG_PATH_INFO path, out string outputTechnology)
    {
        var identity = new DisplayIdentity();

        var targetName = new DisplayConfig.DISPLAYCONFIG_TARGET_DEVICE_NAME();
        targetName.header.type = DisplayConfig.DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_TARGET_NAME;
        targetName.header.size = (uint)Marshal.SizeOf<DisplayConfig.DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        targetName.header.adapterId = path.targetInfo.adapterId;
        targetName.header.id = path.targetInfo.id;

        outputTechnology = path.targetInfo.outputTechnology.ToString();

        if (DisplayConfig.DisplayConfigGetDeviceInfo(ref targetName) == DisplayConfig.ERROR_SUCCESS)
        {
            identity.DevicePath = Clean(targetName.monitorDevicePath);
            identity.FriendlyName = Clean(targetName.monitorFriendlyDeviceName);
            identity.EdidValid = targetName.EdidIdsValid;
            identity.EdidManufacturer = DisplayConfig.DecodeEdidManufacturer(targetName.edidManufactureId);
            identity.EdidProductCode = targetName.edidProductCodeId;
            outputTechnology = targetName.outputTechnology.ToString();
        }

        return identity;
    }

    /// <summary>
    /// Built-in laptop panels usually report no EDID friendly name. Names are presentation
    /// only: matching never uses them.
    /// </summary>
    private static string FallbackName(DisplayIdentity identity, DisplayConfig.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY technology)
    {
        switch (technology)
        {
            case DisplayConfig.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.INTERNAL:
            case DisplayConfig.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYPORT_EMBEDDED:
            case DisplayConfig.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.UDI_EMBEDDED:
            case DisplayConfig.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.LVDS:
                return "Built-in display";
        }

        return !string.IsNullOrEmpty(identity.EdidManufacturer) ? identity.EdidManufacturer + " display" : identity.GdiDeviceName;
    }

    private static DisplayMode BuildMode(DisplayConfig.DISPLAYCONFIG_PATH_INFO path, DisplayConfig.DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var mode = new DisplayMode
        {
            RefreshRate = path.targetInfo.refreshRate.ToHz(),
            Interlaced = path.targetInfo.scanLineOrdering is
                DisplayConfig.DISPLAYCONFIG_SCANLINE_ORDERING.INTERLACED_UPPERFIELDFIRST or
                DisplayConfig.DISPLAYCONFIG_SCANLINE_ORDERING.INTERLACED_LOWERFIELDFIRST,
        };

        bool virtualMode = (path.flags & DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE) != 0;
        int sourceIdx = ResolveModeIndex(path.sourceInfo.modeInfoIdx, virtualMode);
        int targetIdx = ResolveModeIndex(path.targetInfo.modeInfoIdx, virtualMode);

        if (TryGetMode(modes, sourceIdx, DisplayConfig.DISPLAYCONFIG_MODE_INFO_TYPE.SOURCE,
                path.sourceInfo.adapterId, path.sourceInfo.id, out var sourceMode))
        {
            mode.Width = (int)sourceMode.sourceMode.width;
            mode.Height = (int)sourceMode.sourceMode.height;
        }

        if (TryGetMode(modes, targetIdx, DisplayConfig.DISPLAYCONFIG_MODE_INFO_TYPE.TARGET,
                path.targetInfo.adapterId, path.targetInfo.id, out var targetMode))
        {
            DisplayConfig.DISPLAYCONFIG_VIDEO_SIGNAL_INFO signal = targetMode.targetMode.targetVideoSignalInfo;
            mode.SignalWidth = (int)signal.activeSize.cx;
            mode.SignalHeight = (int)signal.activeSize.cy;

            // The target mode carries the precise vertical sync rate; the path's is rounded.
            double vsync = signal.vSyncFreq.ToHz();
            if (vsync > 0) mode.RefreshRate = vsync;
        }

        return mode;
    }

    /// <summary>
    /// Unpacks a mode index. When the path supports virtual modes the field is two 16-bit
    /// indices; both the source and the target index sit in the high half of their field.
    /// </summary>
    private static int ResolveModeIndex(uint modeInfoIdx, bool virtualMode)
    {
        if (!virtualMode) return modeInfoIdx == DISPLAYCONFIG_PATH_MODE_IDX_INVALID ? -1 : (int)modeInfoIdx;

        ushort packed = (ushort)((modeInfoIdx >> 16) & 0xFFFF);
        return packed == DISPLAYCONFIG_PATH_SPLIT_IDX_INVALID ? -1 : packed;
    }

    /// <summary>
    /// Looks a mode up by index, validating that the entry really is the one the path refers
    /// to, and scanning the array if the index does not hold up.
    /// </summary>
    private static bool TryGetMode(DisplayConfig.DISPLAYCONFIG_MODE_INFO[] modes, int index,
        DisplayConfig.DISPLAYCONFIG_MODE_INFO_TYPE type, DisplayConfig.LUID adapterId, uint id,
        out DisplayConfig.DISPLAYCONFIG_MODE_INFO mode)
    {
        if (index >= 0 && index < modes.Length && Matches(modes[index]))
        {
            mode = modes[index];
            return true;
        }

        foreach (DisplayConfig.DISPLAYCONFIG_MODE_INFO candidate in modes)
        {
            if (Matches(candidate))
            {
                mode = candidate;
                return true;
            }
        }

        mode = default;
        return false;

        bool Matches(DisplayConfig.DISPLAYCONFIG_MODE_INFO m) =>
            m.infoType == type && m.adapterId.LowPart == adapterId.LowPart && m.adapterId.HighPart == adapterId.HighPart && m.id == id;
    }

    #endregion

    #region Screen fallback

    /// <summary>
    /// Used when the CCD query fails outright. The result has geometry and a mode but no
    /// stable identity, so it is flagged FromDisplayConfig = false.
    /// </summary>
    private static List<DisplayInfo> GetDisplaysFromScreens()
    {
        var result = new List<DisplayInfo>();

        foreach (SWF.Screen screen in SWF.Screen.AllScreens)
        {
            string deviceName = Clean(screen.DeviceName);
            var info = new DisplayInfo
            {
                Identity = new DisplayIdentity { GdiDeviceName = deviceName, FriendlyName = deviceName },
                Mode = new DisplayMode { Width = screen.Bounds.Width, Height = screen.Bounds.Height },
                TargetAvailable = true,
                OutputTechnology = "unknown",
                FromDisplayConfig = false,
            };

            ApplyScreen(info, screen);
            result.Add(info);
        }

        return result;
    }

    private static Dictionary<string, SWF.Screen> GetScreensByDeviceName()
    {
        var map = new Dictionary<string, SWF.Screen>(StringComparer.OrdinalIgnoreCase);
        foreach (SWF.Screen screen in SWF.Screen.AllScreens)
        {
            string name = Clean(screen.DeviceName);
            if (!string.IsNullOrEmpty(name)) map[name] = screen;
        }
        return map;
    }

    private static void ApplyScreen(DisplayInfo info, SWF.Screen screen)
    {
        info.Bounds = screen.Bounds;
        info.WorkingArea = screen.WorkingArea;
        info.IsPrimary = screen.Primary;

        if (string.IsNullOrEmpty(info.Identity.GdiDeviceName))
            info.Identity.GdiDeviceName = Clean(screen.DeviceName);

        // Effective DPI of the monitor; 96 when shcore cannot tell.
        IntPtr monitor = Native.MonitorFromPoint(
            new Point(screen.Bounds.Left + screen.Bounds.Width / 2, screen.Bounds.Top + screen.Bounds.Height / 2),
            Native.MONITOR_DEFAULTTONULL);
        info.DpiX = monitor != IntPtr.Zero && Native.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 ? dpiX : 96;
    }

    #endregion

    /// <summary>Fixed-size interop strings come back padded with NULs.</summary>
    private static string Clean(string? value) => string.IsNullOrEmpty(value) ? string.Empty : value.TrimEnd('\0').Trim();
}
