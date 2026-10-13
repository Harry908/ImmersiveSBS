using System.Drawing;
using ImmersiveSBS.Depth;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ImmersiveSBS.Tests;

/// <summary>The delayed picture's frame store, on an offscreen device (hardware, or WARP).</summary>
public sealed class VideoDelayBufferTests : IDisposable
{
    private const long Tps = 1000; // times in ms
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);
    private readonly ID3D11Device device;
    private readonly ID3D11Texture2D source;
    private readonly VideoDelayBuffer buffer;

    public VideoDelayBufferTests()
    {
        device = TestDevice.Create();
        source = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 64, Height = 32, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        buffer = new VideoDelayBuffer(device);
    }

    private static DepthFrame Depth(long id, float value)
    {
        var d = new DepthFrame(4, 4) { FrameId = id };
        Array.Fill(d.Nearness, value);
        return d;
    }

    [Fact]
    public void ARegionThatChangesKeepsTheHeldFrames()
    {
        // The player's controls appear and the found box shrinks: the picture must run on from
        // the frames already held, not stand still for the whole delay.
        var before = new Rectangle(0, 0, 40, 24);
        var after = new Rectangle(0, 0, 40, 20);
        for (int i = 0; i < 10; i++) Assert.True(buffer.Push(source, before, i * 100, i, Delay, Tps));
        for (int i = 10; i < 15; i++) Assert.True(buffer.Push(source, after, i * 100, i, Delay, Tps));

        Assert.Equal(15, buffer.Count);
        int shown = buffer.Select(1250 - 1000);   // a quarter second in
        Assert.Equal(2, buffer[shown].Id);
        Assert.Equal(before, buffer[shown].Region);
        Assert.Equal(new Size(40, 24), new Size((int)buffer[shown].TextureOrThrow.Description.Width, (int)buffer[shown].TextureOrThrow.Description.Height));
        Assert.Equal(after, buffer[buffer.Select(1400)].Region);
    }

    [Fact]
    public void DepthIsSmoothedOnlyOverFramesCutTheSameWay()
    {
        var before = new Rectangle(0, 0, 40, 24);
        var after = new Rectangle(8, 0, 40, 24);
        for (int i = 0; i < 4; i++) buffer.Push(source, before, i * 100, i, Delay, Tps);
        for (int i = 4; i < 8; i++) buffer.Push(source, after, i * 100, i, Delay, Tps);
        for (int i = 0; i < 8; i++) buffer.Attach(Depth(i, i < 4 ? 0.2f : 0.8f));

        var output = new float[16];
        Assert.True(buffer.SmoothedNearness(3, output));   // the last frame before the change
        Assert.All(output, v => Assert.Equal(0.2f, v, 5));
        Assert.True(buffer.SmoothedNearness(4, output));   // the first after it
        Assert.All(output, v => Assert.Equal(0.8f, v, 5));
    }

    [Fact]
    public void OldFramesGoOnceTheDelayHasPassed()
    {
        var region = new Rectangle(0, 0, 32, 16);
        for (int i = 0; i < 40; i++) buffer.Push(source, region, i * 100, i, Delay, Tps);
        // Kept: the delay plus half a second, plus the one frame just older.
        Assert.InRange(buffer.Count, 15, 17);
        Assert.Equal(29, buffer[buffer.Select(3900 - 1000)].Id);
    }

    [Fact]
    public void TheMemoryCapCountsTheVideosFrameRate()
    {
        // Room for 70 frames: a 2 s delay of a 30 fps video fits, so none may be skipped, even
        // with captures arriving a refresh early or late. Counted at 60 fps, every early one was.
        var region = new Rectangle(0, 0, 32, 16);
        buffer.MaxBytes = 70L * 32 * 16 * 4;
        var twoSeconds = TimeSpan.FromSeconds(2);
        long t = 0;
        int[] steps = [33, 33, 17, 50];
        for (int i = 0; i < 120; i++)
        {
            t += steps[i % steps.Length];
            Assert.True(buffer.Push(source, region, t, i, twoSeconds, Tps, frameRate: 30), $"frame {i} skipped");
        }
        Assert.Equal(0, buffer.TakeSkipped());

        // A 60 fps video does not fit: every other frame goes, the delay stays.
        buffer.Clear();
        int kept = 0;
        for (int i = 0; i < 240; i++) kept += buffer.Push(source, region, i * 1000L / 60, i, twoSeconds, Tps, frameRate: 60) ? 1 : 0;
        Assert.InRange(kept, 110, 130);
        Assert.Equal(240 - kept, buffer.TakeSkipped());
        Assert.InRange(buffer[buffer.Count - 1].Time - buffer[0].Time, 2000, 2600);
    }

    [Fact]
    public void RecycledFramesLendTheirDepthMapsToNewOnes()
    {
        // At model size a nearness map is a large object: one made per frame meant full collections.
        var region = new Rectangle(0, 0, 32, 16);
        var maps = new HashSet<float[]>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < 60; i++)
        {
            buffer.Push(source, region, i * 100, i, Delay, Tps);
            Assert.True(buffer.Attach(Depth(i, 0.5f)));
            maps.Add(buffer[buffer.Count - 1].Nearness!);
        }
        // About the frames held at once (the delay plus half a second), not one per frame.
        Assert.InRange(maps.Count, 15, 25);
        Assert.False(buffer.Attach(Depth(3, 0.5f)));   // long gone
    }

    [Fact]
    public void ItSaysWhichFramesDepthTheShownOneUsesAndWhenTheNextIsDue()
    {
        var region = new Rectangle(0, 0, 32, 16);
        for (int i = 0; i < 8; i++) buffer.Push(source, region, i * 100, i, Delay, Tps);
        buffer.Attach(Depth(1, 0.2f));
        buffer.Attach(Depth(6, 0.8f));
        var n = new float[16];

        Assert.Equal(VideoDelayBuffer.DepthMatch.Interpolated, buffer.InterpolatedNearness(3, n, out _));
        Assert.Equal((1L, 6L), buffer.LastSources);   // depth for 2–5 would change it, for 7 not
        Assert.Equal(VideoDelayBuffer.DepthMatch.Held, buffer.InterpolatedNearness(7, n, out _));
        Assert.Equal((6L, long.MaxValue), buffer.LastSources);
        Assert.True(buffer.SmoothedNearness(4, n));
        Assert.Equal((2L, 6L), buffer.LastSources);

        Assert.Equal(400, buffer.NextTime(3));
        Assert.Equal(long.MaxValue, buffer.NextTime(7));
    }

    public void Dispose()
    {
        buffer.Dispose();
        source.Dispose();
        device.Dispose();
    }
}
