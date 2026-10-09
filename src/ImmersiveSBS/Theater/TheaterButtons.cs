using System.Drawing;

namespace ImmersiveSBS.Theater;

/// <summary>What an on-screen button in the glasses does.</summary>
public enum TheaterAction
{
    /// <summary>Fill both eyes with the video.</summary>
    EnterTheater,

    /// <summary>Back to the desktop.</summary>
    ExitTheater,

    /// <summary>The media Play/Pause key, which browsers obey.</summary>
    PlayPause,

    /// <summary>Opens or closes the 3D settings panel in the theater.</summary>
    Settings,

    /// <summary>The panel's background and value labels: the click is caught and does nothing.</summary>
    Panel,

    DepthLess,
    DepthMore,
    PlaneBack,
    PlaneForward,
    ModelPrevious,
    ModelNext,
    SizePrevious,
    SizeNext,
    BufferLess,
    BufferMore,
    ToggleEdgeSnap,
    ToggleCaptions,
}

/// <summary>
/// The on-screen buttons currently shown in the glasses, as rectangles on the desktop (the
/// virtual monitor's part of it) where a click must land to press them. Written by the render
/// thread every frame, read by the mouse hook thread on every click.
/// </summary>
public sealed class TheaterButtons
{
    private readonly object gate = new();
    private (TheaterAction Action, Rectangle Desktop)[] buttons = [];

    public void Set((TheaterAction Action, Rectangle Desktop)[] current)
    {
        lock (gate) buttons = current;
    }

    public void Clear() => Set([]);

    /// <summary>The button under a desktop point, or null.</summary>
    public TheaterAction? Hit(Point desktop)
    {
        lock (gate)
        {
            foreach (var (action, rect) in buttons)
                if (rect.Contains(desktop)) return action;
            return null;
        }
    }
}
