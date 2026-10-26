using System.Diagnostics;
using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Tests;

/// <summary>The depth worker's two threads (improvement-plan.md §9 step 1), with a stand-in model.</summary>
/// <remarks>Its look-ahead lets frames go after 200 ms idle: run alone, or a loaded machine makes that window.</remarks>
[Collection("Timing")]
public class DepthServiceTests
{
    private static readonly DepthModel Model = new(DepthTier.Base, DepthPreset.Balanced);   // 518×294

    /// <summary>A model that takes <paramref name="ms"/> and answers with a ramp scaled by the input's first value.</summary>
    private sealed class FakeRunner(int length, int width, double ms) : IDepthRunner
    {
        public int OutputLength => length;

        public int Runs;

        public void Run(float[] input, float[] output)
        {
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < length; i++) output[i] = (i % width) * (1 + input[0]);
            while (clock.Elapsed.TotalMilliseconds < ms) Thread.SpinWait(100);
            Interlocked.Increment(ref Runs);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A short wait that does not depend on the system timer's 15.6 ms resolution.</summary>
    private static void Wait(double ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalMilliseconds < ms) Thread.SpinWait(50);
    }

    private static DepthService Start(double modelMs, out FakeRunner runner)
    {
        var fake = new FakeRunner(Model.Width * Model.Height, Model.Width, modelMs);
        runner = fake;
        var service = new DepthService(Model, (_, _) => fake);
        var clock = Stopwatch.StartNew();
        while (service.State != DepthServiceState.Ready && clock.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(5);
        Assert.Equal(DepthServiceState.Ready, service.State);
        return service;
    }

    [Fact]
    public void FramesComeOutInOrderAndNewestFirst()
    {
        using DepthService service = Start(modelMs: 8, out FakeRunner runner);
        var input = new float[3 * Model.Width * Model.Height];
        DepthFrame? frame = null;
        var seen = new List<long>();
        var clock = Stopwatch.StartNew();
        long id = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(1))
        {
            // Frames every 2 ms for an 8 ms model: most must be dropped, not queued.
            service.Submit(input, ++id, clock.Elapsed.TotalSeconds);
            Wait(2);
            if (service.TryTake(ref frame)) seen.Add(frame!.FrameId);
        }
        Assert.True(seen.Count > 10, $"{seen.Count} depth frames");
        for (int i = 1; i < seen.Count; i++) Assert.True(seen[i] > seen[i - 1], string.Join(",", seen));
        // The model is never given the same frame twice, and frames it was too slow for are dropped.
        Assert.True(runner.Runs < id, $"{runner.Runs} runs for {id} frames");
        // The nearness is a normalised ramp.
        Assert.InRange(frame!.Nearness[Model.Width - 1], 0.95f, 1f);
        Assert.InRange(frame.Nearness[0], 0f, 0.05f);
    }

