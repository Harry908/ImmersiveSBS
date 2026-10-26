namespace ImmersiveSBS.Depth;

/// <summary>
/// Turns the model's relative inverse depth into a steady nearness map (video-3d-plan.md
/// §6.1–6.2), at model resolution on the CPU. The model's scale and offset change every frame,
/// so raw output "breathes"; this normalizes by smoothed percentiles and filters per pixel
/// along the picture's motion (<see cref="FollowMotion"/>), following quickly where the picture
/// does not match what was there and settling where it does. A scene cut resets
/// everything. Then, on the output only (the temporal state keeps the model's own values, so
/// switching any of them off restores its depth at once): the foreground curve, depth
/// anti-aliasing and soft edges (improvement-plan.md §11, §7), then static overlays and subtitles at the screen plane, so text
/// stays flat and sharp-edged. Pure: no GPU, no Windows state. Every per-pixel step runs over
/// bands of rows on several cores (<see cref="RowBands"/>); one call at a time.
/// </summary>
public sealed class DepthPostProcessor
{
    private readonly int count;
    private float[] previousNearness;
    private float[] nextNearness;
    private readonly MotionField motion;
    private readonly float[] previousLuma;
    private readonly float[] stirred, stirredRows;

    /// <summary>How far around a pixel a change of the picture counts as the picture moving there, px.</summary>
    private const int StirRadius = 4;
    private readonly float[] sample;
    private readonly float[] staticTime;
    private readonly float[] staticWeight;
    private readonly float[] previousOutput;
    private readonly float[] pageMask;
    private readonly bool[] inPageBox;
    private readonly CaptionLock captions;
    private readonly SoftEdges softEdges;
    private readonly DepthAntiAlias antiAlias;
    private double globalChange;
    private bool hasHistory;
    private double low;
    private double high;

    public DepthPostProcessor(int width, int height)
    {
        Width = width;
        Height = height;
        count = width * height;
        previousNearness = new float[count];
        nextNearness = new float[count];
        motion = new MotionField(width, height);
        previousLuma = new float[count];
        stirred = new float[count];
        stirredRows = new float[count];
        sample = new float[(count + SampleStride - 1) / SampleStride];
        staticTime = new float[count];
        staticWeight = new float[count];
        previousOutput = new float[count];
        pageMask = new float[count];
        inPageBox = new bool[count];
        captions = new CaptionLock(width, height);
        softEdges = new SoftEdges(width, height);
        antiAlias = new DepthAntiAlias(width, height);
        // In the log, so a wrong lock (a flattened block of scene) can be told from a right one.
        captions.Changed += box => Log.Write(box is System.Drawing.Rectangle b
            ? $"Depth: subtitles found at {b.X},{b.Y} {b.Width}×{b.Height} (of {width}×{height}); kept flat."
            : "Depth: subtitles gone.");
    }

    /// <summary>Every n-th value enters the percentile estimate.</summary>
    private const int SampleStride = 4;

    public int Width { get; }

    public int Height { get; }

    /// <summary>Time constant of the percentile averages, seconds (plan: τ ≈ 0.5 s).</summary>
    public double PercentileTimeConstant { get; set; } = 0.5;

    /// <summary>Per-pixel blend toward the new value where nothing moves, per frame at 30 fps.</summary>
    public double AlphaMin { get; set; } = 0.2;

    /// <summary>
    /// Each pixel's history is the smoothed depth where its part of the picture was in the frame
    /// before (<see cref="MotionField"/>), not what was at its place: a moving thing is smoothed
    /// as much as a still one, and leaves no trail. Off: at its place, as before (for comparing).
    /// </summary>
    public bool FollowMotion { get; set; } = true;

    /// <summary>
    /// Where the picture moves around a pixel (it changed within <see cref="StirRadius"/> px), a
    /// depth this far from the pixel's history (nearness, 0..1) starts to be taken as it is,
    /// fully from <see cref="DepthJumpTo"/>: a jump far beyond the model's flicker there is what a
    /// moving thing uncovered, whose picture may look like what covered it, so the luma at the
    /// pixel alone missed it and the thing's depth trailed behind it. Where the picture is still,
    /// its depth still eases into a change. With <see cref="FollowMotion"/> only.
    /// </summary>
    public double DepthJumpFrom { get; set; } = 0.1;

