using System;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Measures one synchronous workload in a checked no-GC region. Warm-up belongs to the caller.</summary>
internal static class AllocationMeasurement
{
    internal static long Measure(string description, Action workload)
    {
        using var region = AllocationRegion.Start(description);
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
