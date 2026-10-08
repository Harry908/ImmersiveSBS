using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ImmersiveSBS.Capture;
using ImmersiveSBS.Displays;
using ImmersiveSBS.Input;

namespace ImmersiveSBS;

/// <summary>Settings, plus the live status and measurements of the running session.</summary>
public partial class SettingsWindow : Window
{
    /// <summary>A display in a picker; a null identity means "pick automatically".</summary>
    private sealed record DisplayChoice(DisplayIdentity? Identity, string Label)
    {
        // The themed ComboBox shows the item itself, not DisplayMemberPath.
        public override string ToString() => Label;
    }

    private readonly AppController controller;
    private readonly DispatcherTimer statsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public SettingsWindow(AppController controller)
    {
        this.controller = controller;
        InitializeComponent();

        AppSettings s = controller.Settings;
        List<DisplayInfo> displays = DisplayConfigurationService.GetDesktopDisplays(retryWhileTargetUnavailable: false);
        Fill(VirtualMonitorBox, displays, s.VirtualMonitorIdentity, s.ResolveVirtualMonitorIdentity(displays));
        Fill(GlassesBox, displays, s.GlassesIdentity, s.ResolveGlassesIdentity(displays));

        CaptureBox.SelectedIndex = s.CaptureMethod == CaptureMethod.GraphicsCapture ? 1 : 0;
        ShowIn2DBox.IsChecked = s.ShowIn2D;
        CursorBox.IsChecked = s.KeepCursorOffGlasses;
        WindowsBox.IsChecked = s.MoveWindowsOffGlasses;
        ShowInWindowBox.IsChecked = s.ShowInWindowWhenNotOnGlasses;
        EvacuateBox.IsChecked = s.MoveWindowsOffVirtualMonitorOnExit;
        StartWithWindowsBox.IsChecked = AppSettings.StartWithWindows;
        PauseShortcutBox.Text = s.Shortcuts.GetValueOrDefault(ShortcutCommand.Pause, "");
        SettingsShortcutBox.Text = s.Shortcuts.GetValueOrDefault(ShortcutCommand.Settings, "");
        ExitShortcutBox.Text = s.Shortcuts.GetValueOrDefault(ShortcutCommand.Exit, "");

        controller.StatusChanged += UpdateStatus;
        statsTimer.Tick += (_, _) => UpdateStatus();
        statsTimer.Start();
        UpdateStatus();
    }

    /// <summary>
    /// Lists the desktop displays, with an "Automatic" entry first naming what it currently
    /// picks, and selects the saved display (or Automatic when none is saved).
    /// </summary>
    private static void Fill(ComboBox box, List<DisplayInfo> displays, DisplayIdentity? saved, DisplayIdentity? automatic)
    {
        var choices = new List<DisplayChoice>
        {
            new(null, "Automatic" + (automatic != null ? $" ({automatic})" : " (none found)")),
        };
        choices.AddRange(displays.Select(d => new DisplayChoice(d.Identity,
            $"{d.Identity} — {d.Mode}, {d.Identity.EdidKey}, at {d.Bounds.X},{d.Bounds.Y}{(d.IsPrimary ? ", primary" : "")}")));

        box.ItemsSource = choices;
        DisplayInfo? match = DisplayConfigurationService.ResolveBestMatch(displays, saved, out _);
        box.SelectedIndex = match == null ? 0 : 1 + displays.IndexOf(match);
    }

    private void UpdateStatus()
    {
        StatusText.Text = controller.StatusText;
        MainDisplayWarning.Visibility = controller.VirtualMonitor is { IsPrimary: true } ? Visibility.Visible : Visibility.Collapsed;
        StatsText.Text = controller.Session?.Stats?.ToString() ?? string.Empty;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var shortcuts = new Dictionary<ShortcutCommand, string>();
        foreach (var (command, box) in new[]
                 {
                     (ShortcutCommand.Pause, PauseShortcutBox),
                     (ShortcutCommand.Settings, SettingsShortcutBox),
                     (ShortcutCommand.Exit, ExitShortcutBox),
                 })
        {
            string text = box.Text.Trim();
            if (text.Length == 0)
            {
                shortcuts[command] = string.Empty;
            }
            else if (Shortcut.TryParse(text, out Shortcut s))
            {
                shortcuts[command] = s.ToString();
            }
            else
            {
                ErrorText.Text = $"\"{text}\" is not a valid shortcut.";
                ErrorText.Visibility = Visibility.Visible;
                box.Focus();
                return;
            }
        }

        AppSettings settings = controller.Settings.Clone();
        settings.VirtualMonitorIdentity = (VirtualMonitorBox.SelectedItem as DisplayChoice)?.Identity;
        settings.GlassesIdentity = (GlassesBox.SelectedItem as DisplayChoice)?.Identity;
        settings.CaptureMethod = CaptureBox.SelectedIndex == 1 ? CaptureMethod.GraphicsCapture : CaptureMethod.DesktopDuplication;
        settings.ShowIn2D = ShowIn2DBox.IsChecked == true;
        settings.KeepCursorOffGlasses = CursorBox.IsChecked == true;
        settings.MoveWindowsOffGlasses = WindowsBox.IsChecked == true;
        settings.ShowInWindowWhenNotOnGlasses = ShowInWindowBox.IsChecked == true;
        settings.MoveWindowsOffVirtualMonitorOnExit = EvacuateBox.IsChecked == true;
        settings.Shortcuts = shortcuts;

        try
        {
            if (AppSettings.StartWithWindows != (StartWithWindowsBox.IsChecked == true))
                AppSettings.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        }
        catch (Exception ex)
        {
            Log.Write("Settings: could not change Start with Windows: " + ex.Message);
        }

        controller.ApplySettings(settings);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        statsTimer.Stop();
        controller.StatusChanged -= UpdateStatus;
        base.OnClosed(e);
    }
}
