using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Tests;

public class DepthPostProcessorTests
{
    private const int W = 64, H = 36;

    private static float[] Ramp(float scale, float offset)
    {
        var a = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                a[y * W + x] = offset + scale * x / (W - 1);
        return a;
    }

    private static float[] Flat(float v) => Enumerable.Repeat(v, W * H).ToArray();

    [Fact]
    public void FirstFrameIsNormalizedToZeroOne()
    {
        var p = new DepthPostProcessor(W, H);
        var n = new float[W * H];
        p.Process(Ramp(10, 5), Flat(0.5f), 1 / 30.0, n);

        Assert.InRange(n.Min(), 0f, 0.001f);
        Assert.InRange(n.Max(), 0.999f, 1f);
        Assert.True(n[W - 1] > n[0], "larger raw value is nearer");
    }

    [Fact]
    public void ScaleAndOffsetChangesDoNotChangeNearness()
    {
        // The model's output scale "breathes" frame to frame; a still picture must stay still.
        var p = new DepthPostProcessor(W, H);
        var first = new float[W * H];
        var later = new float[W * H];
        p.Process(Ramp(10, 5), Flat(0.5f), 1 / 30.0, first);
        for (int i = 0; i < 60; i++) p.Process(Ramp(25, -3), Flat(0.5f), 1 / 30.0, later);

        for (int i = 0; i < W * H; i++) Assert.InRange(later[i] - first[i], -0.02f, 0.02f);
    }

    [Fact]
    public void StillPixelsSettleSlowlyMovingPixelsFollowAtOnce()
    {
        var p = new DepthPostProcessor(W, H) { SceneCutThreshold = 1 };
        var n = new float[W * H];
        var luma = Flat(0.5f);
        p.Process(Flat(0f).Select((_, i) => i % W < W / 2 ? 0f : 1f).ToArray(), luma, 1 / 30.0, n);

        // The left half becomes near; only the top row's luma changes there.
        float[] raw = Flat(1f);
        raw[W - 1] = 0; // keep the range non-degenerate
        var luma2 = (float[])luma.Clone();
        for (int x = 0; x < W / 2; x++) luma2[x] = 0.9f;
        p.Process(raw, luma2, 1 / 30.0, n);

        float moving = n[0];          // top row, luma changed
        float still = n[W * (H - 1)];  // bottom row, luma unchanged
        Assert.True(moving > 0.9f, $"moving pixel followed ({moving})");
        Assert.InRange(still, 0.05f, 0.5f);
    }

    /// <summary>
    /// A frame for §6.5: noise everywhere (the picture moves), except a static "subtitle" of
    /// high-contrast stripes at x 8..23, y 26..31, and a static smooth patch at x 40..55, y 4..9.
    /// The raw depth puts both static areas near (1).
    /// </summary>
    private static (float[] Raw, float[] Luma) SubtitleFrame(Random random, bool moving)
    {
        var raw = new float[W * H];
        var luma = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                bool subtitle = x is >= 8 and < 24 && y is >= 26 and < 32;
                bool smooth = x is >= 40 and < 56 && y is >= 4 and < 10;
                raw[i] = subtitle || smooth ? 1f : 0.5f * x / W;
                luma[i] = subtitle ? (x % 2 == 0 ? 0.1f : 0.9f)
                        : smooth ? 0.5f
                        : moving ? (float)random.NextDouble() * 0.1f + 0.3f : 0.35f;
            }
        return (raw, luma);
    }

    [Fact]
    public void StaticOverlaysGoToTheScreenPlane()
    {
        var p = new DepthPostProcessor(W, H) { StaticOverlays = true, StaticNearness = 0.35, SceneCutThreshold = 1 };
        var n = new float[W * H];
        var random = new Random(1);
        for (int f = 0; f < 90; f++)
        {
            var (raw, luma) = SubtitleFrame(random, moving: true);
            p.Process(raw, luma, 1 / 30.0, n);
        }

        Assert.InRange(n[28 * W + 15], 0.3f, 0.45f);     // subtitle: pulled to c
        Assert.InRange(n[6 * W + 47], 0.9f, 1f);         // smooth static patch: no edges, keeps its depth
    }

    [Fact]
    public void APausedPictureKeepsItsDepth()
    {
        var p = new DepthPostProcessor(W, H) { StaticOverlays = true, StaticNearness = 0.35 };
        var n = new float[W * H];
        var random = new Random(2);
        for (int f = 0; f < 90; f++)
        {
            var (raw, luma) = SubtitleFrame(random, moving: false);
            p.Process(raw, luma, 1 / 30.0, n);
        }
        Assert.InRange(n[28 * W + 15], 0.9f, 1f);
    }

    [Fact]
    public void StaticOverlaysOffChangesNothing()
    {
        var p = new DepthPostProcessor(W, H) { SceneCutThreshold = 1 };
        var n = new float[W * H];
        var random = new Random(3);
        for (int f = 0; f < 90; f++)
        {
            var (raw, luma) = SubtitleFrame(random, moving: true);
            p.Process(raw, luma, 1 / 30.0, n);
        }
        Assert.InRange(n[28 * W + 15], 0.9f, 1f);
    }

    [Fact]
    public void SceneCutResetsHistory()
    {
        var p = new DepthPostProcessor(W, H);
        var n = new float[W * H];
        p.Process(Ramp(10, 0), Flat(0.1f), 1 / 30.0, n);
        bool cut = p.Process(Ramp(-10, 10), Flat(0.9f), 1 / 30.0, n);

        Assert.True(cut);
        Assert.True(n[0] > n[W - 1], "the new depth applies fully after a cut");
    }
}
