using ImmersiveSBS.Depth;
using Xunit.Abstractions;

namespace ImmersiveSBS.Tests;

/// <summary>
/// The models do not give the same kind of output: Depth Anything V2 and Distill-Any-Depth give
/// relative inverse depth (affine-invariant disparity: larger = nearer, unknown scale and
/// shift); Depth Anything 3 gives depth (larger = farther, unknown scale). The estimator turns
/// DA3's into inverse depth (1/d), and the post-processor's percentile normalisation removes
/// scale and shift, so every model should come out as the same quantity. This checks it on a
/// real clip: after the app's conversion, every model orders near and far like the others
/// (rank correlation), DA3's depth stays clear of the inversion's floor, and it shows what
/// treating DA3's depth like V2's output (flipped, not inverted) would do to the picture.
/// </summary>
[Collection("Timing")]
public class ModelAgreementTests(ITestOutputHelper output)
{
    private const int Frames = 12;

    /// <summary>Every 7th pixel enters the comparisons.</summary>
    private const int Stride = 7;

    [Fact]
    public async Task EveryModelComesOutAsTheSameKindOfDepth()
    {
        DepthTier[] tiers = Enum.GetValues<DepthTier>();
        var probe = new DepthModel(DepthTier.Small, DepthPreset.Balanced);
        int w = probe.Width, h = probe.Height, plane = w * h;
        // Frames 2/3 s apart over most of the clip: different shots, not one.
        List<byte[]> pictures = await SampleClip.Frames(Frames, w, h, step: 20);
        List<float[]> inputs = [.. pictures.Select(p => SampleClip.ModelInput(p, plane))];
        output.WriteLine(SampleClip.Exists ? $"{Frames} frames of {System.IO.Path.GetFileName(SampleClip.Path)}, {w}×{h}." : "No sample clip: a drawn scene.");

        var outputs = new Dictionary<DepthTier, List<float[]>>();
        foreach (DepthTier tier in tiers)
        {
            using DepthEstimator? estimator = SampleClip.TryLoad(new DepthModel(tier, DepthPreset.Balanced), out string why);
            if (estimator == null)
            {
                output.WriteLine($"{DepthModel.Name(tier)}: skipped ({why}).");
                continue;
            }
            outputs[tier] = [.. inputs.Select(input =>
            {
                var o = new float[plane];
                estimator.Run(input, o);
                return o;
            })];
        }
        if (!outputs.ContainsKey(DepthTier.Small))
        {
            output.WriteLine("Skipped: Depth Anything V2 Small, the reference, does not run here.");
            return;
        }

        output.WriteLine("model                     output (p2..p98)                 rank corr. with DA2 S / DAD B   mean |Δn| to DA2 S");
        foreach ((DepthTier tier, List<float[]> maps) in outputs)
        {
            var model = new DepthModel(tier, DepthPreset.Balanced);
            double withSmall = Mean(maps, outputs[DepthTier.Small], Spearman);
            double withBase = outputs.TryGetValue(DepthTier.Base, out List<float[]>? b) ? Mean(maps, b, Spearman) : double.NaN;
            double difference = Mean(maps, outputs[DepthTier.Small], MeanAbsoluteDifference);
            (float p2, float p98) = Percentiles(maps[0]);
            string range = model.OutputIsDepth
                ? $"depth {1 / p98:0.###}..{1 / p2:0.###}, inverted"
                : $"inverse depth {p2:0.###}..{p98:0.###}";
            output.WriteLine($"{DepthModel.Name(tier),-25} {range,-32} {withSmall,6:0.00} / {withBase,4:0.00}" +
                             $"                {difference,6:0.000}");

            // Same near-and-far order as the reference: a model read the wrong way round would be strongly negative.
            Assert.True(withSmall > 0.5, $"{DepthModel.Name(tier)} orders depth unlike DA2 Small (ρ = {withSmall:0.00})");

            if (model.OutputIsDepth)
            {
                // The inversion's floor (depth 1e-3) must not flatten the nearest things.
                double floored = maps.Average(m => m.Count(v => v >= 999.5f) / (double)m.Length);
                // What a V2-style pipeline would do with it: depth flipped and normalised linearly.
                double flippedDifference = maps.Select(m =>
                {
                    float[] flipped = [.. m.Select(v => -1 / v)];
                    return MeanAbsoluteDifference(m, flipped);
                }).Average();
                output.WriteLine($"  at the inversion's floor: {floored:0.####%} of pixels; read like V2's output (flipped, " +
                                 $"not inverted) its nearness would move by {flippedDifference:0.000} on average");
                Assert.True(floored < 0.005, $"{floored:P} of DA3's pixels hit the floor");
            }
        }
    }

    private static double Mean(List<float[]> a, List<float[]> b, Func<float[], float[], double> measure) =>
        a.Zip(b).Average(p => measure(p.First, p.Second));

    /// <summary>Spearman's ρ over every <see cref="Stride"/>-th pixel: how alike the near-to-far order is, whatever the curve.</summary>
    private static double Spearman(float[] a, float[] b)
    {
        double[] ra = Ranks(a), rb = Ranks(b);
        double ma = ra.Average(), mb = rb.Average(), sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < ra.Length; i++)
        {
            sab += (ra[i] - ma) * (rb[i] - mb);
            saa += (ra[i] - ma) * (ra[i] - ma);
            sbb += (rb[i] - mb) * (rb[i] - mb);
        }
        return sab / Math.Sqrt(saa * sbb);
    }

    private static double[] Ranks(float[] values)
    {
        float[] sampled = [.. values.Where((_, i) => i % Stride == 0)];
        int[] order = [.. Enumerable.Range(0, sampled.Length)];
        Array.Sort((float[])sampled.Clone(), order);
        var ranks = new double[sampled.Length];
        for (int r = 0; r < order.Length; r++) ranks[order[r]] = r;
        return ranks;
    }

    /// <summary>Mean |Δ| of the two maps after the app's normalisation (2nd–98th percentile to 0..1).</summary>
    private static double MeanAbsoluteDifference(float[] a, float[] b)
    {
        float[] na = Normalised(a), nb = Normalised(b);
        double sum = 0;
        for (int i = 0; i < na.Length; i += Stride) sum += Math.Abs(na[i] - nb[i]);
        return sum / ((na.Length + Stride - 1) / Stride);
    }

    private static float[] Normalised(float[] m)
    {
        (float p2, float p98) = Percentiles(m);
        float span = Math.Max(p98 - p2, 1e-9f);
        return [.. m.Select(v => Math.Clamp((v - p2) / span, 0, 1))];
    }

    private static (float P2, float P98) Percentiles(float[] m)
    {
        float[] sorted = [.. m.Where((_, i) => i % 4 == 0).Order()];
        return (sorted[(int)(0.02 * (sorted.Length - 1))], sorted[(int)(0.98 * (sorted.Length - 1))]);
    }
}
