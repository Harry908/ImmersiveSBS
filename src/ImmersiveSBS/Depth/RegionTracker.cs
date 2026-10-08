using System.Drawing;

namespace ImmersiveSBS.Depth;

public enum RegionState
{
    /// <summary>No video found yet.</summary>
    None,

    /// <summary>A video rectangle is accepted and its content is changing.</summary>
    Tracking,

    /// <summary>The accepted rectangle has stopped changing (a paused video): keep it, skip inference.</summary>
    Paused,
}

/// <summary>
/// Finds the playing video on the virtual monitor from a coarse luma grid (video-3d-plan.md
/// §5.3–5.4): change energy per cell with decay, a thresholded and closed mask, the largest
/// connected component, shape checks, and acceptance only once the box has held still for half
/// a second. Static letterbox bars carry no change energy, so they stay outside the box. Pure:
/// no GPU, no Windows state.
/// </summary>
public sealed class RegionTracker
{
    private readonly int gridWidth;
    private readonly int gridHeight;
    private readonly int cellSize;
    private readonly Size source;
    private readonly float[] energy;
    private readonly byte[] previous;
    private readonly bool[] mask;
    private readonly bool[] scratch;
    private readonly int[] queue;
    private readonly int[] label;
    private bool hasPrevious;

    private Rectangle candidate;
    private double candidateTime;
    private double stillTime;

    /// <param name="gridWidth">Grid cells across (source width / cell size, rounded up).</param>
    /// <param name="gridHeight">Grid cells down.</param>
    /// <param name="cellSize">Source pixels per cell side.</param>
    /// <param name="source">Source frame size, to clamp the result.</param>
    public RegionTracker(int gridWidth, int gridHeight, int cellSize, Size source)
    {
        this.gridWidth = gridWidth;
        this.gridHeight = gridHeight;
        this.cellSize = cellSize;
        this.source = source;
        int n = gridWidth * gridHeight;
        energy = new float[n];
        previous = new byte[n];
        mask = new bool[n];
        scratch = new bool[n];
        queue = new int[n];
        label = new int[n];
    }

    /// <summary>Energy decay per frame at 30 fps (plan: E ← 0.9·E + |ΔY|, about 2 s).</summary>
    public double Decay { get; set; } = 0.9;

    /// <summary>Energy above which a cell counts as changing (luma levels, 0..255 scale).</summary>
    public double Threshold { get; set; } = 8;

    /// <summary>How long a candidate must hold still before it is accepted.</summary>
    public double AcceptSeconds { get; set; } = 0.5;

    /// <summary>How long the accepted box may be still before it counts as paused.</summary>
    public double PauseSeconds { get; set; } = 3;

    public RegionState State { get; private set; }

    /// <summary>The accepted video rectangle in source pixels; empty in state None.</summary>
    public Rectangle Region { get; private set; }

    /// <summary>The user picked the region; automatic detection does not replace it (§5.6).</summary>
    public bool Locked { get; private set; }

