namespace ImmersiveSBS.Depth;

/// <summary>How the eye images are made from the picture and its depth.</summary>
public enum WarpMode
{
    /// <summary>Each source pixel moved to its place in each eye; gaps filled (improvement-plan.md §2.1).</summary>
    Splat,

    /// <summary>Each eye pixel looks up the source pixel that lands there; nothing left empty (§8).</summary>
    Lookup,
}

/// <summary>A switch whose automatic choice depends on the model.</summary>
public enum AutoSwitch
{
    Auto,
    On,
    Off,
}

/// <summary>
/// The fine controls of AI 3D, edited live in the tuning panel and stored with the settings.
/// Defaults are the plan's (video-3d-plan.md §6–§7, improvement-plan.md §7).
/// </summary>
public sealed record StereoTuning
{
    /// <summary>The defaults' version; settings saved before it get the new defaults (<see cref="Migrated"/>).</summary>
    public const int CurrentVersion = 2;

    /// <summary>The version of the defaults these settings were saved with; 0 before versions were kept.</summary>
    public int Version { get; init; }

    /// <summary>
    /// Foreground dilation at screen resolution, px (§6.4): a hard maximum that keeps every depth
    /// step a step, so the warp opens a hole at each (improvement-plan.md §2.1). 0 since soft
    /// edges grow the near side at model resolution instead.
    /// </summary>
    public int DilateRadius { get; init; }

    /// <summary>
    /// Snap the model's soft depth edges to the picture's own edges (outlines, hair). Off by
    /// default: a snapped edge is a sharp step, and the forward warp opens a hole at every one.
    /// </summary>
    public bool EdgeSnap { get; init; }

    /// <summary>Find subtitles and keep them flat, just in front of the scene (<see cref="CaptionLock"/>).</summary>
    public bool Captions { get; init; } = true;

    /// <summary>
    /// Soft depth edges (improvement-plan.md §7.1): passes across, 0–4; −1 (Auto) picks enough
    /// for the depth strength and the video's size. The guided upsample softens with them.
    /// </summary>
    public int SoftEdges { get; init; } = -1;

    /// <summary>Depth anti-aliasing (§7.2); Auto is on for Depth Anything V2 and 3, off for Distill-Any-Depth.</summary>
    public AutoSwitch AntiAlias { get; init; }

    /// <summary>
    /// Foreground scale (improvement-plan.md §11 item 4, <see cref="ForegroundCurve"/>): −3..3;
    /// above 0 near things get more of the depth range, for scenes where people look flat.
    /// </summary>
    public int Foreground { get; init; }

    /// <summary>The warp: the forward splat until the lookup warp has been compared in the glasses (§8).</summary>
    public WarpMode Warp { get; init; }

    /// <summary>Per-frame blend where nothing moves, at 30 fps (§6.2): smaller is steadier, slower.</summary>
    public double Smoothing { get; init; } = 0.2;

    /// <summary>Mean luma change that counts as a scene cut (§6.2).</summary>
    public double SceneCut { get; init; } = 0.12;

    /// <summary>Black bands at the side edges where something in front of the screen touches them (§7.3).</summary>
    public bool FloatingWindow { get; init; } = true;

    /// <summary>Subtitles and logos at the screen plane (§6.5); experimental, off by default.</summary>
    public bool StaticOverlays { get; init; }

    public static readonly StereoTuning Default = new() { Version = CurrentVersion };

    /// <summary>
    /// These settings with the defaults that changed since they were saved: version 2 (the edge
    /// pipeline) turned screen dilation and edge snapping off, which a saved file would otherwise
    /// keep on.
    /// </summary>
    public StereoTuning Migrated()
    {
        StereoTuning t = this;
        if (t.Version < 2) t = t with { DilateRadius = 0, EdgeSnap = false };
        return t.Version == CurrentVersion ? t : t with { Version = CurrentVersion };
    }
}
