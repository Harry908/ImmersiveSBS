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
