using System.Diagnostics;
using System.Drawing;
using ImmersiveSBS.Depth;
using ImmersiveSBS.Interop;
using ImmersiveSBS.Presentation;

namespace ImmersiveSBS.Theater;

/// <summary>What the theater's 3D settings panel shows, from the session's current settings.</summary>
public sealed record TheaterPanel(string Model, string Size, double Depth, double Plane, string Timing, bool EdgeSnap, bool Captions);

/// <summary>
/// The theater video's playback, as the browser extension reports it, for the bottom bar: paused,
/// where it is and how long it is (seconds; NaN while unknown or live), muted.
/// </summary>
public sealed record TheaterMedia(bool Paused, double Time, double Duration, bool Muted);

/// <summary>
/// Builds the on-screen layer each frame on the render thread: in theater mode the region fitted
/// to the whole eye with 3D settings and Exit at the top and a player's bar at the bottom (seek
/// bar, time, back / play-pause / on, mute; it stays while the video is paused); otherwise a
/// "3D theater" button at the top of the video while the mouse is over it. Buttons show while the mouse moves
/// and fade after <see cref="ShowSeconds"/>, like a video player's controls. Where a click must
/// land to press each button is published to <see cref="Buttons"/> for <see cref="TheaterInput"/>.
/// In theater mode a "3D settings" button opens a panel of − / + rows (model, resolution, depth,
/// screen plane, buffer) and switches; it stays while the mouse moves and closes after
/// <see cref="PanelSeconds"/> without movement.
/// While 3D is wanted, a badge at the video's top-left corner shows the depth model's state: a
/// spinner while it loads, "3D ready" for <see cref="ReadySeconds"/> after, a warning if it failed.
/// </summary>
public sealed class TheaterUi
{
    /// <summary>How long the buttons stay after the mouse last moved.</summary>
    public const double ShowSeconds = 2.5;

    /// <summary>How long the settings panel stays open after the mouse last moved.</summary>
    public const double PanelSeconds = 10;

    /// <summary>How long "3D ready" stays after the model finished loading.</summary>
    public const double ReadySeconds = 2;

