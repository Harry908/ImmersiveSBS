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

    /// <summary>
    /// <paramref name="count"/> buttons in a row ending at the top-right corner of
    /// <paramref name="area"/> (eye px), rightmost last.
    /// </summary>
    public static Rectangle[] TopRight(Rectangle area, int count)
    {
        var result = new Rectangle[count];
        int x = area.Right - Margin - ButtonSize.Width;
        for (int i = count - 1; i >= 0; i--)
        {
            result[i] = new Rectangle(new Point(x, area.Top + Margin), ButtonSize);
            x -= ButtonSize.Width + Margin;
        }
        return result;
    }
}
