namespace ImmersiveSBS.Depth;

/// <summary>The layout of video that is already stereo.</summary>
public enum Native3DLayout
{
    /// <summary>Ordinary 2D video: convert with depth.</summary>
    None,

    /// <summary>Left and right views side by side (usually half-width each).</summary>
    SideBySide,

    /// <summary>Left view on top, right view below.</summary>
    TopBottom,
}

/// <summary>
/// What the video is, as the user says (the settings, the theater's 3D panel): 2D, converted
/// with depth, or already 3D, shown as is (video-3d-plan.md §7.4). It used to be detected from
/// the picture by default; the detector took 2D video with a top-to-bottom brightness change for
/// side-by-side every few seconds (2026-10-12) and was removed.
/// </summary>
public enum Native3DMode
{
    /// <summary>2D: converted with depth.</summary>
    Off,

    /// <summary>Side-by-side 3D: shown as is.</summary>
    ForceSideBySide,

    /// <summary>Top-bottom 3D: shown as is.</summary>
    ForceTopBottom,
}
