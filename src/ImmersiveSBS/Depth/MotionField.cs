namespace ImmersiveSBS.Depth;

/// <summary>
/// Where each pixel of a frame was in the frame before, from their luma at model resolution, so
/// the depth's temporal smoothing follows the picture (<see cref="DepthPostProcessor.FollowMotion"/>).
/// Block matching from coarse to fine at 1/16, 1/8 and 1/4 of the size: at each level the
/// coarser level's motion here and at its four neighbours (and none) are each tried ±1 px (±4 at
/// the coarsest) on 3×3 texels, with a cost for straying from them (flat areas keep their
/// neighbours' motion) and a small bonus for none (still pictures, subtitles); then a 3×3 vector
/// median, a refinement of each motion at 1/4 by half then quarter texels, another median, and
/// each pixel's motion interpolated from that grid (tuned on the sample clip), backward: a pixel
/// here, where it was. The levels are kept with a
/// border of repeated edge texels as wide as the largest motion, so the search reads them
/// without bounds checks. Over bands of rows on several cores; one caller.
/// </summary>
public sealed class MotionField
{
    private const int Levels = 3;
    private const int CoarseRadius = 4;
    private const int Pad = 24;                 // texels of border: the largest motion is ±(Pad − 2)
    private const float MaxMotion = Pad - 2;
    private const float StepCost = 0.08f, StillBonus = 0.02f;

    private static readonly (int X, int Y)[] Around = [(0, 0), (1, 0), (-1, 0), (0, 1), (0, -1)];

    private readonly int width, height;
    private readonly int[] levelWidth = new int[Levels], levelHeight = new int[Levels];
    private float[][] previous = new float[Levels][], current = new float[Levels][];   // with the border
    private readonly float[][] motionX = new float[Levels][], motionY = new float[Levels][];
    private readonly float[] medianX, medianY;
    private bool hasPrevious;

    public MotionField(int width, int height)
    {
        this.width = width;
        this.height = height;
        for (int k = 0, w = width, h = height; k < Levels; k++)
        {
            int f = k == 0 ? 4 : 2;
            w = Math.Max(1, (w + f - 1) / f);
            h = Math.Max(1, (h + f - 1) / f);
            levelWidth[k] = w;
            levelHeight[k] = h;
            previous[k] = new float[(w + 2 * Pad) * (h + 2 * Pad)];
            current[k] = new float[previous[k].Length];
            motionX[k] = new float[w * h];
            motionY[k] = new float[w * h];
        }
        medianX = new float[levelWidth[0] * levelHeight[0]];
        medianY = new float[medianX.Length];
        Dx = new float[width * height];
        Dy = new float[width * height];
    }

    /// <summary>Each pixel's motion since the frame before, model px: it was at (x − Dx, y − Dy).</summary>
    public float[] Dx { get; }

    /// <inheritdoc cref="Dx"/>
    public float[] Dy { get; }

    /// <summary>Forgets the frame before (a scene cut, a seek): the next has no motion.</summary>
    public void Reset() => hasPrevious = false;

    /// <summary>
    /// The motion from the frame before to this one (<paramref name="luma"/>, 0..1 at the size
    /// given), into <see cref="Dx"/> and <see cref="Dy"/>; false for a first frame (none found).
    /// </summary>
    public bool Update(float[] luma)
    {
        if (luma.Length != width * height) throw new ArgumentException("The luma must match the field's size.");
        Shrink(luma, width, height, 0, current[0], levelWidth[0], levelHeight[0], 4);
        for (int k = 1; k < Levels; k++)
            Shrink(current[k - 1], levelWidth[k - 1], levelHeight[k - 1], Pad, current[k], levelWidth[k], levelHeight[k], 2);
        bool found = hasPrevious;
        if (found)
        {
            for (int k = Levels - 1; k >= 0; k--) Search(k);
            Median(motionX[0], motionY[0], medianX, medianY);
            Refine(0.5f, medianX, medianY, motionX[0], motionY[0]);
            Refine(0.25f, motionX[0], motionY[0], medianX, medianY);
            Median(medianX, medianY, motionX[0], motionY[0]);
            Spread(motionX[0], motionY[0]);
        }
        (previous, current) = (current, previous);
        hasPrevious = true;
        return found;
    }

