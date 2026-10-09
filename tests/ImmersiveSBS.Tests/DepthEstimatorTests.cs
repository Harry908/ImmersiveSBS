using System.Diagnostics;
using ImmersiveSBS.Depth;
using Xunit.Abstractions;

namespace ImmersiveSBS.Tests;

/// <summary>
/// Runs the real model on TensorRT for RTX. Needs an NVIDIA RTX GPU, the Windows ML provider
/// package and the model files; without them the test reports why and returns. The first run
/// of each model on a machine compiles its engine (20–30 s) and caches it, so this also prepares
/// the app's first use; the output lists each model's time per frame on this GPU. (On this
/// synthetic picture every model puts the top nearer, so the printed means compare models; they
/// are not checked.)
/// </summary>
public class DepthEstimatorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(DepthTier.Small, DepthPreset.Balanced)]
    [InlineData(DepthTier.Base, DepthPreset.Balanced)]
    [InlineData(DepthTier.Large, DepthPreset.Balanced)]
    [InlineData(DepthTier.V2Base, DepthPreset.Balanced)]
    [InlineData(DepthTier.V2Large, DepthPreset.Balanced)]
    [InlineData(DepthTier.Small, DepthPreset.Quality)]
    [InlineData(DepthTier.Base, DepthPreset.Quality)]
    [InlineData(DepthTier.V2Base, DepthPreset.Quality)]
    [InlineData(DepthTier.V2Large, DepthPreset.Quality)]
    [InlineData(DepthTier.DA3Small, DepthPreset.Balanced)]
    [InlineData(DepthTier.DA3Small, DepthPreset.Quality)]
    [InlineData(DepthTier.DA3Base, DepthPreset.Balanced)]
    [InlineData(DepthTier.DA3Base, DepthPreset.Quality)]
    public void ModelRunsOnTensorRt(DepthTier tier, DepthPreset preset)
    {
        var model = new DepthModel(tier, preset);
        if (!model.Exists)
        {
            output.WriteLine($"Skipped: {model.FilePath} is missing.");
            return;
        }

        DepthEstimator estimator;
        try
        {
            estimator = DepthEstimator.Load(model, output.WriteLine);
        }
        catch (InvalidOperationException e)
        {
            output.WriteLine("Skipped: " + e.Message);
            return;
        }

        using (estimator)
        {
            // A floor: a horizontal texture whose stripes get finer toward the top, the classic
            // perspective cue; the bottom should come out nearer than the top.
            int w = model.Width, h = model.Height, plane = w * h;
            var input = new float[3 * plane];
            for (int y = 0; y < h; y++)
            {
                double distance = 1 + 8.0 * (h - 1 - y) / (h - 1);
                for (int x = 0; x < w; x++)
                {
                    double v = 0.5 + 0.4 * Math.Sin((x - w / 2.0) / distance * 0.6) * Math.Sin(30.0 / distance);
                    input[y * w + x] = (float)((v - 0.485) / 0.229);
                    input[plane + y * w + x] = (float)((v - 0.456) / 0.224);
                    input[2 * plane + y * w + x] = (float)((v - 0.406) / 0.225);
                }
            }

            var depth = new float[plane];
            estimator.Run(input, depth); // warm-up
            var watch = Stopwatch.StartNew();
            const int runs = 100;
            for (int i = 0; i < runs; i++) estimator.Run(input, depth);
            double ms = watch.Elapsed.TotalMilliseconds / runs;
            output.WriteLine($"{model}: {ms:0.0} ms per frame on TensorRT for RTX {TensorRtRtx.Version}.");

            Assert.All(depth, v => Assert.True(float.IsFinite(v)));
            Assert.True(depth.Max() > depth.Min(), "depth is not constant");
            // Catches an engine gone badly wrong (V2 Base 756×420 once seemed to run at 825 ms; it is
            // 20 ms), with room for the GPU being shared with a running app (times rise ~1.5–2×).
            // DA3 is fp32 (the fp16 converter breaks its graph): Base ~36 ms Balanced, ~110 Quality.
            Assert.True(ms < (preset == DepthPreset.Quality ? 250 : 100), $"inference took {ms:0.0} ms");

            double bottom = depth.Skip((h - 20) * w).Take(10 * w).Average();
            double top = depth.Skip(10 * w).Take(10 * w).Average();
            output.WriteLine($"mean inverse depth: bottom {bottom:0.###}, top {top:0.###}");
        }
    }
}
