using System.Diagnostics;
using System.Drawing;
using ImmersiveSBS.Depth;
using ImmersiveSBS.Presentation;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ImmersiveSBS.Tests;

/// <summary>GPU time per pass (improvement-plan.md M5) on a real device.</summary>
public sealed class GpuTimerTests : IDisposable
{
    private readonly ID3D11Device device;

    public GpuTimerTests()
    {
        device = TestDevice.Create();
    }

    [Fact]
    public void EachMarkedPassGetsATimeWithoutWaitingOnTheGpu()
    {
        using var timer = new GpuTimer(device);
        using var synth = new StereoSynth(device) { Timer = timer };
        using ID3D11Texture2D source = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 256, Height = 128, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        var depth = new DepthFrame(64, 32);
        Array.Fill(depth.Nearness, 0.5f);
        synth.UploadNearness(depth);

        string summary = "";
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            timer.BeginFrame();
            synth.Synthesize(source, new Rectangle(0, 0, 256, 128), 20, 0.35f);
            timer.Mark("draw");
            timer.EndFrame();
            device.ImmediateContext.Flush();
            timer.Collect();
            summary = timer.TakeSummary();
            if (summary.Contains("splat")) break;
            Thread.Sleep(5);
        }
        Assert.Contains("upsample", summary);
        Assert.Contains("splat", summary);
        Assert.Contains("resolve", summary);
        Assert.Contains("total", summary);
    }

    public void Dispose() => device.Dispose();
}

/// <summary>The followed video's frame events and state, as the extension's protocol 2 reports them.</summary>
public class PageVideoTests
{
    [Fact]
    public void ACaptureIsMatchedWithTheFrameShownWhenItWasMade()
    {
        var page = new PageVideo("1:0:a#1");
        long f = Stopwatch.Frequency;
        for (int i = 0; i < 10; i++) page.AddFrame(new PageVideo.FrameEvent(100 + i, 5 + i / 25.0, 1000 * f + i * f / 25));

        Assert.True(page.TryFrameAt(1000 * f + 3 * f / 25 + f / 100, f / 120, out PageVideo.FrameEvent e));
        Assert.Equal(103, e.Presented);
        // Just before frame 4's display time, within the slack: frame 4.
        Assert.True(page.TryFrameAt(1000 * f + 4 * f / 25 - f / 200, f / 120, out e));
        Assert.Equal(104, e.Presented);
        Assert.False(page.TryFrameAt(999 * f, f / 120, out _));
        Assert.Equal(25, page.FrameRate, 3);
    }

    [Fact]
    public void ACaptureAheadOfTheReportsCountsTheFramesOnWhilePlaying()
    {
        // The capture of a frame often arrives before the page's report of it: in a run, 18 of
        // 30 frames a second went to the model, the others taken for the frame before.
        var page = new PageVideo("x");
        long f = Stopwatch.Frequency, period = f / 25;
        for (int i = 0; i < 10; i++) page.AddFrame(new PageVideo.FrameEvent(100 + i, 5 + i / 25.0, 1000 * f + i * period));
        long newest = 1000 * f + 9 * period;

        Assert.True(page.TryFrameAt(newest + period + period / 2, f / 120, out PageVideo.FrameEvent e));
        Assert.Equal(110, e.Presented);
        Assert.Equal(5 + 10 / 25.0, e.MediaTime, 6);
        Assert.True(page.TryFrameAt(newest + period / 2, f / 120, out e));
        Assert.Equal(109, e.Presented);
        // Far past the reports, the page has stalled: no more than a few frames on.
        Assert.True(page.TryFrameAt(newest + 20 * period, f / 120, out e));
        Assert.Equal(109 + PageVideo.MaxFramesAhead, e.Presented);
        // Paused, nothing is counted on.
        page.SetState(paused: true, seeking: false, seeks: 0);
        Assert.True(page.TryFrameAt(newest + 2 * period, f / 120, out e));
        Assert.Equal(109, e.Presented);
    }

    [Fact]
    public void EachFrameGetsTheBoxesOnTheScreenWhenItWasCaptured()
    {
        var page = new PageVideo("x");
        Assert.Null(page.BoxesAt(100));
        var controls = new[] { new Rectangle(0, 900, 1920, 180) };
        var caption = new[] { new Rectangle(600, 800, 700, 50) };
        page.AddBoxes(100, controls, []);
        page.AddBoxes(200, [], caption);

        Assert.Same(controls, page.BoxesAt(150)!.Overlays);
        Assert.Same(caption, page.BoxesAt(200)!.Captions);
        Assert.Same(caption, page.BoxesAt(900)!.Captions);
        Assert.Null(page.BoxesAt(50));   // before the first report
        // A report timed before the previous one (the page's clock re-synced) does not go back.
        page.AddBoxes(150, [], []);
        Assert.Same(controls, page.BoxesAt(199)!.Overlays);
        Assert.Empty(page.BoxesAt(200)!.Captions);
    }

    [Fact]
    public void RepeatedEventsAreIgnoredAndARestartedCounterStartsOver()
    {
        var page = new PageVideo("x");
        page.AddFrame(new PageVideo.FrameEvent(10, 1.0, 100));
        page.AddFrame(new PageVideo.FrameEvent(11, 1.04, 200));
        page.AddFrame(new PageVideo.FrameEvent(11, 1.04, 200));
        Assert.Equal(11, page.Latest!.Value.Presented);
        page.AddFrame(new PageVideo.FrameEvent(2, 0.0, 300));   // a new source: counting again
        Assert.Equal(2, page.Latest!.Value.Presented);
        Assert.Equal(0, page.FrameRate);
    }

    [Fact]
    public void PageDataGoesStaleAfterASecond()
    {
        var page = new PageVideo("x");
        long now = Stopwatch.GetTimestamp();
        Assert.False(page.Fresh(now));
        page.SetState(paused: true, seeking: false, seeks: 3);
        Assert.True(page.Fresh(Stopwatch.GetTimestamp()));
        Assert.False(page.Fresh(Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency));
        Assert.True(page.Paused);
        Assert.Equal(3, page.Seeks);
    }
}
