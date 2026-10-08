using System.Drawing;
using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Tests;

public class RegionTrackerTests
{
    private const int Cell = 8;
    private const int GW = 240, GH = 135; // 1920×1080 / 8

    private static RegionTracker Tracker() => new(GW, GH, Cell, new Size(1920, 1080));

    /// <summary>A static page with a "video" whose cells change every frame.</summary>
    private static byte[] Frame(Rectangle videoCells, int frame, Random random)
    {
        var g = new byte[GW * GH];
        for (int i = 0; i < g.Length; i++) g[i] = 200;
        for (int y = videoCells.Top; y < videoCells.Bottom; y++)
            for (int x = videoCells.Left; x < videoCells.Right; x++)
                g[y * GW + x] = (byte)random.Next(20, 230);
        return g;
    }

    [Fact]
    public void FindsAMovingVideoAfterItHoldsStill()
    {
        var t = Tracker();
        var video = new Rectangle(40, 20, 120, 68); // cells: 960×544 px at 320,160
        var random = new Random(1);

        for (int f = 0; f < 10; f++) t.Update(Frame(video, f, random), 1 / 30.0);
        Assert.Equal(RegionState.None, t.State); // not yet stable for 0.5 s

        for (int f = 10; f < 40; f++) t.Update(Frame(video, f, random), 1 / 30.0);
        Assert.Equal(RegionState.Tracking, t.State);

        Rectangle r = t.Region;
        Assert.InRange(r.Left, 320 - Cell, 320 + Cell);
        Assert.InRange(r.Top, 160 - Cell, 160 + Cell);
        Assert.InRange(r.Width, 960 - 2 * Cell, 960 + 2 * Cell);
        Assert.InRange(r.Height, 544 - 2 * Cell, 544 + 2 * Cell);
    }

    [Fact]
    public void IgnoresSmallChanges()
    {
        var t = Tracker();
        var caret = new Rectangle(10, 10, 4, 4);
        var random = new Random(2);
        for (int f = 0; f < 60; f++) t.Update(Frame(caret, f, random), 1 / 30.0);
        Assert.Equal(RegionState.None, t.State);
    }

    [Fact]
    public void RejectsTallShapes()
    {
        var t = Tracker();
        var tall = new Rectangle(100, 0, 40, 130);
        var random = new Random(3);
        for (int f = 0; f < 60; f++) t.Update(Frame(tall, f, random), 1 / 30.0);
        Assert.Equal(RegionState.None, t.State);
    }

    [Fact]
    public void PickingLocksASmallerVideoOverABiggerAd()
    {
        var t = Tracker();
        var ad = new Rectangle(120, 10, 110, 70);    // bigger: detection prefers it
        var video = new Rectangle(10, 80, 80, 45);
        var random = new Random(6);
        byte[] Both()
        {
            byte[] g = Frame(ad, 0, random);
            byte[] v = Frame(video, 0, random);
            for (int y = video.Top; y < video.Bottom; y++)
                for (int x = video.Left; x < video.Right; x++)
                    g[y * GW + x] = v[y * GW + x];
            return g;
        }

        for (int f = 0; f < 40; f++) t.Update(Both(), 1 / 30.0);
        Assert.InRange(t.Region.Left, (ad.Left - 1) * Cell, (ad.Left + 1) * Cell);

        Assert.True(t.Pick(new Point((video.Left + 10) * Cell, (video.Top + 10) * Cell)));
        Assert.True(t.Locked);
        for (int f = 0; f < 60; f++) t.Update(Both(), 1 / 30.0);

        Assert.InRange(t.Region.Left, (video.Left - 1) * Cell, (video.Left + 1) * Cell);
        Assert.InRange(t.Region.Width, (video.Width - 2) * Cell, (video.Width + 2) * Cell);

        t.Unlock();
        for (int f = 0; f < 40; f++) t.Update(Both(), 1 / 30.0);
        Assert.InRange(t.Region.Left, (ad.Left - 1) * Cell, (ad.Left + 1) * Cell);
    }

    [Fact]
    public void PickingWhereNothingChangesFails()
    {
        var t = Tracker();
        var random = new Random(7);
        for (int f = 0; f < 10; f++) t.Update(Frame(new Rectangle(10, 10, 60, 40), f, random), 1 / 30.0);
        Assert.False(t.Pick(new Point(200 * Cell, 120 * Cell)));
        Assert.False(t.Locked);
    }

    [Fact]
    public void AStoppedVideoBecomesPausedAndKeepsItsRegion()
    {
        var t = Tracker();
        var video = new Rectangle(0, 0, GW, GH); // full screen
        var random = new Random(4);
        for (int f = 0; f < 40; f++) t.Update(Frame(video, f, random), 1 / 30.0);
        Assert.Equal(RegionState.Tracking, t.State);
        Rectangle region = t.Region;

        byte[] frozen = Frame(video, 0, new Random(5));
        for (int f = 0; f < 30 * 6; f++) t.Update(frozen, 1 / 30.0);

        Assert.Equal(RegionState.Paused, t.State);
        Assert.Equal(region, t.Region);
    }
}
