using ImmersiveSBS.Depth;
using Xunit.Abstractions;

namespace ImmersiveSBS.Tests;

/// <summary>Soft edges and depth anti-aliasing at model resolution (improvement-plan.md §7.1–7.2).</summary>
[Collection("Timing")]
public class EdgeFilterTests(ITestOutputHelper testOutput)
{
    private const int W = 96, H = 64;

    private static float[] Step(Func<double, double> edge, float near = 0.7f, float far = 0.3f)
    {
        var n = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                n[y * W + x] = x + 0.5 < edge(y + 0.5) ? near : far;
        return n;
    }

    /// <summary>Where each row crosses halfway between near and far (sub-pixel), and the RMS distance of those from their line.</summary>
    private static double Jaggedness(float[] n, float level = 0.5f)
    {
        var points = new List<(double Y, double X)>();
        for (int y = 4; y < H - 4; y++)
            for (int x = 1; x < W; x++)
            {
                float a = n[y * W + x - 1], b = n[y * W + x];
                if (a >= level && b < level)
                {
                    points.Add((y, x - 1 + (a - level) / (a - b) + 0.5));
                    break;
                }
            }
        double my = points.Average(p => p.Y), mx = points.Average(p => p.X);
        double slope = points.Sum(p => (p.Y - my) * (p.X - mx)) / points.Sum(p => (p.Y - my) * (p.Y - my));
        return Math.Sqrt(points.Average(p => Math.Pow(p.X - (mx + slope * (p.Y - my)), 2)));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.33)]
    [InlineData(1.7)]
    public void AntiAliasingStraightensAStaircase(double slope)
    {
        float[] n = Step(y => 30 + slope * y);
        double before = Jaggedness(n);
        new DepthAntiAlias(W, H).Apply(n);
        double after = Jaggedness(n);
        testOutput.WriteLine($"slope {slope}: RMS {before:0.000} px before, {after:0.000} px after");
        Assert.True(after < before / 2, $"{before:0.000} → {after:0.000}");
    }

    [Fact]
    public void AntiAliasingLeavesFlatDepthAndStraightEdgesAlone()
    {
        float[] vertical = Step(_ => 40);
        float[] copy = (float[])vertical.Clone();
        new DepthAntiAlias(W, H).Apply(vertical);
        for (int i = 0; i < copy.Length; i++) Assert.Equal(copy[i], vertical[i], 5);

        float[] flat = Enumerable.Repeat(0.4f, W * H).ToArray();
        new DepthAntiAlias(W, H).Apply(flat);
        Assert.All(flat, v => Assert.Equal(0.4f, v));
    }

    [Fact]
    public void SoftEdgesLeaveFlatDepthAlone()
    {
        float[] flat = Enumerable.Repeat(0.4f, W * H).ToArray();
        new SoftEdges(W, H).Apply(flat, 4, 1);
        Assert.All(flat, v => Assert.Equal(0.4f, v));
    }

    [Fact]
    public void SoftEdgesGrowTheNearSideIntoARamp()
    {
        float[] n = Step(_ => 40);
        new SoftEdges(W, H).Apply(n, 2, 1);
        int row = H / 2 * W;
        // The near side (left) keeps its depth, the far side well away too...
        Assert.Equal(0.7f, n[row + 30], 3);
        Assert.Equal(0.3f, n[row + 50], 3);
        // ...and the step is now spread over more than one pixel, on the far side.
        float steepest = 0;
        for (int x = 31; x < 50; x++) steepest = Math.Max(steepest, n[row + x - 1] - n[row + x]);
        testOutput.WriteLine("row: " + string.Join(" ", Enumerable.Range(36, 10).Select(x => n[row + x].ToString("0.00"))));
        Assert.True(steepest < 0.4f * 0.8f, $"steepest step {steepest:0.000}");
        Assert.True(n[row + 40] > 0.3f + 0.05f, "the near side grew onto the far side");
    }

    [Theory]
    [InlineData(518, 294)]
    [InlineData(756, 420)]
    public void PostProcessingCostAtModelResolution(int mw, int mh)
    {
        // M6's post part with the edge steps, at the Balanced and Quality sizes: printed, and well inside a frame.
        var random = new Random(9);
        var raw = Enumerable.Range(0, mw * mh).Select(i => (float)((i % mw) / (double)mw + (i / mw % 37 < 18 ? 0.5 : 0))).ToArray();
        var luma = Enumerable.Range(0, mw * mh).Select(_ => (float)random.NextDouble()).ToArray();
        var output = new float[mw * mh];
        foreach ((bool aa, int passes) in new[] { (false, 0), (true, 0), (false, 2), (true, 2), (true, 4) })
        {
            var p = new DepthPostProcessor(mw, mh) { AntiAlias = aa, SoftEdgePassesX = passes, SoftEdgePassesY = Math.Min(passes, 1), Captions = true };
            // Warm up past the JIT's first, unoptimised code (methods are recompiled after 30 calls).
            for (int f = 0; f < 40; f++) p.Process(raw, luma, 1 / 30.0, output);
            Thread.Sleep(300);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 0; f < 20; f++) p.Process(raw, luma, 1 / 30.0, output);
            double ms = clock.Elapsed.TotalMilliseconds / 20;
            output.ToString();
            testOutput.WriteLine($"anti-alias {aa}, soft passes {passes}: {ms:0.00} ms a frame{(Optimized ? "" : " (Debug build: unoptimised)")}");
            if (Optimized) Assert.True(ms < 30, $"{ms:0.0} ms");
        }

        // Each filter alone.
        var soft = new SoftEdges(mw, mh);
        var anti = new DepthAntiAlias(mw, mh);
        var n = (float[])raw.Clone();
        for (int f = 0; f < 40; f++) { soft.Apply(n, 1, 1); anti.Apply(n); }
        Thread.Sleep(300);
        var c1 = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 0; f < 20; f++) soft.Apply(n, 1, 1);
        double softMs = c1.Elapsed.TotalMilliseconds / 20;
        var c2 = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 0; f < 20; f++) anti.Apply(n);
        double antiMs = c2.Elapsed.TotalMilliseconds / 20;
        testOutput.WriteLine($"one soft-edge pass {softMs:0.00} ms, anti-aliasing {antiMs:0.00} ms (Vector<float> {System.Numerics.Vector<float>.Count} wide, accelerated {System.Numerics.Vector.IsHardwareAccelerated})");

        // The rest: the range, the captions and the plain pass.
        var plain = new DepthPostProcessor(mw, mh);
        var captions = new CaptionLock(mw, mh);
        for (int f = 0; f < 40; f++) { plain.MeasureRange(raw); captions.Apply(luma, output, 1 / 30.0, 0.35); plain.Process(raw, luma, 1 / 30.0, output, (0, 1)); }
        var c3 = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 0; f < 20; f++) plain.MeasureRange(raw);
        double rangeMs = c3.Elapsed.TotalMilliseconds / 20;
        var c4 = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 0; f < 20; f++) captions.Apply(luma, output, 1 / 30.0, 0.35);
        double captionMs = c4.Elapsed.TotalMilliseconds / 20;
        var c5 = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 0; f < 20; f++) plain.Process(raw, luma, 1 / 30.0, output, (0, 1));
        double plainMs = c5.Elapsed.TotalMilliseconds / 20;
        testOutput.WriteLine($"range {rangeMs:0.00} ms, captions {captionMs:0.00} ms, plain pass with a given range {plainMs:0.00} ms");
    }

    /// <summary>The app's code is JIT-optimised (a Release build); timings only mean something then.</summary>
    private static bool Optimized =>
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>(typeof(SoftEdges).Assembly)
            ?.IsJITOptimizerDisabled != true;

    [Theory]
    [InlineData(0.4, 33.8, 1920, 518, 2)]
    [InlineData(0.6, 33.8, 1920, 518, 2)]
    [InlineData(0.6, 33.8, 1920, 756, 3)]
    [InlineData(0.8, 45.0, 1920, 518, 3)]
    [InlineData(0.8, 45.0, 1920, 756, 4)]
    [InlineData(1.0, 56.3, 1920, 518, 4)]
    [InlineData(1.0, 56.3, 1920, 756, 4)]
    public void PassesFollowThePlansTable(double sigma, double strength, int region, int model, int expected)
    {
        // §7.1's table; σ = 0.4 gives S = 22.5.
        double s = sigma == 0.4 ? 22.5 : strength;
        Assert.Equal(expected, SoftEdges.AutoPasses(s, region, model));
    }
}
