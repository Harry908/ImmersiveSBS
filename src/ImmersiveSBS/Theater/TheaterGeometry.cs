using System.Drawing;

namespace ImmersiveSBS.Theater;

/// <summary>
/// Where things go in theater mode and on the on-screen buttons: the video region fitted into
/// the eye, points and rectangles mapped between the virtual monitor and the eye, and the
/// buttons' places. Pure: no GPU, no Windows state.
/// </summary>
public static class TheaterGeometry
{
    /// <summary>Button size in eye px.</summary>
    public static readonly Size ButtonSize = new(216, 56);

    /// <summary>Space between buttons and from the edge, eye px.</summary>
    public const int Margin = 16;

    /// <summary>The settings panel's − and + buttons, and its value labels, eye px.</summary>
    public static readonly Size StepSize = new(56, 56);

    public static readonly Size ValueSize = new(380, 56);

    /// <summary>Space between a panel row's parts and between rows, eye px.</summary>
    public const int PanelGap = 8;

    /// <summary>
    /// The settings panel's rows, centred under the buttons at the top of <paramref name="area"/>:
    /// each row's −, value and + (a toggle row uses the value place only, widened over the whole row).
    /// </summary>
    public static (Rectangle Minus, Rectangle Value, Rectangle Plus)[] PanelRows(Rectangle area, int rows)
    {
        int width = StepSize.Width + PanelGap + ValueSize.Width + PanelGap + StepSize.Width;
        int x = area.Left + (area.Width - width) / 2;
        int y = area.Top + Margin + ButtonSize.Height + Margin;
        var result = new (Rectangle, Rectangle, Rectangle)[rows];
        for (int i = 0; i < rows; i++, y += StepSize.Height + PanelGap)
        {
            var minus = new Rectangle(new Point(x, y), StepSize);
            var value = new Rectangle(new Point(minus.Right + PanelGap, y), ValueSize);
            var plus = new Rectangle(new Point(value.Right + PanelGap, y), StepSize);
            result[i] = (minus, value, plus);
        }
        return result;
    }

    /// <summary><paramref name="content"/>'s shape, as large as fits in <paramref name="area"/>, centred.</summary>
    public static Rectangle Fit(Size content, Size area)
    {
        if (content.Width <= 0 || content.Height <= 0) return new Rectangle(Point.Empty, area);
        double scale = Math.Min((double)area.Width / content.Width, (double)area.Height / content.Height);
        int w = (int)Math.Round(content.Width * scale), h = (int)Math.Round(content.Height * scale);
        return new Rectangle((area.Width - w) / 2, (area.Height - h) / 2, w, h);
    }

    /// <summary>Maps <paramref name="p"/> from <paramref name="from"/>'s space linearly into <paramref name="to"/>'s.</summary>
    public static Point Map(Point p, Rectangle from, Rectangle to) => new(
        to.Left + (int)Math.Round((double)(p.X - from.Left) * to.Width / Math.Max(1, from.Width)),
        to.Top + (int)Math.Round((double)(p.Y - from.Top) * to.Height / Math.Max(1, from.Height)));

    public static Rectangle Map(Rectangle r, Rectangle from, Rectangle to)
    {
        Point a = Map(r.Location, from, to), b = Map(new Point(r.Right, r.Bottom), from, to);
        return Rectangle.FromLTRB(a.X, a.Y, b.X, b.Y);
    }

    /// <summary>The bottom bar's round buttons, eye px.</summary>
    public static readonly Size IconSize = new(56, 56);

    /// <summary>The time label's width, eye px.</summary>
    public const int TimeWidth = 260;

    /// <summary>The seek bar's drawn height, and how far above and below it a click still counts, eye px.</summary>
    public const int SeekHeight = 8, SeekReach = 16;

    /// <summary>
    /// The theater's bottom bar inside <paramref name="area"/> (eye px), like a video player's: the
    /// seek bar across the width, and under it the time at the left, back / play-pause / on in
    /// the middle and mute at the right; <c>Backdrop</c> is the darkened strip behind them.
    /// </summary>
    public static (Rectangle Backdrop, Rectangle Seek, Rectangle Time, Rectangle Back, Rectangle Play, Rectangle Forward, Rectangle Mute)
        BottomBar(Rectangle area)
    {
        int rowY = area.Bottom - Margin - IconSize.Height;
        int cx = area.Left + area.Width / 2;
        var play = new Rectangle(new Point(cx - IconSize.Width / 2, rowY), IconSize);
        var back = new Rectangle(new Point(play.Left - 2 * PanelGap - IconSize.Width, rowY), IconSize);
        var forward = new Rectangle(new Point(play.Right + 2 * PanelGap, rowY), IconSize);
        var mute = new Rectangle(new Point(area.Right - 2 * Margin - IconSize.Width, rowY), IconSize);
        var time = new Rectangle(area.Left + 2 * Margin, rowY, TimeWidth, IconSize.Height);
        var seek = Rectangle.FromLTRB(area.Left + 2 * Margin, rowY - Margin - SeekHeight, area.Right - 2 * Margin, rowY - Margin);
        var backdrop = Rectangle.FromLTRB(area.Left, seek.Top - 3 * Margin, area.Right, area.Bottom);
        return (backdrop, seek, time, back, play, forward, mute);
    }

    /// <summary>Where a click at <paramref name="x"/> lands on the seek bar, 0–1.</summary>
    public static double SeekFraction(Rectangle bar, int x) =>
        bar.Width <= 0 ? 0 : Math.Clamp((double)(x - bar.Left) / bar.Width, 0, 1);

    /// <summary>"1:05", or "1:02:03" from an hour; "--:--" while unknown.</summary>
    public static string Clock(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "--:--";
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>
    /// <paramref name="count"/> buttons in a row centred at the top of <paramref name="area"/>
    /// (eye px), left to right. The top centre: players keep their title at the top left, their
    /// own buttons (share, watch later, cast) at the top right and their controls along the
    /// bottom; the first place, the top right, covered and swallowed clicks on those.
    /// </summary>
    public static Rectangle[] TopCentre(Rectangle area, int count)
    {
        var result = new Rectangle[count];
        int width = count * ButtonSize.Width + (count - 1) * Margin;
        int x = area.Left + (area.Width - width) / 2;
        for (int i = 0; i < count; i++, x += ButtonSize.Width + Margin)
            result[i] = new Rectangle(new Point(x, area.Top + Margin), ButtonSize);
        return result;
    }
}
