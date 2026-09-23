using System.Collections.Generic;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SkinnedPointShadowRowsTests
{
    [Fact]
    public void OverflowUsesDistanceThenKeyThenLightIndexAndClearsOldMappings()
    {
        var candidates = new List<PointShadowTransientCandidate>
        {
            new(4, 19, 90, 4f),
            new(1, 3, 20, 1f),
            new(3, 8, 10, 1f),
            new(2, 7, 10, 1f),
        };
        int[] rows = { 7, 7, 7, 7, 7 };
        var selected = new List<int>();

        int count = PointShadowTransientRows.Assign(candidates, 3, rows, selected);

        Assert.Equal(3, count);
        Assert.Equal(new[] { -1, 2, 0, 1, -1 }, rows);
        Assert.Equal(new[] { 2, 3, 1 }, selected);
    }

    [Fact]
    public void ZeroCapacityPublishesMinusOneForEveryCandidate()
    {
        int[] rows = { 9, 9 };
        var selected = new List<int> { 99 };
        int count = PointShadowTransientRows.Assign(
            new List<PointShadowTransientCandidate> { new(0, 0, 1, 0f) }, 0, rows, selected);

        Assert.Equal(0, count);
        Assert.Equal(new[] { -1, -1 }, rows);
        Assert.Empty(selected);
    }
}
