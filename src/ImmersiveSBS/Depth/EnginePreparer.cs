using System.Diagnostics;

namespace ImmersiveSBS.Depth;

/// <summary>
/// Builds at start, in the background and one at a time, the engines of every model and size the
/// app offers that has none for this GPU yet, so a later change of model loads in about a second
/// instead of waiting for its build (4–9 s a graph, 26 s for Video Depth Anything's two at
/// 756×420 on the RTX 5060). A build uses the GPU hard for seconds, so it waits while
/// <c>mayRun</c> says no (3D in use, or the chosen model loading). Once per PC, driver and
/// provider: after that every engine is there and it ends at once.
/// </summary>
public sealed class EnginePreparer : IDisposable
{
    private readonly Func<bool> mayRun;
    private readonly IReadOnlyList<DepthModel> models;
    private readonly CancellationTokenSource stop = new();
    private readonly Thread thread;

    /// <param name="first">The preset in use: its models are built first.</param>
    /// <param name="mayRun">Whether a build may start now; asked from the preparer's thread.</param>
    public EnginePreparer(DepthPreset first, Func<bool> mayRun)
    {
        this.mayRun = mayRun;
        models = [.. Enum.GetValues<DepthPreset>().OrderBy(p => p == first ? 0 : 1)
            .SelectMany(p => Enum.GetValues<DepthTier>().Select(t => new DepthModel(t, p)))
            .Where(m => m.Exists)];
        thread = new Thread(Run) { Name = "Engine preparation", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    private void Run()
    {
        try
        {
            var all = Stopwatch.StartNew();
            int built = 0;
            foreach (DepthModel model in models)
            {
                while (!mayRun())
                    if (stop.Token.WaitHandle.WaitOne(2000)) return;
                if (stop.IsCancellationRequested) return;
                var watch = Stopwatch.StartNew();
                if (!DepthEstimator.Prepare(model)) continue;
                built++;
                Log.Write($"Depth: prepared {model} for this GPU in the background ({watch.Elapsed.TotalSeconds:0} s).");
            }
            if (built > 0) Log.Write($"Depth: every model is prepared for this GPU ({built} built in {all.Elapsed.TotalSeconds:0} s).");
        }
        catch (Exception e)
        {
            // TensorRT for RTX missing or failing: the chosen model's load says so; nothing more to do here.
            Log.Write("Depth: background preparation stopped: " + e.Message);
        }
    }

    /// <summary>Stops before the next build; one under way finishes (it cannot be interrupted) unless the process ends first.</summary>
    public void Dispose()
    {
        stop.Cancel();
        thread.Join(100);
    }
}
