using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// A REAL GPU DRAIN BUILT FROM WHAT THIS ROW OWNS: commit an empty command buffer and block on it. Because a
    /// Metal queue executes its buffers in ENQUEUE order and <c>-commit</c> enqueues, a buffer that has completed
    /// is proof that everything committed to that queue before it has completed too. So this waits for the whole
    /// queue without a shared event, without a fence and without a counter.
    ///
    /// <para><b>WHY IT EXISTS AT ALL: METAL HAS NO DEVICE-LEVEL WAIT.</b> The Vulkan sibling's device row could
    /// call <c>vkDeviceWaitIdle</c> and be finished, so its teardown wait needed nothing from its timeline row.
    /// Metal has no such call, and M-F6 still requires a drain BEFORE teardown, which is the half phase 3 had to
    /// correct on Vulkan and which the incumbent Metal backend already gets right. This is how the requirement is
    /// met with the timeline row still in flight.</para>
    ///
    /// <para><b>AND IT IS THE SEAM ROW 5 REPLACES, LEFT DELIBERATELY WHERE ITS SIBLING LEFT ONE.</b> The timeline
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/571) makes <c>WaitForIdle</c> a
    /// <c>waitUntilSignaledValue:timeoutMS:</c> on an <c>MTLSharedEvent</c>, with <c>DrainCount</c> and
    /// <c>DrainMs</c> counted, and it adds its own entries to the teardown order the same way phase 3's row 5 added
    /// two to its device's. What that row supersedes is this call site and not this reasoning: a blocking wait on
    /// a committed buffer stays correct, it is simply neither counted nor bounded by a timeout, so it is the
    /// weaker of the two and the one that goes.</para>
    ///
    /// <para><b>IT DOES NOT COUNT ITSELF, ON PURPOSE.</b> <c>GpuDeviceCounters.DrainCount</c> and
    /// <c>DrainMs</c> are the timeline row's to fill (M-G6), and a number written here from a different mechanism
    /// would be a channel a capture reads as the timeline's when it is not. Absent is not zero, which the counter
    /// struct's own doc already says.</para>
    /// </summary>
    internal static class MetalQueueDrain
    {
        /// <summary>
        /// Block until everything committed to <paramref name="queue"/> has finished, and report what the
        /// draining buffer itself saw. The reading goes to <see cref="MetalDeviceLossLatch"/>, because a drain is
        /// one of the few places this row can observe a command-buffer failure at all (M-G4).
        /// <para>
        /// A queue that will not make a command buffer reports as COMPLETED rather than as a failure, which is
        /// the honest answer: there is nothing to wait for and nothing went wrong that this call can see. A
        /// device in that state has already failed somewhere the latch was watching.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MetalCommandBufferFault DrainBlocking(MTLCommandQueue queue)
        {
            // -commandBuffer returns an AUTORELEASED object, and this method is called from teardown, which runs
            // on whatever thread the consumer disposed on. Its own pool rather than the caller's, so a drain can
            // be called from anywhere without the caller having to know that.
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            if (queue.IsNull) return MetalCommandBufferFault.Completed;

            MTLCommandBuffer buffer = queue.CommandBuffer();
            if (buffer.IsNull) return MetalCommandBufferFault.Completed;

            buffer.Commit();
            buffer.WaitUntilCompleted();
            return MetalCommandBufferFault.Read(buffer);
        }
    }
}
