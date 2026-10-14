using System;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests;

[Collection("AllocSensitive")]
public sealed class AllocationMeasurementTests
{
    [Fact]
    public void AOneOffAllocationFailsWithoutRepeatingTheWorkload()
    {
        int calls = 0;
        Assert.ThrowsAny<XunitException>(() => AllocAssert.NoPerCallAllocation("one-off allocation", () =>
        {
            if (++calls == 1) GC.KeepAlive(new byte[37]);
        }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AMeasuredAllocationKeepsItsExactByteCount()
    {
        Action allocate = static () => GC.KeepAlive(new byte[37]);
        allocate();
        Assert.Equal(64, AllocationMeasurement.Measure("one array", allocate));
    }

    [Fact]
    public void AnEmptyWorkloadMeasuresZero()
    {
        Assert.Equal(0, AllocationMeasurement.Measure("empty workload", static () => { }));
    }

    [Fact]
    public void AnInducedCollectionInvalidatesTheCounter()
    {
        var failure = Assert.ThrowsAny<XunitException>(() =>
            AllocationMeasurement.Measure("induced collection", static () => GC.Collect()));
        Assert.Contains("garbage collection", failure.Message);
        Assert.Contains("induced collection", failure.Message);
    }

    [Fact]
    public void AnExhaustedBudgetFailsWithProcessDiagnostics()
    {
        using var region = AllocationRegion.Start("budget control", totalSize: 64 * 1024);
        GC.KeepAlive(new byte[8 * 1024 * 1024]);
        var failure = Assert.ThrowsAny<XunitException>(region.Dispose);
        Assert.Contains("budget was exhausted", failure.Message);
        Assert.Contains("process=", failure.Message);
        region.Dispose();
    }

    [Fact]
    public void AnExternallyEndedRegionInvalidatesTheCounter()
    {
        using var region = AllocationRegion.Start("ended region");
        GC.EndNoGCRegion();
        var failure = Assert.ThrowsAny<XunitException>(region.Dispose);
        Assert.Contains("ended before", failure.Message);
        region.Dispose();
    }

    [Fact]
    public void ARegionThatCannotStartDoesNotEndItsOwnersRegion()
    {
        using var owner = AllocationRegion.Start("owner");
        var failure = Assert.ThrowsAny<XunitException>(() => AllocationRegion.Start("nested"));
        Assert.Contains("could not start", failure.Message);
        owner.Dispose();
    }

    [Fact]
    public void AWorkloadExceptionReleasesTheRegion()
    {
        var error = new InvalidOperationException("workload failure");
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() =>
            AllocationMeasurement.Measure("throwing workload", () => throw error)));
        using var next = AllocationRegion.Start("next measurement");
        next.Dispose();
        next.Dispose();
    }
}
