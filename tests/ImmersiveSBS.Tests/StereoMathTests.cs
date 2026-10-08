using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Tests;

public class StereoMathTests
{
    [Fact]
    public void GtMaxBudgetIsAboutThirtySevenPixels()
    {
        // RayNeo GT Max: 59° diagonal, 1920×1080 per eye (video-3d-plan.md §7.1: B = 37 px).
        double b = StereoMath.ComfortBudget(59, 1920, 1080);
        Assert.InRange(b, 36, 38);
    }

    [Fact]
    public void HorizontalFovOfSixteenByNineIsSmallerThanDiagonal()
    {
        double h = StereoMath.HorizontalFov(59, 1920, 1080);
        Assert.InRange(h, 52, 53);
    }

    [Theory]
    [InlineData(0.35)]
    [InlineData(0.5)]
    [InlineData(0.2)]
    [InlineData(0.8)]
    public void MaxStrengthKeepsBothExtremesWithinBudget(double c)
    {
        const double b = 37;
        double s = StereoMath.MaxStrength(b, c);
        double nearest = s * (1 - c);   // D at n = 1
        double farthest = s * c;        // |D| at n = 0
        Assert.True(nearest <= b + 1e-9);
        Assert.True(farthest <= b + 1e-9);
        Assert.True(Math.Abs(Math.Max(nearest, farthest) - b) < 1e-9, "one extreme sits exactly on the budget");
    }

    [Fact]
    public void DefaultsMatchThePlanTable()
    {
        // c = 0.35, σ = 0.6, B = 37: S ≈ 34 px, D from about −12 to +22 px.
        double s = StereoMath.Strength(37, 0.35, 0.6);
        Assert.InRange(s, 33.5, 34.5);
        Assert.InRange(2 * StereoMath.Shift(0, s, 0.35), -12.5, -11.5);
        Assert.InRange(2 * StereoMath.Shift(1, s, 0.35), 21.5, 22.5);
    }

    [Fact]
    public void ConvergencePlaneHasNoShift()
    {
        Assert.Equal(0, StereoMath.Shift(0.35, 34, 0.35), 9);
    }
}
