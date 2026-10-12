using System.Diagnostics;
using System.Drawing;
using ImmersiveSBS.Depth;
using ImmersiveSBS.Presentation;

namespace ImmersiveSBS.Theater;

/// <summary>What the theater's 3D settings panel shows, from the session's current settings; sent to the extension, which draws it.</summary>
public sealed record TheaterPanel(string Model, string Size, double Depth, double Plane, string Timing, bool EdgeSnap, bool Captions);

/// <summary>
/// What the app itself draws over the picture in the glasses, on the render thread: while 3D is
/// wanted, a badge at the video's top-left corner (or the screen's) with the depth model's
/// state: a spinner while it loads, "3D ready" for <see cref="ReadySeconds"/> after, a warning if
/// it failed. The 3D theater is the browser extension's: its button over a video and its
/// player, full screen at the video's own size with its own controls.
/// </summary>
/// <remarks>
/// The app used to draw its own theater button and controls in the glasses, place them from the
/// page's report (a quarter of a second late) and catch their clicks with a mouse hook; the
/// page does all that better itself (2026-10-12).
/// </remarks>
public sealed class TheaterUi
{
    /// <summary>How long "3D ready" stays after the model finished loading.</summary>
    public const double ReadySeconds = 2;

    private DepthServiceState? lastModel;
    private long readySince;
    private BadgeKind? drawnBadge;
    private int drawnFrame;

    /// <summary>True when the frame must be redrawn for the layer alone: the spinner due to turn, or "3D ready" due to go.</summary>
    public bool NeedsRedraw =>
        (drawnBadge is BadgeKind.Loading or BadgeKind.Preparing && ButtonImages.SpinnerFrame() != drawnFrame)
        || (drawnBadge == BadgeKind.Ready && Stopwatch.GetElapsedTime(readySince).TotalSeconds >= ReadySeconds);

    /// <param name="overlay">The video's eye images, or null when nothing is drawn over it.</param>
    /// <param name="found">The video this frame (source px), or empty.</param>
    /// <param name="eye">One eye's size on the glasses.</param>
    /// <param name="source">The captured frame's size.</param>
    /// <param name="target">What AI 3D converts (the theater's video included).</param>
    /// <param name="model">The depth model's state; null when there is none.</param>
    public UiLayer? Build(StereoOverlay? overlay, Rectangle found, Size eye, Size source, AiTarget target, DepthServiceState? model)
    {
        BadgeKind? badge = Badge(target, model);
        if (source.Width <= 0 || source.Height <= 0)
        {
            drawnBadge = null;
            return null;
        }

        // The source is fitted into the eye the same way the renderer does it.
        double scale = Math.Min((double)eye.Width / source.Width, (double)eye.Height / source.Height);
        var fitted = new Rectangle(
            (eye.Width - (int)Math.Round(source.Width * scale)) / 2,
            (eye.Height - (int)Math.Round(source.Height * scale)) / 2,
            (int)Math.Round(source.Width * scale),
            (int)Math.Round(source.Height * scale));
        Rectangle videoRect = overlay?.Region ?? found;
        // The whole screen has no video to wait for; a video does, and without one there is no place for it.
        if (target != AiTarget.WholeScreen && videoRect.IsEmpty) badge = null;
        Rectangle area = target == AiTarget.WholeScreen || videoRect.IsEmpty
            ? fitted
            : TheaterGeometry.Map(videoRect, new Rectangle(Point.Empty, source), fitted);
        return WithBadge(badge, area);
    }

    /// <summary>Clears the layer, e.g. when nothing is shown in side-by-side.</summary>
    public void Hide() => drawnBadge = null;

    /// <summary>Which badge the model's state calls for while 3D is wanted, if any.</summary>
    private BadgeKind? Badge(AiTarget target, DepthServiceState? model)
    {
        DepthServiceState? previous = lastModel;
        lastModel = model;
        if (target == AiTarget.Off) return null;
        switch (model)
        {
            case DepthServiceState.Loading: return BadgeKind.Loading;
            case DepthServiceState.Preparing: return BadgeKind.Preparing;
            case DepthServiceState.Failed: return BadgeKind.Failed;
            case null: return BadgeKind.Missing;
        }
        // Ready: announced only when it just finished loading while 3D was wanted.
        if (previous is DepthServiceState.Loading or DepthServiceState.Preparing) readySince = Stopwatch.GetTimestamp();
        return readySince != 0 && Stopwatch.GetElapsedTime(readySince).TotalSeconds < ReadySeconds ? BadgeKind.Ready : null;
    }

    /// <summary>Adds the badge at the top-left corner of <paramref name="area"/> (eye px).</summary>
    private UiLayer? WithBadge(BadgeKind? badge, Rectangle area)
    {
        drawnBadge = badge;
        if (badge is not BadgeKind kind) return null;

        drawnFrame = ButtonImages.SpinnerFrame();
        UiImage image = ButtonImages.Badge(kind, drawnFrame);
        var place = new Rectangle(area.Left + TheaterGeometry.Margin, area.Top + TheaterGeometry.Margin, image.Width, image.Height);
        return new UiLayer { Images = [(image, place)] };
    }
}
