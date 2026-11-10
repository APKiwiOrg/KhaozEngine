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
        long before = GC.GetAllocatedBytesForCurrentThread();
        allocate();
        long expected = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(expected > 0);
        Assert.Equal(expected, AllocationMeasurement.Measure("one array", allocate));
    }

    [Fact]
    public void AnEmptyWorkloadMeasuresZero()
    {
        Assert.Equal(0, AllocationMeasurement.Measure("empty workload", static () => { }));
    }

    [Fact]
    public void AnInducedCollectionInvalidatesTheCounter()
    {
        var failure = Assert.Throws<AllocationMeasurementException>(() =>
            AllocationMeasurement.Measure("induced collection", static () => GC.Collect()));
        Assert.Equal(AllocationMeasurementRefusal.CollectionInterrupted, failure.Reason);
        Assert.Equal("induced collection", failure.Description);
        Assert.True(failure.Collections!.Value.Gen0 > 0);
    }

    [Fact]
    public void AnExhaustedBudgetFailsWithProcessDiagnostics()
    {
        using var region = AllocationRegion.Start("budget control", totalSize: 64 * 1024);
        GC.KeepAlive(new byte[8 * 1024 * 1024]);
        var failure = Assert.Throws<AllocationMeasurementException>(region.Dispose);
        Assert.Equal(AllocationMeasurementRefusal.BudgetExhausted, failure.Reason);
        Assert.Equal("budget control", failure.Description);
        Assert.Equal(64 * 1024, failure.Budget);
        Assert.True(failure.ProcessBytes >= failure.Budget);
        Assert.True(failure.ThreadBytes > 0);
        Assert.Equal(failure.ProcessBytes - failure.ThreadBytes, failure.OtherThreadBytes);
        Assert.True(failure.ReportedMessages >= 0);
        Assert.NotNull(failure.Collections);
        region.Dispose();
    }

    [Fact]
    public void AnExternallyEndedRegionInvalidatesTheCounter()
    {
        using var region = AllocationRegion.Start("ended region");
        GC.EndNoGCRegion();
        var failure = Assert.Throws<AllocationMeasurementException>(region.Dispose);
        Assert.Equal(AllocationMeasurementRefusal.RegionEnded, failure.Reason);
        Assert.Equal("ended region", failure.Description);
        Assert.NotNull(failure.RuntimeReason);
        region.Dispose();
    }

    [Fact]
    public void ARegionThatCannotStartDoesNotEndItsOwnersRegion()
    {
        using var owner = AllocationRegion.Start("owner");
        var refusal = Assert.Throws<AllocationMeasurementException>(() => AllocationRegion.Start("nested"));
        Assert.Equal(AllocationMeasurementRefusal.RegionAlreadyActive, refusal.Reason);
        Assert.Equal("nested", refusal.Description);
        Assert.Equal(AllocationRegion.DefaultBudget, refusal.Budget);
        Assert.Null(refusal.ProcessBytes);
        Assert.Null(refusal.Collections);
        Assert.Equal(System.Runtime.GCLatencyMode.NoGCRegion, System.Runtime.GCSettings.LatencyMode);
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
