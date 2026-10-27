using ImmersiveSBS.Interop;

namespace ImmersiveSBS.Displays;

/// <summary>
/// Takes the virtual monitor off the desktop while the app is closed and puts it back when it
/// starts (<see cref="AppSettings.DisconnectVirtualMonitor"/>), as "Disconnect this display" in
/// Windows Settings does: with nothing showing it, windows, dialogs and the mouse went there out
/// of sight. The only change the app makes to the display layout, and only to the virtual
/// monitor's path; the others are applied as they are. The driver's own control cannot do it:
/// asked for no monitors (its pipe's SETDISPLAYCOUNT 0) it loads one (tried 2026-10-26).
/// </summary>
public static class VirtualMonitorSwitch
{
    /// <summary>Takes <paramref name="monitor"/> off the desktop; false when it is not on it, is the only display, or Windows refused (logged).</summary>
    public static bool Disconnect(DisplayIdentity monitor)
    {
        if (!DisplayConfigurationService.TryQuery(DisplayConfig.QDC_ONLY_ACTIVE_PATHS, out var paths, out var modes))
            return Failed("disconnect", "the display layout could not be read");
        int index = Array.FindIndex(paths, p => Is(p, monitor));
        if (index < 0 || paths.Length < 2) return false;

        // Only the active paths are applied: the virtual monitor's, made inactive, is left out.
        paths[index].flags &= ~DisplayConfig.DISPLAYCONFIG_PATH_ACTIVE;
        paths[index].sourceInfo.modeInfoIdx = DisplayConfig.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        paths[index].targetInfo.modeInfoIdx = DisplayConfig.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        int result = DisplayConfig.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
            DisplayConfig.SDC_APPLY | DisplayConfig.SDC_USE_SUPPLIED_DISPLAY_CONFIG | DisplayConfig.SDC_ALLOW_CHANGES | DisplayConfig.SDC_SAVE_TO_DATABASE);
        if (result != DisplayConfig.ERROR_SUCCESS) return Failed("disconnect", $"Windows refused (error {result})");
        Log.Write($"Displays: disconnected the virtual monitor {monitor} while the app is closed.");
        return true;
    }

    /// <summary>
    /// Puts <paramref name="monitor"/> back on the desktop: where Windows remembers it for this set
    /// of displays, else beside them. True when it is on the desktop (also when it already was).
    /// </summary>
    public static bool Connect(DisplayIdentity monitor)
    {
        if (!DisplayConfigurationService.TryQuery(DisplayConfig.QDC_ONLY_ACTIVE_PATHS, out var active, out var modes))
            return Failed("connect", "the display layout could not be read");
        if (active.Any(p => Is(p, monitor))) return true;
        if (!DisplayConfigurationService.TryQuery(DisplayConfig.QDC_ALL_PATHS, out var all, out _))
            return Failed("connect", "the display layout could not be read");

        // A path to the monitor from a source no active path uses.
        var used = active.Select(p => (p.sourceInfo.adapterId.LowPart, p.sourceInfo.adapterId.HighPart, p.sourceInfo.id)).ToHashSet();
        int index = Array.FindIndex(all, p => p.targetInfo.targetAvailable && Is(p, monitor)
                                              && !used.Contains((p.sourceInfo.adapterId.LowPart, p.sourceInfo.adapterId.HighPart, p.sourceInfo.id)));
        if (index < 0) return Failed("connect", "it is not there (is the virtual display driver running?)");
        DisplayConfig.DISPLAYCONFIG_PATH_INFO path = all[index];
        path.flags |= DisplayConfig.DISPLAYCONFIG_PATH_ACTIVE;
        path.sourceInfo.modeInfoIdx = DisplayConfig.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        path.targetInfo.modeInfoIdx = DisplayConfig.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;

        // Where it was: Windows keeps the modes and places of each set of displays it has shown.
        DisplayConfig.DISPLAYCONFIG_PATH_INFO[] topology = [.. active.Select(WithoutModes), path];
        int result = DisplayConfig.SetDisplayConfig((uint)topology.Length, topology, 0, null,
            DisplayConfig.SDC_APPLY | DisplayConfig.SDC_TOPOLOGY_SUPPLIED | DisplayConfig.SDC_ALLOW_PATH_ORDER_CHANGES);
        if (result == DisplayConfig.ERROR_SUCCESS)
        {
            Log.Write($"Displays: connected the virtual monitor {monitor} again, where it was.");
            return true;
        }

        // Never shown with these displays: the others as they are, it where Windows puts it.
        DisplayConfig.DISPLAYCONFIG_PATH_INFO[] supplied = [.. active, path];
        result = DisplayConfig.SetDisplayConfig((uint)supplied.Length, supplied, (uint)modes.Length, modes,
            DisplayConfig.SDC_APPLY | DisplayConfig.SDC_USE_SUPPLIED_DISPLAY_CONFIG | DisplayConfig.SDC_ALLOW_CHANGES | DisplayConfig.SDC_SAVE_TO_DATABASE);
        if (result != DisplayConfig.ERROR_SUCCESS) return Failed("connect", $"Windows refused (error {result})");
        Log.Write($"Displays: connected the virtual monitor {monitor} again, placed by Windows (none remembered for these displays).");
        return true;
    }

    private static bool Is(DisplayConfig.DISPLAYCONFIG_PATH_INFO path, DisplayIdentity monitor) =>
        DisplayConfigurationService.TargetIdentity(path, out _).MatchAgainst(monitor) >= DisplayMatchStrength.Edid;

    private static DisplayConfig.DISPLAYCONFIG_PATH_INFO WithoutModes(DisplayConfig.DISPLAYCONFIG_PATH_INFO path)
    {
        path.sourceInfo.modeInfoIdx = DisplayConfig.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        path.targetInfo.modeInfoIdx = DisplayConfig.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        return path;
    }

    private static bool Failed(string what, string why)
    {
        Log.Write($"Displays: could not {what} the virtual monitor: {why}.");
        return false;
    }
}
