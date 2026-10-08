namespace ImmersiveSBS.Depth;

/// <summary>
/// Disparity for AI 3D (video-3d-plan.md §7.1; signs of DesktopSbS math.md §5.4): crossed
/// disparity is positive and means toward the viewer; the left eye's copy moves right.
///
/// A pixel of nearness n ∈ [0, 1] (1 = nearest) is shifted by s = (S/2)·(n − c) in each eye,
/// left +s, right −s, for a total disparity D = S·(n − c). The virtual monitor fills the eye,
/// so the window shift δw of the plan is 0. The comfort cap keeps both extremes within the
/// budget B: S·(1 − c) ≤ B (nearest) and S·c ≤ B (farthest).
/// </summary>
public static class StereoMath
{
    /// <summary>Default comfort budget: 1° of disparity.</summary>
    public const double ComfortDegrees = 1.0;

    /// <summary>Horizontal field of view from a published diagonal one and the eye's pixel shape.</summary>
    public static double HorizontalFov(double diagonalDegrees, int eyeWidth, int eyeHeight)
    {
        double diagonal = Math.Sqrt((double)eyeWidth * eyeWidth + (double)eyeHeight * eyeHeight);
        double half = Math.Atan(Math.Tan(diagonalDegrees * Math.PI / 360) * eyeWidth / diagonal);
        return half * 360 / Math.PI;
    }

    /// <summary>The disparity budget B in eye pixels: <paramref name="degrees"/> × pixels per degree.</summary>
    public static double ComfortBudget(double diagonalFovDegrees, int eyeWidth, int eyeHeight, double degrees = ComfortDegrees) =>
        degrees * eyeWidth / HorizontalFov(diagonalFovDegrees, eyeWidth, eyeHeight);

    /// <summary>The largest S for which neither the nearest nor the farthest pixel exceeds B.</summary>
    public static double MaxStrength(double budget, double convergence)
    {
        double c = Math.Clamp(convergence, 0.01, 0.99);
        return Math.Min(budget / (1 - c), budget / c);
    }

    /// <summary>S = σ · S_max, with the user's strength σ ∈ [0, 1].</summary>
    public static double Strength(double budget, double convergence, double sigma) =>
        Math.Clamp(sigma, 0, 1) * MaxStrength(budget, convergence);

    /// <summary>Per-eye shift s for nearness <paramref name="n"/>: left eye +s, right eye −s.</summary>
    public static double Shift(double n, double strength, double convergence) => strength / 2 * (n - convergence);
}
