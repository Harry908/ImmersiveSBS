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
        if (D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out ID3D11Device? d).Failure)
        {
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out d).CheckError();
        }
        device = d!;
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
