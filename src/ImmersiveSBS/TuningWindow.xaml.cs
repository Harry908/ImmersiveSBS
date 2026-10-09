using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ImmersiveSBS.Depth;

namespace ImmersiveSBS;

/// <summary>
/// Live AI 3D tuning: every slider is applied on the next frame through
/// <see cref="AppController.PreviewSettings"/>; Save writes the settings file, Cancel (or closing)
/// goes back to what was saved. Hotkey changes made while it is open show up here.
/// </summary>
public partial class TuningWindow : Window
{
    private sealed record Row(Slider Slider, TextBlock Value, Func<AppSettings, double> Get, Func<double, string> Format);

    private readonly AppController controller;
    private readonly List<Row> rows = [];
    private readonly DispatcherTimer statsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private AppSettings saved;
    private AppSettings working;
    private bool loading;
    private bool closingSaved;

    public TuningWindow(AppController controller)
    {
        this.controller = controller;
        saved = controller.Settings.Clone();
        working = controller.Settings.Clone();
        InitializeComponent();

        AddSlider("Depth", 0, 1, 0.05, s => s.Ai3DStrength, (s, v) => s.Ai3DStrength = v, v => $"{v:0.00}");
        AddSlider("Screen plane", 0.1, 0.9, 0.05, s => s.Ai3DConvergence, (s, v) => s.Ai3DConvergence = v,
            v => $"{v:0.00} ({(v < 0.5 ? "behind" : v > 0.5 ? "in front" : "balanced")})");
        AddSlider("Edge dilation", 0, 8, 1, s => s.Tuning.DilateRadius, (s, v) => s.Tuning = s.Tuning with { DilateRadius = (int)v },
            v => $"{v:0} px");
        AddSlider("Edge softness", 0.02, 0.3, 0.01, s => s.Tuning.EdgeSoftness, (s, v) => s.Tuning = s.Tuning with { EdgeSoftness = v },
            v => $"{v:0.00}");
        AddSlider("Responsiveness", 0.05, 1, 0.05, s => s.Tuning.Smoothing, (s, v) => s.Tuning = s.Tuning with { Smoothing = v },
            v => $"{v:0.00} ({(v < 0.3 ? "steady" : "quick")})");
        AddSlider("Scene-cut threshold", 0.04, 0.3, 0.01, s => s.Tuning.SceneCut, (s, v) => s.Tuning = s.Tuning with { SceneCut = v },
            v => $"{v:0.00}");
        AddSlider("Video stretch", 1, 1.5, 0.01, s => s.VideoStretch, (s, v) => s.VideoStretch = v, v => $"×{v:0.00}");
        AddSlider("Sound offset", -150, 150, 10, s => s.AudioOffsetMs, (s, v) => s.AudioOffsetMs = (int)v,
            v => v == 0 ? "in step" : $"{Math.Abs(v):0} ms {(v > 0 ? "later" : "earlier")}");

        Load(working);
        controller.StatusChanged += UpdateStatus;
        statsTimer.Tick += (_, _) => UpdateStatus();
        statsTimer.Start();
        UpdateStatus();
    }

    /// <summary>Settings changed elsewhere (a hotkey, which saves): show and keep them.</summary>
    public void Reload()
    {
        saved = controller.Settings.Clone();
        working = controller.Settings.Clone();
        Load(working);
        UpdateStatus();
    }

    private void AddSlider(string label, double min, double max, double step,
        Func<AppSettings, double> get, Action<AppSettings, double> set, Func<double, string> format)
    {
        int row = Rows.RowDefinitions.Count;
        Rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 10) };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 4,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 10),
        };
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(name, row);
        Grid.SetRow(slider, row);
        Grid.SetColumn(slider, 1);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 2);
        Rows.Children.Add(name);
        Rows.Children.Add(slider);
        Rows.Children.Add(value);

        var r = new Row(slider, value, get, format);
        rows.Add(r);
        slider.ValueChanged += (_, e) =>
        {
            value.Text = format(e.NewValue);
            if (loading) return;
            set(working, Math.Round(e.NewValue, 4));
            controller.PreviewSettings(working.Clone());
        };
    }

    private void Load(AppSettings s)
    {
        loading = true;
        foreach (Row r in rows)
        {
            r.Slider.Value = r.Get(s);
            r.Value.Text = r.Format(r.Slider.Value);
        }
        FloatingWindowBox.IsChecked = s.Tuning.FloatingWindow;
        StaticOverlaysBox.IsChecked = s.Tuning.StaticOverlays;
        EdgeSnapBox.IsChecked = s.Tuning.EdgeSnap;
        CaptionsBox.IsChecked = s.Tuning.Captions;
        loading = false;
    }

    private void OnFloatingWindow(object sender, RoutedEventArgs e)
    {
        working.Tuning = working.Tuning with { FloatingWindow = FloatingWindowBox.IsChecked == true };
        controller.PreviewSettings(working.Clone());
    }

    private void OnEdgeSnap(object sender, RoutedEventArgs e)
    {
        working.Tuning = working.Tuning with { EdgeSnap = EdgeSnapBox.IsChecked == true };
        controller.PreviewSettings(working.Clone());
    }

    private void OnCaptions(object sender, RoutedEventArgs e)
    {
        working.Tuning = working.Tuning with { Captions = CaptionsBox.IsChecked == true };
        controller.PreviewSettings(working.Clone());
    }

    private void OnStaticOverlays(object sender, RoutedEventArgs e)
    {
        working.Tuning = working.Tuning with { StaticOverlays = StaticOverlaysBox.IsChecked == true };
        controller.PreviewSettings(working.Clone());
    }

    private void OnTopmost(object sender, RoutedEventArgs e) => Topmost = TopmostBox.IsChecked == true;

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        working.Ai3DStrength = defaults.Ai3DStrength;
        working.Ai3DConvergence = defaults.Ai3DConvergence;
        working.VideoStretch = defaults.VideoStretch;
        working.AudioOffsetMs = defaults.AudioOffsetMs;
        working.Tuning = StereoTuning.Default;
        Load(working);
        controller.PreviewSettings(working.Clone());
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        controller.ApplySettings(working.Clone());
        closingSaved = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void UpdateStatus()
    {
        string status = controller.StatusText;
        if (controller.AudioStatus is string audio) status += "; " + audio;
        if (controller.Settings.Ai3D == AiTarget.Off) status += "\nAI 3D is off: Depth and the edge controls take effect once it is on.";
        if (controller.LastAdjustment is string last) status += "\nLast hotkey change: " + last;
        StatusText.Text = status;
        StatsText.Text = controller.Session?.Stats?.ToString() ?? string.Empty;
    }

    protected override void OnClosed(EventArgs e)
    {
        statsTimer.Stop();
        controller.StatusChanged -= UpdateStatus;
        // Closing without Save goes back to the saved settings.
        if (!closingSaved) controller.PreviewSettings(saved.Clone());
        base.OnClosed(e);
    }
}
