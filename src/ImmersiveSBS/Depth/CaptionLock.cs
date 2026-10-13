using System.Drawing;

namespace ImmersiveSBS.Depth;

/// <summary>
/// Keeps subtitles flat and readable. The model gives a caption the depth of whatever is behind
/// it, so its letters bend and flicker with the scene. This finds subtitle-like text in the lower
/// part of the picture (rows with several bright strokes, each outlined by something much darker
/// on both sides, in a short, centred band that holds still) and gives that band one depth just
/// in front of what is behind it. Unlike the static overlays of §6.5 it does not need the text
/// to stay a second, so changing captions are caught. At model resolution on the CPU; pure.
/// It has to be strict: a busy scene (foliage, patterns) also has bright strokes beside dark
/// ones, and the first version, which joined every such band in the lower picture, flattened
/// whole blocks of it, a seam between the top and the bottom of the picture.
/// </summary>
public sealed class CaptionLock
{
    private readonly int width, height;
    private readonly float[] mask;
    private readonly bool[] inBox;
    private readonly float[] boxValues;
    private readonly int[] rowStrokes, rowLefts, rowRights;
    private double target = double.NaN;
    private Rectangle candidate;
    private double candidateSeconds;
    private double sinceLost = double.PositiveInfinity;

    public CaptionLock(int width, int height)
    {
        this.width = width;
        this.height = height;
        mask = new float[width * height];
        inBox = new bool[width * height];
        boxValues = new float[width * height];
        rowStrokes = new int[height];
        rowLefts = new int[height];
        rowRights = new int[height];
    }

    /// <summary>Captions are looked for below this fraction of the height.</summary>
    public double SearchFrom { get; set; } = 0.55;

    /// <summary>A stroke pixel is at least this bright (luma 0..1)...</summary>
    public float StrokeLuma { get; set; } = 0.6f;

    /// <summary>...and this much brighter than a pixel within <see cref="OutlineReach"/> of it.</summary>
    public float OutlineContrast { get; set; } = 0.35f;

    /// <summary>How far the dark outline or shadow may be from a stroke, px at model resolution.</summary>
    public int OutlineReach { get; set; } = 2;

    /// <summary>Strokes a row needs to count as a row of text.</summary>
    public int MinStrokes { get; set; } = 4;

    /// <summary>How long a band must hold still (top and bottom within a few px) to count, seconds.</summary>
    public double StableSeconds { get; set; } = 0.3;

    /// <summary>
    /// Within <see cref="RecentSeconds"/> of losing a box, a new one must hold this long: one run
    /// logged a false box flipping between found and gone for minutes (improvement-plan.md §11).
    /// </summary>
    public double RecentStableSeconds { get; set; } = 1;

    public double RecentSeconds { get; set; } = 5;

    /// <summary>How far in front of the scene behind it the caption sits, in nearness.</summary>
    public float Margin { get; set; } = 0.03f;

    /// <summary>The caption box found in the last frame (model px), if any.</summary>
    public Rectangle? Found { get; private set; }

    /// <summary>Raised when a caption starts or stops being found, with its box or null; on the caller's thread.</summary>
    public event Action<Rectangle?>? Changed;

