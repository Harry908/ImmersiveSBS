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

    [Fact]
    public void LookingAheadTheRangeOpensBeforeTheNearObjectComes()
    {
        // The frame's own range is 0..1; a near object two frames on will raise it to 2. Told so,
        // the frame is already normalised toward the wider range: its far part stays far, its
        // nearest no longer reads as the nearest there will be.
        var own = new DepthPostProcessor(W, H);
        var ahead = new DepthPostProcessor(W, H);
        var a = new float[W * H];
        var b = new float[W * H];
        own.Process(Ramp(1, 0), Flat(0.5f), 1 / 30.0, a);
        (double p2, double p98) = ahead.MeasureRange(Ramp(1, 0));
        ahead.Process(Ramp(1, 0), Flat(0.5f), 1 / 30.0, b, (p2, 2 * p98));
        Assert.InRange(a[W - 1], 0.99f, 1f);
        Assert.InRange(b[W - 1], 0.45f, 0.55f);
        Assert.Equal(a[0], b[0], 2);
    }

    [Theory]
    [InlineData(64, 36)]
    [InlineData(756, 420)]
    public void TheRangeIsTheExactPercentilesOfEveryFourthValue(int w, int h)
    {
        // Counted by histogram in parts now: the same values a full sort gives, ties and all.
        var random = new Random(5);
        float[] raw = Enumerable.Range(0, w * h).Select(i => i % 7 == 0 ? 0.25f : (float)Math.Pow(random.NextDouble(), 3) * 40 - 3).ToArray();
        float[] sampled = raw.Where((_, i) => i % 4 == 0).Order().ToArray();
        int n = sampled.Length;
        (double p2, double p98) = new DepthPostProcessor(w, h).MeasureRange(raw);
        Assert.Equal(sampled[(int)(0.02 * (n - 1))], p2);
        Assert.Equal(sampled[(int)(0.98 * (n - 1))], p98);
    }

    [Fact]
    public void FlickerIsDepthChangeWhereThePictureStoodStill()
    {
        // M4: the picture holds still and the model's answer jitters; the jitter shows as flicker,
        // and a steady answer as none.
        var p = new DepthPostProcessor(W, H) { SceneCutThreshold = 1 };
        var n = new float[W * H];
        var random = new Random(4);
        p.Process(Ramp(1, 0), Flat(0.5f), 1 / 30.0, n);
        Assert.True(double.IsNaN(p.Flicker), "no flicker on the first frame");

        p.Process(Ramp(1, 0), Flat(0.5f), 1 / 30.0, n);
        Assert.Equal(0, p.Flicker, 6);

        float[] noisy = Ramp(1, 0).Select(v => v + (float)(random.NextDouble() - 0.5) * 0.2f).ToArray();
        p.Process(noisy, Flat(0.5f), 1 / 30.0, n);
        Assert.True(p.Flicker > 0.001, $"flicker {p.Flicker}");
    }

    [Fact]
    public void EdgeStepsChangeOnlyTheOutputNotTheHistory()
    {
        // The foreground curve, soft edges and anti-aliasing work on the output: switching them
        // off restores the model's own depth on the very next frame.
        var raw = new float[W * H];
        for (int i = 0; i < raw.Length; i++) raw[i] = i % W < W / 2 ? 1 : 0;
        var plain = new DepthPostProcessor(W, H) { SceneCutThreshold = 1 };
        var edged = new DepthPostProcessor(W, H) { SceneCutThreshold = 1, AntiAlias = true, SoftEdgePassesX = 3, SoftEdgePassesY = 1, Foreground = 2 };
        var a = new float[W * H];
        var b = new float[W * H];
        for (int f = 0; f < 5; f++)
        {
            plain.Process(raw, Flat(0.5f), 1 / 30.0, a);
            edged.Process(raw, Flat(0.5f), 1 / 30.0, b);
        }
        Assert.NotEqual(a[H / 2 * W + W / 2], b[H / 2 * W + W / 2]);

        edged.SoftEdgePassesX = 0;
        edged.AntiAlias = false;
        edged.Foreground = 0;
        plain.Process(raw, Flat(0.5f), 1 / 30.0, a);
        edged.Process(raw, Flat(0.5f), 1 / 30.0, b);
        Assert.Equal(a, b);
    }
}
