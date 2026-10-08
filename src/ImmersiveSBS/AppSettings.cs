using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImmersiveSBS.Capture;
using ImmersiveSBS.Depth;
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

    /// <summary>Switch AI 3D off, or back on to the last target.</summary>
    ToggleAi3D,

    /// <summary>More AI 3D depth (strength +0.1).</summary>
    MoreDepth,

    /// <summary>Less AI 3D depth (strength −0.1).</summary>
    LessDepth,

    /// <summary>Screen plane toward the viewer: more of the picture behind it (convergence −0.05).</summary>
    PictureBack,

    /// <summary>Screen plane away from the viewer: more of the picture in front (convergence +0.05).</summary>
    PictureForward,

    /// <summary>Pick the changing area under the cursor as the video and lock it, or release the lock.</summary>
    PickVideo,

    /// <summary>Open the live tuning panel.</summary>
    Tuning,
}

/// <summary>Labels for the shortcut editor.</summary>
public static class ShortcutCommands
{
    public static string Label(ShortcutCommand command) => command switch
    {
        ShortcutCommand.Pause => "Pause or resume",
        ShortcutCommand.ToggleAi3D => "AI 3D on or off",
        ShortcutCommand.MoreDepth => "More depth",
        ShortcutCommand.LessDepth => "Less depth",
        ShortcutCommand.PictureBack => "Picture further back",
        ShortcutCommand.PictureForward => "Picture further forward",
        ShortcutCommand.PickVideo => "Pick the video under the cursor",
        ShortcutCommand.Tuning => "Tuning panel",
        ShortcutCommand.Settings => "Settings",
        ShortcutCommand.Exit => "Exit",
        _ => command.ToString(),
    };
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
        [ShortcutCommand.ToggleAi3D] = "Ctrl+Alt+D",
        // DesktopSbS's depth keys.
        [ShortcutCommand.MoreDepth] = "Ctrl+Alt+X",
        [ShortcutCommand.LessDepth] = "Ctrl+Alt+W",
        [ShortcutCommand.PictureBack] = "Ctrl+Alt+PageDown",
        [ShortcutCommand.PictureForward] = "Ctrl+Alt+PageUp",
        [ShortcutCommand.PickVideo] = "Ctrl+Alt+Shift+D",
        [ShortcutCommand.Tuning] = "Ctrl+Alt+T",
    };

    /// <summary>Fine tuning of AI 3D, edited live in the tuning panel.</summary>
    public StereoTuning Tuning { get; set; } = new();

    /// <summary>What AI 3D converts; Off shows the virtual monitor flat in both eyes.</summary>
    public AiTarget Ai3D { get; set; } = AiTarget.Off;

    /// <summary>What the AI 3D shortcut switches back on to.</summary>
    public AiTarget Ai3DLastTarget { get; set; } = AiTarget.Video;

    /// <summary>σ: depth strength as a fraction of the comfort-limited maximum (video-3d-plan.md §7.1).</summary>
    public double Ai3DStrength { get; set; } = 0.6;

    /// <summary>c: the nearness at the screen plane; lower puts more of the picture behind it.</summary>
    public double Ai3DConvergence { get; set; } = 0.35;

    public DepthTier DepthTier { get; set; } = DepthTier.Base;

    public DepthPreset DepthPreset { get; set; } = DepthPreset.Balanced;

    /// <summary>
    /// Shows the video this late, with the app's sound delayed by the same amount, for steadier
    /// depth (video-3d-plan.md P5); 0 = live. Only while AI 3D follows a video; not for calls.
    /// </summary>
    public double BufferSeconds { get; set; }

    /// <summary>Lip-sync trim for the delayed sound, ms; positive plays the sound later.</summary>
    public int AudioOffsetMs { get; set; }

    /// <summary>Horizontal stretch of the video around its centre, for content in the wrong aspect ratio.</summary>
    public double VideoStretch { get; set; } = 1;

    /// <summary>Video that is already side-by-side or top-bottom: detected and shown as is by default.</summary>
    public Native3DMode Native3D { get; set; } = Native3DMode.Auto;

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
