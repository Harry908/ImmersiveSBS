using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ImmersiveSBS.Theater;

/// <summary>
/// The 3D theater's player window: a borderless window over the whole virtual monitor that the
/// capture does not see (<c>WDA_EXCLUDEFROMCAPTURE</c>), so the page's video keeps being captured
/// underneath while the render thread draws it in 3D in the window's place. Every click, double
/// click and key lands on this window instead of the page: before it, a click in the theater went
/// through to the player under it, and a double click turned its video full screen, which resized
/// and cropped the theater's picture.
/// </summary>
/// <remarks>
/// A tool window: Chromium does not count tool windows when it decides whether a browser window is
/// covered, so the video under it keeps playing and painting (a covered window stops painting).
/// </remarks>
public sealed class TheaterWindow : Form
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int WS_EX_TOOLWINDOW = 0x80;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private readonly IntPtr previous = GetForegroundWindow();
    private readonly Rectangle target;

    /// <summary>A key or click the player handles; raised on the UI thread.</summary>
    public event Action<TheaterKey>? Command;

    public TheaterWindow(Rectangle bounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        Text = "ImmersiveSBS 3D theater";
        KeyPreview = true;
        Bounds = target = bounds;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams p = base.CreateParams;
            p.ExStyle |= WS_EX_TOOLWINDOW;
            return p;
        }
    }

    /// <summary>Whether the capture leaves the window out; without that the theater would show it, not the video.</summary>
    public bool Excluded { get; private set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Excluded = SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
        if (!Excluded) Log.Write("Theater: the player window cannot be left out of the capture, error " + Marshal.GetLastPInvokeError() + ".");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Physical px (per-monitor DPI): set again in case a DPI change on the way moved it.
        if (Bounds != target) Bounds = target;
        Activate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        TheaterKey? key = e.KeyCode switch
        {
            Keys.Escape => TheaterKey.Exit,
            Keys.Space or Keys.K or Keys.MediaPlayPause => TheaterKey.PlayPause,
            Keys.Left or Keys.J => TheaterKey.Back,
            Keys.Right or Keys.L => TheaterKey.Forward,
            _ => null,
        };
        if (key is TheaterKey k)
        {
            e.Handled = e.SuppressKeyPress = true;
            Command?.Invoke(k);
        }
        base.OnKeyDown(e);
    }

    /// <summary>Clicks on the video (not on a button: the mouse hook takes those) are swallowed here; a double click leaves.</summary>
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) Command?.Invoke(TheaterKey.Exit);
    }

    /// <summary>Closes the window and gives the window that had the keyboard before (the browser) back its focus.</summary>
    public void CloseAndRestore()
    {
        bool hadFocus = GetForegroundWindow() == Handle;
        Close();
        Dispose();
        if (hadFocus && previous != IntPtr.Zero) SetForegroundWindow(previous);
    }
}

/// <summary>What a key or a double click in the theater's player window asks for.</summary>
public enum TheaterKey
{
    Exit,
    PlayPause,
    Back,
    Forward,
}
