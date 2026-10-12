using System.Numerics;

namespace ImmersiveSBS.Depth;

/// <summary>
/// Soft depth edges (improvement-plan.md §7.1, iw3's "edge fix"). The forward warp leaves a hole
/// wherever nearness changes by more than 2/S per screen pixel (§2.1), and the model's sharp
/// edges always do. Each pass grows the near side by about one model pixel through a slightly
/// blurred maximum, blended in only where the depth changes steeply, so a step becomes a ramp
/// the warp can stretch over and flat depth is untouched:
/// r = max − min over the window, w = its clipped z-score scaled to 0..1,
/// N ← (1 − w)·N + w·max(G * N), G ≈ a Gaussian of σ 0.76 px.
/// A pass works along x, y or both: disparity is horizontal, so more passes are needed across
/// than down. Pure, at model resolution: five sweeps over the map a pass, row by row with SIMD,
/// each split over bands of rows on several cores (<see cref="RowBands"/>); one call at a time.
/// </summary>
public sealed class SoftEdges
{
    /// <summary>The 3-tap kernel whose outer product is G: (73, 110, 73)/256.</summary>
    private const float Side = 73f / 256, Centre = 110f / 256;

    private static readonly int V = Vector<float>.Count;

    private readonly int width, height;
    private readonly float[] lo, hi, range, work;

    public SoftEdges(int width, int height)
    {
        this.width = width;
        this.height = height;
        int n = width * height;
        lo = new float[n];
        hi = new float[n];
        range = new float[n];
        work = new float[n];
    }

    /// <summary>
    /// How many passes along x a step of <paramref name="referenceStep"/> needs to be spread over
    /// the §2.1 width Δn·S/2 (screen px), given <paramref name="strength"/> S and the region's
    /// source px per model px; 1 to 4.
    /// </summary>
    public static int AutoPasses(double strength, double regionWidth, double modelWidth, double referenceStep = 0.4)
    {
        if (strength <= 0 || regionWidth <= 0 || modelWidth <= 0) return 1;
        double wanted = referenceStep * strength / 2 / (regionWidth / modelWidth);
        return Math.Clamp((int)Math.Ceiling(wanted - 1e-9), 1, 4);
    }

    /// <summary><paramref name="passesX"/> passes along x, the first <paramref name="passesY"/> of them along y too.</summary>
    public void Apply(float[] nearness, int passesX, int passesY)
    {
        if (nearness.Length != width * height) throw new ArgumentException("Size does not match.");
        int passes = Math.Max(passesX, passesY);
        for (int p = 0; p < passes; p++) Pass(nearness, p < passesX, p < passesY);
    }

