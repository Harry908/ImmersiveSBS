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
/// appearing, the tracker refining its box) does not empty the buffer: the first version did,
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

        /// <summary>Nearness at model resolution, once the depth for this frame has arrived.</summary>
        public float[]? Nearness { get; internal set; }

        public ID3D11Texture2D TextureOrThrow => Texture ?? throw new InvalidOperationException("Empty frame.");
    }

    private readonly ID3D11Device device;
    private readonly List<Frame> frames = []; // oldest first
    private readonly Stack<ID3D11Texture2D> free = new(); // textures of the current size
    private Size size;
    private long lastPush = long.MinValue;

    public VideoDelayBuffer(ID3D11Device device)
    {
        this.device = device;
    }

    /// <summary>GPU memory the held frames may use.</summary>
    public long MaxBytes { get; set; } = 600L * 1024 * 1024;

    public int Count => frames.Count;

    /// <summary>Forgets the held frames (the delay stopped); their textures are reused if the size matches.</summary>
    public void Clear()
    {
        while (frames.Count > 0) Recycle(0);
        lastPush = long.MinValue;
    }

    /// <summary>
    /// Copies <paramref name="region"/> of <paramref name="source"/> in, stamped with its capture
    /// time (Stopwatch ticks). Returns false when the frame was skipped to stay within memory.
    /// </summary>
    public bool Push(ID3D11Texture2D source, Rectangle region, long time, long id, TimeSpan delay, long ticksPerSecond)
    {
        if (region.Size != size)
        {
            // Held frames keep their textures; spare ones of the old size are no use any more.
            while (free.Count > 0) free.Pop().Dispose();
            size = region.Size;
        }

        // Enough frames for the delay at 60 fps, plus a margin, within the memory cap.
        long frameBytes = (long)size.Width * size.Height * 4;
        int capacity = (int)Math.Max(4, Math.Min(Math.Ceiling(delay.TotalSeconds * 60) + 8, MaxBytes / frameBytes));
        double minInterval = delay.TotalSeconds / Math.Max(1, capacity - 4) * ticksPerSecond;
        if (capacity < delay.TotalSeconds * 60 && lastPush != long.MinValue && time - lastPush < minInterval) return false;

        // Frames older than the delay plus a margin are no longer needed.
        long keep = time - (long)((delay.TotalSeconds + 0.5) * ticksPerSecond);
        while (frames.Count > 1 && frames[1].Time < keep) Recycle(0);
        while (frames.Count >= capacity) Recycle(0);

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

        frames.Add(new Frame { Texture = texture, Time = time, Id = id, Region = region });
        lastPush = time;
        return true;
    }

    /// <summary>Files arriving depth with its frame (dropped when the frame is gone).</summary>
    public void Attach(DepthFrame depth)
    {
        foreach (Frame f in frames)
        {
            if (f.Id != depth.FrameId) continue;
            if (f.Nearness == null || f.Nearness.Length != depth.Nearness.Length) f.Nearness = new float[depth.Nearness.Length];
            depth.Nearness.CopyTo(f.Nearness, 0);
            return;
        }
    }

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

    public Frame this[int index] => frames[index];

    /// <summary>
    /// Nearness for frame <paramref name="index"/> averaged with its neighbours that have depth,
    /// weights 1-2-3-2-1: look-ahead smoothing the delay makes possible. Only neighbours cropped
    /// the same way count (another crop's depth would not line up). False when none has depth.
    /// </summary>
    public bool SmoothedNearness(int index, float[] output)
    {
        Array.Clear(output);
        float total = 0;
        Rectangle crop = frames[index].Region;
        for (int d = -2; d <= 2; d++)
        {
            int i = index + d;
            if (i < 0 || i >= frames.Count || frames[i].Region != crop) continue;
            if (frames[i].Nearness is not float[] n || n.Length != output.Length) continue;
            float w = 3 - Math.Abs(d);
            for (int k = 0; k < output.Length; k++) output[k] += w * n[k];
            total += w;
        }
        if (total == 0) return false;
        for (int k = 0; k < output.Length; k++) output[k] /= total;
        return true;
    }

    private void Recycle(int index)
    {
        Frame f = frames[index];
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
