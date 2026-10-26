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
            Tuning = new StereoTuning { DilateRadius = 5, SoftEdges = 3, AntiAlias = AutoSwitch.Off, Foreground = -2, Smoothing = 0.4, SceneCut = 0.2, FloatingWindow = false, StaticOverlays = true },
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

    /// <summary>
    /// A file from before the extension became part of the app: the pick shortcut, detected 3D
    /// video, the browser video target and page data are gone; the rest of the file loads.
    /// </summary>
    [Fact]
    public void ChoicesSinceRemovedLoadAsTheirReplacements()
    {
        var options = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""
            {"Ai3D":"Video","Ai3DLastTarget":"Video","Native3D":"Auto","UsePageData":false,"Ai3DStrength":0.8,
             "Shortcuts":{"Pause":"Ctrl+Alt+P","PickVideo":"Ctrl+Alt+Shift+D"}}
            """, options)!;
        s.ForgetRemovedChoices();
        Assert.Equal(AiTarget.Off, s.Ai3D);
        Assert.Equal(AiTarget.WholeScreen, s.Ai3DLastTarget);
        Assert.Equal(Native3DMode.Off, s.Native3D);
        Assert.Equal(0.8, s.Ai3DStrength);
        Assert.Equal("Ctrl+Alt+P", s.Shortcuts[ShortcutCommand.Pause]);
        Assert.Single(s.Shortcuts);
        // Written back, the file names what is there now.
        string json = System.Text.Json.JsonSerializer.Serialize(s, options);
        Assert.DoesNotContain("PickVideo", json);
        Assert.Contains("\"Native3D\":\"Off\"", json);
    }

    [Fact]
    public void SettingsFromBeforeTheEdgePipelineGetItsDefaults()
    {
        // A file saved before version 2 has screen dilation and edge snapping on (the old defaults).
        var old = System.Text.Json.JsonSerializer.Deserialize<StereoTuning>("""{"DilateRadius":1,"EdgeSnap":true,"Captions":false,"EdgeSoftness":0.1}""")!;
        Assert.Equal(0, old.Version);
        StereoTuning t = old.Migrated();
        Assert.Equal(StereoTuning.CurrentVersion, t.Version);
        Assert.Equal(0, t.DilateRadius);
        Assert.False(t.EdgeSnap);
        Assert.False(t.Captions);            // a choice the user made stays
        Assert.Equal(-1, t.SoftEdges);       // Auto
        // Once migrated, the user's own choices are kept.
        StereoTuning mine = t with { EdgeSnap = true, DilateRadius = 2 };
        Assert.Equal(mine, mine.Migrated());
    }

    [Theory]
    [InlineData("V2Large", DepthTier.Base)]
    [InlineData("Large", DepthTier.Base)]
    [InlineData("V2Base", DepthTier.Base)]
    [InlineData("DA3Small", DepthTier.DA3Small)]
    [InlineData("Small", DepthTier.Base)]
    public void ARemovedModelInASavedFileLoadsAsTheDefault(string saved, DepthTier expected)
    {
        var options = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>($$"""{"DepthTier":"{{saved}}","Ai3DStrength":0.8}""", options)!;
        Assert.Equal(expected, s.DepthTier);
        Assert.Equal(0.8, s.Ai3DStrength);   // the rest of the file still loads
        Assert.Contains($"\"DepthTier\":\"{expected}\"", System.Text.Json.JsonSerializer.Serialize(s, options));
    }

    [Theory]
    [InlineData("Fast", DepthPreset.Balanced)]
    [InlineData("Quality", DepthPreset.Quality)]
    public void ARemovedResolutionInASavedFileLoadsAsTheDefault(string saved, DepthPreset expected)
    {
        var options = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>($$"""{"DepthPreset":"{{saved}}","Ai3DStrength":0.8}""", options)!;
        Assert.Equal(expected, s.DepthPreset);
        Assert.Equal(0.8, s.Ai3DStrength);   // the rest of the file still loads
    }

    [Fact]
    public void EveryTimingChoiceIsReachableStepByStep()
    {
        // Live, Auto, Matched, then the buffers: the theater panel's + goes through all of them
        // (no browser check: the panel is the extension's), and stops at either end.
        var s = new AppSettings();
        AppController.SetTiming(s, 0);
        Assert.Equal(0, AppController.NextTiming(s, -1));
        for (int i = 1; i < AppController.TimingChoices.Length; i++)
        {
            AppController.SetTiming(s, AppController.NextTiming(s, +1));
            Assert.Equal(i, AppController.TimingIndex(s));
        }
        Assert.Equal(AppController.TimingChoices.Length - 1, AppController.NextTiming(s, +1));
    }

    [Fact]
    public void TheTimingChoicesRoundTrip()
    {
        var s = new AppSettings();
        Assert.Equal(VideoTiming.Auto, s.Timing);
        for (int i = 0; i < AppController.TimingChoices.Length; i++)
        {
            AppController.SetTiming(s, i);
            Assert.Equal(i, AppController.TimingIndex(s));
        }
        // From a buffer back to none, the timing chosen before is kept.
        AppController.SetTiming(s, 0);
        AppController.SetTiming(s, 4);
        Assert.Equal(VideoTiming.Live, s.Timing);
        Assert.Equal(3, s.BufferSeconds);
    }

    [Theory]
    [InlineData(2, 0, 0)]
    [InlineData(2, 0.5, 2)]
    [InlineData(2, 1, 2)]
    [InlineData(2, 3, 3)]
    [InlineData(2, 6, 4)]
    public void TheBufferIsTwoSecondsByDefaultAndAtLeast(double fresh, double saved, double loaded)
    {
        // 2 s by default; a saved 0.5 or 1 s (choices since removed) loads as 2, none stays none.
        Assert.Equal(fresh, new AppSettings().BufferSeconds);
        Assert.Equal(AppSettings.MinBufferSeconds, AppController.TimingChoices[3].Buffer);
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>($$"""{"BufferSeconds":{{saved}}}""")!;
        s.ForgetRemovedChoices();
        Assert.Equal(loaded, s.BufferSeconds);
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
