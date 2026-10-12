using System.Drawing;
using ImmersiveSBS.Theater;

namespace ImmersiveSBS.Tests;

public class TheaterTests
{
    [Fact]
    public void MappingRoundTrips()
    {
        var region = new Rectangle(320, 160, 960, 540);   // on the virtual monitor
        var theater = new Rectangle(0, 0, 1920, 1080);   // in the eye
        var p = new Point(800, 400);
        Point eye = TheaterGeometry.Map(p, region, theater);
        Assert.Equal(new Point(960, 480), eye);
        Assert.Equal(p, TheaterGeometry.Map(eye, theater, region));

        var badge = new Rectangle(16, 16, 216, 56);
        Rectangle back = TheaterGeometry.Map(TheaterGeometry.Map(badge, theater, region), region, theater);
        Assert.InRange(Math.Abs(back.X - badge.X), 0, 2);
        Assert.InRange(Math.Abs(back.Width - badge.Width), 0, 2);
    }

    [Fact]
    public void ModelBadgesSpinWhileLoading()
    {
        UiImage first = ButtonImages.Badge(BadgeKind.Loading, 0);
        UiImage turned = ButtonImages.Badge(BadgeKind.Loading, 3);
        Assert.Equal(TheaterGeometry.BadgeHeight, first.Height);
        Assert.Equal(first.Width, turned.Width);
        Assert.False(first.Bgra.AsSpan().SequenceEqual(turned.Bgra));
        // Done states have one picture; the longer label gets a wider pill.
        Assert.Same(ButtonImages.Badge(BadgeKind.Ready, 0), ButtonImages.Badge(BadgeKind.Ready, 5));
        Assert.True(ButtonImages.Badge(BadgeKind.Preparing, 0).Width > ButtonImages.Badge(BadgeKind.Ready, 0).Width);
        Assert.InRange(ButtonImages.SpinnerFrame(), 0, ButtonImages.SpinnerFrames - 1);
    }
}
