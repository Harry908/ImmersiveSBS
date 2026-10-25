using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ImmersiveSBS.Depth;
using ImmersiveSBS.Displays;
using ImmersiveSBS.Input;

namespace ImmersiveSBS;

/// <summary>Settings, with the live status of the app and its depth model.</summary>
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
    private readonly List<(ShortcutCommand Command, TextBox Box)> shortcutBoxes = [];

    /// <summary>One labelled box per command, in the order of <see cref="ShortcutCommand"/>.</summary>
    private void BuildShortcutRows(AppSettings s)
    {
        foreach (ShortcutCommand command in Enum.GetValues<ShortcutCommand>())
        {
            int row = ShortcutsGrid.RowDefinitions.Count;
            ShortcutsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock
            {
                Text = ShortcutCommands.Label(command),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 8),
            };
            var box = new TextBox { Text = s.Shortcuts.GetValueOrDefault(command, ""), Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(label, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            ShortcutsGrid.Children.Add(label);
            ShortcutsGrid.Children.Add(box);
            shortcutBoxes.Add((command, box));
        }
    }

    public SettingsWindow(AppController controller)
    {
        this.controller = controller;
        InitializeComponent();
        Title = $"ImmersiveSBS {AppController.Version} settings";

        AppSettings s = controller.Settings;
        List<DisplayInfo> displays = DisplayConfigurationService.GetDesktopDisplays(retryWhileTargetUnavailable: false);
        Fill(VirtualMonitorBox, displays, s.VirtualMonitorIdentity, s.ResolveVirtualMonitorIdentity(displays));
        Fill(GlassesBox, displays, s.GlassesIdentity, s.ResolveGlassesIdentity(displays));

        ShowIn2DBox.IsChecked = s.ShowIn2D;
        StartWithWindowsBox.IsChecked = AppSettings.StartWithWindows;
        BuildShortcutRows(s);

        WholeScreenBox.IsChecked = s.Ai3D == AiTarget.WholeScreen;
        DepthTierBox.SelectedIndex = (int)s.DepthTier;
        DepthPresetBox.SelectedIndex = (int)s.DepthPreset;
        shown = Shown(s);

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
        StatusText.Text = controller.StatusLine;
        ModelText.Text = controller.ModelStatus;
        ModelSpinner.Visibility = controller.ModelBusy ? Visibility.Visible : Visibility.Collapsed;
        ModelIcon.Visibility = controller.ModelBusy ? Visibility.Collapsed : Visibility.Visible;
        bool ready = controller.ModelState == DepthServiceState.Ready;
        ModelIcon.Text = ready ? "" : "";   // CheckMark, Warning
        ModelIcon.SetResourceReference(TextBlock.ForegroundProperty, ready ? "AccentFillBrush" : "CautionBrush");
        MainDisplayWarning.Visibility = controller.VirtualMonitor is { IsPrimary: true } ? Visibility.Visible : Visibility.Collapsed;
        BrowserText.Text = controller.BrowserStatus;
        BrowserHelp.Visibility = controller.BrowserConnected ? Visibility.Collapsed : Visibility.Visible;
        FollowSettings();
    }

    /// <summary>The 3D values this window last took from the settings.</summary>
    private (bool WholeScreen, int Model, int Size) shown;

    private static (bool, int, int) Shown(AppSettings s) =>
        (s.Ai3D == AiTarget.WholeScreen, (int)s.DepthTier, (int)s.DepthPreset);

    /// <summary>
    /// Values changed elsewhere while the window is open (the theater's 3D panel, the tray): a box
    /// the user has not changed here follows, so Save keeps the other change instead of writing
    /// back what the window showed when it opened.
    /// </summary>
    private void FollowSettings()
    {
        var now = Shown(controller.Settings);
        if (now == shown) return;
        if (WholeScreenBox.IsChecked == shown.WholeScreen) WholeScreenBox.IsChecked = now.Item1;
        if (DepthTierBox.SelectedIndex == shown.Model) DepthTierBox.SelectedIndex = now.Item2;
        if (DepthPresetBox.SelectedIndex == shown.Size) DepthPresetBox.SelectedIndex = now.Item3;
        shown = now;
    }


    private void OnOpenExtensionFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppController.ExtensionFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("Settings: could not open the extension folder: " + ex.Message);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var shortcuts = new Dictionary<ShortcutCommand, string>();
        foreach (var (command, box) in shortcutBoxes)
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
        settings.ShowIn2D = ShowIn2DBox.IsChecked == true;
        settings.Shortcuts = shortcuts;
        settings.Ai3D = WholeScreenBox.IsChecked == true ? AiTarget.WholeScreen : AiTarget.Off;
        settings.DepthTier = (DepthTier)Math.Max(0, DepthTierBox.SelectedIndex);
        settings.DepthPreset = (DepthPreset)Math.Max(0, DepthPresetBox.SelectedIndex);

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
