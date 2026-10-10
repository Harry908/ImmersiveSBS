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
        if (D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out ID3D11Device? d).Failure)
        {
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out d).CheckError();
        }
        device = d!;
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

    public void Dispose()
    {
        buffer.Dispose();
        source.Dispose();
        device.Dispose();
    }
}
