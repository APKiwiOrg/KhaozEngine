using System;
using KhaozEngine.Tests.Physics;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class PhysicsRebaseTimingTests
{
    [Fact]
    public void Verdict_UsesMedianOfPairedRatios_AndRetainsOriginalOrder()
    {
        PhysicsRebaseTimingResult result = PhysicsRebaseTiming.Summarize([1, 10, 10, 10, 100], [1, 1, 20, 100, 100]);
        Assert.Equal(1d, result.MedianRatio);
        Assert.True(result.Passed);
        Assert.Contains("| 2 | 10000.000000 | 1000.000000 | 0.100000x |", result.Summary);
        Assert.Contains("Median paired ratio 1.000000x", result.Summary);
    }

    [Fact]
    public void Verdict_PassesAtThree_AndFailsImmediatelyAboveThree()
    {
        Assert.True(PhysicsRebaseTiming.Summarize([1, 1, 1, 1, 1], [3, 3, 3, 3, 3]).Passed);
        double over = Math.BitIncrement(3d);
        Assert.False(PhysicsRebaseTiming.Summarize([1, 1, 1, 1, 1], [over, over, over, over, over]).Passed);
    }

    [Fact]
    public void Verdict_RefusesMissingOrInvalidSamples()
    {
        Assert.Throws<ArgumentException>(() => PhysicsRebaseTiming.Summarize([1], [1]));
        Assert.Throws<ArgumentException>(() => PhysicsRebaseTiming.Summarize([1, 1, 0, 1, 1], [1, 1, 1, 1, 1]));
        Assert.Throws<ArgumentException>(() => PhysicsRebaseTiming.Summarize([1, 1, 1, 1, 1], [1, 1, double.NaN, 1, 1]));
    }
}
