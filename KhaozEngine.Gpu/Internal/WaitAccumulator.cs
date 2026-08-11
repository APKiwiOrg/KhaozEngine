using System.Threading;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// A COUNT-AND-DURATION ACCUMULATOR BEHIND ONE <see cref="WaitTotals"/> PAIR: the nine lines that record a
    /// wait and hand the running pair out as a snapshot. Both native backends that keep one of these keep two,
    /// one for backpressure and one for swapchain acquires, and they were four code-identical types before
    /// #531's second extraction took them.
    /// <para>
    /// <b>THE ROLE IS THE FIELD'S, NOT THE TYPE'S, AND THAT IS THE ONE COST THIS MERGE ACCEPTS.</b> Four
    /// role-named types became one, so a mix-up at an injection site now COMPILES where it used to be a type
    /// error. What holds the line instead is the field name and its doc at each host, two injection sites per
    /// backend, and a counters test that would fail immediately: a backpressure reading landing in the acquire
    /// pair is visible in <c>GpuDeviceCounters</c> the first time either is read.
    /// </para>
    /// <para>
    /// <b>A WAIT THAT DID NOT BLOCK IS NOT A STALL, AND THE CALLER IS WHAT ENFORCES THAT.</b> This type is a pure
    /// accumulator with no opinion about when to call it. Every caller but one polls first and records only when
    /// it then had to block, for the reason a drain applies the same rule: a counter that ticks on a non-wait
    /// cannot answer "was anything ever blocked" with a zero, and the zero is the whole exit criterion. The one
    /// exception is the Metal drawable acquire, where <c>-[CAMetalLayer nextDrawable]</c> has no zero-timeout
    /// form, no semaphore form and no "is one ready" query, so the boundary cannot tell an instant return from a
    /// whole refresh interval except by TIMING it and records every acquire with whatever it cost. That
    /// difference is a caller's, is argued where the caller makes it, and is why the seam's own
    /// <c>AcquireWaitCount</c> doc says a blocking-acquire backend reports one per frame.
    /// </para>
    /// <para>
    /// WHAT DOES NOT FOLD INTO ONE OF THESE. The off-timeline deferral counters are not waits at all and live in
    /// <see cref="RingPatchStats"/>: a deferred patch says a caller wrote a uniform buffer while an earlier frame
    /// was still reading a segment of it, which costs nobody a stall, and folding it in would turn a load-time
    /// write into evidence against the frames-in-flight setting. Backpressure and acquire waits stay two
    /// accumulators for the same shape of reason: a ring stall says the pipeline is deeper than the
    /// frames-in-flight cap allows and the lever is that number, where an acquire wait says the CPU reached the
    /// presentation engine before it was ready and the lever is the acquire model or the refresh rate.
    /// </para>
    /// <para>
    /// <see cref="Interlocked"/> RATHER THAN <c>++</c>, because recording is lock-free on both backends that use
    /// this and two command lists on two threads can be inside their own waits at once. It costs two interlocked
    /// adds on a path that either just spent milliseconds blocked or is not calling in at all. See
    /// <see cref="WaitTotals"/> for what a concurrent sample of the pair does and does not promise.
    /// </para>
    /// </summary>
    internal sealed class WaitAccumulator
    {
        long _count;
        long _ticks;

        /// <summary>The pair as a snapshot, cumulative since the device was created. Read from any thread.
        /// </summary>
        internal WaitTotals Totals => WaitTotals.Sample(ref _count, ref _ticks);

        /// <summary>
        /// Record ONE entry, for <paramref name="elapsedTicks"/> <see cref="System.Diagnostics.Stopwatch"/>
        /// ticks. Whether an entry is a wait that BLOCKED or every call regardless is the caller's rule, argued
        /// at the caller. See the class note.
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
