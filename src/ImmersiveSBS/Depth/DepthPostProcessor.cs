namespace ImmersiveSBS.Depth;

/// <summary>
/// Turns the model's relative inverse depth into a steady nearness map (video-3d-plan.md
/// §6.1–6.2), at model resolution on the CPU. The model's scale and offset change every frame,
/// so raw output "breathes"; this normalizes by smoothed percentiles and filters per pixel,
/// following quickly where the picture moves and settling where it is still. A scene cut resets
/// everything. Pure: no GPU, no Windows state.
/// </summary>
public sealed class DepthPostProcessor
{
    private readonly int count;
    private readonly float[] previousNearness;
    private readonly float[] previousLuma;
    private readonly float[] sample;
    private readonly float[] staticTime;
    private readonly float[] staticWeight;
    private readonly CaptionLock captions;
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
        previousLuma = new float[count];
        sample = new float[(count + SampleStride - 1) / SampleStride];
        staticTime = new float[count];
        staticWeight = new float[count];
        captions = new CaptionLock(width, height);
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

    /// <summary>The caption box found in the last frame (model px), if any.</summary>
    public System.Drawing.Rectangle? CaptionBox => Captions ? captions.Found : null;

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

    /// <summary>The smoothed 2nd and 98th percentiles of raw depth, for diagnostics.</summary>
    public (double Low, double High) Range => (low, high);

    public void Reset() => hasHistory = false;

    /// <summary>
    /// Processes one frame. <paramref name="luma"/> is the model input's luma in [0, 1] at the
    /// same resolution. Returns true when the frame was taken as a scene cut.
    /// </summary>
    public bool Process(ReadOnlySpan<float> raw, ReadOnlySpan<float> luma, double dtSeconds, Span<float> nearness)
    {
        if (raw.Length != count || luma.Length != count || nearness.Length != count)
            throw new ArgumentException("Buffers must match the processor's size.");

        double dt = Math.Clamp(dtSeconds, 1.0 / 240, 1.0);

        bool cut = false;
        double meanChange = 0;
        if (hasHistory)
        {
            double sum = 0;
            for (int i = 0; i < count; i++) sum += Math.Abs(luma[i] - previousLuma[i]);
            meanChange = sum / count;
            cut = meanChange > SceneCutThreshold;
        }
        bool reset = !hasHistory || cut;

        // §6.5 needs to know whether the picture as a whole is moving (a paused video is not).
        globalChange = reset ? 0 : globalChange + (meanChange - globalChange) * (1 - Math.Exp(-dt / StaticSeconds));
        bool pictureMoving = globalChange > StaticGlobalChange;
        if (reset)
        {
            Array.Clear(staticTime);
            Array.Clear(staticWeight);
        }

        // §6.1: percentiles of this frame, then an exponential moving average.
        (double p2, double p98) = Percentiles(raw);
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

        for (int i = 0; i < count; i++)
        {
            double n = Math.Clamp((raw[i] - low) / span, 0, 1);
            if (Gamma != 1.0) n = Math.Pow(n, Gamma);

            if (!reset)
            {
                double motion = Math.Clamp(MotionGain * Math.Abs(luma[i] - previousLuma[i]), 0, 1);
                double alpha = alphaMin + (1 - alphaMin) * motion;
                n = previousNearness[i] + (n - previousNearness[i]) * alpha;
            }

            previousNearness[i] = (float)n;

            // §6.5: a pixel that has not changed for StaticSeconds while the picture moves, on a
            // strong edge (text, logo outlines), goes to the screen plane. The temporal state
            // above keeps the model's own value, so switching this off restores depth at once.
            if (StaticOverlays && !reset)
            {
                if (Math.Abs(luma[i] - previousLuma[i]) < StaticEpsilon) staticTime[i] += (float)dt;
                else staticTime[i] = 0;

                if (pictureMoving)
                {
                    int x = i % Width;
                    double edge = Math.Max(
                        x > 0 ? Math.Abs(luma[i] - luma[i - 1]) : 0,
                        i >= Width ? Math.Abs(luma[i] - luma[i - Width]) : 0);
                    float target = staticTime[i] >= StaticSeconds && edge > StaticEdge ? 1 : 0;
                    staticWeight[i] += (target - staticWeight[i]) * (float)Math.Min(1, dt / 0.3);
                }
                n += (StaticNearness - n) * staticWeight[i];
            }

            nearness[i] = (float)n;
            previousLuma[i] = luma[i];
        }

        // Like the static overlays, only the output: the temporal state keeps the model's value.
        if (Captions) captions.Apply(luma, nearness, dt, StaticNearness);

        hasHistory = true;
        return cut;
    }

    private (double P2, double P98) Percentiles(ReadOnlySpan<float> raw)
    {
        int n = 0;
        for (int i = 0; i < count; i += SampleStride) sample[n++] = raw[i];
        Span<float> s = sample.AsSpan(0, n);
        int low = (int)(0.02 * (n - 1)), high = (int)(0.98 * (n - 1));
        float p2 = Selection.Nth(s, low);
        // Everything after the 2nd percentile is at least it: the 98th is found among those.
        float p98 = high == low ? p2 : Selection.Nth(s[(low + 1)..], high - low - 1);
        return (p2, p98);
    }
}
