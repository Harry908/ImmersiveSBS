using System.Drawing;
using System.Windows.Input;
using ImmersiveSBS.Displays;
using ImmersiveSBS.Input;

namespace ImmersiveSBS.Tests;

public class DisplayLogicTests
{
    private static DisplayInfo Display(int w, int h, string name = "SmartGlasses") => new()
    {
        Identity = new DisplayIdentity { FriendlyName = name },
        Mode = new DisplayMode { Width = w, Height = h },
        Bounds = new Rectangle(0, 0, w, h),
    };

    [Theory]
    [InlineData(3840, 1080, StereoFrameShape.SideBySide)]
    [InlineData(1920, 1080, StereoFrameShape.None)]
    [InlineData(1920, 2160, StereoFrameShape.TopBottom)]
    [InlineData(1080, 1920, StereoFrameShape.None)] // portrait monitor
    public void ClassifiesFrames(int w, int h, StereoFrameShape expected) =>
        Assert.Equal(expected, GlassesStateEvaluator.ClassifyFrame(w, h));

    [Fact]
    public void SideBySideIsThreeD() =>
        Assert.Equal(GlassesState.Mode3D, GlassesStateEvaluator.Evaluate(Display(3840, 1080), showIn2D: false).State);

    [Fact]
    public void TwoDPresentsOnlyWhenAsked()
    {
        Assert.True(GlassesStateEvaluator.Evaluate(Display(1920, 1080), showIn2D: true).ShouldPresent);
        Assert.False(GlassesStateEvaluator.Evaluate(Display(1920, 1080), showIn2D: false).ShouldPresent);
    }

    [Fact]
    public void TopBottomAndOddWidthAreRefused()
    {
        Assert.Equal(GlassesState.UnsupportedMode, GlassesStateEvaluator.Evaluate(Display(1920, 2160), true).State);
        Assert.Equal(GlassesState.UnsupportedMode, GlassesStateEvaluator.Evaluate(Display(3841, 1080), true).State);
    }

    [Fact]
    public void MissingGlassesAreDisconnected() =>
        Assert.Equal(GlassesState.Disconnected, GlassesStateEvaluator.Evaluate(null, true).State);

    [Fact]
    public void IdentityRoundTripsThroughSettings()
    {
        var id = new DisplayIdentity
        {
            DevicePath = @"\\?\DISPLAY#TCL03D4#5&1a2b3c4d&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            EdidManufacturer = "TCL",
            EdidProductCode = 0x03D4,
            EdidValid = true,
            FriendlyName = "Smart|Glasses=1%",
            GdiDeviceName = @"\\.\DISPLAY5",
            LastKnownBounds = new Rectangle(80, -1080, 3840, 1080),
        };

        DisplayIdentity? back = DisplayIdentity.Parse(id.Serialize());

        Assert.NotNull(back);
        Assert.Equal(id.DevicePath, back.DevicePath);
        Assert.Equal(id.FriendlyName, back.FriendlyName);
        Assert.Equal("TCL-03D4", back.EdidKey);
        Assert.Equal(id.LastKnownBounds, back.LastKnownBounds);
        Assert.Equal(DisplayMatchStrength.DevicePath, back.MatchAgainst(id));
    }

    [Fact]
    public void EdidMatchesWhenThePathChanged()
    {
        var saved = new DisplayIdentity { DevicePath = "a", EdidManufacturer = "TCL", EdidProductCode = 0x03D4, EdidValid = true };
        var now = new DisplayIdentity { DevicePath = "b", EdidManufacturer = "TCL", EdidProductCode = 0x03D4, EdidValid = true };
        Assert.Equal(DisplayMatchStrength.Edid, now.MatchAgainst(saved));
    }

    [Fact]
    public void GtMaxIsKnown() => Assert.Equal("rayneo-gt-max", KnownGlasses.FindByEdidKey("TCL-03D4")?.Id);

    [Theory]
    [InlineData("Ctrl+Alt+B", ModifierKeys.Control | ModifierKeys.Alt, Key.B)]
    [InlineData("alt + shift + f1", ModifierKeys.Alt | ModifierKeys.Shift, Key.F1)]
    public void ParsesShortcuts(string text, ModifierKeys modifiers, Key key)
    {
        Assert.True(Shortcut.TryParse(text, out Shortcut s));
        Assert.Equal(new Shortcut(modifiers, key), s);
    }

    [Theory]
    [InlineData("B")]          // no modifier
    [InlineData("Ctrl+Alt")]   // no key
    [InlineData("Ctrl+Foo")]
    [InlineData("")]
    public void RejectsBadShortcuts(string text) => Assert.False(Shortcut.TryParse(text, out _));

    [Fact]
    public void ShortcutTextRoundTrips()
    {
        Assert.True(Shortcut.TryParse("Ctrl+Alt+F1", out Shortcut s));
        Assert.Equal("Ctrl+Alt+F1", s.ToString());
    }
}
