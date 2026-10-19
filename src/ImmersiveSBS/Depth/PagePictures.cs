using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Channels;

namespace ImmersiveSBS.Depth;

/// <summary>
/// The theater's frames as its player sends them: every frame the video presents, as it plays, at
/// the depth model's input size (JPEG). The player shows each one later, with the sound (the
/// buffer, matched timing), and draws its controls over it live, so the screen the app captures is
/// always the finished picture and is shown as it is. The depth model runs on these frames instead,
/// before they are on the screen, and the render thread takes the depth of the frame shown by its
/// line (the player's count of jumps in the video: seeks, a loop's return) and its time in the
/// video. The app used to delay the captured screen itself, where the page had already merged the
/// video and its controls: those came seconds late, and patched in from the live screen they showed
/// the video at another time.
/// </summary>
/// <remarks>
/// Thread-safe: the link adds frames, one task decodes them and hands them to the model in order,
/// the render thread files and reads the depth.
/// </remarks>
public sealed class PagePictures : IDisposable
{
    /// <summary>Depth frames kept at most (4 s at 60 fps).</summary>
    public const int MaxKept = 240;

    /// <summary>The model's frame ids for these frames start here, apart from the captured frames'.</summary>
    public const long FirstId = 1L << 40;

    private readonly Channel<Picture> queue = Channel.CreateBounded<Picture>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly object gate = new();
    /// <summary>Handed to the model, depth not back yet: id → which frame, and when it was handed over.</summary>
    private readonly Dictionary<long, (Key Key, long Ticks)> pending = [];
    /// <summary>Depth by frame, sorted by line, then time in the video.</summary>
    private readonly List<(Key Key, DepthFrame Depth)> kept = [];
    private readonly Stack<DepthFrame> pool = new();
    private readonly long clockOrigin = Stopwatch.GetTimestamp();
    private long nextId = FirstId;
    private int lastLine = int.MinValue;
    private long lastPictureTicks;
    private float[] input = [];
    private volatile DepthService? target;

    /// <param name="Line">The player's count of jumps in the video; the time in the video is comparable only within one.</param>
    public readonly record struct Key(int Line, double MediaTime) : IComparable<Key>
    {
        public int CompareTo(Key other) => Line != other.Line ? Line.CompareTo(other.Line) : MediaTime.CompareTo(other.MediaTime);
    }

    private sealed record Picture(Key Key, int Width, int Height, byte[] Jpeg, long Received);

    public PagePictures()
    {
        _ = Task.Run(DecodeLoopAsync);
    }

    /// <summary>The model the frames go to (render thread, each frame); null: none, they are dropped.</summary>
    public DepthService? Target
    {
        get => target;
        set => target = value;
    }

    /// <summary>A frame arrived within the last half second.</summary>
    public bool Fresh(long now) => now - Volatile.Read(ref lastPictureTicks) < Stopwatch.Frequency / 2;

    /// <summary>A frame from the page (the link's thread): decoded and handed to the model in order, the oldest dropped when behind.</summary>
    public void Add(int line, double mediaTime, int width, int height, byte[] jpeg, long receivedTicks)
    {
        Volatile.Write(ref lastPictureTicks, receivedTicks);
        queue.Writer.TryWrite(new Picture(new Key(line, mediaTime), width, height, jpeg, receivedTicks));
    }

    private async Task DecodeLoopAsync()
    {
        await foreach (Picture p in queue.Reader.ReadAllAsync())
        {
            try
            {
                if (target is not { State: DepthServiceState.Ready } service) continue;
                DepthModel model = service.Model;
                if (p.Width != model.Width || p.Height != model.Height) continue;   // sent for another model
                if (!Decode(p.Jpeg, model.Width, model.Height, ref input)) continue;
                // A jump in the video: nothing is smoothed across it.
                if (p.Key.Line != lastLine)
                {
                    if (lastLine != int.MinValue) service.ResetHistory();
                    lastLine = p.Key.Line;
                }
                long id;
                lock (gate)
                {
                    id = nextId++;
                    pending[id] = (p.Key, Stopwatch.GetTimestamp());
                    // Ids the model dropped (a newer frame replaced one it had not started) never come back.
                    if (pending.Count > 64) pending.Remove(pending.Keys.Min());
                }
                service.Submit(input, id, (p.Received - clockOrigin) / (double)Stopwatch.Frequency);
            }
            catch (Exception e)
            {
                Log.Write("Browser: a theater frame could not be read: " + e.Message);
            }
        }
    }

