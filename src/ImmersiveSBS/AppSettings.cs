using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImmersiveSBS.Capture;
using ImmersiveSBS.Displays;
using Microsoft.Win32;

namespace ImmersiveSBS;

/// <summary>Commands that can have a keyboard shortcut.</summary>
public enum ShortcutCommand
{
    /// <summary>Stop or resume showing the virtual monitor on the glasses.</summary>
    Pause,

    /// <summary>Open the settings window.</summary>
    Settings,

    /// <summary>Quit ImmersiveSBS.</summary>
    Exit,
}

/// <summary>
/// User settings, stored as JSON at %LOCALAPPDATA%\ImmersiveSBS\settings.json. Displays are
/// stored as serialized <see cref="DisplayIdentity"/> strings so they survive renumbering.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ImmersiveSBS";

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImmersiveSBS", "settings.json");

    /// <summary>The virtual monitor; null picks the virtual display driver's monitor.</summary>
    public string? VirtualMonitor { get; set; }

    /// <summary>The glasses; null picks a display recognised from its EDID.</summary>
    public string? Glasses { get; set; }

    public CaptureMethod CaptureMethod { get; set; } = CaptureMethod.DesktopDuplication;

    /// <summary>Also show the virtual monitor while the glasses are in 2D mode (1:1).</summary>
    public bool ShowIn2D { get; set; } = true;

    /// <summary>Stop the cursor at the edge of the glasses while they show the virtual monitor.</summary>
    public bool KeepCursorOffGlasses { get; set; } = true;

    /// <summary>Move windows that land on the glasses to the virtual monitor.</summary>
    public bool MoveWindowsOffGlasses { get; set; } = true;

    /// <summary>
    /// While the virtual monitor is not on the glasses (paused, glasses away or in 2D), show it in
    /// a window on a real monitor, so what is on it stays in reach.
    /// </summary>
    public bool ShowInWindowWhenNotOnGlasses { get; set; } = true;

    /// <summary>On exit, move windows from the virtual monitor to a real monitor.</summary>
    public bool MoveWindowsOffVirtualMonitorOnExit { get; set; } = true;

    /// <summary>Shortcut text per command, e.g. "Ctrl+Alt+B"; empty disables one.</summary>
    public Dictionary<ShortcutCommand, string> Shortcuts { get; set; } = DefaultShortcuts();

    public static Dictionary<ShortcutCommand, string> DefaultShortcuts() => new()
    {
        // The same keys as DesktopSbS's pause and exit.
        [ShortcutCommand.Pause] = "Ctrl+Alt+B",
        [ShortcutCommand.Settings] = "Ctrl+Alt+F1",
        [ShortcutCommand.Exit] = "Ctrl+Alt+C",
    };

    [JsonIgnore]
    public DisplayIdentity? VirtualMonitorIdentity
    {
        get => DisplayIdentity.Parse(VirtualMonitor);
        set => VirtualMonitor = value?.Serialize();
    }

    [JsonIgnore]
    public DisplayIdentity? GlassesIdentity
    {
        get => DisplayIdentity.Parse(Glasses);
        set => Glasses = value?.Serialize();
    }

    /// <summary>Whether Windows starts ImmersiveSBS at sign-in (HKCU Run key).</summary>
    [JsonIgnore]
    public static bool StartWithWindows
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, "\"" + Environment.ProcessPath + "\"");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                AppSettings? s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (s != null)
                {
                    // Commands added in later versions get their default shortcut.
                    foreach (var (command, text) in DefaultShortcuts()) s.Shortcuts.TryAdd(command, text);
                    return s;
                }
            }
        }
        catch (Exception e)
        {
            // A corrupt file degrades to defaults rather than blocking startup.
            Log.Write("AppSettings: could not read settings: " + e.Message);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception e)
        {
            Log.Write("AppSettings: could not save settings: " + e.Message);
        }
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    /// <summary>
    /// The saved virtual monitor, or the virtual display driver's monitor when none is saved.
    /// </summary>
    public DisplayIdentity? ResolveVirtualMonitorIdentity(IReadOnlyList<DisplayInfo> displays) =>
        VirtualMonitorIdentity
        ?? displays.FirstOrDefault(d => d.Identity.EdidKey == KnownGlasses.VirtualMonitorEdidKey)?.Identity
        ?? displays.FirstOrDefault(d => d.IsIndirectVirtual)?.Identity;

    /// <summary>The saved glasses, or a display recognised from its EDID when none is saved.</summary>
    public DisplayIdentity? ResolveGlassesIdentity(IReadOnlyList<DisplayInfo> displays) =>
        GlassesIdentity
        ?? displays.FirstOrDefault(d => KnownGlasses.FindByEdidKey(d.Identity.EdidKey) != null)?.Identity;
}