    private void Pass(float[] n, bool alongX, bool alongY)
    {
        int w = width, h = height;

        // 1. Along the rows: each window's lowest and highest depth.
        if (alongX) RowBands.For(h, (_, from, to) => MinMaxAlongRows(n, from, to));
        else
        {
            n.CopyTo(lo, 0);
            n.CopyTo(hi, 0);
        }

        // 2. Down the columns: the same, their difference (the range: where depth changes
        // steeply), and the range's mean, deviation and extremes (summed per band).
        int bands = RowBands.Count(h);
        var sums = new double[bands];
        var squares = new double[bands];
        var lows = new float[bands];
        var highs = new float[bands];
        RowBands.For(h, (band, from, to) =>
        {
            double sum = 0, sumSq = 0;
            var vMin = new Vector<float>(float.MaxValue);
            var vMax = new Vector<float>(float.MinValue);
            float rMin = float.MaxValue, rMax = float.MinValue;
            for (int y = from; y < to; y++)
            {
                int up = (alongY ? Math.Max(y - 1, 0) : y) * w, mid = y * w, down = (alongY ? Math.Min(y + 1, h - 1) : y) * w;
                Vector<float> s = Vector<float>.Zero, q = Vector<float>.Zero;
                int x = 0;
                for (; x + V <= w; x += V)
                {
                    Vector<float> a = Vector.Min(new Vector<float>(lo, up + x), Vector.Min(new Vector<float>(lo, mid + x), new Vector<float>(lo, down + x)));
                    Vector<float> b = Vector.Max(new Vector<float>(hi, up + x), Vector.Max(new Vector<float>(hi, mid + x), new Vector<float>(hi, down + x)));
                    Vector<float> r = b - a;
                    r.CopyTo(range, mid + x);
                    s += r;
                    q += r * r;
                    vMin = Vector.Min(vMin, r);
                    vMax = Vector.Max(vMax, r);
                }
                sum += Vector.Sum(s);
                sumSq += Vector.Sum(q);
                for (; x < w; x++)
                {
                    float r = Math.Max(hi[up + x], Math.Max(hi[mid + x], hi[down + x])) - Math.Min(lo[up + x], Math.Min(lo[mid + x], lo[down + x]));
                    range[mid + x] = r;
                    sum += r;
                    sumSq += (double)r * r;
                    rMin = Math.Min(rMin, r);
                    rMax = Math.Max(rMax, r);
                }
            }
            for (int k = 0; k < V; k++)
            {
                rMin = Math.Min(rMin, vMin[k]);
                rMax = Math.Max(rMax, vMax[k]);
            }
            sums[band] = sum;
            squares[band] = sumSq;
            lows[band] = rMin;
            highs[band] = rMax;
        });
        double sum = sums.Sum(), sumSq = squares.Sum();
        float rMin = lows.Min(), rMax = highs.Max();
        int count = w * h;
        double mean = sum / count;
        double sd = Math.Sqrt(Math.Max(0, sumSq / count - mean * mean));
        if (sd < 1e-6) return;   // flat: no edges

        // The weight: z clipped to ±3, scaled so the least steep pixel gets 0 and the steepest 1.
        float inverse = (float)(1 / sd), m0 = (float)mean;
        float zMin = Math.Clamp((rMin - m0) * inverse, -3, 3), zMax = Math.Clamp((rMax - m0) * inverse, -3, 3);
        if (zMax - zMin < 1e-6f) return;
        float scale = 1 / (zMax - zMin);

        // 3. Along the rows: the blur.
        if (alongX) RowBands.For(h, (_, from, to) => BlurAlongRows(n, from, to));
        else n.CopyTo(work, 0);

        // 4. Down the columns: the blur, then along that row its maximum (into lo).
        RowBands.For(h, (_, from, to) =>
        {
            Span<float> row = w <= 4096 ? stackalloc float[w] : new float[w];
            for (int y = from; y < to; y++)
            {
                int up = (alongY ? Math.Max(y - 1, 0) : y) * w, mid = y * w, down = (alongY ? Math.Min(y + 1, h - 1) : y) * w;
                int x = 0;
                if (alongY)
                {
                    for (; x + V <= w; x += V)
                        (new Vector<float>(work, mid + x) * Centre + (new Vector<float>(work, up + x) + new Vector<float>(work, down + x)) * Side).CopyTo(row.Slice(x));
                    for (; x < w; x++) row[x] = Centre * work[mid + x] + Side * (work[up + x] + work[down + x]);
                }
                else
                {
                    work.AsSpan(mid, w).CopyTo(row);
                }
                if (alongX) MaxAlongRow(row, lo.AsSpan(mid, w));
                else row.CopyTo(lo.AsSpan(mid, w));
            }
        });

        // 5. Down the columns: the maximum, blended in by the weight.
        RowBands.For(h, (_, from, to) =>
        {
            var vMean = new Vector<float>(m0);
            var vInverse = new Vector<float>(inverse);
            var vZMin = new Vector<float>(zMin);
            var vScale = new Vector<float>(scale);
            var three = new Vector<float>(3f);
            for (int y = from; y < to; y++)
            {
                int up = (alongY ? Math.Max(y - 1, 0) : y) * w, mid = y * w, down = (alongY ? Math.Min(y + 1, h - 1) : y) * w;
                Span<float> nRow = n.AsSpan(mid, w);
                int x = 0;
                for (; x + V <= w; x += V)
                {
                    Vector<float> m = Vector.Max(new Vector<float>(lo, up + x), Vector.Max(new Vector<float>(lo, mid + x), new Vector<float>(lo, down + x)));
                    Vector<float> z = Vector.Min(three, Vector.Max(-three, (new Vector<float>(range, mid + x) - vMean) * vInverse));
                    var v = new Vector<float>(nRow.Slice(x));
                    (v + (m - v) * ((z - vZMin) * vScale)).CopyTo(nRow.Slice(x));
                }
                for (; x < w; x++)
                {
                    float m = Math.Max(lo[up + x], Math.Max(lo[mid + x], lo[down + x]));
                    float z = Math.Clamp((range[mid + x] - m0) * inverse, -3, 3);
                    nRow[x] += (m - nRow[x]) * (z - zMin) * scale;
                }
            }
        });
    }

