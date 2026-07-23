using System;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Shared zero-allocation assertion for the AllocSensitive collection (see
/// <see cref="AllocSensitiveCollection"/>). Measures <c>GC.GetAllocatedBytesForCurrentThread()</c> around a
/// workload and asserts the delta is zero, retrying once before failing.
///
/// The retry exists because a gen-0 collection triggered by unrelated parallel collections elsewhere in the
/// process can land inside the few-millisecond measurement window and attribute foreign bytes to this thread's
/// delta. That is the mechanism <see cref="AllocSensitiveCollection"/>'s doc comment names, and the flake
/// diagnosed in issue #284 (SkinnedLimbTests.Update_ReusesBuffers_NoPerFrameAllocation, one failure on the
/// 14.18.0 full-suite Metal leg, not reproducible solo). A genuine per-frame leak allocates deterministically on
/// every call and still fails both passes. A one-off collision passes the retry. Do not raise this past one
/// retry: a real leak must keep failing reliably, and each extra retry only widens the window for hiding one.
/// </summary>
internal static class AllocAssert
{
    /// <summary>
    /// Runs <paramref name="loop"/> once, measuring bytes allocated on the current thread. If the first
    /// measurement is nonzero, re-arms the baseline and runs <paramref name="loop"/> a second time, failing only
    /// if that second delta is also nonzero.
    /// </summary>
    /// <param name="description">Names what is being measured, used in the failure message.</param>
    /// <param name="loop">The measurement workload. Must be safe to run twice in a row.</param>
    public static void NoPerCallAllocation(string description, Action loop)
    {
        long firstDelta = Measure(loop);
        if (firstDelta == 0) return;

        long retryDelta = Measure(loop);
        Assert.True(retryDelta == 0,
            $"{description} allocated {firstDelta} bytes on the first pass and {retryDelta} bytes on the retry, expected zero on at least one");
    }

    static long Measure(Action loop)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        loop();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
