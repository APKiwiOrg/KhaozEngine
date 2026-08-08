using System.Threading;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// MV2'S ACCUMULATOR: every time the present boundary BLOCKED waiting for the presentation engine to hand back
    /// the next swapchain image, counted and timed into the pair <c>GpuDeviceCounters.AcquireWaitCount</c> and
    /// <c>AcquireWaitMs</c> report (V-G6).
    /// <para>
    /// <b>IT IS SEPARATE FROM <see cref="VulkanBackpressure"/> RATHER THAN FOLDED INTO IT, and the test for that
    /// is the one the two backpressure sources pass and this one fails: same statement, same lever.</b> A command
    /// list's slot wait and a uniform ring's segment wait both say the pipeline is deeper than
    /// <c>KE_VULKAN_FRAMES_IN_FLIGHT</c> allows, and the fix for either is that one number. An acquire wait says
    /// something else entirely, that the CPU reached the presentation engine before the presentation engine was
    /// ready, and the lever for it is the acquire MODEL rather than the depth. Folding them would leave MV2 unable
    /// to read its own result on a machine that also stalls on depth, and MV3 unable to read its own on a machine
    /// that presents into vsync.
    /// </para>
    /// <para>
    /// <b>ZERO IS THE EXPECTED READING ON THE SHIPPED PATH, and that is what makes it a measurement.</b> With the
    /// semaphore acquire the CPU hands the wait to the GPU and never blocks, so a non-zero count is either the
    /// stall mode or a driver returning an image late enough for the boundary to notice. With the stall mode it
    /// ticks once per frame by construction. Those two shapes are the whole of the A/B.
    /// </para>
    /// <para>
    /// A WAIT THAT DID NOT BLOCK IS NOT RECORDED, and the caller enforces that, exactly as
    /// <see cref="VulkanBackpressure"/> and <see cref="VulkanTimeline.WaitForIdle"/> do: this is a pure
    /// accumulator with no opinion about when to call it. A counter that ticked on a non-wait could never answer
    /// "was the CPU ever blocked here" with a zero, which is the only answer the exit criterion accepts.
    /// </para>
    /// <para>
    /// <see cref="Interlocked"/> for the same reason the backpressure accumulator uses it, though the contention
    /// here is theoretical: the present boundary is one thread's. It costs two interlocked adds on a path that has
    /// either just blocked for milliseconds or is not calling in at all. See <see cref="VulkanWaitTotals"/> for
    /// what a concurrent sample of the pair does and does not promise.
    /// </para>
    /// </summary>
    internal sealed class VulkanAcquireWaits
    {
        long _count;
        long _ticks;

        /// <summary>The pair as a snapshot, cumulative since the device was created. Read from any thread.
        /// </summary>
        internal VulkanWaitTotals Totals => VulkanWaitTotals.Sample(ref _count, ref _ticks);

        /// <summary>
        /// Record ONE acquire that actually blocked, for <paramref name="elapsedTicks"/>
        /// <see cref="System.Diagnostics.Stopwatch"/> ticks.
        /// </summary>
        /// <param name="elapsedTicks">How long the boundary was blocked. Ticks rather than milliseconds, because a
        /// soak is tens of millions of entries and summing a converted double per entry accumulates rounding the
        /// raw counter does not.</param>
        internal void Record(long elapsedTicks)
        {
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _ticks, elapsedTicks);
        }
    }
}