    /// <inheritdoc cref="DepthJumpFrom"/>
    public double DepthJumpTo { get; set; } = 0.25;

    /// <summary>Gain from luma change (0..1) to full update.</summary>
    public double MotionGain { get; set; } = 10;

    /// <summary>Mean |ΔY| over the frame above which the frame is a new scene.</summary>
    public double SceneCutThreshold { get; set; } = 0.12;

    /// <summary>Optional perceptual curve n ← n^γ.</summary>
    public double Gamma { get; set; } = 1.0;

    /// <summary>
    /// Subtitles, logos and lingering player controls at the screen plane (§6.5). Off by default
    /// until tuned on real subtitles: a locked-off shot's static background could be flattened.
    /// </summary>
    public bool StaticOverlays { get; set; }

    /// <summary>Subtitles kept flat just in front of the scene (<see cref="CaptionLock"/>).</summary>
    public bool Captions { get; set; }

    /// <summary>The nearness static overlays are pulled to, and below which captions never go: the convergence c.</summary>
    public double StaticNearness { get; set; } = 0.35;

    /// <summary>How long a pixel must stay unchanged to count as static, seconds (plan: 1 s).</summary>
    public double StaticSeconds { get; set; } = 1;

    /// <summary>Luma change below which a pixel counts as unchanged.</summary>
    public double StaticEpsilon { get; set; } = 1.5 / 255;

    /// <summary>Luma step to a neighbour above which a pixel sits on a strong edge.</summary>
    public double StaticEdge { get; set; } = 0.15;

    /// <summary>Smoothed mean luma change above which the picture counts as moving (not paused).</summary>
    public double StaticGlobalChange { get; set; } = 0.008;

    /// <summary>Smooths stair-stepped depth edges along their direction (improvement-plan.md §7.2).</summary>
    public bool AntiAlias { get; set; }

    /// <summary>Soft-edge passes along x (§7.1); 0 = off.</summary>
    public int SoftEdgePassesX { get; set; }

    /// <summary>Soft-edge passes along y, at most <see cref="SoftEdgePassesX"/>.</summary>
    public int SoftEdgePassesY { get; set; }

    /// <summary>The foreground-scale curve's level (<see cref="ForegroundCurve"/>), −3..3; 0 = linear.</summary>
    public int Foreground
    {
        get => curve.Level;
        set
        {
            if (Math.Clamp(value, -ForegroundCurve.MaxLevel, ForegroundCurve.MaxLevel) != curve.Level) curve = new ForegroundCurve(value);
        }
    }

    private ForegroundCurve curve = new(0);

    /// <summary>
    /// What the page draws over the picture (improvement-plan.md §7.4), model px: overlays are held
    /// at the screen plane, captions replace the subtitle detection while there are any. Null: no
    /// page data.
    /// </summary>
    public PageBoxes? PageBoxes { get; set; }

    /// <summary>The smoothed 2nd and 98th percentiles of raw depth, for diagnostics.</summary>
    public (double Low, double High) Range => (low, high);

    /// <summary>
    /// Flicker of the last frame (improvement-plan.md M4): the mean change of the output over
    /// pixels whose picture did not change (luma by less than <see cref="StaticEpsilon"/>), where
    /// any change of depth is noise; NaN after a reset or with no such pixel.
    /// </summary>
    public double Flicker { get; private set; } = double.NaN;

    public void Reset() => hasHistory = false;