    private void MinMaxAlongRows(float[] n, int from, int to)
    {
        int w = width;
        for (int y = from; y < to; y++)
        {
            ReadOnlySpan<float> s = n.AsSpan(y * w, w);
            Span<float> a = lo.AsSpan(y * w, w), b = hi.AsSpan(y * w, w);
            if (w == 1)
            {
                a[0] = b[0] = s[0];
                continue;
            }
            a[0] = Math.Min(s[0], s[1]);
            b[0] = Math.Max(s[0], s[1]);
            int x = 1;
            for (; x + V < w; x += V)
            {
                Vector<float> l = new(s.Slice(x - 1)), c = new(s.Slice(x)), r = new(s.Slice(x + 1));
                Vector.Min(l, Vector.Min(c, r)).CopyTo(a.Slice(x));
                Vector.Max(l, Vector.Max(c, r)).CopyTo(b.Slice(x));
            }
            for (; x < w - 1; x++)
            {
                a[x] = Math.Min(s[x - 1], Math.Min(s[x], s[x + 1]));
                b[x] = Math.Max(s[x - 1], Math.Max(s[x], s[x + 1]));
            }
            a[w - 1] = Math.Min(s[w - 2], s[w - 1]);
            b[w - 1] = Math.Max(s[w - 2], s[w - 1]);
        }
    }

    private void BlurAlongRows(float[] n, int from, int to)
    {
        int w = width;
        for (int y = from; y < to; y++)
        {
            ReadOnlySpan<float> s = n.AsSpan(y * w, w);
            Span<float> d = work.AsSpan(y * w, w);
            if (w == 1)
            {
                d[0] = s[0];
                continue;
            }
            d[0] = Centre * s[0] + Side * (s[0] + s[1]);
            int x = 1;
            for (; x + V < w; x += V)
                (new Vector<float>(s.Slice(x)) * Centre + (new Vector<float>(s.Slice(x - 1)) + new Vector<float>(s.Slice(x + 1))) * Side).CopyTo(d.Slice(x));
            for (; x < w - 1; x++) d[x] = Centre * s[x] + Side * (s[x - 1] + s[x + 1]);
            d[w - 1] = Centre * s[w - 1] + Side * (s[w - 2] + s[w - 1]);
        }
    }

    private static void MaxAlongRow(ReadOnlySpan<float> s, Span<float> d)
    {
        int w = s.Length;
        if (w == 1)
        {
            d[0] = s[0];
            return;
        }
        d[0] = Math.Max(s[0], s[1]);
        int x = 1;
        for (; x + V < w; x += V)
            Vector.Max(new Vector<float>(s.Slice(x - 1)), Vector.Max(new Vector<float>(s.Slice(x)), new Vector<float>(s.Slice(x + 1)))).CopyTo(d.Slice(x));
        for (; x < w - 1; x++) d[x] = Math.Max(s[x - 1], Math.Max(s[x], s[x + 1]));
        d[w - 1] = Math.Max(s[w - 2], s[w - 1]);
    }
}

/// <summary>
/// Depth anti-aliasing without a network (improvement-plan.md §7.2, after Depth3D): Depth
/// Anything V2 and 3 give depth edges that step in whole model pixels, which the warp turns into
/// jagged outlines (doors, frames). Along each straight, clear edge the depth is smoothed in the
/// edge's own direction, never across it: the structure tensor gives the direction and how
/// clearly there is one (coherence C); a 9-tap Gaussian of σ 2 px along it, folded into five
/// bilinear taps, is blended in by smoothstep(0.5, 0.8, C). (The plan's 0.7–0.9 half-smoothed
/// gentle staircases, whose alternating gradient gives them only C ≈ 0.8.) Corners and texture
/// are left alone. Pure, at model resolution: four sweeps over the map with SIMD, then work at
/// the edge pixels only, each split over bands of rows (<see cref="RowBands"/>); one call at a
/// time.
/// </summary>
public sealed class DepthAntiAlias
{
    /// <summary>The 9-tap Gaussian e^(−k²/8) folded into bilinear taps (Depth3D's numbers, checked).</summary>
    private const float Offset1 = 1.40733f, Offset2 = 3.29421f, Weight1 = 1.48903f, Weight2 = 0.45999f;

    private const float Norm = 1 / (1 + 2 * (Weight1 + Weight2));

    /// <summary>
    /// The structure tensor's smoothing: (1 4 6 4 1)/16 applied twice, as one 9-tap binomial
    /// (1 8 28 56 70 56 28 8 1)/256 each way (σ ≈ 1.4 px). A staircase of gentle slope has
    /// straight runs of several pixels, and only over more than one run does its direction show.
    /// </summary>
    private static readonly float[] Binomial = [1 / 256f, 8 / 256f, 28 / 256f, 56 / 256f, 70 / 256f, 56 / 256f, 28 / 256f, 8 / 256f, 1 / 256f];

