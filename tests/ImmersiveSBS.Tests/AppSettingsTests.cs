using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Tests;

public class AppSettingsTests
{
    [Fact]
    public void CloneKeepsTuningVideoAndShortcuts()
    {
        var s = new AppSettings
        {
            Ai3D = AiTarget.WholeScreen,
            Ai3DStrength = 0.8,
            BufferSeconds = 1,
            AudioOffsetMs = -40,
            VideoStretch = 1.3333,
            Tuning = new StereoTuning { DilateRadius = 5, EdgeSoftness = 0.05, Smoothing = 0.4, SceneCut = 0.2, FloatingWindow = false, StaticOverlays = true },
        };
        s.Shortcuts[ShortcutCommand.MoreDepth] = "Ctrl+Shift+Up";

        AppSettings c = s.Clone();

        Assert.Equal(AiTarget.WholeScreen, c.Ai3D);
        Assert.Equal(0.8, c.Ai3DStrength);
        Assert.Equal(1, c.BufferSeconds);
        Assert.Equal(-40, c.AudioOffsetMs);
        Assert.Equal(1.3333, c.VideoStretch);
        Assert.Equal(s.Tuning, c.Tuning);
        Assert.Equal("Ctrl+Shift+Up", c.Shortcuts[ShortcutCommand.MoreDepth]);
        Assert.NotSame(s.Tuning, c.Tuning);
    }

    [Fact]
    public void EveryCommandHasADefaultShortcutAndALabel()
    {
        var defaults = AppSettings.DefaultShortcuts();
        foreach (ShortcutCommand command in Enum.GetValues<ShortcutCommand>())
        {
            Assert.True(defaults.ContainsKey(command), $"{command} has no default shortcut");
            Assert.True(Input.Shortcut.TryParse(defaults[command], out _), $"{command}: \"{defaults[command]}\" does not parse");
            Assert.False(string.IsNullOrWhiteSpace(ShortcutCommands.Label(command)));
        }
        Assert.Equal(defaults.Count, defaults.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
