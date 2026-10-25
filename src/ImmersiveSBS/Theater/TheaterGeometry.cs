using System.Drawing;

namespace ImmersiveSBS.Theater;

/// <summary>
/// Where the model badge goes: rectangles mapped between the virtual monitor and the eye. Pure:
/// no GPU, no Windows state.
/// </summary>
public static class TheaterGeometry
{
    /// <summary>The badge's height, eye px.</summary>
    public const int BadgeHeight = 56;

    /// <summary>Space from the video's edge to the badge, eye px.</summary>
    public const int Margin = 16;

    /// <summary>
    /// <paramref name="source"/> fitted into one eye as the renderer draws it: eye px per source px,
    /// and the picture's place in the eye, centred on whole px (which keeps a 1:1 picture sharp).
    /// </summary>
    public static (double Scale, Rectangle Fitted) Fit(Size source, Size eye)
    {
        double scale = Math.Min((double)eye.Width / source.Width, (double)eye.Height / source.Height);
        int w = (int)Math.Round(source.Width * scale), h = (int)Math.Round(source.Height * scale);
        return (scale, new Rectangle((eye.Width - w) / 2, (eye.Height - h) / 2, w, h));
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
}
