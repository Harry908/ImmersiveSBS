namespace ImmersiveSBS.Depth;

/// <summary>
/// The picture delay of matched timing (improvement-plan.md §6): long enough that every frame
/// shown has depth on both sides of it in time, so its depth is exact or interpolated, never
/// old. From each depth frame's age on reaching the render thread (mean μ and deviation σ,
/// smoothed over 2 s) and the time between the frames depth is computed for (T̄):
/// D* = μ + 2σ + T̄, plus the present latency, since a frame is chosen that much before it is
/// seen. The delay rises after the target has been above it for 0.5 s and falls only after the
/// target has been more than a step (and 20 ms) below for 10 s: every change holds or skips the
/// picture and bends the delayed sound.
/// Also decides Auto timing: matched once D* stays above 60 ms, live again below 45 ms. Pure.
/// </summary>
public sealed class MatchedDelay
{
    /// <summary>Smoothing of the age and period averages, seconds.</summary>
    public const double TimeConstant = 2;

    /// <summary>The longest matched delay, ms; beyond it the buffer is the better choice.</summary>
    public const double MaxMs = 300;

    /// <summary>How far the target may stray before the delay follows, ms (at least one step).</summary>
    public const double ToleranceMs = 20;

    /// <summary>How long Auto timing's choice must hold before it switches to matched, seconds.</summary>
    public const double HoldSeconds = 2;

    /// <summary>How long the target must stay above the delay before it rises, seconds...</summary>
    public const double UpHoldSeconds = 0.5;

    /// <summary>...and below it before it falls.</summary>
    public const double DownHoldSeconds = 10;

    /// <summary>Delays are whole multiples of this, ms.</summary>
    public const double StepMs = 10;

    /// <summary>Auto timing turns matched when D* exceeds this, ms (§6: large models, Quality sizes)...</summary>
    public const double AutoOnMs = 60;

    /// <summary>...and live again below this.</summary>
    public const double AutoOffMs = 45;

    private double mean = double.NaN, variance, period = double.NaN;
    private double lastSample = double.NaN;
    private double divergingSince = double.NaN;
    private bool divergingUp;
    private double autoSince = double.NaN;

    /// <param name="presentLatencyMs">How long before it is seen a frame is chosen.</param>
    public MatchedDelay(double presentLatencyMs)
    {
        PresentLatencyMs = presentLatencyMs;
    }

    public double PresentLatencyMs { get; }

    public bool HasSamples => !double.IsNaN(mean);

    /// <summary>μ + 2σ + T̄: how far behind the picture depth runs, ms (the plan's D*); NaN before any depth.</summary>
    public double NeedMs => HasSamples ? mean + 2 * Math.Sqrt(variance) + (double.IsNaN(period) ? 0 : period) : double.NaN;

    /// <summary>
    /// The video's frame rate when the page reports it, fps (0: unknown): the delay is then a
    /// whole number of video frames, D = ceil(D* f) / f, rather than a multiple of 10 ms.
    /// </summary>
    public double FrameRate { get; set; }

    /// <summary>The delay wanted now, ms, before the hysteresis.</summary>
    public double TargetMs
    {
        get
        {
            if (!HasSamples) return double.NaN;
            double step = Step;
            return Math.Min(Math.Max(Math.Ceiling((NeedMs + PresentLatencyMs) / step - 1e-9) * step, step), MaxMs);
        }
    }

    /// <summary>The smoothed time between the frames depth is computed for, ms; NaN before two.</summary>
    public double PeriodMs => period;

    /// <summary>The delay in use, ms; NaN until the first depth frame.</summary>
    public double DelayMs { get; private set; } = double.NaN;

    /// <summary>Auto timing's choice: true for matched.</summary>
    public bool AutoMatched { get; private set; }

    /// <summary>
    /// One depth frame reached the render thread <paramref name="ageMs"/> after its picture was
    /// captured, <paramref name="periodMs"/> after the previous depth frame's picture (gaps over
    /// 250 ms, a pause, are left out of the period).
    /// </summary>
    public void Add(double ageMs, double periodMs, double nowSeconds)
    {
        double a = double.IsNaN(lastSample) ? 1 : 1 - Math.Exp(-Math.Clamp(nowSeconds - lastSample, 0, 10) / TimeConstant);
        lastSample = nowSeconds;
        if (double.IsNaN(mean))
        {
            mean = ageMs;
            variance = 0;
        }
        else
        {
            // Exponentially weighted mean and variance (West's incremental form).
            double d = ageMs - mean;
            mean += a * d;
            variance = (1 - a) * (variance + a * d * d);
        }
        if (periodMs > 0 && periodMs < 250) period = double.IsNaN(period) ? periodMs : period + a * (periodMs - period);
    }

    /// <summary>
    /// Moves the delay toward the target under the hysteresis rule; true when it changed. Up
    /// soon (<see cref="UpHoldSeconds"/>: until then some frames lack their own depth), down
    /// slowly (<see cref="DownHoldSeconds"/>) and only once the target is more than a step below,
    /// to the target plus a step: a target wandering across one step's boundary is never
    /// followed. In the first glasses run, with whole 33 ms frames as steps, the old symmetric
    /// rule moved the delay every 10–20 s, and each move skipped or repeated a frame and bent
    /// the page's sound.
    /// </summary>
    public bool Update(double nowSeconds)
    {
        if (!HasSamples) return false;
        UpdateAuto(nowSeconds);
        double target = TargetMs;
        if (double.IsNaN(DelayMs))
        {
            DelayMs = target;
            return true;
        }
        double step = Step;
        double margin = Math.Max(step, ToleranceMs);
        bool up = target - DelayMs > (step >= ToleranceMs ? 1e-6 : ToleranceMs);
        bool down = DelayMs - target > margin + 1e-6;
        if (!up && !down || divergingUp != up)
        {
            divergingSince = up || down ? nowSeconds : double.NaN;
            divergingUp = up;
            return false;
        }
        if (double.IsNaN(divergingSince))
        {
            divergingSince = nowSeconds;
            return false;
        }
        if (nowSeconds - divergingSince < (up ? UpHoldSeconds : DownHoldSeconds)) return false;
        DelayMs = up ? target : Math.Min(target + step, MaxMs);
        divergingSince = double.NaN;
        return true;
    }

    private double Step => FrameRate is >= 10 and <= 240 ? 1000 / FrameRate : StepMs;

    private void UpdateAuto(double nowSeconds)
    {
        double need = NeedMs;
        bool wanted = AutoMatched ? need >= AutoOffMs : need > AutoOnMs;
        if (wanted == AutoMatched)
        {
            autoSince = double.NaN;
            return;
        }
        if (double.IsNaN(autoSince)) autoSince = nowSeconds;
        // Quick to turn matched (tearing), slow to go back (each switch jumps the picture).
        if (nowSeconds - autoSince >= (AutoMatched ? 5 : HoldSeconds))
        {
            AutoMatched = wanted;
            autoSince = double.NaN;
        }
    }

    /// <summary>Forgets everything, e.g. for another model.</summary>
    public void Reset()
    {
        mean = period = lastSample = divergingSince = autoSince = double.NaN;
        variance = 0;
        DelayMs = double.NaN;
        divergingUp = false;
        AutoMatched = false;
    }
}