    /// <summary>
    /// A smaller picture with its border: each texel the mean of <paramref name="f"/>×<paramref name="f"/>
    /// of the source (fewer at the edges), whose texels start <paramref name="sourcePad"/> in from its rows' ends.
    /// </summary>
    private static void Shrink(float[] source, int sw, int sh, int sourcePad, float[] into, int w, int h, int f)
    {
        int sourceStride = sw + 2 * sourcePad, stride = w + 2 * Pad;
        RowBands.For(h, (_, from, to) =>
        {
            for (int y = from; y < to; y++)
            {
                int y0 = y * f, y1 = Math.Min(y0 + f, sh), row = (y + Pad) * stride + Pad;
                for (int x = 0; x < w; x++)
                {
                    int x0 = x * f, x1 = Math.Min(x0 + f, sw);
                    float sum = 0;
                    for (int sy = y0; sy < y1; sy++)
                    {
                        int s = (sy + sourcePad) * sourceStride + sourcePad;
                        for (int sx = x0; sx < x1; sx++) sum += source[s + sx];
                    }
                    into[row + x] = sum / ((y1 - y0) * (x1 - x0));
                }
                // The border on both ends of the row.
                Array.Fill(into, into[row], row - Pad, Pad);
                Array.Fill(into, into[row + w - 1], row + w, Pad);
            }
        });
        // The border above and below: copies of the first and last rows, ends included.
        for (int y = 0; y < Pad; y++)
        {
            Array.Copy(into, Pad * stride, into, y * stride, stride);
            Array.Copy(into, (Pad + h - 1) * stride, into, (Pad + h + y) * stride, stride);
        }
    }

    /// <summary>Level <paramref name="k"/>'s motion, in whole texels of it, from the coarser level's.</summary>
    private void Search(int k)
    {
        int w = levelWidth[k], h = levelHeight[k], stride = w + 2 * Pad;
        float[] now = current[k], before = previous[k], mx = motionX[k], my = motionY[k];
        bool coarsest = k == Levels - 1;
        float[] cx = coarsest ? mx : motionX[k + 1], cy = coarsest ? my : motionY[k + 1];
        int cw = coarsest ? 0 : levelWidth[k + 1], ch = coarsest ? 0 : levelHeight[k + 1];
        int radius = coarsest ? CoarseRadius : 1, limit = (int)MaxMotion;
        RowBands.For(h, (_, from, to) =>
        {
            Span<int> candidateX = stackalloc int[Around.Length + 1], candidateY = stackalloc int[Around.Length + 1];
            for (int y = from; y < to; y++)
                for (int x = 0; x < w; x++)
                {
                    int n = 1;
                    candidateX[0] = candidateY[0] = 0;
                    if (!coarsest)
                    {
                        foreach ((int ox, int oy) in Around)
                        {
                            int c = Math.Clamp(y / 2 + oy, 0, ch - 1) * cw + Math.Clamp(x / 2 + ox, 0, cw - 1);
                            int vx = 2 * (int)cx[c], vy = 2 * (int)cy[c];
                            bool seen = false;
                            for (int j = 0; j < n && !seen; j++) seen = candidateX[j] == vx && candidateY[j] == vy;
                            if (seen) continue;
                            candidateX[n] = vx;
                            candidateY[n] = vy;
                            n++;
                        }
                    }
                    int at = (y + Pad) * stride + x + Pad;
                    int bestX = 0, bestY = 0;
                    float best = Cost(now, before, stride, at, 0, 0) - StillBonus;
                    for (int i = 0; i < n; i++)
                        for (int dy = -radius; dy <= radius; dy++)
                            for (int dx = -radius; dx <= radius; dx++)
                            {
                                int vx = Math.Clamp(candidateX[i] + dx, -limit, limit), vy = Math.Clamp(candidateY[i] + dy, -limit, limit);
                                float c = Cost(now, before, stride, at, vx, vy) + StepCost * (Math.Abs(dx) + Math.Abs(dy));
                                if (c < best)
                                {
                                    best = c;
                                    bestX = vx;
                                    bestY = vy;
                                }
                            }
                    mx[y * w + x] = bestX;
                    my[y * w + x] = bestY;
                }
        });
    }