    private static readonly int V = Vector<float>.Count;

    private readonly int width, height;
    private readonly float[] gx, gy, jxx, jxy, jyy, copy, tXX, tXY, tYY;

    public DepthAntiAlias(int width, int height)
    {
        this.width = width;
        this.height = height;
        int n = width * height;
        gx = new float[n];
        gy = new float[n];
        jxx = new float[n];
        jxy = new float[n];
        jyy = new float[n];
        copy = new float[n];
        tXX = new float[n];
        tXY = new float[n];
        tYY = new float[n];
    }

    /// <summary>Gradient magnitude (nearness per model px) below which a pixel is not on an edge.</summary>
    public float MinGradient { get; set; } = 0.01f;

    public void Apply(float[] nearness)
    {
        if (nearness.Length != width * height) throw new ArgumentException("Size does not match.");
        nearness.CopyTo(copy, 0);
        RowBands.For(height, (_, from, to) => Gradients(from, to));
        RowBands.For(height, (_, from, to) => SmoothAlongRows(from, to));
        RowBands.For(height, (_, from, to) => SmoothDownColumns(from, to));
        RowBands.For(height, (_, from, to) => AlongEdges(nearness, from, to));
    }

    /// <summary>Each clear edge pixel in rows [from, to) smoothed along its edge, from the unfiltered copy.</summary>
    private void AlongEdges(float[] nearness, int from, int to)
    {
        float minSq = MinGradient * MinGradient;
        for (int y = from; y < to; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                if (gx[i] * gx[i] + gy[i] * gy[i] <= minSq) continue;
                float a = jxx[i], b = jxy[i], c = jyy[i];
                float trace = a + c;
                if (trace <= 1e-12f) continue;
                float diff = a - c;
                float root = MathF.Sqrt(diff * diff / 4 + b * b);
                float coherence = 4 * root * root / (trace * trace);   // ((λ1 − λ2)/(λ1 + λ2))²
                float beta = SmoothStep(0.5f, 0.8f, coherence);
                if (beta <= 0) continue;

                // The gradient's direction is λ1's eigenvector; the edge runs across it.
                float l1 = trace / 2 + root;
                float vx = b, vy = l1 - a;
                if (MathF.Abs(vx) + MathF.Abs(vy) < 1e-12f) (vx, vy) = a >= c ? (1f, 0f) : (0f, 1f);
                float length = MathF.Sqrt(vx * vx + vy * vy);
                float tx = -vy / length, ty = vx / length;

                float filtered = Norm * (copy[i]
                    + Weight1 * (Sample(x + Offset1 * tx, y + Offset1 * ty) + Sample(x - Offset1 * tx, y - Offset1 * ty))
                    + Weight2 * (Sample(x + Offset2 * tx, y + Offset2 * ty) + Sample(x - Offset2 * tx, y - Offset2 * ty)));
                nearness[i] = copy[i] + beta * (filtered - copy[i]);
            }
        }
    }

    /// <summary>Sobel gradients of the unfiltered nearness (per px, divided by 8) and the tensor's entries gx², gx·gy, gy²; borders clamped.</summary>
    private void Gradients(int from, int to)
    {
        int w = width;
        for (int y = from; y < to; y++)
        {
            ReadOnlySpan<float> up = copy.AsSpan(Math.Max(y - 1, 0) * w, w);
            ReadOnlySpan<float> mid = copy.AsSpan(y * w, w);
            ReadOnlySpan<float> down = copy.AsSpan(Math.Min(y + 1, height - 1) * w, w);
            int o = y * w;
            int x = 1;
            for (; x + V < w; x += V)
            {
                Vector<float> ul = new(up.Slice(x - 1)), uc = new(up.Slice(x)), ur = new(up.Slice(x + 1));
                Vector<float> ml = new(mid.Slice(x - 1)), mr = new(mid.Slice(x + 1));
                Vector<float> dl = new(down.Slice(x - 1)), dc = new(down.Slice(x)), dr = new(down.Slice(x + 1));
                Vector<float> dx = (ur + mr * 2 + dr - (ul + ml * 2 + dl)) * 0.125f;
                Vector<float> dy = (dl + dc * 2 + dr - (ul + uc * 2 + ur)) * 0.125f;
                dx.CopyTo(gx, o + x);
                dy.CopyTo(gy, o + x);
                (dx * dx).CopyTo(jxx, o + x);
                (dx * dy).CopyTo(jxy, o + x);
                (dy * dy).CopyTo(jyy, o + x);
            }
            for (; x < w - 1; x++) GradientAt(up, mid, down, o, x);
            GradientAt(up, mid, down, o, 0);
            if (w > 1) GradientAt(up, mid, down, o, w - 1);
        }
    }

    private void GradientAt(ReadOnlySpan<float> up, ReadOnlySpan<float> mid, ReadOnlySpan<float> down, int o, int x)
    {
        int l = Math.Max(x - 1, 0), r = Math.Min(x + 1, mid.Length - 1);
        float dx = ((up[r] + 2 * mid[r] + down[r]) - (up[l] + 2 * mid[l] + down[l])) / 8;
        float dy = ((down[l] + 2 * down[x] + down[r]) - (up[l] + 2 * up[x] + up[r])) / 8;
        gx[o + x] = dx;
        gy[o + x] = dy;
        jxx[o + x] = dx * dx;
        jxy[o + x] = dx * dy;
        jyy[o + x] = dy * dy;
    }

    /// <summary>The 9-tap binomial along the rows, all three entries in one sweep (into the scratch arrays).</summary>
    private void SmoothAlongRows(int from, int to)
    {
        int w = width;
        for (int y = from; y < to; y++)
        {
            int o = y * w;
            int x = 4;
            for (; x + 4 + V <= w; x += V)
            {
                Vector<float> a = Vector<float>.Zero, b = Vector<float>.Zero, c = Vector<float>.Zero;
                for (int k = 0; k < 9; k++)
                {
                    float weight = Binomial[k];
                    int at = o + x + k - 4;
                    a += new Vector<float>(jxx, at) * weight;
                    b += new Vector<float>(jxy, at) * weight;
                    c += new Vector<float>(jyy, at) * weight;
                }
                a.CopyTo(tXX, o + x);
                b.CopyTo(tXY, o + x);
                c.CopyTo(tYY, o + x);
            }
            for (int px = 0; px < w; px++)
            {
                if (px >= 4 && px < x) continue;   // done above
                float a = 0, b = 0, c = 0;
                for (int k = 0; k < 9; k++)
                {
                    int at = o + Math.Clamp(px + k - 4, 0, w - 1);
                    a += jxx[at] * Binomial[k];
                    b += jxy[at] * Binomial[k];
                    c += jyy[at] * Binomial[k];
                }
                tXX[o + px] = a;
                tXY[o + px] = b;
                tYY[o + px] = c;
            }
        }
    }

    /// <summary>The 9-tap binomial down the columns, row by row, from the scratch arrays back into the entries.</summary>
    private void SmoothDownColumns(int from, int to)
    {
        int w = width, h = height;
        Span<int> rows = stackalloc int[9];
        for (int y = from; y < to; y++)
        {
            for (int k = 0; k < 9; k++) rows[k] = Math.Clamp(y + k - 4, 0, h - 1) * w;
            int o = y * w;
            int x = 0;
            for (; x + V <= w; x += V)
            {
                Vector<float> a = Vector<float>.Zero, b = Vector<float>.Zero, c = Vector<float>.Zero;
                for (int k = 0; k < 9; k++)
                {
                    float weight = Binomial[k];
                    int at = rows[k] + x;
                    a += new Vector<float>(tXX, at) * weight;
                    b += new Vector<float>(tXY, at) * weight;
                    c += new Vector<float>(tYY, at) * weight;
                }
                a.CopyTo(jxx, o + x);
                b.CopyTo(jxy, o + x);
                c.CopyTo(jyy, o + x);
            }
            for (; x < w; x++)
            {
                float a = 0, b = 0, c = 0;
                for (int k = 0; k < 9; k++)
                {
                    a += tXX[rows[k] + x] * Binomial[k];
                    b += tXY[rows[k] + x] * Binomial[k];
                    c += tYY[rows[k] + x] * Binomial[k];
                }
                jxx[o + x] = a;
                jxy[o + x] = b;
                jyy[o + x] = c;
            }
        }
    }

    private static float SmoothStep(float edge0, float edge1, float v)
    {
        float t = Math.Clamp((v - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Bilinear sample of the unfiltered nearness, borders clamped.</summary>
    private float Sample(float x, float y)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
        float fx = x - x0, fy = y - y0;
        float top = copy[y0 * width + x0] + (copy[y0 * width + x1] - copy[y0 * width + x0]) * fx;
        float bottom = copy[y1 * width + x0] + (copy[y1 * width + x1] - copy[y1 * width + x0]) * fx;
        return top + (bottom - top) * fy;
    }
}
