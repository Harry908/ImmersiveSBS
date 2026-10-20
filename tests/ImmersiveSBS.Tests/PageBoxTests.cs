using System.Drawing;
using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Tests;

/// <summary>What the page draws over the video, in the depth (improvement-plan.md §7.4).</summary>
public class PageBoxTests
{
    private const int W = 64, H = 36;

    [Fact]
    public void ABoxLandsOnTheModelPixelsItCoversThroughTheCrop()
    {
        // A 1280×720 video at (320, 180) of the screen, a 64×36 model: 20 source px per model px.
        var region = new Rectangle(320, 180, 1280, 720);
        PageBoxes boxes = PageBoxes.ToModel(
            [new Rectangle(320 + 200, 180 + 600, 400, 60), new Rectangle(0, 0, 100, 100)],
            [new Rectangle(320 + 210, 180 + 590, 25, 25)],
            region, new Size(W, H));
        Assert.Equal([new Rectangle(10, 30, 20, 3)], boxes.Overlays);        // off the region: left out
        Assert.Equal([Rectangle.FromLTRB(10, 29, 12, 31)], boxes.Captions);  // partly covered pixels count
    }

    [Fact]
    public void FlattenPutsTheControlsAndTheirMarginAtThePlane()
    {
        var n = Enumerable.Repeat(0.9f, W * H).ToArray();
        PageBoxes.Flatten(n, W, H, [new Rectangle(10, 20, 5, 4), new Rectangle(62, 34, 10, 10)], 2, 0.35f);
        Assert.Equal(0.35f, n[18 * W + 8]);              // the margin's corner
        Assert.Equal(0.35f, n[25 * W + 16]);             // the margin's far corner
        Assert.Equal(0.9f, n[17 * W + 8]);               // just outside it
        Assert.Equal(0.9f, n[26 * W + 16]);
        Assert.Equal(0.35f, n[35 * W + 63]);             // a box past the edge is cut to the frame
        Assert.Equal(0.9f, n[0]);
    }

    [Fact]
    public void OverlayBoxesGoToTheScreenPlaneAndOnlyThey()
    {
        var p = new DepthPostProcessor(W, H) { SceneCutThreshold = 1, StaticNearness = 0.35 };
        var plain = new DepthPostProcessor(W, H) { SceneCutThreshold = 1, StaticNearness = 0.35 };
        var raw = Enumerable.Range(0, W * H).Select(i => 0.2f + 0.8f * (i / W) / H).ToArray();   // nearer down the picture
        var luma = Enumerable.Repeat(0.5f, W * H).ToArray();
        var n = new float[W * H];
        var own = new float[W * H];
        p.PageBoxes = new PageBoxes([new Rectangle(10, 30, 20, 3)], []);
        for (int f = 0; f < 30; f++)
        {
            p.Process(raw, luma, 1 / 30.0, n);
            plain.Process(raw, luma, 1 / 30.0, own);
        }
        Assert.Equal(0.35f, n[31 * W + 15], 2);           // in the box: flat at c
        Assert.Equal(own[20 * W + 15], n[20 * W + 15]);   // outside: its own depth
        Assert.True(own[31 * W + 15] > 0.8f);

        // The controls fade: the box goes, and the depth comes back over about a second.
        p.PageBoxes = new PageBoxes([], []);
        p.Process(raw, luma, 1 / 30.0, n);
        plain.Process(raw, luma, 1 / 30.0, own);
        Assert.InRange(n[31 * W + 15], 0.36f, 0.6f);
        for (int f = 0; f < 60; f++)
        {
            p.Process(raw, luma, 1 / 30.0, n);
            plain.Process(raw, luma, 1 / 30.0, own);
        }
        Assert.Equal(own[31 * W + 15], n[31 * W + 15], 0.01);   // e^(−2 s / 0.4 s) of the box left
    }

    [Fact]
    public void ThePagesCaptionsReplaceTheDetection()
    {
        // Busy foliage-like texture in the lower picture that the detection could mistake for
        // text: with the page's caption box given, only that box is flattened.
        var random = new Random(5);
        var luma = new float[W * H];
        for (int i = 0; i < luma.Length; i++) luma[i] = random.NextDouble() < 0.5 ? 0.9f : 0.1f;
        var lockedOn = new CaptionLock(W, H);
        var n = Enumerable.Range(0, W * H).Select(i => (float)(i % W) / W).ToArray();
        var box = new Rectangle(20, 28, 24, 4);
        for (int f = 0; f < 20; f++) lockedOn.Apply(luma, n, 1 / 30.0, 0.35, [box]);
        Assert.Equal(box, lockedOn.Found);
        // Flat inside: one depth across the box.
        float inside = n[29 * W + 22];
        Assert.Equal(inside, n[29 * W + 40], 3);
        // Outside the box nothing moved.
        Assert.Equal(5f / W, n[10 * W + 5], 5);
    }
}
