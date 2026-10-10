using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Tests;

public class SelectionTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(1000, 20)]
    [InlineData(1000, 980)]
    [InlineData(38000, 760)]
    public void TheNthIsWhatSortingGives(int length, int k)
    {
        var random = new Random(length + k);
        // Many repeats too: depth maps have flat areas.
        float[] values = Enumerable.Range(0, length).Select(_ => random.Next(4) == 0 ? 0.5f : (float)random.NextDouble()).ToArray();
        float[] sorted = values.Order().ToArray();

        float nth = Selection.Nth(values, k);

        Assert.Equal(sorted[k], nth);
        Assert.Equal(nth, values[k]);
        Assert.All(values.AsSpan(0, k).ToArray(), v => Assert.True(v <= nth));
        Assert.All(values.AsSpan(k).ToArray(), v => Assert.True(v >= nth));
    }

    [Fact]
    public void AllEqualValuesAndSortedInputWork()
    {
        Assert.Equal(3f, Selection.Nth(Enumerable.Repeat(3f, 501).ToArray(), 250));
        float[] ascending = Enumerable.Range(0, 1001).Select(i => (float)i).ToArray();
        Assert.Equal(980f, Selection.Nth(ascending, 980));
        float[] descending = ascending.Reverse().ToArray();
        Assert.Equal(20f, Selection.Nth(descending, 20));
    }
}
