using System.Drawing;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Size = System.Drawing.Size;

namespace ImmersiveSBS.Depth;

/// <summary>
/// Holds the video region's recent frames on the GPU so the picture can be shown a fixed delay
/// late, in step with the delayed sound (video-3d-plan.md §4.4, P5). Only the region is held,
/// never the desktop, so the cursor and typing stay live. Each frame keeps the crop it was taken
/// from and the depth computed for it when that arrives, so the frame shown can use depth from
/// frames on both sides of it, and a region that moves or resizes (the player's controls
/// appearing, the player going full screen) does not empty the buffer: the first version did,
/// and the picture then stood still for the whole delay while the sound went on. The
/// GPU memory is capped: when the cap cannot hold the whole delay at the frame rate, fewer frames
/// are kept (a lower frame rate for the delayed video) rather than a shorter delay. Render
/// thread only.
/// </summary>
public sealed class VideoDelayBuffer : IDisposable
{
    public sealed class Frame
    {
        internal ID3D11Texture2D? Texture;

        public long Time { get; internal set; }

        public long Id { get; internal set; }

        /// <summary>Where on the source the frame was cropped from, px.</summary>
        public Rectangle Region { get; internal set; }

        /// <summary>The video's own time of the frame, seconds, when the page reports it; NaN otherwise.</summary>
        public double MediaTime { get; internal set; } = double.NaN;

        /// <summary>Counts the video's jumps (seeks): depth is never blended across one.</summary>
        public int Segment { get; internal set; }

        /// <summary>Nearness at model resolution, once the depth for this frame has arrived.</summary>
        public float[]? Nearness { get; internal set; }

        public ID3D11Texture2D TextureOrThrow => Texture ?? throw new InvalidOperationException("Empty frame.");
    }

    private readonly ID3D11Device device;
    private readonly List<Frame> frames = []; // oldest first
    private readonly Stack<ID3D11Texture2D> free = new(); // textures of the current size
    /// <summary>Nearness maps of recycled frames, for the next frames' depth: at model size they are large objects.</summary>
    private readonly Stack<float[]> freeNearness = new();
    private Size size;
    private long lastPush = long.MinValue;

    /// <summary>Spare textures kept beyond this are released: a shorter delay needs fewer.</summary>
    private const int MaxFree = 8;

    public VideoDelayBuffer(ID3D11Device device)
    {
        this.device = device;
    }

    /// <summary>GPU memory the held frames may use.</summary>
    public long MaxBytes { get; set; } = DefaultMaxBytes;

    public const long DefaultMaxBytes = 600L * 1024 * 1024;

    /// <summary>Frames skipped to stay within <see cref="MaxBytes"/> since the last <see cref="TakeSkipped"/>.</summary>
    public int Skipped { get; private set; }

    /// <summary>Returns <see cref="Skipped"/> and starts counting again (the statistics' window).</summary>
    public int TakeSkipped()
    {
        int n = Skipped;
        Skipped = 0;
        return n;
    }

    public int Count => frames.Count;

    /// <summary>
    /// Forgets the held frames (the delay stopped) and lets their GPU memory go: up to
    /// <see cref="MaxBytes"/> of spare textures stayed allocated through live timing before.
    /// </summary>
    public void Clear()
    {
        while (frames.Count > 0) Recycle(0);
        while (free.Count > 0) free.Pop().Dispose();
        freeNearness.Clear();
        lastPush = long.MinValue;
    }

