using System.Numerics;

namespace ImmersiveSBS.Depth;

/// <summary>
/// The render thread's blends of whole nearness maps, for each frame the buffer or matched timing
/// shows (<see cref="VideoDelayBuffer"/>): one SIMD pass over the map, in parts on several cores
/// (<see cref="RowBands.ForRange"/>), instead of a scalar pass per map.
/// </summary>
public static class NearnessBlend
{
    private static readonly int V = Vector<float>.Count;

    /// <summary>output = Σ wᵢ·mapᵢ / Σ wᵢ over the first <paramref name="count"/> maps.</summary>
    public static void Weighted(float[][] maps, float[] weights, int count, float[] output)
    {
        float total = 0;
        for (int j = 0; j < count; j++) total += weights[j];
        if (count == 0 || total <= 0) throw new ArgumentException("Nothing to blend.");
        var scaled = new float[count];
        for (int j = 0; j < count; j++) scaled[j] = weights[j] / total;

        RowBands.ForRange(output.Length, (_, from, to) =>
        {
            int k = from;
            for (; k + V <= to; k += V)
            {
                Vector<float> sum = new Vector<float>(maps[0], k) * scaled[0];
                for (int j = 1; j < count; j++) sum += new Vector<float>(maps[j], k) * scaled[j];
                sum.CopyTo(output, k);
            }
            for (; k < to; k++)
            {
                float sum = 0;
                for (int j = 0; j < count; j++) sum += maps[j][k] * scaled[j];
                output[k] = sum;
            }
        });
    }

    /// <summary>output = a + (b − a)·t.</summary>
    public static void Lerp(float[] a, float[] b, float t, float[] output)
    {
        RowBands.ForRange(output.Length, (_, from, to) =>
        {
            int k = from;
            for (; k + V <= to; k += V)
            {
                var va = new Vector<float>(a, k);
                (va + (new Vector<float>(b, k) - va) * t).CopyTo(output, k);
            }
            for (; k < to; k++) output[k] = a[k] + (b[k] - a[k]) * t;
        });
    }
}
