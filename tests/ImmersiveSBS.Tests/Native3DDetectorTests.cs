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