    /// <summary>
    /// Copies <paramref name="region"/> of <paramref name="source"/> in, stamped with its capture
    /// time (Stopwatch ticks). Returns false when the frame was skipped to stay within memory.
    /// </summary>
    /// <param name="frameRate">The video's frame rate, fps, for how many frames the delay needs; 0 = unknown (60).</param>
    public bool Push(ID3D11Texture2D source, Rectangle region, long time, long id, TimeSpan delay, long ticksPerSecond,
        double mediaTime = double.NaN, int segment = 0, double frameRate = 0)
    {
        if (region.Size != size)
        {
            // Held frames keep their textures; spare ones of the old size are no use any more.
            while (free.Count > 0) free.Pop().Dispose();
            size = region.Size;
        }

        // Enough frames for the delay at the video's frame rate, plus a margin, within the memory
        // cap. Counting 60 fps for every video made a 2 s buffer of a 1080p 30 fps video skip
        // frames that fitted.
        double fps = frameRate is >= 10 and <= 240 ? frameRate : 60;
        long frameBytes = (long)size.Width * size.Height * 4;
        int capacity = (int)Math.Max(4, Math.Min(Math.Ceiling(delay.TotalSeconds * fps) + 8, MaxBytes / frameBytes));
        double minInterval = delay.TotalSeconds / Math.Max(1, capacity - 4) * ticksPerSecond;
        if (capacity < delay.TotalSeconds * fps && lastPush != long.MinValue && time - lastPush < minInterval)
        {
            Skipped++;
            return false;
        }

        // Frames older than the delay plus a margin are no longer needed.
        long keep = time - (long)((delay.TotalSeconds + 0.5) * ticksPerSecond);
        while (frames.Count > 1 && frames[1].Time < keep) Recycle(0);
        while (frames.Count >= capacity) Recycle(0);
        while (free.Count > MaxFree) free.Pop().Dispose();

        ID3D11Texture2D texture = free.Count > 0 ? free.Pop() : device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)size.Width,
            Height = (uint)size.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = source.Description.Format,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        device.ImmediateContext.CopySubresourceRegion(texture, 0, 0, 0, 0, source, 0,
            new Box(region.Left, region.Top, 0, region.Right, region.Bottom, 1));

