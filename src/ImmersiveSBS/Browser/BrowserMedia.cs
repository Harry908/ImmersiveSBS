using System.Drawing;
using System.Text.Json;

namespace ImmersiveSBS.Browser;

/// <summary>A rectangle in a page's CSS px.</summary>
public sealed record CssRect(double X, double Y, double Width, double Height);

/// <summary>A page viewport's size in CSS px.</summary>
public sealed record CssSize(double Width, double Height);

/// <summary>
/// One video or audio element as the extension reports it (extension/content.js `describe`):
/// its state, where its picture is in the tab's top-level viewport, and whether its sound can be
/// (and is) delayed inside the page.
/// </summary>
/// <param name="Id">Unique within the connection: "tab:frame:document#n".</param>
/// <param name="Rect">The picture (inside the element's letterbox), top-frame CSS px; null for audio or hidden video.</param>
/// <param name="Viewport">The tab's top-level viewport, CSS px; null until a frame learns it.</param>
/// <param name="Dpr">Physical px per CSS px (Windows scale × page zoom).</param>
/// <param name="Seeks">How many times the element has seeked; a change means a jump in the video.</param>
/// <param name="Reason">Why the sound cannot be delayed in the page: "protected", "cross-origin", "blocked", "source".</param>
/// <param name="Delay">The delay applied to its sound now, seconds.</param>
/// <param name="Latency">The page's Web Audio output latency, ms, once it has one.</param>
public sealed record BrowserMedia(
    string Id,
    int Tab,
    int Frame,
    string Kind,
    bool Playing,
    bool Audible,
    int Seeks,
    CssRect? Rect,
    CssSize? Viewport,
    double Dpr,
    bool Visible,
    bool Fullscreen,
    int VideoWidth,
    int VideoHeight,
    bool Delayable,
    string? Reason,
    double Delay,
    int? Latency)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsVideo => Kind == "video";

    /// <summary>Parses the "media" list of a report; entries that do not parse are left out.</summary>
    public static IReadOnlyList<BrowserMedia> ParseList(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) return [];
        var result = new List<BrowserMedia>(list.GetArrayLength());
        foreach (JsonElement item in list.EnumerateArray())
        {
            try
            {
                if (item.Deserialize<BrowserMedia>(Json) is { Id: not null, Kind: not null } m) result.Add(m);
            }
            catch (JsonException)
            {
            }
        }
        return result;
    }

    /// <summary>
    /// Where the picture is on the screen (physical px), given the browser window's client area
    /// there; null when it is not on that window's page. Chromium puts the page in the bottom-left
    /// of its client area, under the tab strip and toolbar (in full screen it fills it all), so the
    /// viewport's size in physical px places it.
    /// </summary>
    public Rectangle? ScreenRect(Rectangle client)
    {
        if (Rect is not { } r || Viewport is not { } v || Dpr <= 0 || r.Width < 2 || r.Height < 2) return null;
        double viewWidth = v.Width * Dpr, viewHeight = v.Height * Dpr;
        // Another window's page cannot be bigger than this window (a few px of rounding allowed).
        if (viewWidth > client.Width + 4 || viewHeight > client.Height + 4) return null;
        double left = client.Left, top = client.Bottom - viewHeight;
        var view = new Rectangle(client.Left, (int)Math.Round(top), (int)Math.Round(viewWidth), (int)Math.Round(viewHeight));
        var picture = Rectangle.FromLTRB(
            (int)Math.Round(left + r.X * Dpr), (int)Math.Round(top + r.Y * Dpr),
            (int)Math.Round(left + (r.X + r.Width) * Dpr), (int)Math.Round(top + (r.Y + r.Height) * Dpr));
        // Scrolled partly out of the page, only the shown part is on the screen.
        picture.Intersect(view);
        return picture.Width >= 16 && picture.Height >= 16 ? picture : null;
    }

    /// <summary>Intersection over union of two rectangles, 0 to 1.</summary>
    public static double Overlap(Rectangle a, Rectangle b)
    {
        Rectangle i = Rectangle.Intersect(a, b);
        if (i.IsEmpty) return 0;
        double inter = (double)i.Width * i.Height;
        return inter / ((double)a.Width * a.Height + (double)b.Width * b.Height - inter);
    }

    /// <summary>
    /// The visible video whose picture best covers <paramref name="region"/> (screen px) on the
    /// window with client area <paramref name="client"/>, if it overlaps by half or more.
    /// </summary>
    public static (T Owner, BrowserMedia Media, Rectangle Screen)? Match<T>(IEnumerable<(T Owner, BrowserMedia Media)> media,
        Rectangle client, Rectangle region, double minOverlap = 0.5)
    {
        (T, BrowserMedia, Rectangle)? best = null;
        double bestOverlap = minOverlap;
        foreach ((T owner, BrowserMedia m) in media)
        {
            if (!m.IsVideo || !m.Visible || m.ScreenRect(client) is not Rectangle screen) continue;
            double overlap = Overlap(screen, region);
            if (overlap < bestOverlap) continue;
            bestOverlap = overlap;
            best = (owner, m, screen);
        }
        return best;
    }
}
