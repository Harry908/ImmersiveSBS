namespace ImmersiveSBS.Depth;

/// <summary>
/// Runs a step of the depth post-processing over horizontal bands of rows in parallel. Every
/// step there works row by row (a row reads its neighbours from the step before, never from its
/// own step), so bands only need the step boundary that <see cref="For"/> gives by returning
/// after all bands. At most 8 bands, and at least 16 rows each: the render, capture and model
/// threads keep their cores. In the 2026-10-11 run the post-processing at 756×420 took 20–26 ms
/// on one core, two to three times the model. A band no pool thread takes runs on the caller,
/// so a busy pool costs no more than one core would.
/// </summary>
public static class RowBands
{
    private static readonly int Workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    /// <summary>The most bands this thread may use now; 0: no limit of its own (<see cref="Limit"/>).</summary>
    [ThreadStatic]
    private static int limit;

    /// <summary>
    /// How many bands <see cref="For"/> splits <paramref name="height"/> rows into: twice the
    /// workers, so one whose band holds most of the picture's edges does not hold up the rest.
    /// </summary>
    public static int Count(int height) => Math.Clamp(height / 16, 1, limit > 0 ? MaxParts : 2 * Workers);

    private static int MaxParts => limit > 0 ? Math.Min(limit, Workers) : Workers;

    /// <summary>
    /// Until disposed, work started on this thread uses at most <paramref name="parts"/> bands
    /// (1: all on this thread, as before the split). For comparing the two in tests.
    /// </summary>
    public static IDisposable Limit(int parts)
    {
        int was = limit;
        limit = Math.Max(1, parts);
        return new Restore(was);
    }

    private sealed class Restore(int was) : IDisposable
    {
        public void Dispose() => limit = was;
    }

    /// <summary>
    /// Calls <paramref name="band"/>(index, first row, row after the last) for each band, in
    /// parallel, and returns once all are done.
    /// </summary>
    public static void For(int height, Action<int, int, int> band)
    {
        int bands = Count(height);
        if (bands == 1)
        {
            band(0, 0, height);
            return;
        }
        Parallel.For(0, bands, Options, b => band(b, b * height / bands, (b + 1) * height / bands));
    }

    /// <summary>No more threads than <see cref="Workers"/> at once, however many bands.</summary>
    private static readonly ParallelOptions Options = new() { MaxDegreeOfParallelism = Workers };

    /// <summary>How many parts <see cref="ForRange"/> splits <paramref name="length"/> values into.</summary>
    public static int RangeCount(int length) => Math.Clamp(length / MinRange, 1, MaxParts);

    /// <summary>At least this many values a part: below, starting a part costs more than it saves.</summary>
    private const int MinRange = 32768;

    /// <summary>
    /// Calls <paramref name="part"/>(index, first value, value after the last) for each part of
    /// a flat array of <paramref name="length"/> values (point by point work), in parallel.
    /// Parts start on multiples of 64 values, so SIMD loops in them stay aligned alike.
    /// </summary>
    public static void ForRange(int length, Action<int, int, int> part)
    {
        int parts = RangeCount(length);
        if (parts == 1)
        {
            part(0, 0, length);
            return;
        }
        Parallel.For(0, parts, Options, p =>
        {
            int from = p == 0 ? 0 : (int)((long)length * p / parts) & ~63;
            int to = p == parts - 1 ? length : (int)((long)length * (p + 1) / parts) & ~63;
            part(p, from, to);
        });
    }
}
