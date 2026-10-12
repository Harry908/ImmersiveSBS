using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ImmersiveSBS.Theater;

/// <summary>A small BGRA image (straight alpha) the renderer draws in the glasses; uploaded once.</summary>
public sealed class UiImage(byte[] bgra, int width, int height)
{
    public byte[] Bgra { get; } = bgra;

    public int Width { get; } = width;

    public int Height { get; } = height;
}

/// <summary>The depth model badges shown in the glasses while 3D is wanted.</summary>
public enum BadgeKind
{
    Loading,
    Preparing,
    Ready,
    Failed,
    Missing,
}

/// <summary>
/// The model badges' pictures: a dark rounded pill with a spinner (<see cref="SpinnerFrames"/>
/// pre-drawn turns) or a Segoe Fluent icon, and a label. Drawn once with System.Drawing.
/// </summary>
public static class ButtonImages
{
    /// <summary>Spinner pictures per turn; one turn a second.</summary>
    public const int SpinnerFrames = 12;

    private static readonly Dictionary<(BadgeKind, int), UiImage> Badges = [];

    /// <summary>The spinner picture due now.</summary>
    public static int SpinnerFrame() =>
        (int)(Stopwatch.GetTimestamp() * SpinnerFrames / Stopwatch.Frequency % SpinnerFrames);

    /// <summary>A model badge; <paramref name="frame"/> picks the spinner's turn for the loading kinds.</summary>
    public static UiImage Badge(BadgeKind kind, int frame)
    {
        if (kind is not (BadgeKind.Loading or BadgeKind.Preparing)) frame = 0;
        lock (Badges)
        {
            if (!Badges.TryGetValue((kind, frame), out UiImage? image))
                Badges[(kind, frame)] = image = DrawBadge(kind, frame);
            return image;
        }
    }

    private static UiImage DrawBadge(BadgeKind kind, int frame)
    {
        string label = kind switch
        {
            BadgeKind.Loading => "Loading 3D…",
            BadgeKind.Preparing => "Preparing 3D for this GPU (about 20 s)…",
            BadgeKind.Ready => "3D ready",
            BadgeKind.Failed => "3D unavailable: see Settings",
            _ => "3D model missing: see Settings",
        };
        int height = TheaterGeometry.BadgeHeight;
        using var text = new Font("Segoe UI", 20, FontStyle.Regular, GraphicsUnit.Pixel);
        int width;
        using (var measure = new Bitmap(1, 1))
        using (Graphics m = Graphics.FromImage(measure))
            width = 56 + (int)Math.Ceiling(m.MeasureString(label, text).Width) + 20;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            DrawPill(g, new Size(width, height));

            using var white = new SolidBrush(Color.White);
            var center = new StringFormat { LineAlignment = StringAlignment.Center };
            var accent = Color.FromArgb(255, 96, 205, 255);
            if (kind is BadgeKind.Loading or BadgeKind.Preparing)
            {
                // A three-quarter ring, turned by the frame.
                using var faint = new Pen(Color.FromArgb(70, 255, 255, 255), 3.5f);
                using var ring = new Pen(accent, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                var circle = new RectangleF(28, height / 2f - 10, 20, 20);
                g.DrawEllipse(faint, circle);
                g.DrawArc(ring, circle, frame * 360f / SpinnerFrames - 90, 270);
            }
            else
            {
                (string glyph, Color colour) = kind == BadgeKind.Ready
                    ? ("", accent)                          // CheckMark
                    : ("", Color.FromArgb(255, 255, 196, 64)); // Warning
                using var icon = new Font(IconFont(), 18, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(colour);
                g.DrawString(glyph, icon, brush, new RectangleF(24, 0, 32, height), center);
            }
            g.DrawString(label, text, white, new RectangleF(56, 0, width - 60, height), center);
        }
        return ToImage(bitmap);
    }

    private static void DrawPill(Graphics g, Size size)
    {
        var rect = new Rectangle(1, 1, size.Width - 3, size.Height - 3);
        using GraphicsPath pill = RoundedRect(rect, size.Height / 2 - 2);
        using var fill = new SolidBrush(Color.FromArgb(200, 24, 24, 26));
        using var border = new Pen(Color.FromArgb(120, 255, 255, 255), 2);
        g.FillPath(fill, pill);
        g.DrawPath(border, pill);
    }

    private static UiImage ToImage(Bitmap bitmap)
    {
        Size size = bitmap.Size;
        var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bgra = new byte[size.Width * size.Height * 4];
            for (int y = 0; y < size.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * size.Width * 4, size.Width * 4);
            return new UiImage(bgra, size.Width, size.Height);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static string IconFont() => FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    private static bool FontExists(string name)
    {
        using var families = new InstalledFontCollection();
        return families.Families.Any(f => f.Name == name);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }
}
