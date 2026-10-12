namespace ImmersiveSBS.Depth;

/// <summary>
/// A curve from nearness to nearness (improvement-plan.md §11 item 4, after iw3's softplus
/// mapper): level L &gt; 0 gives the near part more of the depth range and the far part less,
/// for scenes where people look flat; L &lt; 0 the inverse. Both ends stay where they are
/// (f(0) = 0, f(1) = 1) and the curve rises everywhere, so order and the comfort budget hold.
/// <code>
///   f(n) = (sp(k(n − b)) − sp(−kb)) / (sp(k(1 − b)) − sp(−kb)),   sp(y) = ln(1 + e^y)
/// </code>
/// with k = 8 and b = 0.15·L: slope at the nearest 1.22, 1.44 and 1.80 for L = 1, 2, 3, and at
/// the farthest 0.28, 0.12 and 0.05. Evaluated through a table.
/// </summary>
public sealed class ForegroundCurve
{
    public const int MaxLevel = 3;
    private const double K = 8;
    private const int Steps = 256;

    private readonly float[] table = new float[Steps + 1];

    public ForegroundCurve(int level)
    {
        Level = Math.Clamp(level, -MaxLevel, MaxLevel);
        for (int i = 0; i <= Steps; i++) table[i] = (float)Map(i / (double)Steps, Level);
    }

    public int Level { get; }

    /// <summary>The curve at <paramref name="n"/> in [0, 1], exactly.</summary>
    public static double Map(double n, int level)
    {
        if (level == 0) return n;
        double b = 0.15 * Math.Abs(level);
        double lo = Softplus(-K * b), range = Softplus(K * (1 - b)) - lo;
        if (level > 0) return (Softplus(K * (n - b)) - lo) / range;
        // The inverse: the far part gets more of the range.
        return b + InverseSoftplus(lo + n * range) / K;
    }

    /// <summary>Applies the curve to every value, in place.</summary>
    public void Apply(Span<float> nearness)
    {
        if (Level == 0) return;
        for (int i = 0; i < nearness.Length; i++)
        {
            float t = Math.Clamp(nearness[i], 0, 1) * Steps;
            int j = Math.Min((int)t, Steps - 1);
            nearness[i] = table[j] + (table[j + 1] - table[j]) * (t - j);
        }
    }

    private static double Softplus(double y) => y > 30 ? y : Math.Log(1 + Math.Exp(y));

    private static double InverseSoftplus(double v) => v > 30 ? v : Math.Log(Math.Max(Math.Exp(v) - 1, 1e-300));
}