    /// <summary>
    /// Processes one frame. <paramref name="luma"/> is the model input's luma in [0, 1] at the
    /// same resolution. Returns true when the frame was taken as a scene cut.
    /// </summary>
    /// <param name="lookAhead">
    /// The depth range to normalise toward instead of this frame's own 2nd and 98th percentiles:
    /// the lowest and highest over this frame and the ones after it, when they are already known
    /// (the buffer, improvement-plan.md §10): the range opens before a near object comes in.
    /// </param>
    public bool Process(float[] raw, float[] luma, double dtSeconds, float[] nearness,
        (double Low, double High)? lookAhead = null)
    {
        if (raw.Length != count || luma.Length != count || nearness.Length != count)
            throw new ArgumentException("Buffers must match the processor's size.");

        double dt = Math.Clamp(dtSeconds, 1.0 / 240, 1.0);
        int bands = RowBands.Count(Height);
        var partial = new double[bands];

        bool cut = false;
        double meanChange = 0;
        if (hasHistory)
        {
            RowBands.For(Height, (band, from, to) =>
            {
                double sum = 0;
                for (int i = from * Width; i < to * Width; i++) sum += Math.Abs(luma[i] - previousLuma[i]);
                partial[band] = sum;
            });
            meanChange = partial.Sum() / count;
            cut = meanChange > SceneCutThreshold;
        }
        bool reset = !hasHistory || cut;
        if (reset || !FollowMotion) motion.Reset();
        bool follow = FollowMotion && motion.Update(luma) && !reset;

        // §6.5 needs to know whether the picture as a whole is moving (a paused video is not).
        globalChange = reset ? 0 : globalChange + (meanChange - globalChange) * (1 - Math.Exp(-dt / StaticSeconds));
        bool pictureMoving = globalChange > StaticGlobalChange;
        if (reset)
        {
            Array.Clear(staticTime);
            Array.Clear(staticWeight);
        }

        // §6.1: percentiles of this frame (or of the frames ahead), then an exponential moving average.
        (double p2, double p98) = lookAhead ?? MeasureRange(raw);
        if (reset)
        {
            low = p2;
            high = p98;
        }
        else
        {
            double a = 1 - Math.Exp(-dt / PercentileTimeConstant);
            low += (p2 - low) * a;
            high += (p98 - high) * a;
        }

        double span = Math.Max(high - low, 1e-6);
        // §6.2: AlphaMin is per 30 fps frame; the same settling time at any frame rate.
        double alphaMin = 1 - Math.Pow(1 - AlphaMin, dt * 30);
        double lowNow = low, gamma = Gamma, motionGain = MotionGain;
        double jumpFrom = DepthJumpFrom, jumpSpan = Math.Max(DepthJumpTo - DepthJumpFrom, 1e-6);
        bool staticOverlays = StaticOverlays;
        double staticEpsilon = StaticEpsilon, staticSeconds = StaticSeconds, staticEdge = StaticEdge;
        ForegroundCurve curveNow = curve;
        int w = Width, h = Height;
        // Read from one, written to the other: a pixel's history may be a neighbour's.
        float[] history = previousNearness, next = nextNearness, dx = motion.Dx, dy = motion.Dy;
        if (follow) Stir(luma);

        RowBands.For(Height, (_, from, to) =>
        {
            for (int y = from; y < to; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                double n = Math.Clamp((raw[i] - lowNow) / span, 0, 1);
                if (gamma != 1.0) n = Math.Pow(n, gamma);

                if (!reset)
                {
                    // What this part of the picture was a frame ago, and how unlike it it looks
                    // now (uncovered, a wrong motion, a change of light) or how far its depth
                    // jumped: then the new depth.
                    float before = history[i], beforeLuma = previousLuma[i];
                    double change = 0;
                    if (follow)
                    {
                        float sx = x - dx[i], sy = y - dy[i];
                        before = MotionField.Bilinear(history, w, h, sx, sy);
                        beforeLuma = MotionField.Bilinear(previousLuma, w, h, sx, sy);
                        // The picture moving around it: a jump is what was uncovered.
                        double moving = Math.Clamp(motionGain * stirred[i], 0, 1);
                        change = moving * Math.Clamp((Math.Abs(n - before) - jumpFrom) / jumpSpan, 0, 1);
                    }
                    change = Math.Max(change, Math.Clamp(motionGain * Math.Abs(luma[i] - beforeLuma), 0, 1));
                    double alpha = alphaMin + (1 - alphaMin) * change;
                    n = before + (n - before) * alpha;
                }

                next[i] = (float)n;
                nearness[i] = (float)n;

                // §6.5: a pixel that has not changed for StaticSeconds while the picture moves, on a
                // strong edge (text, logo outlines), goes to the screen plane (applied below).
                if (staticOverlays && !reset)
                {
                    if (Math.Abs(luma[i] - previousLuma[i]) < staticEpsilon) staticTime[i] += (float)dt;
                    else staticTime[i] = 0;

                    if (pictureMoving)
                    {
                        double edge = Math.Max(
                            x > 0 ? Math.Abs(luma[i] - luma[i - 1]) : 0,
                            i >= Width ? Math.Abs(luma[i] - luma[i - Width]) : 0);
                        float target = staticTime[i] >= staticSeconds && edge > staticEdge ? 1 : 0;
                        staticWeight[i] += (target - staticWeight[i]) * (float)Math.Min(1, dt / 0.3);
                    }
                }
            }
            // §11 item 4 on the output (point by point, so here).
            curveNow.Apply(nearness.AsSpan(from * Width, (to - from) * Width));
        });
        (previousNearness, nextNearness) = (nextNearness, previousNearness);

        // §7: anti-alias first, then soften (soft edges hide much of the stair-stepping too).
        if (AntiAlias) antiAlias.Apply(nearness);
        if (SoftEdgePassesX > 0) softEdges.Apply(nearness, SoftEdgePassesX, Math.Min(SoftEdgePassesY, SoftEdgePassesX));

        ApplyPageOverlays(nearness, dt);

        if (StaticOverlays && !reset)
        {
            float plane = (float)StaticNearness;
            for (int i = 0; i < count; i++)
                if (staticWeight[i] > 0) nearness[i] += (plane - nearness[i]) * staticWeight[i];
        }

        if (Captions) captions.Apply(luma, nearness, dt, StaticNearness, PageBoxes?.Captions, PageBoxes?.Overlays);

        // M4, then the history for the next frame.
        var stillCounts = new int[bands];
        RowBands.For(Height, (band, from, to) =>
        {
            double flicker = 0;
            int still = 0;
            for (int i = from * Width; i < to * Width; i++)
            {
                if (!reset && Math.Abs(luma[i] - previousLuma[i]) < staticEpsilon)
                {
                    flicker += Math.Abs(nearness[i] - previousOutput[i]);
                    still++;
                }
                previousOutput[i] = nearness[i];
                previousLuma[i] = luma[i];
            }
            partial[band] = flicker;
            stillCounts[band] = still;
        });
        int stillTotal = stillCounts.Sum();
        Flicker = stillTotal > 0 ? partial.Sum() / stillTotal : double.NaN;

        hasHistory = true;
        return cut;
    }

