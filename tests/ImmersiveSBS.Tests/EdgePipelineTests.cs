using System.Drawing;
using ImmersiveSBS.Depth;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit.Abstractions;

namespace ImmersiveSBS.Tests;

/// <summary>
/// The edge pipeline judged by the plan's measures (improvement-plan.md §0, §7): M2, the share of
/// eye pixels the warp left as holes, and M3, how straight a straight edge stays. Real geometry:
/// a 1920×1080 video, depth at 518×294, the default strength σ 0.6 at c 0.35 (S = 33.8 px). The
/// picture's object is bluer than its background, so its outline is a colour edge where the
/// depth steps, as in real video: a strong one (the colour-guided upsample alone puts the depth
/// on it) and a faint one (a door frame against a wall), where the model's stair-steps show.
/// </summary>
public sealed class EdgePipelineTests : IDisposable
{
    private const int W = 1920, H = 1080, MW = 518, MH = 294;
    private const float Convergence = 0.35f;
    private static readonly float Strength = (float)StereoMath.Strength(StereoMath.ComfortBudget(59, 1920, 1080), Convergence, 0.6);

    private readonly ITestOutputHelper output;
    private readonly ID3D11Device device;
    private readonly StereoSynth synth;

    public EdgePipelineTests(ITestOutputHelper output)
    {
        this.output = output;
        if (D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out ID3D11Device? d).Failure)
        {
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out d).CheckError();
        }
        device = d!;
        synth = new StereoSynth(device);
    }

    /// <summary>The object's outline: x of the edge in row y, source px (object to the left).</summary>
    private delegate double Edge(double y);

    private static readonly Edge Diagonal = y => 700 + 0.5 * y;
    private static readonly Edge Gentle = y => 760 + 0.33 * y;
    private static readonly Edge Vertical = _ => 963.3;

    public static TheoryData<string, bool> Scenes => new()
    {
        { "diagonal", false }, { "diagonal", true }, { "gentle", false }, { "gentle", true },
    };

    private ID3D11Texture2D Picture(Edge edge, bool faint)
    {
        var pixels = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            double e = edge(y + 0.5);
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                // The share of the pixel inside the object: the outline is straight to a fraction
                // of a pixel, so whatever is jagged in the eyes comes from the depth.
                double inside = Math.Clamp(e - x, 0, 1);
                pixels[i + 0] = (byte)Math.Round(faint ? 120 + 30 * inside : 20 + 210 * inside);   // B
                pixels[i + 1] = (byte)(90 + (x * 7 + y * 3) % 24);                                // G: some texture
                pixels[i + 2] = (byte)Math.Round(faint ? 100 : 90 - 50 * inside);                 // R
                pixels[i + 3] = 255;
            }
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

    /// <summary>
    /// The model's depth: near inside the object, far outside, decided per model pixel by its
    /// centre, so a slanted outline becomes a staircase of whole model pixels (as Depth Anything
    /// V2 and 3 give it).
    /// </summary>
    private static float[] StaircaseDepth(Edge edge, float near, float far)
    {
        var n = new float[MW * MH];
        for (int my = 0; my < MH; my++)
            for (int mx = 0; mx < MW; mx++)
            {
                double y = (my + 0.5) * H / MH, x = (mx + 0.5) * W / MW;
                n[my * MW + mx] = x < edge(y) ? near : far;
            }
        return n;
    }

    private sealed record Chain(string Name, bool AntiAlias, int SoftPasses, bool Snap, int Dilate, bool Slope, bool Lookup = false);

    private static readonly Chain Before = new("before (snap, dilate 1)", false, 0, true, 1, false);
    private static readonly Chain PlanOnly = new("anti-alias + soft edges, no slope limit", true, -1, false, 0, false);
    private static readonly Chain SlopeOnly = new("slope limit only", false, 0, false, 0, true);
    private static readonly Chain SlopeAntiAlias = new("slope limit + anti-alias", true, 0, false, 0, true);
    private static readonly Chain SoftSlope = new("soft edges + slope limit", false, -1, false, 0, true);
    private static readonly Chain Defaults = new("anti-alias + soft edges + slope limit (defaults)", true, -1, false, 0, true);
    private static readonly Chain SnapSlope = new("snap + slope limit", false, 0, true, 0, true);
    private static readonly Chain SnapSoftSlope = new("anti-alias + soft edges + snap + slope limit", true, -1, true, 0, true);
    private static readonly Chain LookupSoft = new("lookup warp, anti-alias + soft edges", true, -1, false, 0, false, true);
    private static readonly Chain LookupSnap = new("lookup warp, snap", false, 0, true, 0, false, true);
    private static readonly Chain[] All = [Before, PlanOnly, SlopeOnly, SlopeAntiAlias, SoftSlope, Defaults, SnapSlope, SnapSoftSlope, LookupSoft, LookupSnap];

    /// <summary>Warps the scene through <paramref name="chain"/>; returns the left eye's pixels and the hole share.</summary>
    private (byte[] Eyes, double Holes) Run(Edge edge, Chain chain, bool faint = false, float near = 0.7f, float far = 0.3f)
    {
        using ID3D11Texture2D picture = Picture(edge, faint);
        float[] nearness = StaircaseDepth(edge, near, far);
        int passes = chain.SoftPasses < 0 ? SoftEdges.AutoPasses(Strength, W, MW) : chain.SoftPasses;
        if (chain.AntiAlias) new DepthAntiAlias(MW, MH).Apply(nearness);
        if (passes > 0) new SoftEdges(MW, MH).Apply(nearness, passes, 1);

        var frame = new DepthFrame(MW, MH);
        nearness.CopyTo(frame.Nearness, 0);
        synth.UploadNearness(frame);
        synth.EdgeSnap = chain.Snap;
        synth.DilateRadius = chain.Dilate;
        synth.LimitSlope = chain.Slope;
        synth.Lookup = chain.Lookup;
        synth.RangeSigma = 0.1f + 0.05f * passes;
        synth.QueueHoleCount();   // start counting from here
        synth.ReadQueuedHoles(out _);
        ID3D11ShaderResourceView view = synth.Synthesize(picture, new Rectangle(0, 0, W, H), Strength, Convergence);
        byte[] eyes = ReadEyes(view);
        synth.QueueHoleCount();
        Assert.True(synth.ReadQueuedHoles(out double holes));
        return (eyes, holes);
    }

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

    /// <summary>
    /// M3: in each row of the left eye, where the object's blue ends (to sub-pixel, by where blue
    /// crosses halfway); then the RMS distance of those points from their least-squares line, px.
    /// Rows near the top and bottom (the floating window, the picture's border) are left out.
    /// </summary>
    private static double Straightness(byte[] eyes, Edge edge, bool faint)
    {
        double mid = faint ? 135 : 125;   // halfway between the object's blue and the background's
        var points = new List<(double Y, double X)>();
        for (int y = 40; y < H - 40; y += 2)
        {
            int start = (int)edge(y + 0.5) - 60;
            int row = y * 2 * W;
            double crossing = double.NaN;
            for (int x = Math.Max(1, start); x < Math.Min(W - 1, start + 160); x++)
            {
                // Eyes are RGBA: blue is byte 2.
                int b0 = eyes[(row + x - 1) * 4 + 2], b1 = eyes[(row + x) * 4 + 2];
                if (b0 >= mid && b1 < mid)
                {
                    crossing = x - 1 + (b0 - mid) / Math.Max(1, b0 - b1);
                    break;
                }
            }
            if (!double.IsNaN(crossing)) points.Add((y, crossing));
        }
        Assert.True(points.Count > H / 4, $"the edge was found in only {points.Count} rows");

        double my = points.Average(p => p.Y), mx = points.Average(p => p.X);
        double sxy = points.Sum(p => (p.Y - my) * (p.X - mx)), syy = points.Sum(p => (p.Y - my) * (p.Y - my));
        double slope = syy > 0 ? sxy / syy : 0;
        return Math.Sqrt(points.Average(p => Math.Pow(p.X - (mx + slope * (p.Y - my)), 2)));
    }

    [Theory]
    [InlineData(0.7f, 0.3f)]
    [InlineData(0.95f, 0.15f)]
    public void TheDefaultsLeaveNoHolesWhereTheOldChainOpenedThem(float near, float far)
    {
        // §2.1: a step of Δn needs a ramp of Δn·S/2 px (6.8 px for 0.4, 13.5 px for 0.8); snapped
        // and dilated it stays a step, and the warp opens a hole beside every outline (each side
        // in one eye).
        output.WriteLine($"S = {Strength:0.0} px, soft-edge passes {SoftEdges.AutoPasses(Strength, W, MW)}, Δn = {near - far:0.0}");
        var holes = new Dictionary<Chain, double>();
        foreach (Chain chain in All)
        {
            holes[chain] = Run(Vertical, chain, near: near, far: far).Holes;
            output.WriteLine($"{chain.Name}: holes {holes[chain] * 100:0.000} %");
        }
        Assert.True(holes[Before] > 0.001, $"the old chain should open holes ({holes[Before] * 100:0.000} %)");
        Assert.Equal(0, holes[Defaults]);
        Assert.Equal(0, holes[SlopeOnly]);
    }

    [Theory]
    [MemberData(nameof(Scenes))]
    public void AStaircaseEdgeComesOutStraighter(string scene, bool faint)
    {
        Edge edge = scene == "gentle" ? Gentle : Diagonal;
        var m3 = new Dictionary<Chain, double>();
        foreach (Chain chain in All)
        {
            (byte[] eyes, double holes) = Run(edge, chain, faint);
            m3[chain] = Straightness(eyes, edge, faint);
            output.WriteLine($"{scene}, {(faint ? "faint" : "strong")} edge, {chain.Name}: M3 {m3[chain]:0.000} px, holes {holes * 100:0.000} %");
        }
        // G3: the RMS deviation halved, or already under a tenth of a pixel. (The lookup warp's
        // numbers are printed for the comparison; §8 judges it by M2 and the glasses.)
        Assert.True(m3[Defaults] <= Math.Max(m3[Before] / 2, 0.1), $"M3 {m3[Defaults]:0.000} px with the defaults against {m3[Before]:0.000} px before");
        Assert.True(m3[LookupSoft] < 0.3, $"M3 {m3[LookupSoft]:0.000} px with the lookup warp");
    }

    [Fact]
    public void TheEyesTakeUnderThreeMillisecondsOnTheGpu()
    {
        // M5 for a 1920×1080 video at σ 0.6 (§8's target: the eyes within 3 ms a frame), both warps.
        using ID3D11Texture2D picture = Picture(Diagonal, faint: false);
        var frame = new DepthFrame(MW, MH);
        StaircaseDepth(Diagonal, 0.7f, 0.3f).CopyTo(frame.Nearness, 0);
        synth.UploadNearness(frame);
        using var timer = new Presentation.GpuTimer(device);
        synth.Timer = timer;
        synth.EdgeSnap = false;
        synth.DilateRadius = 0;
        synth.RangeSigma = 0.2f;
        // The GPU kept busy (frames back to back, a few in flight; idle between frames a laptop's
        // GPU clocks down), and the ways taken in turns of 20 frames, so a change of clock falls
        // on all of them alike.
        var totals = new Dictionary<string, List<double>> { ["splat"] = [], ["lookup"] = [], ["lookup, no skipping"] = [] };
        for (int round = 0; round < 6; round++)
        {
            foreach (string way in totals.Keys)
            {
                synth.Lookup = way != "splat";
                synth.LookupSkip = way == "lookup";
                timer.TakeAverages();
                for (int i = 0; i < 20; i++)
                {
                    timer.BeginFrame();
                    synth.Synthesize(picture, new Rectangle(0, 0, W, H), Strength, Convergence);
                    timer.Mark("end");
                    timer.EndFrame();
                    device.ImmediateContext.Flush();
                    while (timer.PendingFrames > 2) timer.Collect();
                }
                while (timer.PendingFrames > 0) timer.Collect();
                Dictionary<string, double> ms = timer.TakeAverages();
                if (round > 0) totals[way].Add(ms.Values.Sum());
            }
        }
        synth.LookupSkip = false;
        foreach (var (way, list) in totals)
        {
            list.Sort();
            output.WriteLine($"{way}: median {list[list.Count / 2]:0.000} ms a frame (rounds of 20: {string.Join(", ", list.Select(v => v.ToString("0.00")))})");
        }
        if (!IsHardware) return;
        foreach (string way in new[] { "splat", "lookup, no skipping" })
            Assert.True(totals[way][totals[way].Count / 2] < 3, $"{way} over 3 ms");

    }

    private bool IsHardware
    {
        get
        {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgi.GetAdapter();
            return !adapter.Description.Description.Contains("Basic Render", StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AStraightVerticalEdgeStaysStraight()
    {
        (byte[] eyes, _) = Run(Vertical, Defaults);
        double m3 = Straightness(eyes, Vertical, faint: false);
        output.WriteLine($"vertical edge M3 {m3:0.000} px");
        Assert.True(m3 < 0.25, $"M3 {m3:0.000} px");
    }

    public void Dispose()
    {
        synth.Dispose();
        device.Dispose();
    }
}
