using ImmersiveSBS.Audio;

namespace ImmersiveSBS.Tests;

public class AudioDelayLineTests
{
    private const int Rate = 48000, Channels = 2;
    private static readonly long Ms = TimeSpan.TicksPerMillisecond;

    /// <summary>A packet of <paramref name="frames"/> frames whose value is its frame index.</summary>
    private static float[] Packet(long firstFrame, int frames)
    {
        var p = new float[frames * Channels];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < Channels; c++)
                p[f * Channels + c] = firstFrame + f;
        return p;
    }

    private static long TimeOf(long frame) => frame * TimeSpan.TicksPerSecond / Rate;

    [Fact]
    public void PlaysWhatWasCapturedExactlyTheDelayEarlier()
    {
        var line = new AudioDelayLine(Rate, Channels, TimeSpan.FromSeconds(1));
        for (long f = 0; f < Rate * 2; f += 480) line.Write(Packet(f, 480), TimeOf(f));

        var out1 = new float[480 * Channels];
        line.Read(out1, TimeOf(Rate + 4800)); // heard at 1.1 s → captured at 0.1 s
        Assert.Equal(4800, out1[0]);
        Assert.Equal(4800 + 479, out1[^1]);

        // Consecutive reads continue seamlessly.
        var out2 = new float[480 * Channels];
        line.Read(out2, TimeOf(Rate + 4800 + 480));
        Assert.Equal(4800 + 480, out2[0]);
        Assert.Equal(0, line.Resyncs);
    }

    [Fact]
    public void BeforeTheDelayHasPassedItIsSilent()
    {
        var line = new AudioDelayLine(Rate, Channels, TimeSpan.FromSeconds(1));
        line.Write(Packet(1, 480), TimeOf(0));
        var output = new float[480 * Channels];
        line.Read(output, TimeOf(Rate / 2)); // only 0.5 s later
        Assert.All(output, v => Assert.Equal(0, v));
    }

    [Fact]
    public void AGapInCaptureIsSilenceAndTimingHolds()
    {
        var line = new AudioDelayLine(Rate, Channels, TimeSpan.FromMilliseconds(500));
        line.Write(Packet(0, 4800), TimeOf(0));            // 0–100 ms
        line.Write(Packet(48000, 4800), TimeOf(48000));    // 1000–1100 ms (silence between)

        var output = new float[480 * Channels];
        line.Read(output, TimeOf(48000) + 500 * Ms - TimeOf(24000)); // captured at 500 ms: in the gap
        Assert.All(output, v => Assert.Equal(0, v));

        line.Read(output, TimeOf(48000) + 500 * Ms); // captured at 1000 ms: the second packet
        Assert.Equal(48000, output[0]);
    }

    [Fact]
    public void SmallJitterIsAbsorbed()
    {
        var line = new AudioDelayLine(Rate, Channels, TimeSpan.FromMilliseconds(200));
        var random = new Random(1);
        for (long f = 0; f < Rate; f += 480)
        {
            long jitter = random.Next(-2, 3) * Ms; // ±2 ms timestamp noise
            line.Write(Packet(f, 480), TimeOf(f) + jitter);
        }

        // The first packet's jitter sets the origin, a constant offset of a few ms at most;
        // after that playback must run on without gaps or jumps.
        var output = new float[480 * Channels];
        long start = 9600;
        line.Read(output, TimeOf(start) + 200 * Ms);
        float first = output[0];
        Assert.InRange(first, start - 3 * 48, start + 3 * 48);
        for (int i = 1; i < 20; i++)
        {
            line.Read(output, TimeOf(start + i * 480) + 200 * Ms);
            Assert.Equal(first + i * 480, output[0]);
        }
        Assert.Equal(0, line.Resyncs);
    }

    [Fact]
    public void RenderClockJitterIsFollowedSmoothlyNotByJumps()
    {
        var line = new AudioDelayLine(Rate, Channels, TimeSpan.FromMilliseconds(500));

        // Capture runs 0.6 s ahead; render callbacks every 10 ms read clocks jittering by ±8 ms.
        var random = new Random(2);
        var output = new float[480 * Channels];
        float last = float.NaN;
        int blocks = 0;
        for (int i = 0; i < 600; i++)
        {
            line.Write(Packet(i * 480L, 480), TimeOf(i * 480L));
            int j = i - 60;
            if (j < 0) continue;
            long jitter = random.Next(-8, 9) * Ms;
            line.Read(output, TimeOf(j * 480L) + 500 * Ms + jitter);
            blocks++;
            for (int k = 0; k < 480; k++)
            {
                float v = output[k * Channels];
                // Contiguous, but for a single frame skipped or repeated now and then.
                if (!float.IsNaN(last)) Assert.InRange(v - last, 0, 2);
                last = v;
            }
        }
        Assert.Equal(0, line.Resyncs);
        // And on time on average: within a few ms of where the clock says.
        Assert.InRange(last - (blocks * 480 - 1), -4 * 48, 4 * 48);
    }

    [Fact]
    public void ASteadyOffsetIsCaughtUpGradually()
    {
        var line = new AudioDelayLine(Rate, Channels, TimeSpan.FromMilliseconds(500));
        var output = new float[480 * Channels];
        float last = float.NaN;
        int j = 0;
        for (int i = 0; i < 2000; i++)
        {
            line.Write(Packet(i * 480L, 480), TimeOf(i * 480L));
            j = i - 60;
            if (j < 0) continue;
            // After the first read the clock reads 20 ms later than the samples played: under the
            // resync threshold, so reading catches up a frame at a time.
            line.Read(output, TimeOf(j * 480L) + (j == 0 ? 500 : 520) * Ms);
            if (!float.IsNaN(last)) Assert.InRange(output[0] - last, 1, 2);
            last = output[^Channels];
        }
        Assert.Equal(0, line.Resyncs);
        Assert.InRange(output[0] - (j * 480 + 20 * 48), -2 * 48, 2 * 48);
    }

    [Fact]
    public void ARestartTakesANewTimingAtOnce()
    {
        // The lip-sync trim moved by 20 ms: under the resync threshold, but wanted now.
        var line = new AudioDelayLine(Rate, Channels, TimeSpan.FromMilliseconds(100));
        for (long f = 0; f < Rate; f += 480) line.Write(Packet(f, 480), TimeOf(f));

        var output = new float[480 * Channels];
        line.Read(output, TimeOf(9600) + 100 * Ms);
        line.Restart();
        line.Read(output, TimeOf(10080) + 80 * Ms);   // AudioDelay subtracts the trim from the play time
        Assert.Equal(10080 - 20 * 48, output[0]);
        Assert.Equal(0, line.Resyncs);
    }

    [Fact]
    public void ReadingFarOffTimeResynchronises()
    {
        var line = new AudioDelayLine(Rate, Channels, TimeSpan.FromMilliseconds(100));
        for (long f = 0; f < Rate; f += 480) line.Write(Packet(f, 480), TimeOf(f));

        var output = new float[480 * Channels];
        line.Read(output, TimeOf(9600) + 100 * Ms);
        line.Read(output, TimeOf(24000) + 100 * Ms); // the render clock jumped
        Assert.Equal(24000, output[0]);
        Assert.Equal(1, line.Resyncs);
    }
}
