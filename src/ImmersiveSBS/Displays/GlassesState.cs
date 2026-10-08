// Ported from DesktopSbS: DesktopSbS/Model/DisplayStereoState.cs
namespace ImmersiveSBS.Displays;

/// <summary>What the glasses can currently show.</summary>
public enum GlassesState
{
    /// <summary>The glasses are not attached, or not part of the desktop.</summary>
    Disconnected,

    /// <summary>Attached in an ordinary 2D mode (1920×1080): the desktop is shown once, 1:1.</summary>
    Mode2D,

    /// <summary>Attached in full side-by-side 3D (3840×1080): the desktop goes to both eyes.</summary>
    Mode3D,

    /// <summary>A stereo-shaped mode the app cannot split: top-bottom, or an odd width.</summary>
    UnsupportedMode,
}

/// <summary>The shape of a display mode, independent of what it is used for.</summary>
public enum StereoFrameShape
{
    /// <summary>An ordinary single-view aspect ratio.</summary>
    None,

    /// <summary>Two ordinary views side by side, e.g. 3840×1080.</summary>
    SideBySide,

    /// <summary>Two ordinary views stacked, e.g. 1920×2160.</summary>
    TopBottom,
}

/// <summary>The evaluated glasses state, plus the decision that follows from it.</summary>
public sealed record GlassesStatus(GlassesState State, bool ShouldPresent, string? Message)
{
    public override string ToString() => State + " (present: " + ShouldPresent + ")";
}

/// <summary>
/// Pure decision logic for the glasses state. No Windows calls and no settings access. It never
/// changes the display mode; it only decides whether to present and what to tell the user.
/// </summary>
public static class GlassesStateEvaluator
{
    /// <summary>
    /// A side-by-side frame is at least this wide relative to its height, i.e. each eye is at
    /// least 1.25:1. 32:9 gives 3.56, 16:9 gives 1.78.
    /// </summary>
    public const double MinSideBySideAspect = 2.5;

    /// <summary>
    /// A top-bottom frame falls in this band, i.e. each eye is 1.4:1 to 2.4:1. 1920×2160 gives
    /// 0.89. Portrait monitors (1080×1920 = 0.56) stay out.
    /// </summary>
    public const double MinTopBottomAspect = 0.7;
    public const double MaxTopBottomAspect = 1.2;

    public static StereoFrameShape ClassifyFrame(int width, int height)
    {
        if (width <= 0 || height <= 0) return StereoFrameShape.None;

        double aspect = (double)width / height;
        if (aspect >= MinSideBySideAspect) return StereoFrameShape.SideBySide;
        if (aspect >= MinTopBottomAspect && aspect <= MaxTopBottomAspect) return StereoFrameShape.TopBottom;
        return StereoFrameShape.None;
    }

    /// <param name="glasses">The resolved glasses display, or null if not attached.</param>
    /// <param name="showIn2D">User setting: also show the desktop while the glasses are in 2D.</param>
    public static GlassesStatus Evaluate(DisplayInfo? glasses, bool showIn2D)
    {
        if (glasses == null)
            return new GlassesStatus(GlassesState.Disconnected, false, "The glasses are not connected.");

        string name = string.IsNullOrEmpty(glasses.Identity.FriendlyName) ? "The glasses" : glasses.Identity.FriendlyName;
        int width = glasses.Mode.Width > 0 ? glasses.Mode.Width : glasses.Bounds.Width;
        int height = glasses.Mode.Height > 0 ? glasses.Mode.Height : glasses.Bounds.Height;

        switch (ClassifyFrame(width, height))
        {
            case StereoFrameShape.SideBySide when width % 2 != 0:
                return new GlassesStatus(GlassesState.UnsupportedMode, false,
                    $"{name} run at {width}×{height}; an odd width cannot be split evenly into two eyes.");

            case StereoFrameShape.SideBySide:
                return new GlassesStatus(GlassesState.Mode3D, true, null);

            case StereoFrameShape.TopBottom:
                return new GlassesStatus(GlassesState.UnsupportedMode, false,
                    $"{name} send a top-bottom frame ({width}×{height}); only side-by-side 3D is supported.");

            default:
                return new GlassesStatus(GlassesState.Mode2D, showIn2D,
                    showIn2D ? null : $"{name} are in 2D mode ({width}×{height}). Switch them to 3D to start.");
        }
    }
}
