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
    public static PageBoxes ToModel(IEnumerable<Rectangle> overlays, IEnumerable<Rectangle> captions, Rectangle region, Size model) =>
        new(ToModel(overlays, region, model), ToModel(captions, region, model));

    /// <summary><paramref name="boxes"/> (source px) as model px, every model pixel each touches; boxes outside <paramref name="region"/> are left out.</summary>
    public static List<Rectangle> ToModel(IEnumerable<Rectangle> boxes, Rectangle region, Size model)
    {
        var result = new List<Rectangle>();
        if (region.Width <= 0 || region.Height <= 0) return result;
        double sx = (double)model.Width / region.Width, sy = (double)model.Height / region.Height;
        foreach (Rectangle b in boxes)
        {
            Rectangle r = Rectangle.Intersect(b, region);
            if (r.Width <= 0 || r.Height <= 0) continue;
            int x0 = (int)Math.Floor((r.Left - region.Left) * sx), y0 = (int)Math.Floor((r.Top - region.Top) * sy);
            int x1 = (int)Math.Ceiling((r.Right - region.Left) * sx), y1 = (int)Math.Ceiling((r.Bottom - region.Top) * sy);
            result.Add(Rectangle.Intersect(Rectangle.FromLTRB(x0, y0, x1, y1), new Rectangle(Point.Empty, model)));
        }
        return result;
    }

    /// <summary>
    /// Sets <paramref name="nearness"/> (model px, <paramref name="width"/> wide) to
    /// <paramref name="plane"/> under <paramref name="boxes"/> grown by <paramref name="margin"/>
    /// px: the screen plane, where the warp moves nothing, so the player's controls stay where
    /// the flat copy the renderer draws over them is; the margin covers the upsample's reach.
    /// </summary>
    public static void Flatten(float[] nearness, int width, int height, IReadOnlyList<Rectangle> boxes, int margin, float plane)
    {
        var frame = new Rectangle(0, 0, width, height);
        foreach (Rectangle b in boxes)
        {
            Rectangle r = Rectangle.Intersect(Rectangle.Inflate(b, margin, margin), frame);
            for (int y = r.Top; y < r.Bottom; y++)
                Array.Fill(nearness, plane, y * width + r.Left, r.Width);
        }
    }
}

/// <summary>
/// The player's boxes for the depth thread: those on the screen when a frame was captured, in
/// model px. Read straight from <see cref="PageVideo"/> as the link writes them, so a caption
/// is held flat from its first frame (through the controller's timer, its box came up to a
/// quarter second late, after its first frames had their depth).
/// </summary>
/// <param name="clockOrigin">The Stopwatch ticks of second 0 of the frames' times.</param>
public sealed class PageBoxSource(PageVideo page, Rectangle region, Size model, long clockOrigin)
{
    private PageVideo.BoxSet? lastSet;
    private PageBoxes? lastBoxes;

    public PageVideo Page { get; } = page;
    public Rectangle Region { get; } = region;
    public Size Model { get; } = model;

    /// <summary>The boxes of a frame captured at <paramref name="seconds"/> (the frames' clock); depth thread only.</summary>
    public PageBoxes? At(double seconds)
    {
        if (Page.BoxesAt(clockOrigin + (long)(seconds * Stopwatch.Frequency)) is not { } set) return null;
        if (!ReferenceEquals(set, lastSet))
        {
            lastSet = set;
            lastBoxes = PageBoxes.ToModel(set.Overlays, set.Captions, Region, Model);
        }
        return lastBoxes;
    }
}

/// <summary>
/// The theater's video as its player reports it (improvement-plan.md §5): each frame it draws
/// (its counter, its time in the video, when it is shown, already on the app's clock), whether it
/// is paused or seeking, and the frames it sends for the depth model (<see cref="Pictures"/>). The
/// browser link writes it as messages arrive, the render thread reads it every frame: frame events
/// come 24–60 times a second and must not wait for the controller's tick. Thread-safe.
/// </summary>
public sealed class PageVideo : IDisposable
{
    /// <param name="Presented">The page's count of frames drawn.</param>
    /// <param name="MediaTime">The frame's time in the video, seconds.</param>
    /// <param name="DisplayTicks">When it was to be shown, Stopwatch ticks of this process.</param>
    /// <param name="Line">The player's count of jumps in the video (seeks, a loop's return), as on its <see cref="Pictures"/>.</param>
    public readonly record struct FrameEvent(long Presented, double MediaTime, long DisplayTicks, int Line = 0);

    private const int Ring = 32;
    private readonly object gate = new();
    private readonly FrameEvent[] frames = new FrameEvent[Ring];
    private int count, head;
    private long lastMessage;
    private bool paused, seeking, live, sendsPictures;
    private int seeks;
    private double clockError = double.NaN;

    /// <summary>Page data older than this is no page data (improvement-plan.md §3.5 rule 2).</summary>
    public static readonly long FreshTicks = Stopwatch.Frequency;

    public PageVideo(string mediaId)
    {
        MediaId = mediaId;
    }

    public string MediaId { get; }

