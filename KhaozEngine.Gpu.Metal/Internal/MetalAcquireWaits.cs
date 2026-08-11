using System.Threading;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// M-W4'S ACCUMULATOR: every <c>-[CAMetalLayer nextDrawable]</c> the present boundary took, counted and timed
    /// into the pair <c>GpuDeviceCounters.AcquireWaitCount</c> and <c>AcquireWaitMs</c> report.
    ///
    /// <para><b>EVERY ACQUIRE IS RECORDED, AND THAT IS THE DIFFERENCE FROM EVERY OTHER WAIT ACCUMULATOR IN THIS
    /// BACKEND.</b> <see cref="MetalBackpressure"/> and <c>MetalTimeline.WaitForIdle</c> both record only a wait
    /// that actually BLOCKED, because both can ask first: the ring polls the completion value, and the drain
    /// compares the completed value against the last submitted one. <c>nextDrawable</c> can be asked nothing at
    /// all. There is no zero-timeout variant, no semaphore form and no "is one ready" query, which is exactly
    /// M-W4's finding that the stall is not removable, so a boundary cannot tell a call that returned instantly
    /// from one that waited a whole refresh interval except by TIMING it, and a call that returned instantly is
    /// recorded with a near-zero duration rather than skipped.</para>
    ///
    /// <para><b>THE SEAM ALREADY DOCUMENTS THAT READING, which is what makes it the right one rather than a
    /// convenience.</b> <c>GpuDeviceCounters.AcquireWaitCount</c> says in as many words that a backend acquiring
    /// with a semaphore reports zero and a backend that blocks the CPU on the acquire reports one per frame, and
    /// that the difference between those two shapes is the entire reason the pair exists. Metal is the second
    /// kind and has no way to be the first, so one per boundary IS the documented answer, and
    /// <see cref="MetalWaitTotals.TotalMs"/> divided by the count is the number MM4's exit criterion is stated
    /// against.</para>
    ///
    /// <para><b>SO THE COUNT IS THE DENOMINATOR AND THE MILLISECONDS ARE THE MEASUREMENT.</b> A reading of
    /// "acquire wait per frame near zero on the uncapped capture" is <c>AcquireWaitMs</c> over
    /// <c>AcquireWaitCount</c>, and both halves are needed: MM4 expects the pair to be NON-ZERO under vsync and
    /// says so, because a vsync-paced frame SHOULD wait for a drawable, so a non-zero reading is only evidence
    /// when the capture was taken uncapped.</para>
    ///
    /// <para><b>IT IS SEPARATE FROM <see cref="MetalBackpressure"/> RATHER THAN FOLDED INTO IT</b>, by the test
    /// the two pass and this one fails: a ring stall says the pipeline is deeper than
    /// <c>KE_METAL_FRAMES_IN_FLIGHT</c> allows and the lever is that number, where an acquire wait says the CPU
    /// reached the display before the display was ready and the lever is the refresh rate. Folding them would
    /// leave MM4 unable to read either half on a machine that does both.</para>
    /// </summary>
    internal sealed class MetalAcquireWaits
    {
        long _count;
        long _ticks;

        /// <summary>The pair as a snapshot, cumulative since the device was created. Read from any thread.
        /// </summary>
        internal MetalWaitTotals Totals => MetalWaitTotals.Sample(ref _count, ref _ticks);

        /// <summary>
        /// Record ONE drawable acquire, for <paramref name="elapsedTicks"/>
        /// <see cref="System.Diagnostics.Stopwatch"/> ticks. Called for every acquire, including one that returned
        /// nil, because a nil answer still cost whatever it cost to get.
        /// </summary>
        /// <param name="elapsedTicks">How long the call took. Ticks rather than milliseconds, because a soak is
        /// tens of millions of entries and summing a converted double per entry accumulates rounding the raw
        /// counter does not.</param>
        internal void Record(long elapsedTicks)
        {
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _ticks, elapsedTicks);
        }
    }
}
