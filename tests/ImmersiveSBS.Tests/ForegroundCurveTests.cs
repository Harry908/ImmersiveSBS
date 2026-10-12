using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Tests;

public class ForegroundCurveTests
{
    [Theory]
    [InlineData(-3)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EndsStayAndTheCurveRises(int level)
    {
        Assert.Equal(0, ForegroundCurve.Map(0, level), 9);
        Assert.Equal(1, ForegroundCurve.Map(1, level), 9);
        double previous = 0;
        for (int i = 1; i <= 1000; i++)
        {
            double v = ForegroundCurve.Map(i / 1000.0, level);
            Assert.True(v > previous, $"rises at {i / 1000.0}");
            previous = v;
        }
    }

    [Theory]
    [InlineData(1, 1.22)]
    [InlineData(2, 1.44)]
    [InlineData(3, 1.8)]
    public void NearThingsGetMoreOfTheRange(int level, double nearSlope)
    {
        const double h = 1e-4;
        double near = (ForegroundCurve.Map(1, level) - ForegroundCurve.Map(1 - h, level)) / h;
        double far = (ForegroundCurve.Map(h, level) - ForegroundCurve.Map(0, level)) / h;
        Assert.InRange(near, nearSlope - 0.02, nearSlope + 0.02);
        Assert.True(far < 0.35, $"far slope {far}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void NegativeLevelsUndoPositiveOnes(int level)
    {
        for (int i = 0; i <= 20; i++)
        {
            double n = i / 20.0;
            Assert.Equal(n, ForegroundCurve.Map(ForegroundCurve.Map(n, level), -level), 6);
        }
    }

    [Fact]
    public void TheTableFollowsTheCurve()
    {
        var curve = new ForegroundCurve(3);
        float[] values = Enumerable.Range(0, 1001).Select(i => i / 1000f).ToArray();
        curve.Apply(values);
        for (int i = 0; i <= 1000; i++) Assert.Equal(ForegroundCurve.Map(i / 1000.0, 3), values[i], 1e-3);
    }

    [Fact]
    public void LevelZeroChangesNothing()
    {
        float[] values = [0, 0.3f, 0.7f, 1];
        new ForegroundCurve(0).Apply(values);
        Assert.Equal([0, 0.3f, 0.7f, 1], values);
    }
}
