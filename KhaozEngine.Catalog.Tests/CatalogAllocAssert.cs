using System;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Per-assembly copy of the AllocSensitive collection marker. xUnit collection definitions are per assembly,
/// and a <c>[Collection("AllocSensitive")]</c> with no definition anywhere serializes nothing at all, which
/// is how a collection attribute can look like a fix and do no work.
/// </summary>
[CollectionDefinition("AllocSensitive", DisableParallelization = true)]
public sealed class AllocSensitiveCollection
{
}

/// <summary>
/// Per-assembly copy of the zero-allocation assertion the Server and Render tests carry. Measures
/// <c>GC.GetAllocatedBytesForCurrentThread()</c> around a workload and asserts the delta is zero, retrying
/// once before failing.
/// <para>
/// The retry exists because a gen-0 collection triggered elsewhere in the process can land inside the
/// measurement window and attribute foreign bytes to this thread's delta. A genuine per-call allocation
/// happens on every pass and still fails both. Do not raise this past one retry: each extra retry only
/// widens the window for hiding a real leak.
/// </para>
/// </summary>
internal static class CatalogAllocAssert
{
    /// <summary>Runs the loop, and on a non-zero delta re-arms the baseline and runs it exactly once more.</summary>
    /// <param name="description">Names what is being measured, for the failure message.</param>
    /// <param name="loop">The workload, which must be safe to run twice.</param>
    public static void NoPerCallAllocation(string description, Action loop)
    {
        long first = Measure(loop);
        if (first == 0)
        {
            return;
        }

        long retry = Measure(loop);
        Assert.True(
            retry == 0,
            $"{description} allocated {first} bytes on the first pass and {retry} on the retry, expected zero on at least one");
    }

    static long Measure(Action loop)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        loop();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
