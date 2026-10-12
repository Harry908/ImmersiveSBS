using System.Diagnostics;
using System.Drawing;

namespace ImmersiveSBS.Depth;

/// <summary>
/// What the theater's player draws over the video, in model px (improvement-plan.md §7.4).
/// </summary>
/// <param name="Overlays">Player controls, titles, logos: held at the screen plane.</param>
/// <param name="Captions">The page's captions: these replace the subtitle detection while there are any.</param>
public sealed record PageBoxes(IReadOnlyList<Rectangle> Overlays, IReadOnlyList<Rectangle> Captions)
{
    /// <summary>
    /// <paramref name="overlays"/> and <paramref name="captions"/> (source px) as model px of a
    /// model input made from <paramref name="region"/>; boxes outside it are left out.
    /// </summary>
    public static PageBoxes ToModel(IEnumerable<Rectangle> overlays, IEnumerable<Rectangle> captions, Rectangle region, Size model)
    {
        List<Rectangle> Map(IEnumerable<Rectangle> boxes)
        {
            var result = new List<Rectangle>();
            if (region.Width <= 0 || region.Height <= 0) return result;
            double sx = (double)model.Width / region.Width, sy = (double)model.Height / region.Height;
            foreach (Rectangle b in boxes)
            {
                Rectangle r = Rectangle.Intersect(b, region);
                if (r.Width <= 0 || r.Height <= 0) continue;
                // Every model pixel the box touches.
                int x0 = (int)Math.Floor((r.Left - region.Left) * sx), y0 = (int)Math.Floor((r.Top - region.Top) * sy);
                int x1 = (int)Math.Ceiling((r.Right - region.Left) * sx), y1 = (int)Math.Ceiling((r.Bottom - region.Top) * sy);
                result.Add(Rectangle.Intersect(Rectangle.FromLTRB(x0, y0, x1, y1), new Rectangle(Point.Empty, model)));
            }
            return result;
        }
        return new PageBoxes(Map(overlays), Map(captions));
    }
}

/// <summary>
/// The theater's video as its player reports it (improvement-plan.md §5): each frame it draws
/// (its counter, its time in the video, when it is shown, already on the app's clock), and
/// whether it is paused or seeking. The browser link
/// writes it as messages arrive, the render thread reads it every frame: frame events come 24–60
/// times a second and must not wait for the controller's tick. Thread-safe.
/// </summary>
public sealed class PageVideo
{
    /// <param name="Presented">The page's count of presented frames (requestVideoFrameCallback).</param>
    /// <param name="MediaTime">The frame's time in the video, seconds.</param>
    /// <param name="DisplayTicks">When it was to be shown, Stopwatch ticks of this process.</param>
    public readonly record struct FrameEvent(long Presented, double MediaTime, long DisplayTicks);

    private const int Ring = 32;
    private readonly object gate = new();
    private readonly FrameEvent[] frames = new FrameEvent[Ring];
    private int count, head;
    private long lastMessage;
    private bool paused, seeking;
    private int seeks;
    private double clockError = double.NaN;

    /// <summary>Page data older than this is no page data (improvement-plan.md §3.5 rule 2).</summary>
    public static readonly long FreshTicks = Stopwatch.Frequency;

    public PageVideo(string mediaId)
    {
        MediaId = mediaId;
    }

    public string MediaId { get; }

    /// <summary>Records a frame event; older or repeated ones are ignored.</summary>
    public void AddFrame(FrameEvent e)
    {
        lock (gate)
        {
            if (count > 0 && e.Presented <= frames[(head + Ring - 1) % Ring].Presented)
            {
                // The page started counting again (a new source): start over.
                if (e.Presented >= frames[(head + Ring - 1) % Ring].Presented - 1) return;
                count = 0;
            }
            frames[head] = e;
            head = (head + 1) % Ring;
            count = Math.Min(count + 1, Ring);
            lastMessage = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Records the playback state.</summary>
    public void SetState(bool paused, bool seeking, int seeks)
    {
        lock (gate)
        {
            this.paused = paused;
            this.seeking = seeking;
            this.seeks = seeks;
            lastMessage = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>The page's clock sync: at most this far off, ms (half the best round trip).</summary>
    public double ClockErrorMs
    {
        get { lock (gate) return clockError; }
        set { lock (gate) clockError = value; }
    }

    /// <summary>A message about this video arrived within <see cref="FreshTicks"/>.</summary>
    public bool Fresh(long now)
    {
        lock (gate) return lastMessage != 0 && now - lastMessage < FreshTicks;
    }

    public bool Paused
    {
        get { lock (gate) return paused; }
    }

    public bool Seeking
    {
        get { lock (gate) return seeking; }
    }

    /// <summary>Seeks so far; a change is a jump in the video.</summary>
    public int Seeks
    {
        get { lock (gate) return seeks; }
    }

    /// <summary>The newest frame event, if any.</summary>
    public FrameEvent? Latest
    {
        get
        {
            lock (gate) return count == 0 ? null : frames[(head + Ring - 1) % Ring];
        }
    }

    /// <summary>
    /// The frame a capture made at <paramref name="captureTicks"/> shows: the newest whose display
    /// time is no later than that plus <paramref name="slackTicks"/> (the capture and the page's
    /// estimate both fall on the same refresh, give or take one).
    /// </summary>
    public bool TryFrameAt(long captureTicks, long slackTicks, out FrameEvent frame)
    {
        lock (gate)
        {
            for (int k = 1; k <= count; k++)
            {
                FrameEvent e = frames[(head + Ring - k) % Ring];
                if (e.DisplayTicks <= captureTicks + slackTicks)
                {
                    frame = e;
                    return true;
                }
            }
            frame = default;
            return false;
        }
    }

    /// <summary>
    /// The video's frame rate from the recent events: video time over frames presented, fps;
    /// 0 while unknown (fewer than 4 events, or paused).
    /// </summary>
    public double FrameRate
    {
        get
        {
            lock (gate)
            {
                if (count < 4) return 0;
                FrameEvent newest = frames[(head + Ring - 1) % Ring], oldest = frames[(head + Ring - count) % Ring];
                double seconds = newest.MediaTime - oldest.MediaTime;
                long n = newest.Presented - oldest.Presented;
                return seconds > 0 && n > 0 ? n / seconds : 0;
            }
        }
    }
}
