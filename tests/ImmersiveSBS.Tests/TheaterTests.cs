using System.Drawing;
using ImmersiveSBS.Theater;

namespace ImmersiveSBS.Tests;

public class TheaterTests
{
    private static readonly Size Eye = new(1920, 1080);

    [Fact]
    public void A16By9VideoFillsTheEye() =>
        Assert.Equal(new Rectangle(0, 0, 1920, 1080), TheaterGeometry.Fit(new Size(960, 540), Eye));

    [Fact]
    public void A4By3VideoIsPillarboxed()
    {
        Rectangle e = TheaterGeometry.Fit(new Size(800, 600), Eye);
        Assert.Equal(1080, e.Height);
        Assert.Equal(1440, e.Width);
        Assert.Equal(240, e.X);
    }

    [Fact]
    public void AWideVideoIsLetterboxed()
    {
        Rectangle e = TheaterGeometry.Fit(new Size(2390, 1000), Eye);
        Assert.Equal(1920, e.Width);
        Assert.Equal((1080 - e.Height) / 2, e.Y);
    }

    [Fact]
    public void MappingRoundTrips()
    {
        var region = new Rectangle(320, 160, 960, 540);   // on the virtual monitor
        var theater = new Rectangle(0, 0, 1920, 1080);   // in the eye
        var p = new Point(800, 400);
        Point eye = TheaterGeometry.Map(p, region, theater);
        Assert.Equal(new Point(960, 480), eye);
        Assert.Equal(p, TheaterGeometry.Map(eye, theater, region));

        var button = new Rectangle(1688, 16, 216, 56);
        Rectangle back = TheaterGeometry.Map(TheaterGeometry.Map(button, theater, region), region, theater);
        Assert.InRange(Math.Abs(back.X - button.X), 0, 2);
        Assert.InRange(Math.Abs(back.Width - button.Width), 0, 2);
    }

    [Fact]
    public void ButtonsSitInTheTopRightCorner()
    {
        var area = new Rectangle(240, 0, 1440, 1080);
        Rectangle[] b = TheaterGeometry.TopRight(area, 2);
        Assert.Equal(area.Right - TheaterGeometry.Margin, b[1].Right);
        Assert.Equal(area.Top + TheaterGeometry.Margin, b[1].Top);
        Assert.Equal(b[1].Left - TheaterGeometry.Margin, b[0].Right);
        Assert.True(area.Contains(b[0]) && area.Contains(b[1]));
    }

    [Fact]
    public void ClicksHitOnlyTheButtons()
    {
        var buttons = new TheaterButtons();
        buttons.Set([(TheaterAction.ExitTheater, new Rectangle(100, 100, 50, 20)), (TheaterAction.PlayPause, new Rectangle(40, 100, 50, 20))]);
        Assert.Equal(TheaterAction.ExitTheater, buttons.Hit(new Point(120, 110)));
        Assert.Equal(TheaterAction.PlayPause, buttons.Hit(new Point(45, 101)));
        Assert.Null(buttons.Hit(new Point(95, 110)));
        buttons.Clear();
        Assert.Null(buttons.Hit(new Point(120, 110)));
    }

    [Fact]
    public void ButtonImagesAreDrawn()
    {
        UiImage image = ButtonImages.For(TheaterAction.ExitTheater, hover: false);
        Assert.Equal(TheaterGeometry.ButtonSize.Width, image.Width);
        Assert.Equal(image.Width * image.Height * 4, image.Bgra.Length);
        // Opaque-ish pill in the middle, transparent corner.
        int mid = (image.Height / 2 * image.Width + image.Width / 2) * 4;
        Assert.True(image.Bgra[mid + 3] > 150);
        Assert.Equal(0, image.Bgra[3]);
        Assert.NotSame(image, ButtonImages.For(TheaterAction.ExitTheater, hover: true));
    }

    [Fact]
    public void TheSettingsPanelSitsUnderTheButtonsInsideTheVideo()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        Rectangle[] buttons = TheaterGeometry.TopRight(area, 3);
        var rows = TheaterGeometry.PanelRows(area, 7);
        Assert.All(rows, r =>
        {
            Assert.True(area.Contains(r.Minus) && area.Contains(r.Plus), $"{r}");
            Assert.True(r.Minus.Right < r.Value.Left && r.Value.Right < r.Plus.Left, $"{r}");
            Assert.All(buttons, b => Assert.False(b.IntersectsWith(r.Minus) || b.IntersectsWith(r.Value) || b.IntersectsWith(r.Plus)));
        });
        Assert.Equal(buttons[2].Right, rows[0].Plus.Right);
        for (int i = 1; i < rows.Length; i++) Assert.True(rows[i].Value.Top > rows[i - 1].Value.Bottom);
    }

    [Fact]
    public void PanelLabelsAreDrawnAtTheirWidth()
    {
        UiImage value = ButtonImages.Label("Depth:  0.8", TheaterGeometry.ValueSize.Width, hover: false, centred: false);
        UiImage plus = ButtonImages.Label("+", TheaterGeometry.StepSize.Width, hover: true, centred: true);
        Assert.Equal(TheaterGeometry.ValueSize.Width, value.Width);
        Assert.Equal(TheaterGeometry.StepSize.Width, plus.Width);
        Assert.Equal(value.Width * value.Height * 4, value.Bgra.Length);
        Assert.Same(value, ButtonImages.Label("Depth:  0.8", TheaterGeometry.ValueSize.Width, hover: false, centred: false));
        Assert.NotSame(value, ButtonImages.Label("Depth:  0.9", TheaterGeometry.ValueSize.Width, hover: false, centred: false));
    }

    [Fact]
    public void ModelBadgesSpinWhileLoading()
    {
        UiImage first = ButtonImages.Badge(BadgeKind.Loading, 0);
        UiImage turned = ButtonImages.Badge(BadgeKind.Loading, 3);
        Assert.Equal(TheaterGeometry.ButtonSize.Height, first.Height);
        Assert.Equal(first.Width, turned.Width);
        Assert.False(first.Bgra.AsSpan().SequenceEqual(turned.Bgra));
        // Done states have one picture; the longer label gets a wider pill.
        Assert.Same(ButtonImages.Badge(BadgeKind.Ready, 0), ButtonImages.Badge(BadgeKind.Ready, 5));
        Assert.True(ButtonImages.Badge(BadgeKind.Preparing, 0).Width > ButtonImages.Badge(BadgeKind.Ready, 0).Width);
        Assert.InRange(ButtonImages.SpinnerFrame(), 0, ButtonImages.SpinnerFrames - 1);
    }
}
