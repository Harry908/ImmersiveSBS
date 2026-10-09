using System.Drawing;
using ImmersiveSBS.Depth;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ImmersiveSBS.Tests;

/// <summary>
/// The warp invariants of video-3d-plan.md §7.5 on an offscreen device (hardware, or WARP when
/// there is no GPU). The source is a horizontal ramp, red = 4·x, so a pixel's colour says which
/// source column it came from.
/// </summary>
public sealed class StereoSynthTests : IDisposable
{
    private const int W = 64, H = 16;
    private readonly ID3D11Device device;
    private readonly ID3D11Texture2D source;
    private readonly StereoSynth synth;

    public StereoSynthTests()
    {
        if (D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out ID3D11Device? d).Failure)
        {
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out d).CheckError();
        }
        device = d!;
        source = CreateSource((_, _) => 0);
        synth = new StereoSynth(device) { DilateRadius = 0 };
    }

    /// <summary>The ramp source (red = 4·x, green = 8·y) with <paramref name="blue"/> added.</summary>
    private ID3D11Texture2D CreateSource(Func<int, int, byte> blue)
    {
        var pixels = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                pixels[i + 0] = blue(x, y);     // B
                pixels[i + 1] = (byte)(y * 8);  // G
                pixels[i + 2] = (byte)(x * 4);  // R
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

    private void Upload(Func<int, int, float> nearness)
    {
        var frame = new DepthFrame(W, H);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                frame.Nearness[y * W + x] = nearness(x, y);
        synth.UploadNearness(frame);
    }

    /// <summary>Reads the eyes texture: [eye][y][x] = (r, g, b, a).</summary>
    private byte[] ReadEyes(ID3D11ShaderResourceView view)
    {
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

    private static int Red(byte[] eyes, int eye, int x, int y) => eyes[(y * 2 * W + eye * W + x) * 4];

    private static int Alpha(byte[] eyes, int eye, int x, int y) => eyes[(y * 2 * W + eye * W + x) * 4 + 3];

    /// <summary>The source column each pixel of an eye's middle row came from, for failure messages.</summary>
    private static string Row(byte[] eyes, int eye) =>
        string.Join(" ", Enumerable.Range(0, W).Select(x => $"{x}:{Red(eyes, eye, x, H / 2) / 4}"));

    private ID3D11ShaderResourceView Synthesize(float strength, float convergence) =>
        synth.Synthesize(source, new Rectangle(0, 0, W, H), strength, convergence);

    [Fact]
    public void ConvergencePlaneGivesTheFlatPictureInBothEyes()
    {
        // §7.5.1: n ≡ c → both eyes are pixel-identical to the source.
        Upload((_, _) => 0.35f);
        byte[] eyes = ReadEyes(Synthesize(30, 0.35f));
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                Assert.Equal(x * 4, Red(eyes, 0, x, y));
                Assert.Equal(x * 4, Red(eyes, 1, x, y));
            }
    }

    [Fact]
    public void UniformNearnessShiftsTheEyesOppositeWays()
    {
        // n = 1, c = 0, S = 8 → s = 4: the left eye's copy moves right, the right eye's left (§7.1).
        Upload((_, _) => 1f);
        byte[] eyes = ReadEyes(Synthesize(8, 0f));
        for (int x = 8; x < W - 8; x++)
        {
            Assert.Equal((x - 4) * 4, Red(eyes, 0, x, H / 2));
            Assert.Equal((x + 4) * 4, Red(eyes, 1, x, H / 2));
        }
    }

    [Fact]
    public void EveryOutputPixelIsWritten()
    {
        // §7.5.5, with a sharp depth step that opens holes.
        Upload((x, _) => x < W / 2 ? 1f : 0f);
        byte[] eyes = ReadEyes(Synthesize(12, 0.35f));
        for (int eye = 0; eye < 2; eye++)
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    Assert.Equal(255, Alpha(eyes, eye, x, y));
    }

    [Fact]
    public void NearerWinsWhereTheyOverlap()
    {
        // §7.5.6: a near strip at x 24..39 moves right by 6 in the left eye (s = 12/2·1) and covers
        // the far background it lands on; far pixels (n = 0, c = 0) do not move. The ramp has no
        // luma edge at the step, so the guided upsample softens the strip's first and last
        // pixels; only its interior is checked.
        Upload((x, _) => x is >= 24 and < 40 ? 1f : 0f);
        byte[] eyes = ReadEyes(Synthesize(12, 0f));
        for (int x = 32; x < 44; x++) Assert.InRange(Red(eyes, 0, x, H / 2), (x - 6) * 4 - 3, (x - 6) * 4 + 3);
        Assert.Equal(12 * 4, Red(eyes, 0, 12, H / 2));
        Assert.Equal(56 * 4, Red(eyes, 0, 56, H / 2));
    }

    [Fact]
    public void HolesAreFilledFromTheBackgroundNotTheForegroundEdge()
    {
        // A near object (blue, x 24..39) moves right by 6 in the left eye, opening a hole at
        // 24..29. The background there is unknown; it must be filled from the background (source
        // columns left of the object), never with the object's own edge pixels (columns ≥ 24),
        // which would draw a second outline beside it.
        using ID3D11Texture2D strip = CreateSource((x, _) => x is >= 24 and < 40 ? (byte)255 : (byte)0);
        Upload((x, _) => x is >= 24 and < 40 ? 1f : 0f);
        byte[] eyes = ReadEyes(synth.Synthesize(strip, new Rectangle(0, 0, W, H), 12, 0f));
        for (int x = 24; x < 30; x++) Assert.True(Red(eyes, 0, x, H / 2) / 4 is >= 10 and <= 23, Row(eyes, 0));
        // The right eye mirrors it: the object moves left, the hole at 34..39 comes from columns ≥ 40.
        for (int x = 34; x < 40; x++) Assert.True(Red(eyes, 1, x, H / 2) / 4 is >= 40 and <= 53, Row(eyes, 1));
    }

    [Fact]
    public void SnappingPutsASoftDepthEdgeOnThePictureEdge()
    {
        // As in use, the depth map is coarser than the picture (here 4×), and its edge is in the
        // wrong place: near up to x 36, while the picture's object (blue) ends at x 32. Snapped,
        // columns 32..35 look like the background and stay with it, so target 40 shows the
        // unshifted background; unsnapped they move with the object (by 6 in the left eye) and
        // a non-blue column from the edge lands there: a sliver of background stuck to the object.
        using ID3D11Texture2D edged = CreateSource((x, _) => x < 32 ? (byte)255 : (byte)0);
        var coarse = new DepthFrame(W / 4, H / 4);
        for (int y = 0; y < H / 4; y++)
            for (int x = 0; x < W / 4; x++)
                coarse.Nearness[y * (W / 4) + x] = x < 9 ? 1f : 0f;
        synth.UploadNearness(coarse);
        var region = new Rectangle(0, 0, W, H);

        synth.EdgeSnap = true;
        byte[] snappedEyes = ReadEyes(synth.Synthesize(edged, region, 12, 0f));
        synth.EdgeSnap = false;
        byte[] softEyes = ReadEyes(synth.Synthesize(edged, region, 12, 0f));
        synth.EdgeSnap = true;

        // Snapped: right of the object (from target 38, where it ends) every column is unshifted
        // background. Unsnapped: some background column there is dragged along by the object.
        for (int x = 38; x < 46; x++)
            Assert.True(Math.Abs(Red(snappedEyes, 0, x, H / 2) / 4 - x) <= 1, "snapped: " + Row(snappedEyes, 0));
        Assert.True(Enumerable.Range(38, 8).Any(x => x - Red(softEyes, 0, x, H / 2) / 4 >= 2), "soft: " + Row(softEyes, 0));
    }

    [Fact]
    public void SwappingEyesMirrorsTheShift()
    {
        // §7.5.7: with nearness symmetric around the centre, the right eye is the left eye mirrored.
        Upload((x, _) => 1f - Math.Abs(x - (W - 1) / 2f) / W);
        byte[] eyes = ReadEyes(Synthesize(10, 0.5f));
        for (int x = 12; x < W - 12; x++)
        {
            int left = Red(eyes, 0, x, H / 2) / 4;           // source column seen by the left eye
            int right = Red(eyes, 1, W - 1 - x, H / 2) / 4;  // mirrored position in the right eye
            Assert.InRange(left + right, W - 1 - 1, W - 1 + 1);
        }
    }

    [Fact]
    public void NativeSideBySideStretchesEachHalf()
    {
        byte[] eyes = ReadEyes(synth.SplitNative(source, new Rectangle(0, 0, W, H), Native3DLayout.SideBySide));
        for (int t = 2; t < W - 2; t++)
        {
            Assert.InRange(Red(eyes, 0, t, H / 2), (t / 2) * 4 - 4, (t / 2) * 4 + 6);            // left half
            Assert.InRange(Red(eyes, 1, t, H / 2), (W / 2 + t / 2) * 4 - 4, (W / 2 + t / 2) * 4 + 6); // right half
        }
    }

    [Fact]
    public void FloatingWindowBlacksTheOuterEdges()
    {
        Upload((_, _) => 0.35f);
        byte[] eyes = ReadEyes(synth.Synthesize(source, new Rectangle(0, 0, W, H), 20, 0.35f, bandLeft: 5, bandRight: 7));
        for (int x = 0; x < 5; x++) Assert.Equal(0, Red(eyes, 0, x, H / 2));
        Assert.Equal(5 * 4, Red(eyes, 0, 5, H / 2));
        for (int x = W - 7; x < W; x++) Assert.Equal(0, Red(eyes, 1, x, H / 2));
        Assert.Equal((W - 8) * 4, Red(eyes, 1, W - 8, H / 2));
        // The inner edges are untouched.
        Assert.Equal(0 * 4, Red(eyes, 1, 0, H / 2));
        Assert.Equal((W - 1) * 4, Red(eyes, 0, W - 1, H / 2));
    }

    public void Dispose()
    {
        synth.Dispose();
        source.Dispose();
        device.Dispose();
    }
}
