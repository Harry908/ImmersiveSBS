using System.Numerics;

namespace ImmersiveSBS.Depth;

/// <summary>
/// The render thread's blend of whole nearness maps, for a theater frame whose own depth the
/// model skipped (<see cref="PagePictures.Nearness"/>): one SIMD pass over the map, in parts on
/// several cores (<see cref="RowBands.ForRange"/>), instead of a scalar pass.
/// </summary>
public static class NearnessBlend
{
    private static readonly int V = Vector<float>.Count;

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