    /// <summary>
    /// Finds the caption in <paramref name="luma"/> and flattens it in <paramref name="nearness"/>
    /// (both at this size). <paramref name="screenPlane"/> is the convergence: a caption is never
    /// put behind the screen. <paramref name="given"/>, when it has boxes, are the captions as the
    /// page reports them (model px): they are used instead of looking (improvement-plan.md §7.4).
    /// Text under the page's <paramref name="overlays"/> (its controls) is not taken for captions.
    /// </summary>
    public void Apply(float[] luma, float[] nearness, double dtSeconds, double screenPlane,
        IReadOnlyList<Rectangle>? given = null, IReadOnlyList<Rectangle>? overlays = null)
    {
        double dt = Math.Clamp(dtSeconds, 1.0 / 240, 1.0);
        Array.Clear(inBox);
        Rectangle? was = Found;
        var frame = new Rectangle(0, 0, width, height);
        if (given is { Count: > 0 })
        {
            // The page's captions: exact, and no false boxes. Detection starts afresh after.
            Rectangle union = Rectangle.Empty;
            foreach (Rectangle g in given)
            {
                Rectangle r = Rectangle.Intersect(g, frame);
                if (r.Width <= 0 || r.Height <= 0) continue;
                union = union.IsEmpty ? r : Rectangle.Union(union, r);
                for (int y = r.Top; y < r.Bottom; y++)
                    for (int x = r.Left; x < r.Right; x++) inBox[y * width + x] = true;
            }
            Found = union.IsEmpty ? null : union;
            candidate = Rectangle.Empty;
            candidateSeconds = 0;
        }
        else
        {
            Found = Stable(Detect(luma, overlays), dt);
            if ((was == null) != (Found == null)) Changed?.Invoke(Found);
            if (was != null && Found == null) sinceLost = 0;
            else if (Found == null) sinceLost += dt;
            if (Found is Rectangle detected)
                for (int y = detected.Top; y < detected.Bottom; y++)
                    for (int x = detected.Left; x < detected.Right; x++) inBox[y * width + x] = true;
        }

        if (Found is Rectangle box && box.Width > 0 && box.Height > 0)
        {
            // In front of the nearest of what is behind it (its 90th percentile), at least at the
            // screen plane; smoothed so a caption does not pump with the scene.
            int n = 0;
            for (int y = box.Top; y < box.Bottom; y++)
                for (int i = y * width + box.Left; i < y * width + box.Right; i++)
                    if (inBox[i]) boxValues[n++] = nearness[i];
            if (n == 0) return;
            float p90 = Selection.Nth(boxValues.AsSpan(0, n), (int)(0.9 * (n - 1)));
            double wanted = Math.Min(1, Math.Max(p90, screenPlane) + Margin);
            target = double.IsNaN(target) ? wanted : target + (wanted - target) * (1 - Math.Exp(-dt / 0.3));
        }
        if (double.IsNaN(target)) return;

        // The mask rises fast and fades slowly, so a caption missed for a frame does not blink.
        // The page's captions are exact: flat from their first frame, or a new line's letters
        // bend with the scene behind them while the mask rises (it looked torn).
        float rise = given is { Count: > 0 } ? 1 : (float)(1 - Math.Exp(-dt / 0.1)), fall = (float)(1 - Math.Exp(-dt / 0.4));
        float level = (float)target;
        RowBands.For(height, (_, from, to) =>
        {
            for (int i = from * width; i < to * width; i++)
            {
                float m = mask[i];
                m += inBox[i] ? (1 - m) * rise : -m * fall;
                mask[i] = m;
                if (m > 0.001f) nearness[i] += (level - nearness[i]) * m;
            }
        });
    }

    /// <summary>A detection counts once its top and bottom have held for <see cref="StableSeconds"/>.</summary>
    private Rectangle? Stable(Rectangle? detected, double dt)
    {
        if (detected is not Rectangle box)
        {
            candidateSeconds = 0;
            candidate = Rectangle.Empty;
            return null;
        }
        // The width changes with each new line of text; the lines stay where they are. Compared
        // with where the band first appeared, so a slow drift (scene texture) never adds up.
        bool same = !candidate.IsEmpty && Math.Abs(box.Top - candidate.Top) <= 3 && Math.Abs(box.Bottom - candidate.Bottom) <= 3;
        if (same) candidateSeconds += dt;
        else
        {
            candidate = box;
            candidateSeconds = 0;
        }
        return candidateSeconds >= (sinceLost < RecentSeconds ? RecentStableSeconds : StableSeconds) ? box : null;
    }

