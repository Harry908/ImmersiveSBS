namespace ImmersiveSBS.Depth;

/// <summary>
/// The fine controls of AI 3D, edited live in the tuning panel and stored with the settings.
/// Defaults are the plan's (video-3d-plan.md §6–§7).
/// </summary>
public sealed record StereoTuning
{
    /// <summary>
    /// Foreground dilation radius, px (§6.4): pushes edge errors onto the background. 1 since edge
    /// snapping: wider drags a sliver of background along with every object, a visible halo.
    /// </summary>
    public int DilateRadius { get; init; } = 1;

    /// <summary>Snap the model's soft depth edges to the picture's own edges (outlines, hair).</summary>
    public bool EdgeSnap { get; init; } = true;

    /// <summary>Find subtitles and keep them flat, just in front of the scene (<see cref="CaptionLock"/>).</summary>
    public bool Captions { get; init; } = true;

    /// <summary>Colour sigma of the guided upsample (§6.3): smaller keeps depth edges tighter on picture edges.</summary>
    public double EdgeSoftness { get; init; } = 0.1;

    /// <summary>Per-frame blend where nothing moves, at 30 fps (§6.2): smaller is steadier, slower.</summary>
    public double Smoothing { get; init; } = 0.2;

    /// <summary>Mean luma change that counts as a scene cut (§6.2).</summary>
    public double SceneCut { get; init; } = 0.12;

    /// <summary>Black bands at the side edges where something in front of the screen touches them (§7.3).</summary>
    public bool FloatingWindow { get; init; } = true;

    /// <summary>Subtitles and logos at the screen plane (§6.5); experimental, off by default.</summary>
    public bool StaticOverlays { get; init; }

    public static readonly StereoTuning Default = new();
}