    /// <summary>
    /// Picks the changing area at <paramref name="point"/> (source px) and locks it (§5.6): for
    /// pages where an animated ad outgrows the video. Uses the components found by the last
    /// <see cref="Update"/>, searching a few cells around the point. False when nothing changes there.
    /// </summary>
    public bool Pick(Point point)
    {
        int cx = point.X / cellSize, cy = point.Y / cellSize;
        int found = 0;
        for (int r = 0; r <= 4 && found == 0; r++)
            for (int dy = -r; dy <= r && found == 0; dy++)
                for (int dx = -r; dx <= r && found == 0; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && x < gridWidth && y >= 0 && y < gridHeight) found = label[y * gridWidth + x];
                }
        if (found == 0) return false;

        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int i = 0; i < label.Length; i++)
        {
            if (label[i] != found) continue;
            int x = i % gridWidth, y = i / gridWidth;
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
            y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
        }
        if (x1 - x0 < 3 || y1 - y0 < 3) return false;

        Region = Clamp(new Rectangle(x0 * cellSize, y0 * cellSize, (x1 - x0 + 1) * cellSize, (y1 - y0 + 1) * cellSize));
        State = RegionState.Tracking;
        Locked = true;
        stillTime = 0;
        return true;
    }

    /// <summary>Lets automatic detection choose again.</summary>
    public void Unlock() => Locked = false;

    /// <summary>Feeds one grid of mean luma per cell; returns the state afterwards.</summary>
    public RegionState Update(ReadOnlySpan<byte> grid, double dtSeconds)
    {
        int n = gridWidth * gridHeight;
        if (grid.Length != n) throw new ArgumentException("Grid size does not match.");

        double dt = Math.Clamp(dtSeconds, 1.0 / 240, 1.0);
        float decay = (float)Math.Pow(Decay, dt * 30);

        if (hasPrevious)
        {
            for (int i = 0; i < n; i++) energy[i] = decay * energy[i] + Math.Abs(grid[i] - previous[i]);
        }
        grid.CopyTo(previous);
        hasPrevious = true;

        Rectangle? found = FindCandidate();

        // A candidate is accepted once it has stayed within one cell for AcceptSeconds.
        if (found is Rectangle c)
        {
            if (Near(c, candidate)) candidateTime += dt;
            else
            {
                candidate = c;
                candidateTime = 0;
            }

            if (!Locked && candidateTime >= AcceptSeconds && (State == RegionState.None || !Near(c, Region)))
            {
                Region = Clamp(c);
                State = RegionState.Tracking;
                stillTime = 0;
            }
        }
        else
        {
            candidateTime = 0;
            candidate = Rectangle.Empty;
        }

        // A tracked box with no change inside for PauseSeconds is a paused video.
        if (State != RegionState.None)
        {
            if (MeanEnergyInside(Region) < Threshold / 4) stillTime += dt;
            else stillTime = 0;
            State = stillTime >= PauseSeconds ? RegionState.Paused : RegionState.Tracking;
        }

        return State;
    }

    /// <summary>Forgets the accepted region, e.g. after the user switched it off and on.</summary>
    public void Reset()
    {
        State = RegionState.None;
        Locked = false;
        Region = Rectangle.Empty;
        candidate = Rectangle.Empty;
        candidateTime = 0;
        Array.Clear(energy);
        hasPrevious = false;
    }

    private Rectangle? FindCandidate()
    {
        int n = gridWidth * gridHeight;
        for (int i = 0; i < n; i++) mask[i] = energy[i] > Threshold;

        // Close small gaps: 3×3 dilation, then 3×3 erosion.
        Morph(mask, scratch, dilate: true);
        Morph(scratch, mask, dilate: false);

        // Largest 4-connected component.
        Array.Clear(label);
        int bestSize = 0, bestLabel = 0, next = 0;
        int bx0 = 0, by0 = 0, bx1 = 0, by1 = 0;
        for (int start = 0; start < n; start++)
        {
            if (!mask[start] || label[start] != 0) continue;

            next++;
            int head = 0, tail = 0, size = 0;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            queue[tail++] = start;
            label[start] = next;
            while (head < tail)
            {
                int i = queue[head++];
                int x = i % gridWidth, y = i / gridWidth;
                size++;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);

                if (x > 0) Visit(i - 1);
                if (x < gridWidth - 1) Visit(i + 1);
                if (y > 0) Visit(i - gridWidth);
                if (y < gridHeight - 1) Visit(i + gridWidth);
            }

            if (size > bestSize)
            {
                bestSize = size;
                bestLabel = next;
                (bx0, by0, bx1, by1) = (x0, y0, x1, y1);
            }

            void Visit(int j)
            {
                if (mask[j] && label[j] == 0)
                {
                    label[j] = next;
                    queue[tail++] = j;
                }
            }
        }
        if (bestLabel == 0) return null;

        int w = bx1 - bx0 + 1, h = by1 - by0 + 1;

        // Shape checks (§5.3): big enough, video-shaped, mostly filled.
        double minCells = Math.Max(0.04 * n, (320.0 / cellSize) * (180.0 / cellSize));
        if (w * h < minCells) return null;
        double aspect = (double)w / h;
        if (aspect < 1.0 || aspect > 2.5) return null;
        if ((double)bestSize / (w * h) < 0.6) return null;

        return new Rectangle(bx0 * cellSize, by0 * cellSize, w * cellSize, h * cellSize);
    }

    private void Morph(bool[] src, bool[] dst, bool dilate)
    {
        for (int y = 0; y < gridHeight; y++)
        {
            for (int x = 0; x < gridWidth; x++)
            {
                bool v = !dilate;
                for (int dy = -1; dy <= 1 && v != dilate; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= gridHeight) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx;
                        if (xx < 0 || xx >= gridWidth) continue;
                        if (src[yy * gridWidth + xx] == dilate)
                        {
                            v = dilate;
                            break;
                        }
                    }
                }
                dst[y * gridWidth + x] = v;
            }
        }
    }

    private double MeanEnergyInside(Rectangle r)
    {
        int x0 = r.Left / cellSize, y0 = r.Top / cellSize;
        int x1 = Math.Min(gridWidth, (r.Right + cellSize - 1) / cellSize);
        int y1 = Math.Min(gridHeight, (r.Bottom + cellSize - 1) / cellSize);
        double sum = 0;
        int cells = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                sum += energy[y * gridWidth + x];
                cells++;
            }
        return cells > 0 ? sum / cells : 0;
    }

    private bool Near(Rectangle a, Rectangle b) =>
        !b.IsEmpty
        && Math.Abs(a.Left - b.Left) <= cellSize && Math.Abs(a.Top - b.Top) <= cellSize
        && Math.Abs(a.Right - b.Right) <= cellSize && Math.Abs(a.Bottom - b.Bottom) <= cellSize;

    private Rectangle Clamp(Rectangle r) => Rectangle.Intersect(r, new Rectangle(Point.Empty, source));
}
