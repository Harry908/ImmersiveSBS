// Ported from DesktopSbS: DesktopSbS/View/TrayIcon.cs
using System.Drawing;
using System.Windows.Forms;
using ImmersiveSBS.Capture;
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
    private readonly ToolStripMenuItem duplicationItem;
    private readonly ToolStripMenuItem graphicsCaptureItem;
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
            windowItem,
            captureItem,
            new ToolStripSeparator(),
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
        string text = "ImmersiveSBS - " + controller.StatusText;
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

        // Shortcuts are user-configurable, so read them each time.
        pauseItem.ShortcutKeyDisplayString = controller.ShortcutText(ShortcutCommand.Pause);
        settingsItem.ShortcutKeyDisplayString = controller.ShortcutText(ShortcutCommand.Settings);
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
