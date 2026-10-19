using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Tests;

/// <summary>The theater's own frames for the depth model, and the depth of the frame it shows.</summary>
public class PagePicturesTests
{
    private const int W = 8, H = 4;

    private static DepthFrame Flat(float value)
    {
        var frame = new DepthFrame(W, H);
        Array.Fill(frame.Nearness, value);
        return frame;
    }

    [Fact]
    public void TheShownFrameGetsItsOwnDepth()
    {
        using var pictures = new PagePictures();
        pictures.File(0, 1.000, Flat(0.1f));
        pictures.File(0, 1.033, Flat(0.2f));
        pictures.File(0, 1.067, Flat(0.3f));
        var into = new float[W * H];
        Assert.True(pictures.Nearness(0, 1.0333, 0.15, into, out double lag));
        Assert.Equal(0.2f, into[5]);
        Assert.Equal(0, lag);
    }

    [Fact]
    public void AFrameTheModelSkippedIsInterpolatedFromItsNeighbours()
    {
        using var pictures = new PagePictures();
        pictures.File(0, 1.000, Flat(0.2f));
        pictures.File(0, 1.100, Flat(0.6f));
        var into = new float[W * H];
        Assert.True(pictures.Nearness(0, 1.025, 0.15, into, out double lag));
        Assert.Equal(0.3f, into[0], 5);
        Assert.Equal(25, lag, 6);
        // Too far apart to interpolate: the newest before it, within half a second (live timing).
        pictures.File(0, 1.400, Flat(0.9f));
        Assert.True(pictures.Nearness(0, 1.300, 0.15, into, out lag));
        Assert.Equal(0.6f, into[0]);
        Assert.Equal(200, lag, 6);
    }

    [Fact]
    public void TimesAreComparedOnlyWithinALineAndEarlierLinesAreLetGo()
    {
        using var pictures = new PagePictures();
        pictures.File(0, 5.000, Flat(0.5f));
        // A seek back: the new line's frames arrive before the old line stops showing.
        pictures.File(1, 1.000, Flat(0.1f));
        var into = new float[W * H];
        Assert.True(pictures.Nearness(0, 5.000, 0.15, into, out _));
        Assert.Equal(0.5f, into[0]);
        Assert.Equal(2, pictures.Count);
        Assert.False(pictures.Nearness(1, 0.400, 0.15, into, out _));   // nothing before it on its line
        Assert.Equal(1, pictures.Count);                                 // line 0 is past
        Assert.True(pictures.Nearness(1, 1.000, 0.15, into, out _));
        Assert.Equal(0.1f, into[0]);
    }

    [Fact]
    public void AJpegBecomesTheModelInputAsTheScreenWouldGiveIt()
    {
        using var bitmap = new Bitmap(W, H, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(bitmap)) g.Clear(Color.FromArgb(255, 128, 0));
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Jpeg);
        float[] input = [];
        Assert.True(PagePictures.Decode(stream.ToArray(), W, H, ref input));
        Assert.Equal(3 * W * H, input.Length);
        // ImageNet normalisation, planar: (x − μ) / σ; JPEG leaves a few levels of error.
        Assert.Equal((1 - 0.485) / 0.229, input[0], 0.1);
        Assert.Equal((128 / 255.0 - 0.456) / 0.224, input[W * H], 0.1);
        Assert.Equal((0 - 0.406) / 0.225, input[2 * W * H], 0.1);
        // Another size than the model's is not used.
        Assert.False(PagePictures.Decode(stream.ToArray(), W + 1, H, ref input));
    }
}