    private Point lastCursor;
    private long lastMove;
    private bool shown;
    private bool pinned;
    private volatile bool panelOpen;
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
        (shown && !panelOpen && !pinned && Stopwatch.GetElapsedTime(lastMove).TotalSeconds >= ShowSeconds)
        || (panelOpen && Stopwatch.GetElapsedTime(lastMove).TotalSeconds >= PanelSeconds)
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
    /// <param name="panel">The settings panel's values (theater only).</param>
    /// <param name="offer">Offer the "3D theater" button at all (the setting).</param>
    /// <param name="media">The theater video's playback from the extension; null without it (the bar then has play/pause only, by the media key).</param>
    /// <param name="videos">
    /// The videos the browser extension reports on the virtual monitor (source px): the button is
    /// offered only over these, at the one under the cursor. Null without the extension: no button
    /// (the video found in the picture can be any moving area, the desktop included).
    /// </param>
    public UiLayer? Build(StereoOverlay? overlay, Rectangle found, bool theater, Rectangle monitor, Size eye, Size source,
        AiTarget target, DepthServiceState? model, TheaterPanel? panel = null, bool offer = true, IReadOnlyList<Rectangle>? videos = null,
        TheaterMedia? media = null)
    {
        BadgeKind? badge = Badge(target, model);
        Native.GetCursorPos(out Point cursor);
        if (cursor != lastCursor)
        {
            lastCursor = cursor;
            lastMove = Stopwatch.GetTimestamp();
        }
        double idle = Stopwatch.GetElapsedTime(lastMove).TotalSeconds;
        if (panelOpen && (!theater || idle >= PanelSeconds)) panelOpen = false;
        // A paused video keeps its controls, as in a player.
        pinned = theater && media is { Paused: true };
        bool recent = idle < ShowSeconds || panelOpen || pinned;
        var onMonitor = new Point(cursor.X - monitor.X, cursor.Y - monitor.Y);

        // Where the video is drawn: the overlay's place (stretch included) or where it was found.
        Rectangle videoRect = overlay?.Region ?? found;
        Rectangle? offered = OfferedVideo(offer, theater, videos, onMonitor);
        if ((videoRect.IsEmpty && offered == null) || source.Width <= 0)
        {
            Publish([]);
            // The whole screen has no video to wait for; a video does.
            if (target != AiTarget.WholeScreen) badge = null;
            return WithBadge(new UiLayer { Images = [] }, badge, new Rectangle(Point.Empty, eye));
        }

        // Only the button, no found video: no badge to place on one.
        if (videoRect.IsEmpty && target != AiTarget.WholeScreen) badge = null;

        var buttons = new List<(TheaterAction Action, Rectangle Eye, Rectangle Desktop)>();
        var labels = new Dictionary<int, Func<bool, UiImage>>();   // button index → its picture, when not a plain button
        UiLayer layer;
        Rectangle badgeArea;
        if (theater && overlay is StereoOverlay o)
        {
            Rectangle e = TheaterGeometry.Fit(o.Region.Size, eye);
            badgeArea = e;
            void Add(TheaterAction action, Rectangle place, Func<bool, UiImage>? picture = null)
            {
                if (picture != null) labels[buttons.Count] = picture;
                buttons.Add((action, place, ToDesktop(TheaterGeometry.Map(place, e, o.Source))));
            }

            var under = new List<(UiImage, Rectangle)>();   // drawn under the buttons: the bar's backdrop and track
            if (recent)
            {
                Rectangle[] places = TheaterGeometry.TopCentre(e, 2);
                Add(TheaterAction.Settings, places[0]);
                Add(TheaterAction.ExitTheater, places[1]);
                AddBottomBar(e, media, under, Add, cursor, place => ToDesktop(TheaterGeometry.Map(place, e, o.Source)));
            }
            if (panelOpen && panel != null)
            {
                var rows = PanelRows(panel);
                var places = TheaterGeometry.PanelRows(e, rows.Length);
                for (int i = 0; i < rows.Length; i++)
                {
                    var (text, less, more, toggle) = rows[i];
                    var (minus, value, plus) = places[i];
                    if (toggle is TheaterAction t)
                    {
                        var wide = Rectangle.FromLTRB(minus.Left, value.Top, plus.Right, value.Bottom);
                        Add(t, wide, hover => ButtonImages.Label(text, wide.Width, hover, centred: false));
                        continue;
                    }
                    Add(less!.Value, minus, hover => ButtonImages.Label("−", minus.Width, hover, centred: true));
                    Add(TheaterAction.Panel, value, _ => ButtonImages.Label(text, value.Width, hover: false, centred: false));
                    Add(more!.Value, plus, hover => ButtonImages.Label("+", plus.Width, hover, centred: true));
                }
            }
            layer = new UiLayer
            {
                Theater = e,
                TheaterCursor = TheaterGeometry.Map(onMonitor, o.Source, e),
                Images = under.Concat(buttons.Select((b, i) => (labels.TryGetValue(i, out var picture)
                    ? picture(b.Desktop.Contains(cursor))
                    : ButtonImages.For(b.Action, hover: b.Desktop.Contains(cursor)), b.Eye))).ToList(),
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
            badgeArea = target == AiTarget.WholeScreen || videoRect.IsEmpty ? fitted : TheaterGeometry.Map(videoRect, whole, fitted);
            if (recent && offered is Rectangle video)
            {
                Rectangle place = TheaterGeometry.TopCentre(TheaterGeometry.Map(video, whole, fitted), 1)[0];
                buttons.Add((TheaterAction.EnterTheater, place, ToDesktop(TheaterGeometry.Map(place, fitted, whole))));
            }
            layer = new UiLayer { Images = Images(buttons, cursor) };
        }

        Publish(buttons.Select(b => (b.Action, b.Desktop)).ToArray());
        return WithBadge(layer, badge, layer.Theater ?? badgeArea);

        Rectangle ToDesktop(Rectangle onSource) => new(onSource.X + monitor.X, onSource.Y + monitor.Y, onSource.Width, onSource.Height);
    }

    /// <summary>
    /// The player's bar along the bottom of <paramref name="e"/> (eye px): a darkened strip, the
    /// seek bar with its handle and the time when the video's length is known, back / play-pause
    /// / on, and mute. Without the extension's report only play/pause (the media key).
    /// </summary>
    private static void AddBottomBar(Rectangle e, TheaterMedia? media, List<(UiImage, Rectangle)> under,
        Action<TheaterAction, Rectangle, Func<bool, UiImage>?> add, Point cursor, Func<Rectangle, Rectangle> toDesktop)
    {
        var bar = TheaterGeometry.BottomBar(e);
        under.Add((ButtonImages.Solid(Color.FromArgb(150, 0, 0, 0)), bar.Backdrop));
        bool paused = media?.Paused ?? false;
        add(TheaterAction.PlayPause, bar.Play, hover => ButtonImages.Icon(paused ? ButtonImages.PlayGlyph : ButtonImages.PauseGlyph, hover));
        if (media == null) return;

        add(TheaterAction.SeekBack, bar.Back, hover => ButtonImages.Icon(ButtonImages.BackGlyph, hover));
        add(TheaterAction.SeekForward, bar.Forward, hover => ButtonImages.Icon(ButtonImages.ForwardGlyph, hover));
        add(TheaterAction.Mute, bar.Mute, hover => ButtonImages.Icon(media.Muted ? ButtonImages.MutedGlyph : ButtonImages.VolumeGlyph, hover));
        bool known = double.IsFinite(media.Duration) && media.Duration > 0;
        string time = known ? $"{TheaterGeometry.Clock(media.Time)} / {TheaterGeometry.Clock(media.Duration)}"
                      : double.IsFinite(media.Time) ? TheaterGeometry.Clock(media.Time) : "Live";
        add(TheaterAction.Panel, bar.Time, _ => ButtonImages.Label(time, bar.Time.Width, hover: false, centred: false));
        if (!known) return;

        // The seek bar: its height and some above and below take the click; the track thickens
        // under the cursor. Its click area is drawn by the track and handle, under the buttons.
        Rectangle hit = Rectangle.Inflate(bar.Seek, 0, TheaterGeometry.SeekReach);
        bool hover = toDesktop(hit).Contains(cursor);
        Rectangle track = hover ? Rectangle.Inflate(bar.Seek, 0, 3) : bar.Seek;
        double fraction = Math.Clamp(double.IsFinite(media.Time) ? media.Time / media.Duration : 0, 0, 1);
        int done = (int)Math.Round(track.Width * fraction);
        under.Add((ButtonImages.Solid(Color.FromArgb(90, 255, 255, 255)), track));
        under.Add((ButtonImages.Solid(Color.FromArgb(255, 96, 205, 255)), new Rectangle(track.X, track.Y, done, track.Height)));
        UiImage knob = ButtonImages.Knob();
        under.Add((knob, new Rectangle(track.X + done - knob.Width / 2, track.Y + track.Height / 2 - knob.Height / 2, knob.Width, knob.Height)));
        add(TheaterAction.SeekBar, hit, _ => ButtonImages.Solid(Color.Transparent));
    }

    /// <summary>
    /// The video to offer the "3D theater" button on, or null: the video the browser extension
    /// reports under the cursor (<paramref name="videos"/>), never anything else. Without the
    /// extension (null) none: a video guessed from the picture was any moving area, and the button showed over the
    /// desktop (the tray and the shortcut still open the theater on it). None in the theater or
    /// with the button turned off. All in virtual monitor px.
    /// </summary>
    public static Rectangle? OfferedVideo(bool offer, bool theater, IReadOnlyList<Rectangle>? videos, Point cursor)
    {
        if (!offer || theater || videos == null) return null;
        foreach (Rectangle v in videos)
            if (v.Contains(cursor)) return v;
        return null;
    }

    /// <summary>Opens or closes the theater's settings panel; any thread.</summary>
    public void TogglePanel()
    {
        panelOpen = !panelOpen;
        lastMove = Stopwatch.GetTimestamp();
    }

    /// <summary>The panel's rows: a label with its value, and the − / + actions or the switch's action.</summary>
    private static (string Text, TheaterAction? Less, TheaterAction? More, TheaterAction? Toggle)[] PanelRows(TheaterPanel p) =>
    [
        ($"Model:  {p.Model}", TheaterAction.ModelPrevious, TheaterAction.ModelNext, null),
        ($"Resolution:  {p.Size}", TheaterAction.SizePrevious, TheaterAction.SizeNext, null),
        ($"Depth:  {p.Depth:0.0}", TheaterAction.DepthLess, TheaterAction.DepthMore, null),
        ($"Screen plane:  {p.Plane:0.00} ({(p.Plane < 0.5 ? "behind" : p.Plane > 0.5 ? "in front" : "balanced")})",
            TheaterAction.PlaneBack, TheaterAction.PlaneForward, null),
        ($"Timing:  {p.Timing}", TheaterAction.BufferLess, TheaterAction.BufferMore, null),
        ($"Snap depth edges to the picture:  {(p.EdgeSnap ? "on" : "off")}", null, null, TheaterAction.ToggleEdgeSnap),
        ($"Keep subtitles flat:  {(p.Captions ? "on" : "off")}", null, null, TheaterAction.ToggleCaptions),
    ];

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
