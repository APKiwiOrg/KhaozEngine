using System.Threading;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// MM4'S ONE ACCUMULATOR: every wait this backend does because the CPU got AHEAD of the GPU, counted and
    /// timed into a single pair that <c>GpuDeviceCounters.BackpressureStallCount</c> and
    /// <c>BackpressureStallMs</c> report.
    /// <para>
    /// <b>ONE SOURCE, WHICH IS THE DIFFERENCE FROM THE VULKAN SIBLING AND THE WHOLE OF M-R2.</b> There the same
    /// number folds a command-list pool-slot wait and a uniform-ring segment stall, because that backend has a
    /// per-list <c>VkCommandPool</c> ring that cannot be reset while its buffers are in flight. An
    /// <c>MTLCommandBuffer</c> is single-use and the queue owns its memory, so there is no pool, no slot and no
    /// second wait: the ONLY thing that ever records here is <see cref="MetalRingAllocator.BeginRecording"/> finding
    /// its segment still in flight. That makes a non-zero reading on this backend unambiguous, and it is why
    /// <c>BackpressureStallCount</c> means one thing here where it means two there.
    /// </para>
    /// <para>
    /// A WAIT THAT DID NOT BLOCK IS NOT A STALL, and the caller is what enforces that: this type is a pure
    /// accumulator with no opinion about when to call it, and the one caller polls the completion value first and
    /// records only when it then had to block. That is the same rule <see cref="MetalTimeline.WaitForIdle"/>
    /// applies to <c>DrainCount</c>, for the same reason: a counter that ticks on a non-wait cannot answer "was
    /// anything ever blocked" with a zero, and MM4's exit criterion IS a zero across a whole capture window.
    /// </para>
    /// <para>
    /// <see cref="Interlocked"/> RATHER THAN <c>++</c>. Recording is lock-free on this backend and N lists record
    /// concurrently (M-R3), so two threads can be inside their own <c>Begin</c> at once. It costs two interlocked
    /// adds on a path that just spent milliseconds blocked. See <see cref="MetalWaitTotals"/> for what a
    /// concurrent sample of the pair does and does not promise.
    /// </para>
    /// </summary>
    internal sealed class MetalBackpressure
    {
        long _count;
        long _ticks;

        /// <summary>The pair as a snapshot, cumulative since the device was created. Read from any thread.
        /// </summary>
        internal MetalWaitTotals Totals => MetalWaitTotals.Sample(ref _count, ref _ticks);

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
