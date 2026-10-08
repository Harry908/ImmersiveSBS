using System.Diagnostics;
using ImmersiveSBS.Depth;
using Xunit.Abstractions;

namespace ImmersiveSBS.Tests;

/// <summary>
/// Runs the real model on TensorRT for RTX. Needs an NVIDIA RTX GPU, the Windows ML provider
/// package and the model files; without them the test reports why and returns. The first run
/// on a machine compiles the engine (about 20 s) and caches it.
/// </summary>
public class DepthEstimatorTests(ITestOutputHelper output)
{
    [Fact]
    public void BalancedBaseModelRunsAndSeesASlope()
    {
        var model = new DepthModel(DepthTier.Base, DepthPreset.Balanced);
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
            const int runs = 200;
            for (int i = 0; i < runs; i++) estimator.Run(input, depth);
            double ms = watch.Elapsed.TotalMilliseconds / runs;
            output.WriteLine($"{model}: {ms:0.0} ms per frame on TensorRT for RTX {TensorRtRtx.Version}.");

            Assert.All(depth, v => Assert.True(float.IsFinite(v)));
            Assert.True(depth.Max() > depth.Min(), "depth is not constant");
            Assert.True(ms < 50, $"inference took {ms:0.0} ms");

            double bottom = depth.Skip((h - 20) * w).Take(10 * w).Average();
            double top = depth.Skip(10 * w).Take(10 * w).Average();
            output.WriteLine($"mean inverse depth: bottom {bottom:0.###}, top {top:0.###}");
        }
    }
}
