using System.Numerics;

namespace ImmersiveSBS.Depth;

/// <summary>
/// The depth kept for the theater's delay, 16 bits a pixel (<see cref="PagePictures"/>): half a
/// float's memory, steps of 1/65535 of the nearness's 0..1, far below what the eye or the warp
/// tells apart. The render thread unpacks the frame shown, or blends the two around a frame the
/// model skipped (<see cref="PagePictures.Nearness"/>): one SIMD pass over the map, in parts on
/// several cores (<see cref="RowBands.ForRange"/>), instead of a scalar pass.
/// </summary>
public static class NearnessBlend
{
    private const float Scale = 65535f, Inverse = 1f / Scale;
    private static readonly int V = Vector<float>.Count, U = Vector<ushort>.Count;

    /// <summary>Nearness (0..1, clamped) into 16 bits a pixel.</summary>
    public static void Pack(float[] nearness, ushort[] into)
    {
        RowBands.ForRange(into.Length, (_, from, to) =>
        {
            for (int k = from; k < to; k++) into[k] = (ushort)(Math.Clamp(nearness[k], 0f, 1f) * Scale + 0.5f);
        });
    }

    /// <summary>output = a + (b − a)·t, unpacked; t = 0 unpacks a alone.</summary>
    public static void Lerp(ushort[] a, ushort[] b, float t, float[] output)
    {
        float ta = (1 - t) * Inverse, tb = t * Inverse;
        RowBands.ForRange(output.Length, (_, from, to) =>
        {
            int k = from;
            for (; k + U <= to; k += U)
            {
                Vector.Widen(new Vector<ushort>(a, k), out Vector<uint> a0, out Vector<uint> a1);
                Vector.Widen(new Vector<ushort>(b, k), out Vector<uint> b0, out Vector<uint> b1);
                (Vector.ConvertToSingle(a0) * ta + Vector.ConvertToSingle(b0) * tb).CopyTo(output, k);
                (Vector.ConvertToSingle(a1) * ta + Vector.ConvertToSingle(b1) * tb).CopyTo(output, k + V);
            }
            for (; k < to; k++) output[k] = a[k] * ta + b[k] * tb;
        });
    }
}