    /// <summary>The most the picture changed since the frame before within <see cref="StirRadius"/> px of each pixel (a square), into <see cref="stirred"/>.</summary>
    private void Stir(float[] luma)
    {
        int w = Width, h = Height;
        RowBands.For(h, (_, from, to) =>
        {
            for (int y = from; y < to; y++)
                for (int x = 0; x < w; x++)
                {
                    float most = 0;
                    for (int i = Math.Max(0, x - StirRadius), end = Math.Min(w - 1, x + StirRadius); i <= end; i++)
                        most = Math.Max(most, Math.Abs(luma[y * w + i] - previousLuma[y * w + i]));
                    stirredRows[y * w + x] = most;
                }
        });
        RowBands.For(h, (_, from, to) =>
        {
            for (int y = from; y < to; y++)
                for (int x = 0; x < w; x++)
                {
                    float most = 0;
                    for (int j = Math.Max(0, y - StirRadius), end = Math.Min(h - 1, y + StirRadius); j <= end; j++)
                        most = Math.Max(most, stirredRows[j * w + x]);
                    stirred[y * w + x] = most;
                }
        });
    }

    /// <summary>
    /// The page's overlay boxes at the screen plane, N ← N + (c − N)·M, through a mask that rises
    /// with τ = 0.1 s and fades with τ = 0.4 s (as the captions'), so controls fading in and out
    /// do not pop. After the soft edges: the boxes stay flat and their edges sharp.
    /// </summary>
    private void ApplyPageOverlays(float[] nearness, double dt)
    {
        IReadOnlyList<System.Drawing.Rectangle> boxes = PageBoxes?.Overlays ?? [];
        Array.Clear(inPageBox);
        var frame = new System.Drawing.Rectangle(0, 0, Width, Height);
        foreach (System.Drawing.Rectangle b in boxes)
        {
            System.Drawing.Rectangle r = System.Drawing.Rectangle.Intersect(b, frame);
            for (int y = r.Top; y < r.Bottom; y++) Array.Fill(inPageBox, true, y * Width + r.Left, r.Width);
        }
        float rise = (float)(1 - Math.Exp(-dt / 0.1)), fall = (float)(1 - Math.Exp(-dt / 0.4));
        float plane = (float)StaticNearness;
        RowBands.For(Height, (_, from, to) =>
        {
            for (int i = from * Width; i < to * Width; i++)
            {
                float m = pageMask[i];
                if (m == 0 && !inPageBox[i]) continue;
                m += inPageBox[i] ? (1 - m) * rise : -m * fall;
                if (m < 0.001f) m = 0;
                pageMask[i] = m;
                nearness[i] += (plane - nearness[i]) * m;
            }
        });
    }