    [Fact]
    public void PostProcessingRunsAlongsideTheNextFrame()
    {
        // With heavy post-processing (anti-aliasing and four soft-edge passes) and an 8 ms model,
        // a frame every model + post would be fewer than in one model-time each; alongside, the
        // rate is set by the slower of the two.
        using DepthService service = Start(modelMs: 8, out _);
        service.Tuning = StereoTuning.Default with { AntiAlias = AutoSwitch.On };
        service.SoftEdgePasses = 4;
        var input = new float[3 * Model.Width * Model.Height];
        DepthFrame? frame = null;
        int taken = 0;
        var clock = Stopwatch.StartNew();
        long id = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(2))
        {
            service.Submit(input, ++id, clock.Elapsed.TotalSeconds);
            Thread.Sleep(1);
            if (service.TryTake(ref frame)) taken++;
        }
        // The status names the split: "…: 8.0 ms + post N ms alongside, waiting …".
        Assert.Contains("alongside", service.Status);
        Assert.True(taken > 0);
    }

    [Fact]
    public void LookingAheadHoldsFramesBackAndLetsThemGoWhenTheVideoStops()
    {
        using DepthService service = Start(modelMs: 2, out _);
        service.LookAheadFrames = 5;
        var input = new float[3 * Model.Width * Model.Height];
        DepthFrame? frame = null;
        long newest = 0;
        for (long id = 1; id <= 30; id++)
        {
            service.Submit(input, id, id * 0.033);
            Wait(15);
            if (service.TryTake(ref frame)) newest = frame!.FrameId;
            // Each frame waits for the five after it.
            Assert.True(newest == 0 || newest <= id - 5, $"frame {newest} out with {id} in");
        }
        // Nothing more comes (a paused video): the held frames go after 200 ms.
        Wait(400);
        Assert.True(service.TryTake(ref frame));
        Assert.Equal(30, frame!.FrameId);
    }

    [Fact]
    public void KeepingAllLetsEveryHeldFrameThroughInOrderWhenTheVideoStops()
    {
        // The buffer files each depth frame with its own held frame: when a pause lets the
        // look-ahead go at once, none may be lost to a newer one (the frames before every pause
        // or seek were shown with depth from up to a second away).
        using DepthService service = Start(modelMs: 2, out _);
        service.LookAheadFrames = 5;
        service.KeepAll = true;
        var input = new float[3 * Model.Width * Model.Height];
        DepthFrame? frame = null;
        var seen = new List<long>();
        for (long id = 1; id <= 20; id++)
        {
            // Slower than the post-processing even in Debug, so the model drops none.
            service.Submit(input, id, id * 0.04);
            Wait(40);
            while (service.TryTake(ref frame)) seen.Add(frame!.FrameId);
        }
        Wait(400);
        while (service.TryTake(ref frame))
        {
            seen.Add(frame!.FrameId);
            // Held for the look-ahead: that time is not the depth's latency.
            if (frame.FrameId == 20) Assert.InRange(frame.HeldMs, 150, 1000);
        }
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), seen);
    }

    /// <summary>A model with memory that logs its runs (by the input's first value) and resets, and can hold a run.</summary>
    private sealed class TemporalFake(int length, int width) : ITemporalDepthRunner
    {
        public readonly ManualResetEventSlim Release = new(true);
        public readonly ManualResetEventSlim Started = new(false);
        public readonly List<string> Log = [];

        public int OutputLength => length;

        public void Run(float[] input, float[] output)
        {
            lock (Log) Log.Add($"run {input[0]}");
            Started.Set();
            Release.Wait();
            for (int i = 0; i < length; i++) output[i] = i % width;
        }

        public void Reset()
        {
            lock (Log) Log.Add("reset");
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void AModelWithMemoryStartsOverAtTheFirstFrameSubmittedAfterAReset()
    {
        // Frame 2 is submitted before a seek and run after it: it belongs to the old place, and
        // must not become the first frame of the new window (the streaming model keeps that one).
        var fake = new TemporalFake(Model.Width * Model.Height, Model.Width);
        using var service = new DepthService(Model, (_, _) => fake);
        var clock = Stopwatch.StartNew();
        while (service.State != DepthServiceState.Ready && clock.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(5);
        var input = new float[3 * Model.Width * Model.Height];
        // The warm-up's runs, and the reset after them, come before Ready (WarmsTheModelBeforeItIsReady).
        lock (fake.Log) fake.Log.Clear();
        fake.Started.Reset();

        fake.Release.Reset();
        input[0] = 1;
        service.Submit(input, 1, 0.00);
        Assert.True(fake.Started.Wait(2000));
        input[0] = 2;
        service.Submit(input, 2, 0.04);
        service.ResetHistory();
        fake.Release.Set();
        Assert.True(SpinWait.SpinUntil(() => { lock (fake.Log) return fake.Log.Contains("run 2"); }, 2000));
        input[0] = 3;
        service.Submit(input, 3, 0.08);
        Assert.True(SpinWait.SpinUntil(() => { lock (fake.Log) return fake.Log.Contains("run 3"); }, 2000));

        lock (fake.Log) Assert.Equal(["run 1", "run 2", "reset", "run 3"], fake.Log);
    }

    [Fact]
    public void WarmsTheModelBeforeItIsReady()
    {
        // The first frames of 3D must not pay for the GPU's first work or the post-processing's
        // first, unoptimised calls: the model runs before Ready, and a model with memory forgets it.
        var fake = new TemporalFake(Model.Width * Model.Height, Model.Width);
        using var service = new DepthService(Model, (_, _) => fake);
        var clock = Stopwatch.StartNew();
        while (service.State != DepthServiceState.Ready && clock.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(5);
        Assert.Equal(DepthServiceState.Ready, service.State);
        lock (fake.Log)
        {
            Assert.True(fake.Log.Count(e => e.StartsWith("run ")) >= 2, string.Join(", ", fake.Log));
            Assert.Equal("reset", fake.Log[^1]);
        }
        Assert.False(service.HasResult);   // nothing of it reaches the caller
    }

    /// <summary>A model that takes <paramref name="loadMs"/> to load and says when it is unloaded.</summary>
    private sealed class SlowLoad(int length, int loadMs) : IDepthRunner
    {
        public readonly ManualResetEventSlim Disposed = new(false);

        public int OutputLength => length;

        public SlowLoad Load()
        {
            Thread.Sleep(loadMs);
            return this;
        }

        public void Run(float[] input, float[] output)
        {
        }

        public void Dispose() => Disposed.Set();
    }

    [Fact]
    public void AModelSwappedOutWhileItLoadsIsUnloadedOnceLoadedWithoutWaitingForIt()
    {
        // Clicking through models disposes each while it loads: that must neither hold the
        // caller (the UI thread) until the load ends, nor leave the model in GPU memory for good.
        var fake = new SlowLoad(Model.Width * Model.Height, loadMs: 800);
        var service = new DepthService(Model, (_, _) => fake.Load());
        Thread.Sleep(50);
        var clock = Stopwatch.StartNew();
        service.Dispose();
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(200), $"{clock.Elapsed.TotalMilliseconds:0} ms");
        Assert.False(fake.Disposed.IsSet);
        Assert.True(fake.Disposed.Wait(5000), "never unloaded");
        Assert.NotEqual(DepthServiceState.Ready, service.State);
    }

    [Fact]
    public void DisposeStopsBothThreadsPromptly()
    {
        DepthService service = Start(modelMs: 30, out _);
        var input = new float[3 * Model.Width * Model.Height];
        for (int i = 0; i < 5; i++)
        {
            service.Submit(input, i + 1, i * 0.03);
            Thread.Sleep(10);
        }
        var clock = Stopwatch.StartNew();
        service.Dispose();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"{clock.Elapsed.TotalMilliseconds:0} ms");
    }
}