    /// <summary>The 1/4 level's motion from <paramref name="fromX"/>/<paramref name="fromY"/> tried ±<paramref name="step"/> texel each way (and none), into <paramref name="intoX"/>/<paramref name="intoY"/>.</summary>
    private void Refine(float step, float[] fromX, float[] fromY, float[] intoX, float[] intoY)
    {
        int w = levelWidth[0], h = levelHeight[0], stride = w + 2 * Pad;
        float[] now = current[0], before = previous[0];
        RowBands.For(h, (_, from, to) =>
        {
            for (int y = from; y < to; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x, at = (y + Pad) * stride + x + Pad;
                    float bestX = 0, bestY = 0;
                    float best = Cost(now, before, stride, at, 0, 0) - StillBonus;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            float vx = Math.Clamp(fromX[i] + dx * step, -MaxMotion, MaxMotion);
                            float vy = Math.Clamp(fromY[i] + dy * step, -MaxMotion, MaxMotion);
                            float c = Cost(now, before, stride, at, vx, vy) + StepCost * step * (Math.Abs(dx) + Math.Abs(dy));
                            if (c < best)
                            {
                                best = c;
                                bestX = vx;
                                bestY = vy;
                            }
                        }
                    intoX[i] = bestX;
                    intoY[i] = bestY;
                }
        });
    }

    /// <summary>How unlike 3×3 texels around <paramref name="at"/> are to those the motion (whole texels) says they came from.</summary>
    private static float Cost(float[] now, float[] before, int stride, int at, int vx, int vy)
    {
        float sum = 0;
        int back = at - vy * stride - vx;
        for (int j = -stride; j <= stride; j += stride)
            sum += Math.Abs(now[at + j - 1] - before[back + j - 1]) + Math.Abs(now[at + j] - before[back + j])
                   + Math.Abs(now[at + j + 1] - before[back + j + 1]);
        return sum;
    }

    /// <summary>As the whole-texel cost, for a motion in fractions of a texel.</summary>
    private static float Cost(float[] now, float[] before, int stride, int at, float vx, float vy)
    {
        float fx = MathF.Floor(vx), fy = MathF.Floor(vy);
        // The texels the motion lands between: each of the 3×3 is mixed from four.
        float ax = vx - fx, ay = vy - fy;
        int back = at - (int)fy * stride - (int)fx;
        float w00 = (1 - ax) * (1 - ay), w01 = ax * (1 - ay), w10 = (1 - ax) * ay, w11 = ax * ay;
        float sum = 0;
        for (int j = -stride; j <= stride; j += stride)
            for (int i = -1; i <= 1; i++)
            {
                int b = back + j + i;
                // b is (x − fx, y − fy); the texel at (x − vx) lies between it and the one before it on each axis.
                float sample = w00 * before[b] + w01 * before[b - 1] + w10 * before[b - stride] + w11 * before[b - stride - 1];
                sum += Math.Abs(now[at + j + i] - sample);
            }
        return sum;
    }

    /// <summary>The vector median of 3×3 motions: a stray texel takes its neighbours' motion, an edge between two stays sharp.</summary>
    private void Median(float[] sx, float[] sy, float[] intoX, float[] intoY)
    {
        int w = levelWidth[0], h = levelHeight[0];
        RowBands.For(h, (_, from, to) =>
        {
            Span<float> nx = stackalloc float[9], ny = stackalloc float[9];
            for (int y = from; y < to; y++)
                for (int x = 0; x < w; x++)
                {
                    int k = 0;
                    for (int j = -1; j <= 1; j++)
                        for (int i = -1; i <= 1; i++)
                        {
                            int c = Math.Clamp(y + j, 0, h - 1) * w + Math.Clamp(x + i, 0, w - 1);
                            nx[k] = sx[c];
                            ny[k++] = sy[c];
                        }
                    int best = 4;
                    float bestSum = float.MaxValue;
                    for (int a = 0; a < 9; a++)
                    {
                        float sum = 0;
                        for (int b = 0; b < 9; b++) sum += Math.Abs(nx[a] - nx[b]) + Math.Abs(ny[a] - ny[b]);
                        if (sum < bestSum)
                        {
                            bestSum = sum;
                            best = a;
                        }
                    }
                    intoX[y * w + x] = nx[best];
                    intoY[y * w + x] = ny[best];
                }
        });
    }

    /// <summary>Each pixel's motion in model px, interpolated from the 1/4 grid.</summary>
    private void Spread(float[] gridX, float[] gridY)
    {
        int w = levelWidth[0], h = levelHeight[0];
        float sx = (float)width / w, sy = (float)height / h;
        RowBands.For(height, (_, from, to) =>
        {
            for (int y = from; y < to; y++)
            {
                float gy = (y + 0.5f) / sy - 0.5f;
                for (int x = 0; x < width; x++)
                {
                    float gx = (x + 0.5f) / sx - 0.5f;
                    Dx[y * width + x] = sx * Bilinear(gridX, w, h, gx, gy);
                    Dy[y * width + x] = sy * Bilinear(gridY, w, h, gx, gy);
                }
            }
        });
    }

    /// <summary><paramref name="image"/> (<paramref name="w"/>×<paramref name="h"/>) at a point between pixels, the edges held.</summary>
    public static float Bilinear(float[] image, int w, int h, float x, float y)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float ax = x - x0, ay = y - y0;
        float top = image[y0 * w + x0] + (image[y0 * w + x1] - image[y0 * w + x0]) * ax;
        float bottom = image[y1 * w + x0] + (image[y1 * w + x1] - image[y1 * w + x0]) * ax;
        return top + (bottom - top) * ay;
    }
}
