using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImmersiveSBS.Capture;
using ImmersiveSBS.Depth;
using ImmersiveSBS.Displays;
using Microsoft.Win32;

namespace ImmersiveSBS;

/// <summary>
/// Reads a model by name, and a model since removed (Distill-Any-Depth Large, Depth Anything V2
/// Base or Large) as the default, Distill-Any-Depth Base, rather than failing the whole file.
/// </summary>
public sealed class DepthTierConverter : JsonConverter<DepthTier>
{
    public override DepthTier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Enum.TryParse(reader.GetString(), ignoreCase: true, out DepthTier tier)
        && Enum.IsDefined(tier)
            ? tier
            : DepthTier.Base;

    public override void Write(Utf8JsonWriter writer, DepthTier value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>
/// What the video is, by name; the old "Auto" (detected, since removed) and anything unknown load
/// as 2D rather than failing the whole file.
/// </summary>
public sealed class Native3DModeConverter : JsonConverter<Native3DMode>
{
    public override Native3DMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Enum.TryParse(reader.GetString(), ignoreCase: true, out Native3DMode mode)
        && Enum.IsDefined(mode)
            ? mode
            : Native3DMode.Off;

    public override void Write(Utf8JsonWriter writer, Native3DMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>Shortcuts by command name; a command since removed (the pick shortcut) is skipped rather than failing the whole file.</summary>
public sealed class ShortcutMapConverter : JsonConverter<Dictionary<ShortcutCommand, string>>
{
    public override Dictionary<ShortcutCommand, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var map = new Dictionary<ShortcutCommand, string>();
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Shortcuts must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString() ?? "";
            reader.Read();
            string? keys = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
            if (keys != null && Enum.TryParse(name, ignoreCase: true, out ShortcutCommand command) && Enum.IsDefined(command)) map[command] = keys;
        }
        return map;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<ShortcutCommand, string> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (command, keys) in value) writer.WriteString(command.ToString(), keys);
        writer.WriteEndObject();
    }
}

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

    /// <summary>Desktop Duplication; Windows.Graphics.Capture only by editing the file (troubleshooting).</summary>
    public CaptureMethod CaptureMethod { get; set; } = CaptureMethod.DesktopDuplication;

    /// <summary>Also show the virtual monitor while the glasses are in 2D mode (1:1).</summary>
    public bool ShowIn2D { get; set; } = true;

    /// <summary>Shortcut text per command, e.g. "Ctrl+Alt+B"; empty disables one.</summary>
    [JsonConverter(typeof(ShortcutMapConverter))]
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
        [ShortcutCommand.Tuning] = "Ctrl+Alt+T",
    };

    /// <summary>Fine tuning of AI 3D, edited live in the tuning panel.</summary>
    public StereoTuning Tuning { get; set; } = StereoTuning.Default;

    /// <summary>
    /// Off or Whole screen: whether the whole virtual monitor is converted. Browser video is
    /// converted in the extension's theater, whatever this says.
    /// </summary>
    public AiTarget Ai3D { get; set; } = AiTarget.Off;

    /// <summary>What the AI 3D shortcut switches back on to: Whole screen.</summary>
    public AiTarget Ai3DLastTarget { get; set; } = AiTarget.WholeScreen;

    /// <summary>σ: depth strength as a fraction of the comfort-limited maximum (video-3d-plan.md §7.1).</summary>
    public double Ai3DStrength { get; set; } = 0.6;

    /// <summary>c: the nearness at the screen plane; lower puts more of the picture behind it.</summary>
    public double Ai3DConvergence { get; set; } = 0.35;

    /// <summary>The depth model; a removed one in a saved file loads as the default (<see cref="DepthTierConverter"/>).</summary>
    [JsonConverter(typeof(DepthTierConverter))]
    public DepthTier DepthTier { get; set; } = DepthTier.Base;

    public DepthPreset DepthPreset { get; set; } = DepthPreset.Balanced;

    /// <summary>
    /// Shows the theater's video this late, with its sound delayed in the page by the same amount,
    /// for steadier depth (video-3d-plan.md P5); 0 = none (<see cref="Timing"/>). 2 s by default:
    /// for watching a video the delay costs nothing (a pause holds both, a seek shows a spinner
    /// while it fills); a live stream is never buffered.
    /// </summary>
    public double BufferSeconds { get; set; } = 2;

    /// <summary>
    /// Live or matched while there is no buffer (improvement-plan.md §6): matched delays the
    /// video just enough for every frame to have its own depth. Auto: matched when depth runs
    /// more than 60 ms behind.
    /// </summary>
    public VideoTiming Timing { get; set; } = VideoTiming.Auto;

    /// <summary>Lip-sync trim for the delayed sound, ms; positive plays the sound later.</summary>
    public int AudioOffsetMs { get; set; }

    /// <summary>Horizontal stretch of the theater's picture, for content in the wrong aspect ratio; the player draws it.</summary>
    public double VideoStretch { get; set; } = 1;

    /// <summary>What the video is: 2D (converted) or side-by-side or top-bottom 3D (shown as is).</summary>
    [JsonConverter(typeof(Native3DModeConverter))]
    public Native3DMode Native3D { get; set; } = Native3DMode.Off;

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
                    s.Tuning = (s.Tuning ?? StereoTuning.Default).Migrated();
                    s.ForgetRemovedChoices();
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

    /// <summary>
    /// "The browser video only" went (browser video is converted in the theater): a saved one is
    /// Off, and the AI 3D shortcut switches to the whole screen.
    /// </summary>
    public void ForgetRemovedChoices()
    {
        if (Ai3D == AiTarget.Video) Ai3D = AiTarget.Off;
        Ai3DLastTarget = AiTarget.WholeScreen;
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
