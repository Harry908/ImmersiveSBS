// Ported from DesktopSbS: DesktopSbS/View/TrayIcon.cs
using System.Drawing;
using System.Windows.Forms;
using ImmersiveSBS.Capture;
using ImmersiveSBS.Depth;
using ImmersiveSBS.Theme;

namespace ImmersiveSBS.Tray;

/// <summary>
/// Notification-area icon with every command, so nothing depends on remembering a shortcut.
/// Each menu item shows its current shortcut as a reminder.
///
/// Lives on the UI thread: WPF's dispatcher pumps the Win32 messages NotifyIcon relies on.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppController controller;
    private readonly NotifyIcon notifyIcon;
    private readonly Icon icon;

    private readonly ToolStripMenuItem statusItem;
    private readonly ToolStripMenuItem pauseItem;
    private readonly ToolStripMenuItem windowItem;
    private readonly ToolStripMenuItem captureItem;
    private readonly ToolStripMenuItem aiItem;
    private readonly ToolStripMenuItem aiOffItem;
    private readonly ToolStripMenuItem aiVideoItem;
    private readonly ToolStripMenuItem aiScreenItem;
    private readonly ToolStripMenuItem aiStatusItem;
    private readonly ToolStripMenuItem theaterItem;
    private readonly ToolStripMenuItem stretchItem;
    private readonly ToolStripMenuItem bufferItem;
    private readonly ToolStripMenuItem duplicationItem;
    private readonly ToolStripMenuItem graphicsCaptureItem;
    private readonly ToolStripMenuItem tuningItem;
    private readonly ToolStripMenuItem settingsItem;
    private readonly ToolStripMenuItem logItem;
    private readonly ToolStripMenuItem exitItem;

    public TrayIcon(AppController controller)
    {
        this.controller = controller;

        statusItem = new ToolStripMenuItem { Enabled = false };

        pauseItem = new ToolStripMenuItem("Pause");
        pauseItem.Click += (_, _) => controller.Execute(ShortcutCommand.Pause);

        windowItem = new ToolStripMenuItem("Show in a window");
        windowItem.Click += (_, _) => controller.ToggleWindow();

        duplicationItem = new ToolStripMenuItem("Desktop Duplication");
        duplicationItem.Click += (_, _) => controller.SetCaptureMethod(CaptureMethod.DesktopDuplication);
        graphicsCaptureItem = new ToolStripMenuItem("Windows.Graphics.Capture");
        graphicsCaptureItem.Click += (_, _) => controller.SetCaptureMethod(CaptureMethod.GraphicsCapture);
        captureItem = new ToolStripMenuItem("Capture");
        captureItem.DropDownItems.AddRange([duplicationItem, graphicsCaptureItem]);

        aiStatusItem = new ToolStripMenuItem { Enabled = false };
        aiOffItem = new ToolStripMenuItem("Off");
        aiOffItem.Click += (_, _) => controller.SetAi3D(AiTarget.Off);
        aiVideoItem = new ToolStripMenuItem("Video only");
        aiVideoItem.Click += (_, _) => controller.SetAi3D(AiTarget.Video);
        aiScreenItem = new ToolStripMenuItem("Whole screen");
        aiScreenItem.Click += (_, _) => controller.SetAi3D(AiTarget.WholeScreen);
        aiItem = new ToolStripMenuItem("AI 3D");
        aiItem.DropDownItems.AddRange([aiOffItem, aiVideoItem, aiScreenItem, new ToolStripSeparator(), aiStatusItem]);

        theaterItem = new ToolStripMenuItem("3D theater (video fills the glasses)");
        theaterItem.Click += (_, _) => controller.SetTheater(!controller.Theater);

        stretchItem = new ToolStripMenuItem("Video stretch");
        foreach ((string text, double factor) in new[] { ("None", 1.0), ("×1.1", 1.1), ("×1.2", 1.2), ("×1.25", 1.25), ("×1.33 (4:3 as 16:9)", 1.3333), ("×1.5", 1.5) })
        {
            var item = new ToolStripMenuItem(text) { Tag = factor };
            item.Click += (_, _) => controller.SetStretch(factor);
            stretchItem.DropDownItems.Add(item);
        }

        bufferItem = new ToolStripMenuItem("Video buffer");
        foreach ((string text, double seconds) in new[] { ("Off (live)", 0.0), ("0.5 s", 0.5), ("1 s", 1.0), ("2 s", 2.0) })
        {
            var item = new ToolStripMenuItem(text) { Tag = seconds };
            item.Click += (_, _) => controller.SetBuffer(seconds);
            bufferItem.DropDownItems.Add(item);
        }

        tuningItem = new ToolStripMenuItem("Tuning...");
        tuningItem.Click += (_, _) => controller.Execute(ShortcutCommand.Tuning);

        settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += (_, _) => controller.Execute(ShortcutCommand.Settings);

        logItem = new ToolStripMenuItem("Open log");
        logItem.Click += (_, _) => controller.OpenLog();

        exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => controller.Execute(ShortcutCommand.Exit);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            statusItem,
            new ToolStripSeparator(),
            pauseItem,
            aiItem,
            theaterItem,
            stretchItem,
            bufferItem,
            windowItem,
            captureItem,
            new ToolStripSeparator(),
            tuningItem,
            settingsItem,
            logItem,
            new ToolStripSeparator(),
            exitItem,
        ]);
        menu.Opening += (_, _) => RefreshMenu();

        FluentMenuRenderer.Apply(menu);
        statusItem.Font = new Font(menu.Font, FontStyle.Bold);

        icon = LoadIcon();
        notifyIcon = new NotifyIcon
        {
            Icon = icon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) controller.Execute(ShortcutCommand.Settings);
        };

        Refresh();
    }

    /// <summary>Updates the tooltip. Call after anything that changes the status.</summary>
    public void Refresh()
    {
        // NotifyIcon.Text is limited to 127 characters; keep it to a glance.
        string text = "ImmersiveSBS - " + controller.StatusText + (controller.AudioStatus is string audio ? "; " + audio : "") +
                      (controller.ModelBusy ? "; loading the 3D model" : controller.ModelState == DepthServiceState.Failed ? "; 3D model failed" : "");
        notifyIcon.Text = text.Length > 120 ? text[..117] + "..." : text;
    }

    /// <summary>A balloon for things the user must act on, e.g. the virtual monitor missing.</summary>
    public void Notify(string message)
    {
        notifyIcon.ShowBalloonTip(5000, "ImmersiveSBS", message, ToolTipIcon.Info);
    }

    private void RefreshMenu()
    {
        statusItem.Text = controller.StatusText;
        pauseItem.Checked = controller.Paused;
        windowItem.Checked = controller.WindowShown;
        duplicationItem.Checked = controller.Settings.CaptureMethod == CaptureMethod.DesktopDuplication;
        graphicsCaptureItem.Checked = controller.Settings.CaptureMethod == CaptureMethod.GraphicsCapture;
        theaterItem.Checked = controller.Theater;
        aiOffItem.Checked = controller.Settings.Ai3D == AiTarget.Off;
        aiVideoItem.Checked = controller.Settings.Ai3D == AiTarget.Video;
        aiScreenItem.Checked = controller.Settings.Ai3D == AiTarget.WholeScreen;
        aiItem.ShortcutKeyDisplayString = controller.ShortcutText(ShortcutCommand.ToggleAi3D);
        aiStatusItem.Text = controller.ModelStatus;
        foreach (ToolStripMenuItem item in stretchItem.DropDownItems)
            item.Checked = Math.Abs((double)item.Tag! - controller.Settings.VideoStretch) < 0.01;
        foreach (ToolStripMenuItem item in bufferItem.DropDownItems)
            item.Checked = Math.Abs((double)item.Tag! - controller.Settings.BufferSeconds) < 0.01;

        // Shortcuts are user-configurable, so read them each time.
        pauseItem.ShortcutKeyDisplayString = controller.ShortcutText(ShortcutCommand.Pause);
        settingsItem.ShortcutKeyDisplayString = controller.ShortcutText(ShortcutCommand.Settings);
        tuningItem.ShortcutKeyDisplayString = controller.ShortcutText(ShortcutCommand.Tuning);
        exitItem.ShortcutKeyDisplayString = controller.ShortcutText(ShortcutCommand.Exit);
    }

    private static Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/ImmersiveSBS;component/ImmersiveSBS.ico"));
            if (info != null)
            {
                using (info.Stream)
                {
                    return new Icon(info.Stream, SystemInformation.SmallIconSize);
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the exe's embedded icon.
        }

        return (Environment.ProcessPath != null ? Icon.ExtractAssociatedIcon(Environment.ProcessPath) : null) ?? SystemIcons.Application;
    }

    public void Dispose()
    {
        // Hide first, or Windows leaves a ghost icon until the mouse passes over it.
        notifyIcon.Visible = false;
        notifyIcon.ContextMenuStrip?.Dispose();
        notifyIcon.Dispose();
        icon.Dispose();
    }
}
