using System.Drawing;

namespace ImmersiveSBS.Depth;

/// <summary>The layout of video that is already stereo.</summary>
public enum Native3DLayout
{
    /// <summary>Ordinary 2D video: convert with depth.</summary>
    None,

    /// <summary>Left and right views side by side (usually half-width each).</summary>
    SideBySide,

    /// <summary>Left view on top, right view below.</summary>
    TopBottom,
}

/// <summary>What to do about video that is already 3D (video-3d-plan.md §7.4).</summary>
public enum Native3DMode
{
    Auto,
    Off,
    ForceSideBySide,
    ForceTopBottom,
}

/// <summary>
/// Recognises side-by-side and top-bottom 3D video on the coarse luma grid (video-3d-plan.md
/// §7.4): the normalised cross-correlation of one half against the other, at the best small
/// horizontal offset (the views differ by disparity), must stay above 0.85 for 2 s; dropping out
/// takes as long below 0.7. Pure: no GPU, no Windows state.
/// </summary>
public sealed class Native3DDetector
{
    private double sbsTime;
    private double tbTime;
    private double lostTime;

    public double EnterCorrelation { get; set; } = 0.85;

    public double LeaveCorrelation { get; set; } = 0.7;

    public double HoldSeconds { get; set; } = 2;

    /// <summary>Largest horizontal offset searched, as a fraction of the half width (§7.4: 3%).</summary>
    public double MaxOffsetFraction { get; set; } = 0.03;

    public Native3DLayout Layout { get; private set; }

    public void Reset()
    {
        Layout = Native3DLayout.None;
        sbsTime = tbTime = lostTime = 0;
    }

    /// <summary>
    /// Feeds the grid (cells of <paramref name="cellSize"/> px, <paramref name="gridWidth"/> wide)
    /// and the region in source px; returns the layout afterwards.
    /// </summary>
    public Native3DLayout Update(ReadOnlySpan<byte> grid, int gridWidth, int cellSize, Rectangle region, double dtSeconds)
    {
        int x0 = region.Left / cellSize, y0 = region.Top / cellSize;
        int w = region.Width / cellSize, h = region.Height / cellSize;
        if (w < 8 || h < 8) return Layout;

        double sbs = BestCorrelation(grid, gridWidth, x0, y0, w / 2, h, dx: w / 2, dy: 0);
        double tb = BestCorrelation(grid, gridWidth, x0, y0, w, h / 2, dx: 0, dy: h / 2);

        switch (Layout)
        {
            case Native3DLayout.None:
                sbsTime = sbs > EnterCorrelation ? sbsTime + dtSeconds : 0;
                tbTime = tb > EnterCorrelation ? tbTime + dtSeconds : 0;
                if (sbsTime >= HoldSeconds && sbs >= tb) Enter(Native3DLayout.SideBySide);
                else if (tbTime >= HoldSeconds) Enter(Native3DLayout.TopBottom);
                break;

            case Native3DLayout.SideBySide:
            case Native3DLayout.TopBottom:
                double c = Layout == Native3DLayout.SideBySide ? sbs : tb;
                lostTime = c < LeaveCorrelation ? lostTime + dtSeconds : 0;
                if (lostTime >= HoldSeconds) Reset();
                break;
        }

        return Layout;
    }

    private void Enter(Native3DLayout layout)
    {
        Layout = layout;
        sbsTime = tbTime = lostTime = 0;
    }

    /// <summary>
    /// NCC of the block (x0, y0, w, h) against the block displaced by (dx, dy), maximised over
    /// small horizontal offsets of the second block.
    /// </summary>
    private double BestCorrelation(ReadOnlySpan<byte> grid, int gridWidth, int x0, int y0, int w, int h, int dx, int dy)
    {
        int maxOffset = Math.Max(1, (int)Math.Round(w * MaxOffsetFraction));
        double best = -1;
        for (int off = -maxOffset; off <= maxOffset; off++)
        {
            double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
            int n = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = Math.Max(0, -off); x < w - Math.Max(0, off); x++)
                {
                    double a = grid[(y0 + y) * gridWidth + x0 + x];
                    double b = grid[(y0 + y + dy) * gridWidth + x0 + x + dx + off];
                    sa += a; sb += b; saa += a * a; sbb += b * b; sab += a * b;
                    n++;
                }
            }
            if (n == 0) continue;
            double cov = sab - sa * sb / n;
            double va = saa - sa * sa / n, vb = sbb - sb * sb / n;
            // A flat block (e.g. black) says nothing about stereo.
            if (va < n * 4 || vb < n * 4) continue;
            best = Math.Max(best, cov / Math.Sqrt(va * vb));
        }
        return best;
    }
}
