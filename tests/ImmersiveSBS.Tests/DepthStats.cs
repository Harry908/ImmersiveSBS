namespace ImmersiveSBS.Tests;

/// <summary>Comparing depth maps in the model tests: near-to-far order and the app's normalisation.</summary>
internal static class DepthStats
{
    /// <summary>Every 7th pixel enters the rank comparisons.</summary>
    public const int Stride = 7;

    /// <summary>Spearman's ρ over every <see cref="Stride"/>-th pixel: how alike the near-to-far order is, whatever the curve.</summary>
    public static double Spearman(float[] a, float[] b)
    {
        double[] ra = Ranks(a), rb = Ranks(b);
        double ma = ra.Average(), mb = rb.Average(), sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < ra.Length; i++)
        {
            sab += (ra[i] - ma) * (rb[i] - mb);
            saa += (ra[i] - ma) * (ra[i] - ma);
            sbb += (rb[i] - mb) * (rb[i] - mb);
        }
        return sab / Math.Sqrt(saa * sbb);
    }

    private static double[] Ranks(float[] values)
    {
        float[] sampled = [.. values.Where((_, i) => i % Stride == 0)];
        int[] order = [.. Enumerable.Range(0, sampled.Length)];
        Array.Sort((float[])sampled.Clone(), order);
        var ranks = new double[sampled.Length];
        for (int r = 0; r < order.Length; r++) ranks[order[r]] = r;
        return ranks;
    }

    /// <summary>The app's normalisation: 2nd–98th percentile to 0..1.</summary>
    public static float[] Normalised(float[] m)
    {
        (float p2, float p98) = Percentiles(m);
        float span = Math.Max(p98 - p2, 1e-9f);
        return [.. m.Select(v => Math.Clamp((v - p2) / span, 0, 1))];
    }

    /// <summary>The 2nd and 98th percentiles, over every 4th pixel.</summary>
    public static (float P2, float P98) Percentiles(float[] m)
    {
        float[] sorted = [.. m.Where((_, i) => i % 4 == 0).Order()];
        return (sorted[(int)(0.02 * (sorted.Length - 1))], sorted[(int)(0.98 * (sorted.Length - 1))]);
    }
}
