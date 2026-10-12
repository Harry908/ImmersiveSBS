using System.Drawing;
using ImmersiveSBS.Depth;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ImmersiveSBS.Tests;

/// <summary>
/// The lookup warp (improvement-plan.md §8) on an offscreen device. As in
/// <see cref="StereoSynthTests"/>, the source is a horizontal ramp, red = 4·x, so a pixel's
/// colour says which source column it came from; blue marks a foreground object.
/// </summary>
public sealed class LookupWarpTests : IDisposable
{
    private const int W = 64, H = 16;
    private readonly ID3D11Device device;
    private readonly StereoSynth synth;

    public LookupWarpTests()
    {
        if (D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out ID3D11Device? d).Failure)
        {
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out d).CheckError();
        }
        device = d!;
        synth = new StereoSynth(device) { DilateRadius = 0, EdgeSnap = false, Lookup = true };
    }

    private ID3D11Texture2D Source(Func<int, bool> foreground)
    {
        var pixels = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                pixels[i + 0] = foreground(x) ? (byte)255 : (byte)0;
                pixels[i + 1] = (byte)(y * 8);
                pixels[i + 2] = (byte)(x * 4);
                pixels[i + 3] = 255;
            }
        unsafe
        {
            fixed (byte* p = pixels)
            {
                return device.CreateTexture2D(new Texture2DDescription
                {
                    Width = W, Height = H, MipLevels = 1, ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                }, [new Vortice.DataRectangle((IntPtr)p, W * 4)]);
            }
        }
    }

    private void Upload(Func<int, float> nearness)
    {
        var frame = new DepthFrame(W, H);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                frame.Nearness[y * W + x] = nearness(x);
        synth.UploadNearness(frame);
    }

    private byte[] Eyes(ID3D11Texture2D source, float strength, float convergence)
    {
        ID3D11ShaderResourceView view = synth.Synthesize(source, new Rectangle(0, 0, W, H), strength, convergence);
        using ID3D11Resource resource = view.Resource;
        using ID3D11Texture2D eyes = resource.QueryInterface<ID3D11Texture2D>();
        var d = eyes.Description;
        d.Usage = ResourceUsage.Staging;
        d.BindFlags = BindFlags.None;
        d.CPUAccessFlags = CpuAccessFlags.Read;
        using ID3D11Texture2D staging = device.CreateTexture2D(d);
        device.ImmediateContext.CopyResource(staging, eyes);
        var m = device.ImmediateContext.Map(staging, 0, MapMode.Read);
        var result = new byte[2 * W * H * 4];
        unsafe
        {
            for (int y = 0; y < H; y++)
                new ReadOnlySpan<byte>((byte*)m.DataPointer + y * m.RowPitch, 2 * W * 4).CopyTo(result.AsSpan(y * 2 * W * 4));
        }
        device.ImmediateContext.Unmap(staging, 0);
        return result;
    }

    private static int Red(byte[] eyes, int eye, int x, int y = H / 2) => eyes[(y * 2 * W + eye * W + x) * 4];

    private static int Blue(byte[] eyes, int eye, int x, int y = H / 2) => eyes[(y * 2 * W + eye * W + x) * 4 + 2];

    private static string Row(byte[] eyes, int eye) =>
        string.Join(" ", Enumerable.Range(0, W).Select(x => $"{x}:{Red(eyes, eye, x) / 4}{(Blue(eyes, eye, x) > 128 ? "b" : "")}"));

    [Fact]
    public void ConvergencePlaneGivesTheFlatPictureInBothEyes()
    {
        using ID3D11Texture2D source = Source(_ => false);
        Upload(_ => 0.35f);
        byte[] eyes = Eyes(source, 30, 0.35f);
        for (int x = 0; x < W; x++)
        {
            Assert.Equal(x * 4, Red(eyes, 0, x));
            Assert.Equal(x * 4, Red(eyes, 1, x));
        }
    }

    [Fact]
    public void UniformNearnessShiftsTheEyesOppositeWays()
    {
        // n = 1, c = 0, S = 8: s = 4, to within a tenth of a pixel (red steps 4 per column).
        using ID3D11Texture2D source = Source(_ => false);
        Upload(_ => 1f);
        byte[] eyes = Eyes(source, 8, 0f);
        for (int x = 8; x < W - 8; x++)
        {
            Assert.InRange(Red(eyes, 0, x), (x - 4) * 4 - 1, (x - 4) * 4 + 1);
            Assert.InRange(Red(eyes, 1, x), (x + 4) * 4 - 1, (x + 4) * 4 + 1);
        }
    }

    [Fact]
    public void ANearObjectHidesWhatItLandsOnAndTheGapShowsBackground()
    {
        // A near object (blue, x 24..39, n = 1) on a far background (n = 0), c = 0, S = 12: in the
        // left eye it moves right by 6. Where it lands (30..45) it hides the background; where it
        // was (24..29) the background it uncovered is unknown and stretched from its left, never
        // filled with the object's own pixels.
        using ID3D11Texture2D source = Source(x => x is >= 24 and < 40);
        Upload(x => x is >= 24 and < 40 ? 1f : 0f);
        byte[] eyes = Eyes(source, 12, 0f);
        for (int x = 31; x < 45; x++) Assert.True(Blue(eyes, 0, x) > 128, "object hidden: " + Row(eyes, 0));
        for (int x = 24; x < 30; x++)
        {
            Assert.True(Blue(eyes, 0, x) < 128, "object smeared into the gap: " + Row(eyes, 0));
            Assert.InRange(Red(eyes, 0, x) / 4, 20, 23);
        }
        Assert.Equal(12 * 4, Red(eyes, 0, 12));   // far background unmoved
        Assert.Equal(56 * 4, Red(eyes, 0, 56));
        // The right eye mirrors it: the object moves left, the gap at 34..39 shows what is right of it.
        for (int x = 34; x < 40; x++) Assert.True(Blue(eyes, 1, x) < 128 && Red(eyes, 1, x) / 4 is >= 40 and <= 43, Row(eyes, 1));
        for (int x = 19; x < 33; x++) Assert.True(Blue(eyes, 1, x) > 128, Row(eyes, 1));
    }

    [Fact]
    public void ItMatchesTheSplatWhereNeitherHasAGap()
    {
        // Gentle depth (no gaps for either warp): the two agree to a pixel.
        using ID3D11Texture2D source = Source(_ => false);
        Upload(x => 0.2f + 0.6f * x / W);
        byte[] lookup = Eyes(source, 12, 0.35f);
        synth.Lookup = false;
        synth.LimitSlope = false;
        byte[] splat = Eyes(source, 12, 0.35f);
        synth.Lookup = true;
        for (int eye = 0; eye < 2; eye++)
            for (int x = 8; x < W - 8; x++)
                Assert.InRange(Red(lookup, eye, x) - Red(splat, eye, x), -4, 4);
    }

    public void Dispose()
    {
        synth.Dispose();
        device.Dispose();
    }
}
