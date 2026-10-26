using ImmersiveSBS.Depth;
using Xunit.Abstractions;

namespace ImmersiveSBS.Tests;

public class DepthPostProcessorTests(ITestOutputHelper output)
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

    /// <summary>A picture's texture: noise blurred over 5×5 px twice (a picture changes smoothly from pixel to pixel), 0.2..0.8.</summary>
    private static float[] Texture(Random random, int w, int h)
    {
        float[] a = [.. Enumerable.Range(0, w * h).Select(_ => random.NextSingle())];
        for (int pass = 0; pass < 2; pass++)
        {
            var b = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0;
                    int count = 0;
                    for (int j = -2; j <= 2; j++)
                        for (int i = -2; i <= 2; i++)
                            if (x + i >= 0 && x + i < w && y + j >= 0 && y + j < h)
                            {
                                sum += a[(y + j) * w + x + i];
                                count++;
                            }
                    b[y * w + x] = sum / count;
                }
            a = b;
        }
        float min = a.Min(), max = a.Max();
        return [.. a.Select(v => 0.2f + 0.6f * (v - min) / (max - min))];
    }

    /// <summary>
    /// A textured near object moving 2 px a frame over a textured far background, the model's
    /// depth noisy by ±0.04 each frame: at its place, the smoothing could not smooth the moving
    /// object (its picture changes under every pixel, so each takes the new depth) and its depth
    /// flickered; along the motion it is smoothed as much as the still background, with no trail
    /// behind it.
    /// </summary>
    [Fact]
    public void FollowingTheMotionSmoothsAMovingObjectWithoutATrail()
    {
        const int w = 128, h = 72, ow = 40, oh = 24, oy = 24;
        var random = new Random(3);
        float[] background = Texture(random, w, h), texture = Texture(random, ow, oh);

        (double Moving, double Still, double Trail) Run(bool follow)
        {
            var noise = new Random(7);
            var p = new DepthPostProcessor(w, h) { FollowMotion = follow };
            var n = new float[w * h];
            double moving = 0, still = 0, trail = 0;
            int frames = 0;
            for (int t = 0; t < 30; t++)
            {
                int ox = 20 + 2 * t;
                var luma = (float[])background.Clone();
                var raw = new float[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        bool inside = x >= ox && x < ox + ow && y >= oy && y < oy + oh;
                        if (inside) luma[y * w + x] = texture[(y - oy) * ow + x - ox];
                        raw[y * w + x] = (inside ? 1f : 0.2f) + 0.04f * (2 * noise.NextSingle() - 1);
                    }
                p.Process(raw, luma, 1 / 30.0, n);
                if (t < 15) continue;
                // Spread inside the object (inset 4 px) and in a still patch of background, and
                // the background just left behind (the last 4 frames' places) against the still patch.
                double Spread(int x0, int x1, int y0, int y1, out double mean)
                {
                    var values = new List<float>();
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++) values.Add(n[y * w + x]);
                    double m = values.Average();
                    mean = m;
                    return values.Average(v => Math.Abs(v - m));
                }
                moving += Spread(ox + 4, ox + ow - 4, oy + 4, oy + oh - 4, out _);
                still += Spread(4, 16, 4, 16, out double far);
                Spread(ox - 8, ox, oy + 4, oy + oh - 4, out double behind);
                trail += behind - far;
                frames++;
            }
            return (moving / frames, still / frames, trail / frames);
        }

        var atPlace = Run(follow: false);
        var along = Run(follow: true);
        output.WriteLine($"at its place: moving {atPlace.Moving:0.0000}, still {atPlace.Still:0.0000}, trail {atPlace.Trail:0.0000}");
        output.WriteLine($"along motion: moving {along.Moving:0.0000}, still {along.Still:0.0000}, trail {along.Trail:0.0000}");
        Assert.True(along.Moving < 0.6 * atPlace.Moving, $"moving object {along.Moving:0.0000} against {atPlace.Moving:0.0000} at its place");
        Assert.True(along.Moving < 1.5 * along.Still, $"moving object {along.Moving:0.0000} against still background {along.Still:0.0000}");
        Assert.True(Math.Abs(along.Trail) < 0.03, $"trail {along.Trail:0.0000}");
    }
}
