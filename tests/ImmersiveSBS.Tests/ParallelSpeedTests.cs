using System.Diagnostics;
using ImmersiveSBS.Depth;
using Xunit.Abstractions;

namespace ImmersiveSBS.Tests;

/// <summary>Timing tests run on their own, after the others: tests running alongside would skew them.</summary>
[CollectionDefinition("Timing", DisableParallelization = true)]
public sealed class TimingCollection;

/// <summary>
/// The CPU's part of the depth path on one core and split over several (<see cref="RowBands"/>),
/// on the same frames: what the split buys, and that it changes nothing. The frames come from a
/// real video when <see cref="SampleClip"/> has one, with depth from the real model when it runs
/// here (TensorRT for RTX); otherwise from a drawn scene and depth made from the picture.
/// Timings mean something only in a Release build.
/// </summary>
[Collection("Timing")]
public class ParallelSpeedTests(ITestOutputHelper output)
{
    private const int Frames = 48;

    private sealed record Sequence(string Source, List<float[]> Inputs, List<float[]> Luma, List<float[]> Raw);

    [Theory]
    [InlineData(DepthPreset.Balanced)]
    [InlineData(DepthPreset.Quality)]
    public async Task TheDepthPathsCpuStepsOnOneCoreAndInParallel(DepthPreset preset)
    {
        var model = new DepthModel(DepthTier.DA3Small, preset);
        int w = model.Width, h = model.Height;
        Sequence s = await Load(model);
        output.WriteLine($"{Frames} frames at {w}×{h}: {s.Source}; {Environment.ProcessorCount} logical processors, " +
                         $"{RowBands.Count(h)} bands{(Optimized ? "" : " (Debug build: timings unoptimised)")}.");

        // 1. The post-processing as the app runs it for DA2/DA3 with the edge steps.
        var serialOut = new List<float[]>();
        var parallelOut = new List<float[]>();
        double serialPost = double.MaxValue, parallelPost = double.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            using (RowBands.Limit(1)) serialPost = Math.Min(serialPost, RunPost(s, w, h, serialOut));
            parallelPost = Math.Min(parallelPost, RunPost(s, w, h, parallelOut));
        }
        float largest = 0;
        for (int f = 0; f < Frames; f++)
            for (int i = 0; i < w * h; i++) largest = Math.Max(largest, Math.Abs(serialOut[f][i] - parallelOut[f][i]));

        // 2. The luma the depth thread takes from each input, between two model runs.
        var luma = new float[w * h];
        double serialLuma = Time(() => { using (RowBands.Limit(1)) foreach (float[] x in s.Inputs) DepthService.Luma(x, luma, w * h); }) / Frames;
        double parallelLuma = Time(() => { foreach (float[] x in s.Inputs) DepthService.Luma(x, luma, w * h); }) / Frames;

        // 3. The render thread's blend for a shown frame whose own depth the model skipped (kept in 16 bits).
        ushort[][] maps = [.. parallelOut.Take(2).Select(n => { var p = new ushort[n.Length]; NearnessBlend.Pack(n, p); return p; })];
        var blended = new float[w * h];
        double serialTwo = Time(() => { using (RowBands.Limit(1)) for (int k = 0; k < Frames; k++) NearnessBlend.Lerp(maps[0], maps[1], 0.4f, blended); }) / Frames;
        double parallelTwo = Time(() => { for (int k = 0; k < Frames; k++) NearnessBlend.Lerp(maps[0], maps[1], 0.4f, blended); }) / Frames;

        output.WriteLine("ms per frame          one core   parallel   speed-up");
        Row("post-processing", serialPost, parallelPost);
        Row("luma (depth thread)", serialLuma, parallelLuma);
        Row("blend of 2", serialTwo, parallelTwo);
        output.WriteLine($"Largest difference between the two outputs: {largest:0.#######} (nearness 0..1).");

        Assert.True(largest < 1e-4f, $"the split changed the depth by {largest}");
        if (Optimized) Assert.True(parallelPost < serialPost, $"parallel {parallelPost:0.00} ms against one core {serialPost:0.00} ms");
    }

    private void Row(string step, double serial, double parallel) =>
        output.WriteLine($"{step,-20} {serial,9:0.00} {parallel,10:0.00} {serial / parallel,9:0.0}×");

    /// <summary>One pass over the sequence with a fresh post-processor; the median ms per frame. Keeps the outputs.</summary>
    private static double RunPost(Sequence s, int w, int h, List<float[]> outputs)
    {
        var post = new DepthPostProcessor(w, h) { AntiAlias = true, SoftEdgePassesX = 2, SoftEdgePassesY = 1, Captions = true };
        outputs.Clear();
        var times = new double[Frames];
        for (int f = 0; f < Frames; f++)
        {
            var n = new float[w * h];
            long t0 = Stopwatch.GetTimestamp();
            post.Process(s.Raw[f], s.Luma[f], 1 / 30.0, n);
            times[f] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            outputs.Add(n);
        }
        Array.Sort(times);
        return times[Frames / 2];
    }

    private static double Time(Action action)
    {
        action(); // warm-up, and the JIT
        var watch = Stopwatch.StartNew();
        action();
        return watch.Elapsed.TotalMilliseconds;
    }

    private async Task<Sequence> Load(DepthModel model)
    {
        int w = model.Width, h = model.Height, plane = w * h;
        List<byte[]> pictures = await SampleClip.Frames(Frames, w, h);
        string source = SampleClip.Exists ? $"{System.IO.Path.GetFileName(SampleClip.Path)} from 1 s on" : $"a drawn moving scene (no {SampleClip.Path})";

        var inputs = new List<float[]>();
        var lumas = new List<float[]>();
        foreach (byte[] bgra in pictures)
        {
            float[] input = SampleClip.ModelInput(bgra, plane);
            var luma = new float[plane];
            DepthService.Luma(input, luma, plane);
            inputs.Add(input);
            lumas.Add(luma);
        }

        List<float[]> raw = [];
        using (IDepthRunner? estimator = SampleClip.TryLoad(model, out string why))
        {
            if (estimator != null)
            {
                foreach (float[] input in inputs)
                {
                    var depth = new float[plane];
                    estimator.Run(input, depth);
                    raw.Add(depth);
                }
                return new Sequence($"{source}, depth from {model}", inputs, lumas, raw);
            }
            output.WriteLine($"The model does not run here ({why}).");
        }
        // Without the model: depth from the picture, nearer toward the bottom.
        foreach (float[] l in lumas)
        {
            var d = new float[plane];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    d[y * w + x] = 0.6f * l[y * w + x] + 0.4f * y / h;
            raw.Add(d);
        }
        return new Sequence($"{source}, depth made from the picture ({model} not available)", inputs, lumas, raw);
    }

    /// <summary>The app's code is JIT-optimised (a Release build); timings only mean something then.</summary>
    private static bool Optimized =>
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute<DebuggableAttribute>(typeof(RowBands).Assembly)
            ?.IsJITOptimizerDisabled != true;
}