    /// <summary>The frames the player sends as the video plays, and their depth.</summary>
    public PagePictures Pictures { get; } = new();

    public void Dispose() => Pictures.Dispose();

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
    /// <param name="live">The video has no end (a live stream).</param>
    /// <param name="sendsPictures">The player sends its frames for the depth model (<see cref="Pictures"/>).</param>
    public void SetState(bool paused, bool seeking, int seeks, bool live = false, bool sendsPictures = false)
    {
        lock (gate)
        {
            this.paused = paused;
            this.seeking = seeking;
            this.seeks = seeks;
            this.live = live;
            this.sendsPictures = sendsPictures;
            lastMessage = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// The player sends its frames for the depth model and shows each one itself, late with the
    /// sound: the depth comes from <see cref="Pictures"/>, and the screen is shown as it is.
    /// </summary>
    public bool SendsPictures
    {
        get { lock (gate) return sendsPictures; }
    }

    /// <summary>The video has no end (a live stream): it is not buffered.</summary>
    public bool Live
    {
        get { lock (gate) return live; }
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

    /// <summary>The player's boxes from <see cref="FromTicks"/> on, screen px of the captured monitor.</summary>
    /// <param name="Overlays">Everything but the captions (the bar, the panel, the spinner), drawn flat.</param>
    public sealed record BoxSet(long FromTicks, IReadOnlyList<Rectangle> Overlays, IReadOnlyList<Rectangle> Captions);

    private readonly BoxSet?[] boxSets = new BoxSet?[16];
    private int boxHead, boxCount;

    /// <summary>Records the player's boxes as they are from <paramref name="fromTicks"/> on (never before the previous set).</summary>
    public void AddBoxes(long fromTicks, IReadOnlyList<Rectangle> overlays, IReadOnlyList<Rectangle> captions)
    {
        lock (gate)
        {
            if (boxCount > 0) fromTicks = Math.Max(fromTicks, boxSets[(boxHead + boxSets.Length - 1) % boxSets.Length]!.FromTicks);
            boxSets[boxHead] = new BoxSet(fromTicks, overlays, captions);
            boxHead = (boxHead + 1) % boxSets.Length;
            boxCount = Math.Min(boxCount + 1, boxSets.Length);
        }
    }

    /// <summary>The boxes on the screen at <paramref name="ticks"/>: the newest set from then or before; null before any.</summary>
    public BoxSet? BoxesAt(long ticks)
    {
        lock (gate)
        {
            for (int k = 1; k <= boxCount; k++)
            {
                BoxSet set = boxSets[(boxHead + boxSets.Length - k) % boxSets.Length]!;
                if (set.FromTicks <= ticks) return set;
            }
            return null;
        }
    }

    /// <summary>Frames a capture may be ahead of the newest report: further, the page has stalled.</summary>
    public const int MaxFramesAhead = 3;

    /// <summary>How far a frame's display time may be past a capture's and still be on it: half a 60 Hz refresh, ticks.</summary>
    public static readonly long SameRefresh = Stopwatch.Frequency / 120;

    /// <summary>
    /// The frame a capture made at <paramref name="captureTicks"/> shows: the newest whose display
    /// time is no later than that plus <see cref="SameRefresh"/> (the capture and the page's
    /// estimate both fall on the same refresh, give or take one). A capture often arrives before
    /// the report of the frame it shows (the report goes through the browser's worker and the
    /// socket): past the newest report while playing, the frames that followed are counted on at
    /// the video's cadence, up to <see cref="MaxFramesAhead"/>; not with <paramref name="countOn"/>
    /// false (the player holds a frame while its delayed sound fills, the video playing on).
    /// </summary>
    public bool TryFrameAt(long captureTicks, out FrameEvent frame, bool countOn = true)
    {
        long slackTicks = SameRefresh;
        lock (gate)
        {
            for (int k = 1; k <= count; k++)
            {
                FrameEvent e = frames[(head + Ring - k) % Ring];
                if (e.DisplayTicks <= captureTicks + slackTicks)
                {
                    frame = k == 1 && !paused && countOn ? Extrapolate(e, captureTicks + slackTicks) : e;
                    return true;
                }
            }
            frame = default;
            return false;
        }
    }

    /// <summary>The frame after <paramref name="newest"/> shown by <paramref name="ticks"/>, at the recent cadence. Under the lock.</summary>
    private FrameEvent Extrapolate(FrameEvent newest, long ticks)
    {
        if (count < 4) return newest;
        FrameEvent oldest = frames[(head + Ring - count) % Ring];
        long n = newest.Presented - oldest.Presented;
        if (n <= 0 || newest.DisplayTicks <= oldest.DisplayTicks) return newest;
        double period = (double)(newest.DisplayTicks - oldest.DisplayTicks) / n;
        long ahead = Math.Min((long)((ticks - newest.DisplayTicks) / period), MaxFramesAhead);
        if (ahead <= 0) return newest;
        double mediaStep = (newest.MediaTime - oldest.MediaTime) / n;
        return new FrameEvent(newest.Presented + ahead, newest.MediaTime + ahead * mediaStep,
            newest.DisplayTicks + (long)(ahead * period), newest.Line);
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
