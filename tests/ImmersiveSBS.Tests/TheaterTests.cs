using System.Drawing;
using ImmersiveSBS.Theater;

namespace ImmersiveSBS.Tests;

public class TheaterTests
{
    private static readonly Size Eye = new(1920, 1080);

    [Fact]
    public void WithTheExtensionTheButtonIsOfferedOnlyOnItsVideos()
    {
        // The tracker took a scrolling page for a video; the extension reports one real video.
        var falseFind = new Rectangle(0, 600, 1920, 400);
        var video = new Rectangle(300, 100, 1280, 720);
        IReadOnlyList<Rectangle> reported = [video];

        Assert.Equal(video, TheaterUi.OfferedVideo(true, false, reported, new Point(900, 400)));
        // On the false find, outside the reported video: no button.
        Assert.False(video.Contains(new Point(900, 950)) || !falseFind.Contains(new Point(900, 950)));
        Assert.Null(TheaterUi.OfferedVideo(true, false, reported, new Point(900, 950)));
        // No video in the page at all: no button anywhere.
        Assert.Null(TheaterUi.OfferedVideo(true, false, [], new Point(900, 950)));
        // Without the extension, none either: the tracker's box can be the desktop.
        Assert.Null(TheaterUi.OfferedVideo(true, false, null, new Point(900, 950)));
        // Turned off, or already in the theater: none.
        Assert.Null(TheaterUi.OfferedVideo(false, false, reported, new Point(900, 400)));
        Assert.Null(TheaterUi.OfferedVideo(true, true, reported, new Point(900, 400)));
    }

    [Fact]
    public void TheBottomBarSitsAtTheBottomInsideTheVideo()
    {
        var e = new Rectangle(0, 0, 1920, 1080);
        var bar = TheaterGeometry.BottomBar(e);
        Rectangle[] parts = [bar.Seek, bar.Time, bar.Back, bar.Play, bar.Forward, bar.Mute];
        foreach (Rectangle r in parts) Assert.True(e.Contains(r) && bar.Backdrop.Contains(r), r.ToString());
        // Play in the middle, back to its left, on to its right, mute at the right; the seek bar above them.
        Assert.Equal(960, bar.Play.Left + bar.Play.Width / 2);
        Assert.True(bar.Back.Right < bar.Play.Left && bar.Forward.Left > bar.Play.Right && bar.Mute.Left > bar.Forward.Right);
        Assert.True(bar.Time.Right < bar.Back.Left);
        Assert.True(bar.Seek.Bottom < bar.Play.Top);
        Assert.Equal(e.Bottom, bar.Backdrop.Bottom);
        // A click a quarter of the way along seeks to a quarter.
        Assert.Equal(0.25, TheaterGeometry.SeekFraction(bar.Seek, bar.Seek.Left + bar.Seek.Width / 4), 2);
        Assert.Equal(0, TheaterGeometry.SeekFraction(bar.Seek, -50));
        Assert.Equal(1, TheaterGeometry.SeekFraction(bar.Seek, 5000));
    }

    [Fact]
    public void ClockReadsLikeAPlayer()
    {
        Assert.Equal("0:05", TheaterGeometry.Clock(5.9));
        Assert.Equal("12:34", TheaterGeometry.Clock(754));
        Assert.Equal("1:02:03", TheaterGeometry.Clock(3723));
        Assert.Equal("--:--", TheaterGeometry.Clock(double.NaN));
    }

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
    public void ButtonsSitAtTheTopCentreClearOfThePlayersCorners()
    {
        var area = new Rectangle(240, 0, 1440, 1080);
        Rectangle[] b = TheaterGeometry.TopCentre(area, 3);
        Assert.Equal(area.Top + TheaterGeometry.Margin, b[0].Top);
        Assert.Equal(b[0].Right + TheaterGeometry.Margin, b[1].Left);
        Assert.Equal(b[1].Right + TheaterGeometry.Margin, b[2].Left);
        // Centred: as far from the left edge as from the right.
        Assert.InRange(Math.Abs((b[0].Left - area.Left) - (area.Right - b[2].Right)), 0, 1);
        Assert.All(b, r => Assert.True(area.Contains(r)));
        // The corners stay free (a quarter of the width on each side).
        Assert.True(b[0].Left > area.Left + area.Width / 4 && b[2].Right < area.Right - area.Width / 4);
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
        Rectangle[] buttons = TheaterGeometry.TopCentre(area, 3);
        var rows = TheaterGeometry.PanelRows(area, 7);
        Assert.All(rows, r =>
        {
            Assert.True(area.Contains(r.Minus) && area.Contains(r.Plus), $"{r}");
            Assert.True(r.Minus.Right < r.Value.Left && r.Value.Right < r.Plus.Left, $"{r}");
            Assert.All(buttons, b => Assert.False(b.IntersectsWith(r.Minus) || b.IntersectsWith(r.Value) || b.IntersectsWith(r.Plus)));
        });
        // Centred under the buttons.
        Assert.InRange(Math.Abs((rows[0].Minus.Left + rows[0].Plus.Right) / 2 - (buttons[0].Left + buttons[2].Right) / 2), 0, 1);
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
