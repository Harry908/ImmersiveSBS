using System.Drawing;
using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Tests;

public class CaptionLockTests
{
    private const int W = 518, H = 294;
    private const double Dt = 1.0 / 30, ScreenPlane = 0.35;

    /// <summary>Mid-grey picture; a line of "letters" (2 px white strokes with a black 1 px outline every 6 px) over the given rows.</summary>
    private static float[] Picture(int rowFrom = -1, int rowTo = -1, int strokeWidth = 2, int pitch = 6)
    {
        var luma = new float[W * H];
        Array.Fill(luma, 0.45f);
        for (int y = Math.Max(0, rowFrom); y < rowTo; y++)
            for (int x = 150; x < 370; x += pitch)
            {
                luma[y * W + x - 1] = 0.05f;
                for (int k = 0; k < strokeWidth; k++) luma[y * W + x + k] = 0.95f;
                luma[y * W + x + strokeWidth] = 0.05f;
            }
        return luma;
    }

    /// <summary>A scene: nearer toward the right.</summary>
    private static float[] Scene()
    {
        var n = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                n[y * W + x] = 0.2f + 0.6f * x / W;
        return n;
    }

    [Fact]
    public void ASubtitleIsFoundAndFlattenedInFrontOfTheScene()
    {
        var caption = new CaptionLock(W, H);
        float[] luma = Picture(240, 254);
        float[] n = Scene();
        for (int frame = 0; frame < 30; frame++)   // it must hold still 0.3 s, then settles
        {
            n = Scene();
            caption.Apply(luma, n, Dt, ScreenPlane);
        }

        Rectangle box = Assert.IsType<Rectangle>(caption.Found);
        Assert.True(box.Contains(new Rectangle(150, 240, 220, 14)), $"box {box}");
        Assert.True(box.Height < 30, $"box {box}");

        // Inside: one depth, in front of the nearest part of the scene behind it (0.2 + 0.6·370/518 ≈ 0.63).
        var inside = Enumerable.Range(242, 10).SelectMany(y => Enumerable.Range(155, 210).Select(x => n[y * W + x])).ToArray();
        Assert.True(inside.Max() - inside.Min() < 0.02, $"{inside.Min()}..{inside.Max()}");
        Assert.InRange(inside.Average(), 0.6, 0.7);

        // Outside: untouched.
        float[] scene = Scene();
        Assert.Equal(scene[100 * W + 200], n[100 * W + 200]);
        Assert.Equal(scene[245 * W + 50], n[245 * W + 50]);
    }

    [Fact]
    public void APictureWithoutTextIsLeftAlone()
    {
        var caption = new CaptionLock(W, H);
        float[] n = Scene();
        caption.Apply(Picture(), n, Dt, ScreenPlane);
        Assert.Null(caption.Found);
        Assert.Equal(Scene(), n);
    }

    [Fact]
    public void TextHigherUpIsNotTakenForSubtitles()
    {
        var caption = new CaptionLock(W, H);
        float[] n = Scene();
        caption.Apply(Picture(40, 54), n, Dt, ScreenPlane);
        Assert.Null(caption.Found);
        Assert.Equal(Scene(), n);
    }

    [Fact]
    public void WideBrightStripesAreNotLetters()
    {
        // A white fence with dark gaps: bright runs far wider than a letter stroke.
        var caption = new CaptionLock(W, H);
        float[] n = Scene();
        caption.Apply(Picture(240, 254, strokeWidth: 16, pitch: 24), n, Dt, ScreenPlane);
        Assert.Null(caption.Found);
    }

    [Fact]
    public void ABusyLowerPictureIsNotFlattened()
    {
        // Foliage or a pattern: outlined bright strokes over the whole lower picture. Taken as one
        // block it would flatten half the picture (the seam the first version made).
        var caption = new CaptionLock(W, H);
        float[] luma = Picture(170, H);
        float[] n = Scene();
        for (int frame = 0; frame < 30; frame++)
        {
            n = Scene();
            caption.Apply(luma, n, Dt, ScreenPlane);
        }
        Assert.Null(caption.Found);
        Assert.Equal(Scene(), n);
    }

    [Fact]
    public void SceneTextureAboveASubtitleIsNotJoinedToIt()
    {
        var caption = new CaptionLock(W, H);
        float[] luma = Picture(240, 254);
        float[] texture = Picture(175, 190);
        Array.Copy(texture, 175 * W, luma, 175 * W, 15 * W);
        for (int frame = 0; frame < 30; frame++) caption.Apply(luma, Scene(), Dt, ScreenPlane);

        Rectangle box = Assert.IsType<Rectangle>(caption.Found);
        Assert.True(box.Top > 230, $"box {box}");
    }

    [Fact]
    public void AMovingBandIsNotASubtitle()
    {
        // Text-like texture drifting down the picture never holds still long enough.
        var caption = new CaptionLock(W, H);
        for (int frame = 0; frame < 30; frame++)
            caption.Apply(Picture(200 + 2 * frame, 212 + 2 * frame), Scene(), Dt, ScreenPlane);
        Assert.Null(caption.Found);
    }

    [Fact]
    public void AGoneSubtitleFadesOutRatherThanBlinking()
    {
        var caption = new CaptionLock(W, H);
        float[] luma = Picture(240, 254);
        for (int frame = 0; frame < 30; frame++) caption.Apply(luma, Scene(), Dt, ScreenPlane);

        int i = 246 * W + 200;
        float[] n = Scene();
        caption.Apply(Picture(), n, Dt, ScreenPlane);   // one frame without it: still mostly held
        Assert.True(Math.Abs(n[i] - Scene()[i]) > 0.1, $"{n[i]}");

        for (int frame = 0; frame < 90; frame++)       // three seconds later: gone
        {
            n = Scene();
            caption.Apply(Picture(), n, Dt, ScreenPlane);
        }
        Assert.True(Math.Abs(n[i] - Scene()[i]) < 0.01, $"{n[i]}");
    }
}