    /// <summary>A JPEG of the model's size into its input: planar RGB with ImageNet normalisation, as <see cref="StereoSynth"/> makes it from the screen.</summary>
    public static unsafe bool Decode(byte[] jpeg, int width, int height, ref float[] input)
    {
        using var stream = new MemoryStream(jpeg, writable: false);
        using var bitmap = new Bitmap(stream);
        if (bitmap.Width != width || bitmap.Height != height) return false;
        int plane = width * height;
        if (input.Length != 3 * plane) input = new float[3 * plane];
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            float[] into = input;
            const float r0 = 0.485f * 255, g0 = 0.456f * 255, b0 = 0.406f * 255;
            const float rs = 1 / (0.229f * 255), gs = 1 / (0.224f * 255), bs = 1 / (0.225f * 255);
            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;
            for (int y = 0; y < height; y++)
            {
                byte* row = scan0 + (long)y * stride;
                int i = y * width;
                for (int x = 0; x < width; x++, i++, row += 3)
                {
                    // GDI+ rows are blue, green, red.
                    into[i] = (row[2] - r0) * rs;
                    into[plane + i] = (row[1] - g0) * gs;
                    into[2 * plane + i] = (row[0] - b0) * bs;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return true;
    }

    /// <summary>
    /// Render thread: files every depth frame the model has finished for these frames; returns how
    /// many, with how long each took from being handed over (less the look-ahead's hold), ms.
    /// </summary>
    public int TakeDepth(DepthService service, List<double>? agesMs = null)
    {
        int taken = 0;
        DepthFrame? frame = pool.Count > 0 ? pool.Pop() : null;
        while (service.TryTake(ref frame))
        {
            (Key Key, long Ticks) found;
            bool known;
            lock (gate)
            {
                known = pending.Remove(frame!.FrameId, out found);
            }
            if (!known)
            {
                // Depth of a captured frame, or of one handed over before a change.
                continue;
            }
            taken++;
            agesMs?.Add(Stopwatch.GetElapsedTime(found.Ticks).TotalMilliseconds - frame.HeldMs);
            File(found.Key.Line, found.Key.MediaTime, frame);
            frame = pool.Count > 0 ? pool.Pop() : null;
        }
        if (frame != null) pool.Push(frame);
        return taken;
    }

    /// <summary>Files the depth of a frame (line, time in the video), as <see cref="TakeDepth"/> does for the model's; render thread.</summary>
    public void File(int line, double mediaTime, DepthFrame frame)
    {
        var key = new Key(line, mediaTime);
        int i = kept.Count;
        while (i > 0 && kept[i - 1].Key.CompareTo(key) > 0) i--;
        if (i > 0 && kept[i - 1].Key.Equals(key))
        {
            Recycle(kept[i - 1].Depth);
            kept[i - 1] = (key, frame);
            return;
        }
        kept.Insert(i, (key, frame));
        while (kept.Count > MaxKept)
        {
            Recycle(kept[0].Depth);
            kept.RemoveAt(0);
        }
    }

    private void Recycle(DepthFrame frame)
    {
        if (pool.Count < 8) pool.Push(frame);
    }

    /// <summary>Depth frames filed; render thread.</summary>
    public int Count => kept.Count;

    /// <summary>
    /// The nearness of the frame shown (line, time in the video) into <paramref name="into"/>
    /// (render thread): its own depth; else depth interpolated between the frames on either side
    /// (the model skips frames when it is behind) within <paramref name="spanSeconds"/>; else the
    /// newest before it within half a second (live timing: the shown frame's depth is not done
    /// yet). <paramref name="lagMs"/> is how far in the video the depth used is from the frame, ms.
    /// Depth of frames well before it is let go.
    /// </summary>
    public bool Nearness(int line, double mediaTime, double spanSeconds, float[] into, out double lagMs)
    {
        lagMs = double.NaN;
        // Earlier lines are past; frames of a later one may already be here (a seek, before it shows).
        int drop = 0;
        while (drop < kept.Count && (kept[drop].Key.Line < line
                                     || kept[drop].Key.Line == line && kept[drop].Key.MediaTime < mediaTime - 1)) drop++;
        for (int k = 0; k < drop; k++) Recycle(kept[k].Depth);
        kept.RemoveRange(0, drop);

        int before = -1, after = -1;
        for (int k = 0; k < kept.Count && kept[k].Key.Line == line; k++)
        {
            double t = kept[k].Key.MediaTime;
            if (t <= mediaTime) before = k;
            else
            {
                after = k;
                break;
            }
        }
        const double Same = 0.002;
        if (before >= 0 && mediaTime - kept[before].Key.MediaTime <= Same) return Copy(before, 0, into, out lagMs);
        if (after >= 0 && kept[after].Key.MediaTime - mediaTime <= Same) return Copy(after, 0, into, out lagMs);
        if (before >= 0 && after >= 0 && kept[after].Key.MediaTime - kept[before].Key.MediaTime <= spanSeconds)
        {
            double t0 = kept[before].Key.MediaTime, t1 = kept[after].Key.MediaTime;
            float[] a = kept[before].Depth.Nearness, b = kept[after].Depth.Nearness;
            if (a.Length != into.Length || b.Length != into.Length) return false;
            NearnessBlend.Lerp(a, b, (float)((mediaTime - t0) / (t1 - t0)), into);
            lagMs = Math.Min(mediaTime - t0, t1 - mediaTime) * 1000;
            return true;
        }
        if (before >= 0 && mediaTime - kept[before].Key.MediaTime <= 0.5) return Copy(before, mediaTime - kept[before].Key.MediaTime, into, out lagMs);
        return false;
    }

    private bool Copy(int k, double lagSeconds, float[] into, out double lagMs)
    {
        lagMs = lagSeconds * 1000;
        float[] from = kept[k].Depth.Nearness;
        if (from.Length != into.Length) return false;
        Array.Copy(from, into, into.Length);
        return true;
    }

    /// <summary>Forgets every frame and its depth (another model, another region); render thread.</summary>
    public void Clear()
    {
        foreach (var (_, depth) in kept) Recycle(depth);
        kept.Clear();
        lock (gate) pending.Clear();
    }

    public void Dispose() => queue.Writer.TryComplete();
}
