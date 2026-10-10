using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ImmersiveSBS.Capture;
using ImmersiveSBS.Depth;
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
        BuildShortcutRows(s);

        AiTargetBox.SelectedIndex = (int)s.Ai3D;
        DepthTierBox.SelectedIndex = (int)s.DepthTier;
        DepthPresetBox.SelectedIndex = (int)s.DepthPreset;
        Native3DBox.SelectedIndex = (int)s.Native3D;
        SelectClosest(StrengthBox, s.Ai3DStrength);
        SelectClosest(ConvergenceBox, s.Ai3DConvergence);
        SelectClosest(StretchBox, s.VideoStretch);
        OfferTheaterBox.IsChecked = s.OfferTheater;
        SelectClosest(BufferBox, s.BufferSeconds);
        SelectClosest(AudioOffsetBox, s.AudioOffsetMs);
        ModelsText.Text = $"Model files: {DepthModel.ModelsDirectory}. The first use of a model on this PC prepares it for the GPU (about 20 s).";

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

    /// <summary>Selects the item whose Tag value is nearest to <paramref name="value"/>.</summary>
    private static void SelectClosest(ComboBox box, double value)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>()
            .OrderBy(i => Math.Abs(TagValue(i) - value))
            .FirstOrDefault();
    }

    private static double TagValue(ComboBoxItem item) =>
        double.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture);

    private void UpdateStatus()
    {
        StatusText.Text = controller.StatusText + (controller.AudioStatus is string audio ? "; " + audio : "");
        ModelText.Text = controller.ModelStatus;
        ModelSpinner.Visibility = controller.ModelBusy ? Visibility.Visible : Visibility.Collapsed;
        ModelIcon.Visibility = controller.ModelBusy ? Visibility.Collapsed : Visibility.Visible;
        bool ready = controller.ModelState == DepthServiceState.Ready;
        ModelIcon.Text = ready ? "" : "";   // CheckMark, Warning
        ModelIcon.SetResourceReference(TextBlock.ForegroundProperty, ready ? "AccentFillBrush" : "CautionBrush");
        MainDisplayWarning.Visibility = controller.VirtualMonitor is { IsPrimary: true } ? Visibility.Visible : Visibility.Collapsed;
        BrowserText.Text = controller.BrowserStatus;
        BrowserHelp.Visibility = controller.BrowserConnected ? Visibility.Collapsed : Visibility.Visible;
        StatsText.Text = controller.Session?.Stats?.ToString() ?? string.Empty;
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
        settings.CaptureMethod = CaptureBox.SelectedIndex == 1 ? CaptureMethod.GraphicsCapture : CaptureMethod.DesktopDuplication;
        settings.ShowIn2D = ShowIn2DBox.IsChecked == true;
        settings.KeepCursorOffGlasses = CursorBox.IsChecked == true;
        settings.MoveWindowsOffGlasses = WindowsBox.IsChecked == true;
        settings.ShowInWindowWhenNotOnGlasses = ShowInWindowBox.IsChecked == true;
        settings.MoveWindowsOffVirtualMonitorOnExit = EvacuateBox.IsChecked == true;
        settings.Shortcuts = shortcuts;
        var target = (AiTarget)Math.Max(0, AiTargetBox.SelectedIndex);
        if (target != AiTarget.Off) settings.Ai3DLastTarget = target;
        settings.Ai3D = target;
        settings.DepthTier = (DepthTier)Math.Max(0, DepthTierBox.SelectedIndex);
        settings.DepthPreset = (DepthPreset)Math.Max(0, DepthPresetBox.SelectedIndex);
        settings.Native3D = (Native3DMode)Math.Max(0, Native3DBox.SelectedIndex);
        if (StrengthBox.SelectedItem is ComboBoxItem strength) settings.Ai3DStrength = TagValue(strength);
        if (ConvergenceBox.SelectedItem is ComboBoxItem convergence) settings.Ai3DConvergence = TagValue(convergence);
        if (StretchBox.SelectedItem is ComboBoxItem stretch) settings.VideoStretch = TagValue(stretch);
        settings.OfferTheater = OfferTheaterBox.IsChecked == true;
        if (BufferBox.SelectedItem is ComboBoxItem buffer) settings.BufferSeconds = TagValue(buffer);
        if (AudioOffsetBox.SelectedItem is ComboBoxItem offset) settings.AudioOffsetMs = (int)TagValue(offset);

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
