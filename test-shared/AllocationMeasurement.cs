using System;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Measures one synchronous workload in a checked no-GC region. Warm-up belongs to the caller.</summary>
internal static class AllocationMeasurement
{
    /// <summary>Returns the bytes the workload allocated on this thread. A workload that may allocate more than
    /// the default region budget passes a larger <paramref name="budget"/>.</summary>
    internal static long Measure(string description, Action workload, long budget = AllocationRegion.DefaultBudget)
    {
        using var region = AllocationRegion.Start(description, budget);
        long before = GC.GetAllocatedBytesForCurrentThread();
        workload();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        region.Dispose();
        return allocated;
    }

    internal static void NoPerCallAllocation(string description, Action workload)
    {
        long allocated = Measure(description, workload);
        Assert.True(allocated == 0, $"{description} allocated {allocated} bytes, expected zero.");
    }
}