    /// <summary>
    /// The subtitle box in the search area, or null: the lowest band of text rows shaped like a
    /// line of subtitles (short, wide enough, centred in the middle half), with the line right
    /// above it if there is one (two-line subtitles); never more than a fifth of the picture.
    /// The middle half, not the middle three fifths: a logged false box sat at the edge of the
    /// wider band, while real subtitles in the same log stayed inside the narrower one. Not wider
    /// than 85 % of the picture, nor mostly under a box the page draws over it: in the second
    /// run, a player's control bar (96–99 % wide) was found and lost every few seconds.
    /// </summary>
    private Rectangle? Detect(float[] luma, IReadOnlyList<Rectangle>? overlays)
    {
        int top = (int)(SearchFrom * height);
        int bandStart = -1, bandEnd = -1, gap = 0, left = width, right = -1;
        var bands = new List<Rectangle>();

        // Each row's strokes, rows in parallel; then the bands, in order.
        RowBands.For(height - top, (_, from, to) =>
        {
            for (int y = top + from; y < top + to; y++)
                rowStrokes[y] = Strokes(luma.AsSpan(y * width, width), out rowLefts[y], out rowRights[y]);
        });

        for (int y = top; y <= height; y++)
        {
            bool text = false;
            int rowLeft = width, rowRight = -1;
            if (y < height)
            {
                text = rowStrokes[y] >= MinStrokes;
                rowLeft = rowLefts[y];
                rowRight = rowRights[y];
            }

            if (text)
            {
                if (bandStart < 0) bandStart = y;
                bandEnd = y;
                gap = 0;
                left = Math.Min(left, rowLeft);
                right = Math.Max(right, rowRight);
            }
            else if (bandStart >= 0 && (++gap > 2 || y == height))
            {
                // A band of text rows ended: keep it if it is shaped like a line of subtitles.
                var band = Rectangle.FromLTRB(left, bandStart, right + 1, bandEnd + 1);
                int centre = band.Left + band.Width / 2;
                bool shaped = band.Height >= Math.Max(3, height / 50) && band.Height <= height / 8
                              && band.Width >= width / 16 && band.Width <= width * 85 / 100
                              && centre > width / 4 && centre < width - width / 4
                              && !UnderOverlay(band, overlays);
                if (shaped) bands.Add(band);
                bandStart = -1;
                left = width;
                right = -1;
            }
        }

        if (bands.Count == 0) return null;
        Rectangle found = bands[^1];
        if (bands.Count > 1)
        {
            Rectangle above = bands[^2];
            if (found.Top - above.Bottom <= Math.Max(4, found.Height)) found = Rectangle.Union(found, above);
        }
        if (found.Height > height / 5) return null;
        found.Inflate(2, 2);
        found.Intersect(new Rectangle(0, 0, width, height));
        return found;
    }

    /// <summary>Whether half or more of <paramref name="band"/> lies under the page's overlay boxes.</summary>
    private static bool UnderOverlay(Rectangle band, IReadOnlyList<Rectangle>? overlays)
    {
        if (overlays is not { Count: > 0 }) return false;
        long covered = 0;
        foreach (Rectangle o in overlays)
        {
            Rectangle r = Rectangle.Intersect(band, o);
            if (r.Width > 0 && r.Height > 0) covered += (long)r.Width * r.Height;
        }
        return covered * 2 >= (long)band.Width * band.Height;
    }

    /// <summary>Bright runs in a row outlined by a much darker pixel within reach on both sides.</summary>
    private int Strokes(ReadOnlySpan<float> row, out int left, out int right)
    {
        left = width;
        right = -1;
        int strokes = 0;
        for (int x = 0; x < width;)
        {
            if (row[x] < StrokeLuma)
            {
                x++;
                continue;
            }
            int start = x;
            float peak = 0;
            while (x < width && row[x] >= StrokeLuma) peak = Math.Max(peak, row[x++]);
            int end = x - 1;
            // Wider than a letter stroke is a bright area, not text.
            if (end - start > 3 * OutlineReach + 2) continue;

            bool leftDark = false, rightDark = false;
            for (int d = 1; d <= OutlineReach; d++)
            {
                if (start - d >= 0 && peak - row[start - d] >= OutlineContrast) leftDark = true;
                if (end + d < width && peak - row[end + d] >= OutlineContrast) rightDark = true;
            }
            if (!leftDark || !rightDark) continue;
            strokes++;
            left = Math.Min(left, start);
            right = Math.Max(right, end);
        }
        return strokes;
    }
}
