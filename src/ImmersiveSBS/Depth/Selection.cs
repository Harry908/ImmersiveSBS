namespace ImmersiveSBS.Depth;

/// <summary>
/// Order statistics without sorting. A percentile of a depth map is wanted on every depth frame;
/// sorting tens of thousands of values for it took milliseconds of the depth thread, in series
/// with the model.
/// </summary>
public static class Selection
{
    /// <summary>
    /// Reorders <paramref name="values"/> so that the k-th smallest (0-based) is at index
    /// <paramref name="k"/>, nothing larger before it and nothing smaller after it, and returns
    /// it. Expected linear time (quickselect, median-of-three pivot).
    /// </summary>
    public static float Nth(Span<float> values, int k)
    {
        if ((uint)k >= (uint)values.Length) throw new ArgumentOutOfRangeException(nameof(k));
        int lo = 0, hi = values.Length - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (values[mid] < values[lo]) (values[mid], values[lo]) = (values[lo], values[mid]);
            if (values[hi] < values[lo]) (values[hi], values[lo]) = (values[lo], values[hi]);
            if (values[hi] < values[mid]) (values[hi], values[mid]) = (values[mid], values[hi]);
            float pivot = values[mid];

            int i = lo, j = hi;
            while (i <= j)
            {
                while (values[i] < pivot) i++;
                while (values[j] > pivot) j--;
                if (i > j) break;
                (values[i], values[j]) = (values[j], values[i]);
                i++;
                j--;
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else break;   // between the two runs: equal to the pivot, already in place
        }
        return values[k];
    }
}