        frames.Add(new Frame { Texture = texture, Time = time, Id = id, Region = region, MediaTime = mediaTime, Segment = segment });
        lastPush = time;
        return true;
    }

    /// <summary>Files arriving depth with its frame (dropped when the frame is gone); false when it was.</summary>
    public bool Attach(DepthFrame depth)
    {
        foreach (Frame f in frames)
        {
            if (f.Id != depth.FrameId) continue;
            if (f.Nearness == null || f.Nearness.Length != depth.Nearness.Length)
            {
                ReleaseNearness(f);
                f.Nearness = RentNearness(depth.Nearness.Length);
            }
            depth.Nearness.CopyTo(f.Nearness, 0);
            return true;
        }
        return false;
    }

    private float[] RentNearness(int length)
    {
        while (freeNearness.Count > 0)
        {
            float[] n = freeNearness.Pop();
            if (n.Length == length) return n;
        }
        return new float[length];
    }

    private void ReleaseNearness(Frame f)
    {
        if (f.Nearness is { } n && freeNearness.Count < MaxFree) freeNearness.Push(n);
        f.Nearness = null;
    }

    /// <summary>
    /// The ids of the frames (inclusive, in id order) whose depth the last
    /// <see cref="SmoothedNearness(int, float[], out long)"/> or <see cref="InterpolatedNearness"/>
    /// used or would use once it arrives: new depth for a frame outside them does not change the result.
    /// </summary>
    public (long From, long To) LastSources { get; private set; } = (long.MinValue, long.MaxValue);

    /// <summary>
    /// The frame to show at <paramref name="time"/>: the newest captured at or before it. Until
    /// the buffer covers the delay, the oldest frame is held (the picture waits, like the sound).
    /// </summary>
    public int Select(long time)
    {
        if (frames.Count == 0) return -1;
        for (int i = frames.Count - 1; i >= 0; i--)
            if (frames[i].Time <= time) return i;
        return 0;
    }

    /// <summary>The capture time of the frame after <paramref name="index"/>, when it will be selected; <see cref="long.MaxValue"/> for none yet.</summary>
    public long NextTime(int index) => index >= 0 && index + 1 < frames.Count ? frames[index + 1].Time : long.MaxValue;

    public Frame this[int index] => frames[index];

    /// <summary>
    /// Nearness for frame <paramref name="index"/> averaged with its neighbours that have depth,
    /// weights 1-2-3-2-1: look-ahead smoothing the delay makes possible. Only neighbours cropped
    /// the same way count (another crop's depth would not line up). False when none has depth.
    /// </summary>
    public bool SmoothedNearness(int index, float[] output) => SmoothedNearness(index, output, out _);

    /// <inheritdoc cref="SmoothedNearness(int, float[])"/>
    /// <param name="nearestTicks">How far in time the nearest frame whose depth was used is (0: its own).</param>
    public bool SmoothedNearness(int index, float[] output, out long nearestTicks)
    {
        nearestTicks = long.MaxValue;
        int count = 0;
        Frame frame = frames[index];
        LastSources = (frames[Math.Max(0, index - 2)].Id, frames[Math.Min(frames.Count - 1, index + 2)].Id);
        for (int d = -2; d <= 2; d++)
        {
            int i = index + d;
            if (!HasDepth(i, frame, output.Length)) continue;
            blendMaps[count] = frames[i].Nearness!;
            blendWeights[count++] = 3 - Math.Abs(d);
            nearestTicks = Math.Min(nearestTicks, Math.Abs(frames[i].Time - frame.Time));
        }
        if (count == 0) return false;
        NearnessBlend.Weighted(blendMaps, blendWeights, count, output);
        Array.Clear(blendMaps);
        return true;
    }

    private readonly float[][] blendMaps = new float[5][];
    private readonly float[] blendWeights = new float[5];

    /// <summary>How <see cref="InterpolatedNearness"/> found depth for a frame.</summary>
    public enum DepthMatch
    {
        /// <summary>No frame cropped the same way has depth.</summary>
        None,

        /// <summary>The frame's own depth.</summary>
        Exact,

        /// <summary>Blended from the nearest frames with depth before and after it.</summary>
        Interpolated,

        /// <summary>The nearest frame with depth on one side only: the delay is too short for the depth.</summary>
        Held,
    }

    /// <summary>
    /// Nearness for frame <paramref name="index"/> in matched timing (improvement-plan.md §6):
    /// its own depth, or n = (1 − λ)·n_a + λ·n_b between the nearest frames a before and b after
    /// it that have depth, λ = (t − t_a)/(t_b − t_a) in the video's own time when the page
    /// reports it, else in capture time. Only frames cropped the same way count.
    /// </summary>
    /// <param name="ageTicks">For <see cref="DepthMatch.Held"/>, how far in time the depth used is; else 0.</param>
    public DepthMatch InterpolatedNearness(int index, float[] output, out long ageTicks)
    {
        ageTicks = 0;
        Frame f = frames[index];
        if (HasDepth(index, f, output.Length))
        {
            LastSources = (f.Id, f.Id);
            f.Nearness!.CopyTo(output, 0);
            return DepthMatch.Exact;
        }
        int a = index - 1, b = index + 1;
        while (a >= 0 && !HasDepth(a, f, output.Length)) a--;
        while (b < frames.Count && !HasDepth(b, f, output.Length)) b++;
        bool before = a >= 0, after = b < frames.Count;
        // Depth arriving for any frame between the two (or past the one side found) changes the result.
        LastSources = (before ? frames[a].Id : long.MinValue, after ? frames[b].Id : long.MaxValue);
        if (before && after)
        {
            Frame fa = frames[a], fb = frames[b];
            bool media = !double.IsNaN(f.MediaTime) && !double.IsNaN(fa.MediaTime) && !double.IsNaN(fb.MediaTime)
                         && fb.MediaTime > fa.MediaTime;
            double lambda = media
                ? (f.MediaTime - fa.MediaTime) / (fb.MediaTime - fa.MediaTime)
                : fb.Time > fa.Time ? (double)(f.Time - fa.Time) / (fb.Time - fa.Time) : 0.5;
            float l = (float)Math.Clamp(lambda, 0, 1);
            NearnessBlend.Lerp(fa.Nearness!, fb.Nearness!, l, output);
            return DepthMatch.Interpolated;
        }
        if (!before && !after) return DepthMatch.None;
        Frame held = frames[before ? a : b];
        held.Nearness!.CopyTo(output, 0);
        ageTicks = Math.Abs(f.Time - held.Time);
        return DepthMatch.Held;
    }

    /// <summary>Frame <paramref name="index"/> has depth that fits <paramref name="like"/>: the same crop, no jump between them.</summary>
    private bool HasDepth(int index, Frame like, int length) =>
        index >= 0 && index < frames.Count && frames[index].Region == like.Region && frames[index].Segment == like.Segment
        && frames[index].Nearness is { } n && n.Length == length;

    /// <summary>Forgets every held frame's depth, e.g. after a seek: nothing is blended across the jump.</summary>
    public void ForgetDepth()
    {
        foreach (Frame f in frames) ReleaseNearness(f);
    }

    private void Recycle(int index)
    {
        Frame f = frames[index];
        ReleaseNearness(f);
        if (f.Texture is { } t)
        {
            if (f.Region.Size == size) free.Push(t);
            else t.Dispose();
            f.Texture = null;
        }
        frames.RemoveAt(index);
    }

    public void Dispose()
    {
        Clear();
        while (free.Count > 0) free.Pop().Dispose();
    }
}