    /// <summary>
    /// The 2nd and 98th percentiles of a raw depth map (every 4th value), exactly: a histogram
    /// of the values, counted in parts on several cores, says which bin each rank falls in, and
    /// only that bin's values are searched. Not thread-safe.
    /// </summary>
    public (double P2, double P98) MeasureRange(float[] raw)
    {
        int n = (count + SampleStride - 1) / SampleStride;
        int parts = RowBands.RangeCount(n);
        var lows = new float[parts];
        var highs = new float[parts];
        RowBands.ForRange(n, (part, from, to) =>
        {
            float lo = float.MaxValue, hi = float.MinValue;
            for (int j = from; j < to; j++)
            {
                float v = raw[j * SampleStride];
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
            lows[part] = lo;
            highs[part] = hi;
        });
        float min = lows.Min(), max = highs.Max();
        if (!(max > min)) return (min, min);

        // Bins rise with the value, so every value in a lower bin is below every value in a higher one.
        float scale = (Bins - 1e-3f) / (max - min);
        if (histograms.Length < parts) histograms = new int[parts][];
        RowBands.ForRange(n, (part, from, to) =>
        {
            int[] h = histograms[part] ??= new int[Bins];
            Array.Clear(h);
            for (int j = from; j < to; j++) h[Bin(raw[j * SampleStride])]++;
        });
        Array.Clear(histogram);
        for (int p = 0; p < parts; p++)
            for (int b = 0; b < Bins; b++) histogram[b] += histograms[p][b];

        int low = (int)(0.02 * (n - 1)), high = (int)(0.98 * (n - 1));
        return (Nth(low), Nth(high));

        int Bin(float v) => Math.Clamp((int)((v - min) * scale), 0, Bins - 1);

        // The k-th smallest: its bin from the counts, then among that bin's values only.
        float Nth(int k)
        {
            int bin = 0, before = 0;
            while (before + histogram[bin] <= k) before += histogram[bin++];
            int m = 0;
            for (int j = 0; j < n; j++)
            {
                float v = raw[j * SampleStride];
                if (Bin(v) == bin) sample[m++] = v;
            }
            return Selection.Nth(sample.AsSpan(0, m), k - before);
        }
    }

    private const int Bins = 4096;
    private readonly int[] histogram = new int[Bins];
    private int[][] histograms = [];
}
