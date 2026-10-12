using System.Drawing;
using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Tests;

public class Native3DDetectorTests
{
    private const int Cell = 8, GW = 240, GH = 135;
    private static readonly Rectangle Region = new(0, 0, GW * Cell, GH * Cell);

    /// <summary>A random "scene" of w×h cells.</summary>
    private static byte[,] Scene(int w, int h, int seed)
    {
        var r = new Random(seed);
        var s = new byte[h, w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                s[y, x] = (byte)r.Next(256);
        return s;
    }

    private static byte[] SideBySide(int seed, int disparity)
    {
        var scene = Scene(GW / 2 + 8, GH, seed);
        var g = new byte[GW * GH];
        for (int y = 0; y < GH; y++)
            for (int x = 0; x < GW / 2; x++)
            {
                g[y * GW + x] = scene[y, x + 4];
                g[y * GW + GW / 2 + x] = scene[y, x + 4 + disparity];
            }
        return g;
    }

    private static byte[] TopBottom(int seed)
    {
        var scene = Scene(GW, GH / 2 + 1, seed);
        var g = new byte[GW * GH];
        for (int y = 0; y < GH / 2; y++)
            for (int x = 0; x < GW; x++)
            {
                g[y * GW + x] = scene[y, x];
                g[(y + GH / 2) * GW + x] = scene[y, x];
            }
        return g;
    }

    private static byte[] Flat2D(int seed)
    {
        var scene = Scene(GW, GH, seed);
        var g = new byte[GW * GH];
        for (int y = 0; y < GH; y++)
            for (int x = 0; x < GW; x++)
                g[y * GW + x] = scene[y, x];
        return g;
    }

    [Fact]
    public void RecognisesSideBySideAfterTwoSeconds()
    {
        var d = new Native3DDetector();
        for (int f = 0; f < 30; f++) d.Update(SideBySide(f, 2), GW, Cell, Region, 1 / 30.0);
        Assert.Equal(Native3DLayout.None, d.Layout);
        for (int f = 30; f < 70; f++) d.Update(SideBySide(f, 2), GW, Cell, Region, 1 / 30.0);
        Assert.Equal(Native3DLayout.SideBySide, d.Layout);
    }

    [Fact]
    public void RecognisesTopBottom()
    {
        var d = new Native3DDetector();
        for (int f = 0; f < 70; f++) d.Update(TopBottom(f), GW, Cell, Region, 1 / 30.0);
        Assert.Equal(Native3DLayout.TopBottom, d.Layout);
    }

    [Fact]
    public void OrdinaryVideoStaysTwoD()
    {
        var d = new Native3DDetector();
        for (int f = 0; f < 120; f++) d.Update(Flat2D(f), GW, Cell, Region, 1 / 30.0);
        Assert.Equal(Native3DLayout.None, d.Layout);
    }

    /// <summary>
    /// 2D video whose brightness changes mostly in one direction, with only faint detail: the
    /// halves share the profile but not the detail. Whole-block correlation called the first
    /// side-by-side (the 2026-10-12 run).
    /// </summary>
    private static byte[] Graded2D(int seed, bool fromTop)
    {
        var r = new Random(seed);
        var g = new byte[GW * GH];
        for (int y = 0; y < GH; y++)
            for (int x = 0; x < GW; x++)
            {
                double t = fromTop ? (double)y / GH : (double)x / GW;
                g[y * GW + x] = (byte)Math.Clamp(30 + 190 * t + r.Next(-12, 13), 0, 255);
            }
        return g;
    }

    [Fact]
    public void SkyOverGroundStaysTwoD()
    {
        var d = new Native3DDetector();
        for (int f = 0; f < 120; f++) d.Update(Graded2D(f, fromTop: true), GW, Cell, Region, 1 / 30.0);
        Assert.Equal(Native3DLayout.None, d.Layout);
        for (int f = 0; f < 120; f++) d.Update(Graded2D(500 + f, fromTop: false), GW, Cell, Region, 1 / 30.0);
        Assert.Equal(Native3DLayout.None, d.Layout);
    }

    /// <summary>A frame of the sample clip at one px per grid cell, as luma.</summary>
    private static byte[] Luma(byte[] bgra)
    {
        var g = new byte[GW * GH];
        for (int i = 0; i < g.Length; i++)
            g[i] = (byte)((bgra[4 * i] * 29 + bgra[4 * i + 1] * 150 + bgra[4 * i + 2] * 77) >> 8);
        return g;
    }

    /// <summary>
    /// The real clip (or the drawn scene without it): 2D as it is, and as half-width side-by-side
    /// 3D made from it (each half squeezed, the right view 1 px over).
    /// </summary>
    [Fact]
    public async Task RealVideoIsTwoDAndItsSideBySideIsThreeD()
    {
        List<byte[]> frames = await SampleClip.Frames(90, GW, GH, step: 2);
        var flat = new Native3DDetector();
        var stereo = new Native3DDetector();
        foreach (byte[] frame in frames)
        {
            byte[] g = Luma(frame);
            flat.Update(g, GW, Cell, Region, 1 / 15.0);
            var sbs = new byte[GW * GH];
            for (int y = 0; y < GH; y++)
                for (int x = 0; x < GW / 2; x++)
                {
                    sbs[y * GW + x] = g[y * GW + 2 * x];
                    sbs[y * GW + GW / 2 + x] = g[y * GW + Math.Min(GW - 1, 2 * x + 2)];
                }
            stereo.Update(sbs, GW, Cell, Region, 1 / 15.0);
            Assert.Equal(Native3DLayout.None, flat.Layout);
        }
        Assert.Equal(Native3DLayout.SideBySide, stereo.Layout);
    }

    [Fact]
    public void LeavesSideBySideWhenTheVideoChanges()
    {
        var d = new Native3DDetector();
        for (int f = 0; f < 70; f++) d.Update(SideBySide(f, 1), GW, Cell, Region, 1 / 30.0);
        Assert.Equal(Native3DLayout.SideBySide, d.Layout);
        for (int f = 0; f < 70; f++) d.Update(Flat2D(1000 + f), GW, Cell, Region, 1 / 30.0);
        Assert.Equal(Native3DLayout.None, d.Layout);
    }
}
