using System.Diagnostics;
using System.Drawing;
using ImmersiveSBS.Depth;
using ImmersiveSBS.Interop;
using ImmersiveSBS.Presentation;

namespace ImmersiveSBS.Theater;

/// <summary>
/// Builds the on-screen layer each frame on the render thread: in theater mode the region fitted
/// to the whole eye with Play/Pause and Exit buttons; otherwise a "3D theater" button at the
/// video's top-right corner while the mouse is over the video. Buttons show while the mouse moves
/// and fade after <see cref="ShowSeconds"/>, like a video player's controls. Where a click must
/// land to press each button is published to <see cref="Buttons"/> for <see cref="TheaterInput"/>.
/// While 3D is wanted, a badge at the video's top-left corner shows the depth model's state: a
/// spinner while it loads, "3D ready" for <see cref="ReadySeconds"/> after, a warning if it failed.
/// </summary>
public sealed class TheaterUi
{
    /// <summary>How long the buttons stay after the mouse last moved.</summary>
    public const double ShowSeconds = 2.5;

    /// <summary>How long "3D ready" stays after the model finished loading.</summary>
    public const double ReadySeconds = 2;

    private Point lastCursor;
    private long lastMove;
    private bool shown;
    private DepthServiceState? lastModel;
    private long readySince;
    private BadgeKind? drawnBadge;
    private int drawnFrame;

    /// <summary>Shared with the mouse hook; null while there is none.</summary>
    public TheaterButtons? Buttons { get; set; }

    /// <summary>
    /// True when the frame must be redrawn for the layer alone: buttons due to fade, the spinner
    /// due to turn, or "3D ready" due to go.
    /// </summary>
    public bool NeedsRedraw =>
        (shown && Stopwatch.GetElapsedTime(lastMove).TotalSeconds >= ShowSeconds)
        || (drawnBadge is BadgeKind.Loading or BadgeKind.Preparing && ButtonImages.SpinnerFrame() != drawnFrame)
        || (drawnBadge == BadgeKind.Ready && Stopwatch.GetElapsedTime(readySince).TotalSeconds >= ReadySeconds);

    /// <param name="overlay">The video's eye images, or null when nothing is drawn over it.</param>
    /// <param name="found">The video found this frame (source px), or empty.</param>
    /// <param name="theater">Theater mode is on.</param>
    /// <param name="monitor">The virtual monitor's desktop bounds.</param>
    /// <param name="eye">One eye's size on the glasses.</param>
    /// <param name="source">The captured frame's size.</param>
    /// <param name="target">What AI 3D converts (the theater's video included).</param>
    /// <param name="model">The depth model's state; null when there is none.</param>
    public UiLayer? Build(StereoOverlay? overlay, Rectangle found, bool theater, Rectangle monitor, Size eye, Size source,
        AiTarget target, DepthServiceState? model)
    {
        BadgeKind? badge = Badge(target, model);
        Native.GetCursorPos(out Point cursor);
        if (cursor != lastCursor)
        {
            lastCursor = cursor;
            lastMove = Stopwatch.GetTimestamp();
        }
        bool recent = Stopwatch.GetElapsedTime(lastMove).TotalSeconds < ShowSeconds;
        var onMonitor = new Point(cursor.X - monitor.X, cursor.Y - monitor.Y);

        // Where the video is drawn: the overlay's place (stretch included) or where it was found.
        Rectangle videoRect = overlay?.Region ?? found;
        if (videoRect.IsEmpty || source.Width <= 0)
        {
            Publish([]);
            // The whole screen has no video to wait for; a video does.
            if (target != AiTarget.WholeScreen) badge = null;
            return WithBadge(new UiLayer { Images = [] }, badge, new Rectangle(Point.Empty, eye));
        }

        var buttons = new List<(TheaterAction Action, Rectangle Eye, Rectangle Desktop)>();
        UiLayer layer;
        Rectangle badgeArea;
        if (theater && overlay is StereoOverlay o)
        {
            Rectangle e = TheaterGeometry.Fit(o.Region.Size, eye);
            badgeArea = e;
            if (recent)
            {
                Rectangle[] places = TheaterGeometry.TopRight(e, 2);
                buttons.Add((TheaterAction.PlayPause, places[0], ToDesktop(TheaterGeometry.Map(places[0], e, o.Source))));
                buttons.Add((TheaterAction.ExitTheater, places[1], ToDesktop(TheaterGeometry.Map(places[1], e, o.Source))));
            }
            layer = new UiLayer
            {
                Theater = e,
                TheaterCursor = TheaterGeometry.Map(onMonitor, o.Source, e),
                Images = Images(buttons, cursor),
            };
        }
        else
        {
            // The source is fitted into the eye the same way the renderer does it.
            double scale = Math.Min((double)eye.Width / source.Width, (double)eye.Height / source.Height);
            var fitted = new Rectangle(
                (eye.Width - (int)Math.Round(source.Width * scale)) / 2,
                (eye.Height - (int)Math.Round(source.Height * scale)) / 2,
                (int)Math.Round(source.Width * scale),
                (int)Math.Round(source.Height * scale));
            var whole = new Rectangle(Point.Empty, source);
            Rectangle regionEye = TheaterGeometry.Map(videoRect, whole, fitted);
            badgeArea = target == AiTarget.WholeScreen ? fitted : regionEye;
            if (recent && videoRect.Contains(onMonitor))
            {
                Rectangle place = TheaterGeometry.TopRight(regionEye, 1)[0];
                buttons.Add((TheaterAction.EnterTheater, place, ToDesktop(TheaterGeometry.Map(place, fitted, whole))));
            }
            layer = new UiLayer { Images = Images(buttons, cursor) };
        }

        Publish(buttons.Select(b => (b.Action, b.Desktop)).ToArray());
        return WithBadge(layer, badge, layer.Theater ?? badgeArea);

        Rectangle ToDesktop(Rectangle onSource) => new(onSource.X + monitor.X, onSource.Y + monitor.Y, onSource.Width, onSource.Height);
    }

    /// <summary>Clears the buttons, e.g. when nothing is shown in side-by-side.</summary>
    public void Hide()
    {
        Publish([]);
        drawnBadge = null;
    }

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
    private UiLayer? WithBadge(UiLayer layer, BadgeKind? badge, Rectangle area)
    {
        drawnBadge = badge;
        if (badge is not BadgeKind kind) return layer.Images.Count == 0 && layer.Theater == null ? null : layer;

        drawnFrame = ButtonImages.SpinnerFrame();
        UiImage image = ButtonImages.Badge(kind, drawnFrame);
        var place = new Rectangle(area.Left + TheaterGeometry.Margin, area.Top + TheaterGeometry.Margin, image.Width, image.Height);
        var images = new List<(UiImage, Rectangle)>(layer.Images) { (image, place) };
        return new UiLayer { Theater = layer.Theater, TheaterCursor = layer.TheaterCursor, Images = images };
    }

    private static List<(UiImage, Rectangle)> Images(List<(TheaterAction Action, Rectangle Eye, Rectangle Desktop)> buttons, Point cursor) =>
        buttons.Select(b => (ButtonImages.For(b.Action, hover: b.Desktop.Contains(cursor)), b.Eye)).ToList();

    private void Publish((TheaterAction, Rectangle)[] buttons)
    {
        shown = buttons.Length > 0;
        Buttons?.Set(buttons);
    }
}
