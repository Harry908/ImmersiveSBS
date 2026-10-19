using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Tests;

/// <summary>Matched timing's delay rule (improvement-plan.md §6).</summary>
public class MatchedDelayTests
{
    /// <summary>Depth frames every <paramref name="periodMs"/>, each <paramref name="ageMs"/> old on arrival, for <paramref name="seconds"/>.</summary>
    private static double Feed(MatchedDelay d, double ageMs, double periodMs, double seconds, double start = 0, double jitterMs = 0)
    {
        var random = new Random(7);
        double t = start;
        for (; t < start + seconds; t += periodMs / 1000)
        {
            d.Add(ageMs + (random.NextDouble() - 0.5) * 2 * jitterMs, periodMs, t);
            d.Update(t);
        }
        return t;
    }

    [Fact]
    public void TheDelayCoversTheDepthsAgePlusOnePeriod()
    {
        // A slow model (DAD Large, since removed) as observed: T = 80 ms, depth age ≈ 1.5T + 1.5v ≈ 145 ms (§1.3).
        var d = new MatchedDelay(presentLatencyMs: 20);
        Feed(d, ageMs: 145, periodMs: 80, seconds: 10);
        Assert.Equal(145 + 80, d.NeedMs, 1);
        // Rounded up to 10 ms, with the present latency: 225 + 20 → 250.
        Assert.Equal(250, d.DelayMs);
        Assert.True(d.AutoMatched);
    }

    [Fact]
    public void JitterWidensTheDelayByTwoDeviations()
    {
        var d = new MatchedDelay(20);
        Feed(d, ageMs: 40, periodMs: 15, seconds: 10, jitterMs: 10);
        // Uniform ±10 ms: σ = 10/√3 = 5.8 ms, so μ + 2σ + T ≈ 40 + 11.5 + 15.
        Assert.InRange(d.NeedMs, 62, 71);
    }

    [Fact]
    public void TheDelayRisesAfterHalfASecondOfMoreThan20Ms()
    {
        var d = new MatchedDelay(20);
        double t = Feed(d, ageMs: 100, periodMs: 40, seconds: 10);
        double first = d.DelayMs;
        Assert.Equal(160, first);

        // Jitter of ±8 ms around the same mean moves the target by about 10 ms: never followed.
        double low = double.MaxValue, high = double.MinValue;
        for (double end = t + 10; t < end; t += 0.04)
        {
            d.Add(100 + 8 * Math.Sin(t * 37), 40, t);
            d.Update(t);
            low = Math.Min(low, d.TargetMs);
            high = Math.Max(high, d.TargetMs);
        }
        Assert.True(high > first, $"the target moved ({low}..{high})");
        Assert.Equal(first, d.DelayMs);

        // 60 ms more: followed, but not before it has differed for half a second. (While the
        // mean catches up, the deviation reads large too, so the delay lands at or a little
        // above 220.)
        double changeAt = t;
        bool movedEarly = false;
        for (; t < changeAt + 10; t += 0.04)
        {
            d.Add(160, 40, t);
            d.Update(t);
            if (t - changeAt < 0.5 && d.DelayMs != first) movedEarly = true;
        }
        Assert.False(movedEarly);
        Assert.InRange(d.DelayMs, 220, 300);
    }

    [Fact]
    public void ANeedWanderingAcrossAFrameBoundaryLeavesTheDelayAlone()
    {
        // The first glasses run: DA2 Small at 30 fps needed 74–101 ms, so the target hopped
        // between 100 and 133 ms; the delay must not follow it down and up again.
        var d = new MatchedDelay(20) { FrameRate = 30 };
        double t = Feed(d, ageMs: 60, periodMs: 33.3, seconds: 5);
        double first = d.DelayMs;
        int changes = 0;
        double low = double.MaxValue, high = double.MinValue;
        for (double end = t + 60; t < end; t += 1 / 30.0)
        {
            d.Add(50 + 22 * Math.Sin(t / 3), 33.3, t);
            if (d.Update(t)) changes++;
            low = Math.Min(low, d.TargetMs);
            high = Math.Max(high, d.TargetMs);
        }
        Assert.True(high - low >= 33, $"the target moved a frame or more ({low:0}..{high:0})");
        Assert.True(changes <= 1, $"{changes} changes from {first:0} ms, now {d.DelayMs:0} ms");
    }

    [Fact]
    public void TheDelayFallsOnlyAfterTenSecondsWellBelow()
    {
        var d = new MatchedDelay(20) { FrameRate = 30 };
        double t = Feed(d, ageMs: 150, periodMs: 33.3, seconds: 10);
        double high = d.DelayMs;
        // Depth gets much faster: the target drops by several frames. (The step itself widens the
        // deviation for a moment, which may first raise the delay; only the fall is timed.)
        double fasterAt = t;
        double fellAt = double.NaN;
        for (; t < fasterAt + 20; t += 1 / 30.0)
        {
            d.Add(30, 33.3, t);
            d.Update(t);
            if (d.DelayMs < high && double.IsNaN(fellAt)) fellAt = t;
        }
        Assert.True(fellAt - fasterAt >= MatchedDelay.DownHoldSeconds, $"fell after {fellAt - fasterAt:0.0} s");
        Assert.True(d.DelayMs < high);
        // One frame of headroom above the target.
        Assert.Equal(d.TargetMs + 1000 / 30.0, d.DelayMs, 3);
    }

    [Fact]
    public void AutoTimingTurnsMatchedAboveSixtyAndLiveAgainBelowFortyFive()
    {
        var d = new MatchedDelay(20);
        double t = Feed(d, ageMs: 30, periodMs: 15, seconds: 10);   // 45 ms: a fast model
        Assert.False(d.AutoMatched);
        t = Feed(d, ageMs: 70, periodMs: 25, seconds: 10, start: t);
        Assert.True(d.AutoMatched);
        // 50 ms: between the two thresholds, stays matched.
        t = Feed(d, ageMs: 35, periodMs: 15, seconds: 10, start: t);
        Assert.True(d.AutoMatched);
        Feed(d, ageMs: 25, periodMs: 10, seconds: 10, start: t);
        Assert.False(d.AutoMatched);
    }

    [Fact]
    public void WithTheVideosFrameRateTheDelayIsWholeFrames()
    {
        var d = new MatchedDelay(20) { FrameRate = 24 };
        Feed(d, ageMs: 100, periodMs: 41.7, seconds: 10);
        // 100 + 41.7 + 20 = 161.7 ms → 4 frames of 41.67 ms = 166.7 ms.
        Assert.Equal(4 * 1000 / 24.0, d.DelayMs, 3);
    }
}

