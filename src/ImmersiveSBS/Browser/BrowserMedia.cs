using System.Text.Json;

namespace ImmersiveSBS.Browser;

/// <summary>A rectangle in a page's CSS px.</summary>
public sealed record CssRect(double X, double Y, double Width, double Height);

/// <summary>
/// One video or audio element as the extension reports it (extension/content.js `describe`):
/// what the page's delayed sound needs, and whether the theater shows it.
/// </summary>
/// <param name="Id">Unique within the connection: "tab:frame:document#n".</param>
/// <param name="Area">The picture's size on the page, CSS px², 0 while it is not shown.</param>
/// <param name="Reason">Why the sound cannot be delayed in the page: "protected", "cross-origin", "blocked", "source".</param>
/// <param name="Delay">The delay applied to its sound now, seconds.</param>
/// <param name="Latency">The page's Web Audio output latency, ms, once it has one.</param>
/// <param name="Theater">The extension's 3D theater shows this video.</param>
public sealed record BrowserMedia(
    string Id,
    int Tab,
    int Frame,
    string Kind,
    bool Playing,
    bool Audible,
    double Area,
    int VideoWidth,
    int VideoHeight,
    bool Delayable,
    string? Reason,
    double Delay,
    int? Latency,
    bool Theater = false)
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
}

/// <summary>
/// The extension's 3D theater, as its page reports it: which video, in which tab, where the
/// player shows the picture (the viewport's CSS px, <see cref="Dpr"/> physical px each), and
/// whether the viewport is the whole screen (the page or the window is full screen): only then
/// is that also its place on the screen.
/// </summary>
/// <param name="Since">When it opened (Stopwatch ticks): the newest one is shown, should two be open.</param>
/// <param name="View">What the page measured to decide <see cref="OnScreen"/>, for the log.</param>
public sealed record TheaterReport(string MediaId, int Tab, CssRect? Picture, double Dpr, bool OnScreen, long Since, string View = "")
{
    /// <summary>A box of the page's viewport in physical px from the screen's corner (the viewport is the screen).</summary>
    public System.Drawing.Rectangle ToScreen(CssRect r) => System.Drawing.Rectangle.FromLTRB(
        (int)Math.Round(r.X * Dpr), (int)Math.Round(r.Y * Dpr),
        (int)Math.Round((r.X + r.Width) * Dpr), (int)Math.Round((r.Y + r.Height) * Dpr));
}
