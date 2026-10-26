using System.Diagnostics;
using ImmersiveSBS.Depth;
using Xunit.Abstractions;
using static ImmersiveSBS.Tests.DepthStats;

namespace ImmersiveSBS.Tests;

/// <summary>
/// Every offered model against Distill-Any-Depth Base, the default, on consecutive frames of the
/// sample clip: above all Video Depth Anything Small's streaming mode (improvement-plan.md §10.2,
/// its go/no-go: M4 at least 30 % lower). Flicker on still pixels (M4: mean |nₜ − nₜ₋₁| where the luma moved less than
/// 1.5/255) of the model's own output, normalised as the app does, and of what the depth service
/// makes of it, as the app runs it; time per frame; and whether the two order near and far
/// alike. Reports; asserts only that the streaming model's output is sane.
/// </summary>
[Collection("Timing")]
public class TemporalDepthTests(ITestOutputHelper output)
{
    private const int Frames = 150;
    private const float Still = 1.5f / 255;

    [Theory]
    [InlineData(DepthPreset.Balanced)]
    [InlineData(DepthPreset.Quality)]
    public async Task VideoDepthAnythingAgainstTheDefault(DepthPreset preset)
    {
        var reference = new DepthModel(DepthTier.Base, preset);
        var streaming = new DepthModel(DepthTier.VdaSmall, preset);
        if (!SampleClip.Exists || !reference.Exists || !streaming.Exists)
        {
            output.WriteLine("Skipped: needs the sample clip and both models.");
            return;
        }
        DepthModel[] models = [reference, .. new[] { DepthTier.DA3Small, DepthTier.DA3Base }
            .Select(t => new DepthModel(t, preset)).Where(m => m.Exists), streaming];
        int w = reference.Width, h = reference.Height, plane = w * h;
        // Sampled every 1/30 s, a frame shown longer comes twice; the app hands the model new
        // pictures only, and a model without memory gives a repeat no flicker at all.
        List<byte[]> pictures = await SampleClip.Frames(Frames, w, h);
        List<byte[]> distinct = [pictures[0]];
        foreach (byte[] p in pictures.Skip(1))
            if (!p.AsSpan().SequenceEqual(distinct[^1])) distinct.Add(p);
        List<float[]> inputs = [.. distinct.Select(p => SampleClip.ModelInput(p, plane))];
        List<float[]> lumas = [.. inputs.Select(input =>
        {
            var luma = new float[plane];
            DepthService.Luma(input, luma, plane);
            return luma;
        })];
        output.WriteLine($"{distinct.Count} distinct pictures in {Frames} consecutive frames of {System.IO.Path.GetFileName(SampleClip.Path)} " +
                         $"sampled at 30 fps, {w}×{h}.");

        var raw = new Dictionary<DepthTier, List<float[]>>();
        var served = new Dictionary<DepthTier, List<float[]>>();
        var speed = new Dictionary<DepthTier, double>();
        foreach (DepthModel model in models)
        {
            IDepthRunner runner;
            try
            {
                runner = model.Temporal ? VideoDepthEstimator.Load(model) : DepthEstimator.Load(model);
            }
            catch (Exception e) when (e is InvalidOperationException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
            {
                output.WriteLine($"Skipped: {model} does not run here ({e.Message}).");
                return;
            }
            using (runner)
            {
                var maps = new List<float[]>();
                var times = new List<double>();
                foreach (float[] input in inputs)
                {
                    var o = new float[plane];
                    long t0 = Stopwatch.GetTimestamp();
                    runner.Run(input, o);
                    times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                    maps.Add(o);
                }
                raw[model.Tier] = maps;
                speed[model.Tier] = Median(times.Skip(10));
            }
            served[model.Tier] = await Serve(model, inputs);
        }

        output.WriteLine("model                          ms/frame   M4 model alone   M4 through the service");
        foreach (DepthModel model in models)
        {
            double alone = Flicker([.. raw[model.Tier].Select(Normalised)], lumas);
            double service = Flicker(served[model.Tier], lumas);
            output.WriteLine($"{model,-30} {speed[model.Tier],8:0.0}   {alone,14:0.0000}   {service,22:0.0000}");
        }
        double ratioAlone = Flicker([.. raw[DepthTier.VdaSmall].Select(Normalised)], lumas) / Flicker([.. raw[DepthTier.Base].Select(Normalised)], lumas);
        double ratioService = Flicker(served[DepthTier.VdaSmall], lumas) / Flicker(served[DepthTier.Base], lumas);
        output.WriteLine($"Video Depth Anything's M4 against Distill-Any-Depth Base: {ratioAlone:P0} alone, {ratioService:P0} through the service (go: at most 70 %).");

        double rho = raw[DepthTier.VdaSmall].Zip(raw[DepthTier.Base]).Where((_, i) => i % 10 == 0).Average(p => Spearman(p.First, p.Second));
        output.WriteLine($"Near-and-far order against Distill-Any-Depth Base: Spearman ρ {rho:0.00} (every 10th frame).");
        Assert.True(rho > 0.5, $"Video Depth Anything orders depth unlike Distill-Any-Depth Base (ρ = {rho:0.00})");
        Assert.All(raw[DepthTier.VdaSmall], m => Assert.True(m.All(float.IsFinite)));
    }

    /// <summary>Each frame through a depth service, as the app runs it, one at a time: its nearness maps.</summary>
    private static async Task<List<float[]>> Serve(DepthModel model, List<float[]> inputs)
    {
        using var service = new DepthService(model) { KeepAll = true };
        var wait = Stopwatch.StartNew();
        while (service.State is DepthServiceState.Loading or DepthServiceState.Preparing && wait.Elapsed < TimeSpan.FromMinutes(3))
            await Task.Delay(50);
        Assert.Equal(DepthServiceState.Ready, service.State);
        var maps = new List<float[]>();
        DepthFrame? frame = null;
        for (int i = 0; i < inputs.Count; i++)
        {
            service.Submit(inputs[i], i, i / 30.0);
            var timeout = Stopwatch.StartNew();
            while (!(service.TryTake(ref frame) && frame!.FrameId == i))
            {
                Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(10), $"No depth for frame {i}.");
                await Task.Delay(1);
            }
            maps.Add((float[])frame.Nearness.Clone());
        }
        return maps;
    }

    /// <summary>M4: the mean over frames of mean |nₜ − nₜ₋₁| over the pixels whose luma barely moved.</summary>
    private static double Flicker(List<float[]> maps, List<float[]> lumas)
    {
        double total = 0;
        int frames = 0;
        for (int t = 1; t < maps.Count; t++)
        {
            double sum = 0;
            int still = 0;
            for (int i = 0; i < maps[t].Length; i++)
            {
                if (Math.Abs(lumas[t][i] - lumas[t - 1][i]) >= Still) continue;
                sum += Math.Abs(maps[t][i] - maps[t - 1][i]);
                still++;
            }
            if (still == 0) continue;
            total += sum / still;
            frames++;
        }
        return frames > 0 ? total / frames : double.NaN;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        return sorted[sorted.Length / 2];
    }
}
