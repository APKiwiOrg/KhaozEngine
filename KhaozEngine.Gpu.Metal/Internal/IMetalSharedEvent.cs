using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE THREE NATIVE CALLS AN <c>MTLSharedEvent</c> IS, behind an interface so everything ABOVE them is
    /// device-free and testable: <c>signaledValue</c>, <c>waitUntilSignaledValue:timeoutMS:</c> and
    /// <c>encodeSignalEvent:value:</c>. Creation is the fourth member M-F1 names and it is the implementation's
    /// constructor, because a seam that could hand back an event has to name a device.
    /// <para>
    /// This is the same split <c>IVulkanTimelineSemaphore</c> and <c>ID3D11FenceTimeline</c> take on the other
    /// two backends, and for the same reason. What is left below this line is a handful of driver calls with no
    /// ordering logic in them. What sits above it in <see cref="MetalTimeline"/> is the part that can be WRONG:
    /// the monotonic value allocation, the fence target lifecycle, the dead-device answers, and the drain's
    /// decision about what counts as a drain. All of that runs under <c>dotnet test</c> on a machine with no
    /// Metal at all.
    /// </para>
    /// <para>
    /// THERE IS NO HOST-SIGNAL MEMBER HERE, deliberately. <c>MTLSharedEvent</c> has a settable
    /// <c>signaledValue</c> and this backend never writes it: every value on this timeline is signalled by a
    /// command buffer reaching <c>encodeSignalEvent:value:</c>, because a host signal would raise the counter
    /// with no GPU work having completed, which is exactly the lie the seam's fence contract forbids. The value
    /// ALLOCATION lives on <see cref="MetalTimeline"/> and the submit itself is row 7's
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/573).
    /// </para>
    /// </summary>
    internal interface IMetalSharedEvent : IDisposable
    {
        /// <summary>
        /// The highest value the GPU has reached (<c>signaledValue</c>), as a NON-BLOCKING property read.
        /// Monotonic: it never goes backwards, and it may lag reality, so a completed signal reads as completed
        /// no earlier than the next poll and never later.
        /// <para>
        /// This is what <c>IGpuFence.Signaled</c> becomes, so it must never wait and must never take a lock. It
        /// is also the one call on a polling path, which is why the implementation caches its selector rather
        /// than registering one per read.
        /// </para>
        /// </summary>
        ulong Read();

        /// <summary>
        /// Block until the counter reaches <paramref name="value"/>, or until
        /// <paramref name="timeoutMs"/> milliseconds have passed (<c>waitUntilSignaledValue:timeoutMS:</c>).
        /// <para>
        /// THE TIMEOUT IS A PARAMETER BECAUSE METAL'S CALL HAS ONE, which is the difference from the Vulkan
        /// sibling, where the equivalent takes an infinite timeout and the design argues that blocking forever
        /// on a hung GPU is the honest behaviour. That argument is unchanged here and
        /// <see cref="MetalTimeline.WaitForIdle"/> still blocks for as long as the GPU takes. What it does with
        /// the parameter is re-check DEVICE LIVENESS between slices, which the Vulkan drain has no equivalent
        /// need for. See that member for the whole reasoning.
        /// </para>
        /// </summary>
        /// <param name="value">The timeline value to wait for.</param>
        /// <param name="timeoutMs">How long to block before giving up on this attempt. A <c>ulong</c> because
        /// the SDK's second parameter is a <c>uint64_t</c>, and row 4 absorbs this seam as declared.</param>
        /// <returns>True when the counter reached the value. False when the wait timed out.</returns>
        bool WaitUntil(ulong value, ulong timeoutMs);

        /// <summary>
        /// Encode a signal of <paramref name="value"/> on this event into
        /// <paramref name="commandBuffer"/> (<c>encodeSignalEvent:value:</c>), so the value is reached when the
        /// GPU finishes that buffer's work.
        /// <para>
        /// THE RECEIVER OF THE REAL CALL IS THE COMMAND BUFFER and the event is its argument, which is why this
        /// member takes a buffer handle rather than living on the command buffer's own type. Keeping it here
        /// keeps the whole timeline behind one seam, so a device-free test can drive an entire submission
        /// stream, and it is what makes <see cref="MetalTimeline.EncodeSignalForSubmit"/> able to allocate and
        /// encode as one step inside the caller's submit lock.
        /// </para>
        /// </summary>
        /// <param name="commandBuffer">The <c>MTLCommandBuffer</c> being prepared for commit.</param>
        /// <param name="value">The value that buffer's completion signals.</param>
        void EncodeSignal(IntPtr commandBuffer, ulong value);
    }
}
