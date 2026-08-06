using System.Threading;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// MV3'S ONE ACCUMULATOR: every wait this backend does because the CPU got AHEAD of the GPU, counted and timed
    /// into a single pair that <c>GpuDeviceCounters.BackpressureStallCount</c> and <c>BackpressureStallMs</c>
    /// report.
    /// <para>
    /// <b>TWO SOURCES, ONE NUMBER, AND THE FOLD IS DELIBERATE.</b> A command list's <c>Begin</c> wrapping onto its
    /// own oldest pool slot waits here (this row), and the uniform ring's frame boundary finding its segment still
    /// in flight will wait here too (row 8, https://github.com/APKiwiOrg/KhaozEngine/issues/518). They are the
    /// same statement about the same lever: the pipeline is deeper than <c>KE_VULKAN_FRAMES_IN_FLIGHT</c> allows,
    /// and the fix for either is the same number. Splitting them into two counters would ask a reader to add them
    /// up before the gate meant anything, and MV3's exit criterion is a single zero.
    /// </para>
    /// <para>
    /// IT CHANGES WHAT A SHIPPED MEMBER DOCUMENTS, which is why the fold is written down rather than assumed.
    /// <c>BackpressureStallCount</c> was authored on the Direct3D 11 backend, where it means a constant-buffer ring
    /// segment stall and nothing else. Here it also means a command-buffer slot wait. Row 19
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/529) owns widening that member's own doc comment, so the
    /// seam says so in its own words rather than being widened in silence (section 14).
    /// </para>
    /// <para>
    /// A WAIT THAT DID NOT BLOCK IS NOT A STALL, and the caller is what enforces that: this type is a pure
    /// accumulator with no opinion about when to call it, and every caller polls the completion value first and
    /// records only when it then had to block. That is the same rule <see cref="VulkanTimeline.WaitForIdle"/>
    /// applies to <c>DrainCount</c>, for the same reason: a counter that ticks on a non-wait cannot answer "was
    /// anything ever blocked" with a zero.
    /// </para>
    /// <para>
    /// <see cref="Interlocked"/> RATHER THAN <c>++</c>, because recording is lock-free on this backend and two
    /// lists on two threads can be inside their own slot waits at once. It costs two interlocked adds on a path
    /// that just spent milliseconds blocked. See <see cref="VulkanWaitTotals"/> for what a concurrent sample of
    /// the pair does and does not promise.
    /// </para>
    /// </summary>
    internal sealed class VulkanBackpressure
    {
        long _count;
        long _ticks;

        /// <summary>The pair as a snapshot, cumulative since the device was created. Read from any thread.
        /// </summary>
        internal VulkanWaitTotals Totals => VulkanWaitTotals.Sample(ref _count, ref _ticks);

        /// <summary>
        /// Record ONE wait that actually blocked, for <paramref name="elapsedTicks"/>
        /// <see cref="System.Diagnostics.Stopwatch"/> ticks.
        /// </summary>
        /// <param name="elapsedTicks">How long the caller was blocked. Ticks rather than milliseconds, because a
        /// week-long soak is tens of millions of entries and summing a converted double per entry accumulates
        /// rounding the raw counter does not.</param>
        internal void Record(long elapsedTicks)
        {
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _ticks, elapsedTicks);
        }
    }
}
