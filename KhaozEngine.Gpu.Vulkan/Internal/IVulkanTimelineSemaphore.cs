using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE THREE NATIVE CALLS A TIMELINE SEMAPHORE IS, behind an interface so everything ABOVE them is device-free
    /// and testable: <c>vkGetSemaphoreCounterValue</c>, <c>vkWaitSemaphores</c> and <c>vkDestroySemaphore</c>.
    /// <para>
    /// This is the same split <c>ID3D11FenceTimeline</c> takes on the other backend, and for the same reason. What
    /// is left below this line is a handful of driver calls with no ordering logic in them. What sits above it in
    /// <see cref="VulkanTimeline"/> is the part that can be WRONG: the monotonic value allocation, the fence
    /// target lifecycle, the dead-device answers, the drain's decision about what counts as a drain, and the
    /// retire list's release rule. All of that runs under <c>dotnet test</c> on a machine with no Vulkan loader.
    /// </para>
    /// <para>
    /// THERE IS NO SIGNAL MEMBER HERE, deliberately. <c>vkSignalSemaphore</c> exists and this backend does not use
    /// it: every value on this timeline is signalled by a <c>vkQueueSubmit</c> (V-F3), because a host signal would
    /// raise the counter without any GPU work having completed, which is exactly the lie the seam's fence contract
    /// forbids. The value ALLOCATION for a submit lives on <see cref="VulkanTimeline"/> and the submit itself is
    /// row 7's (https://github.com/APKiwiOrg/KhaozEngine/issues/517).
    /// </para>
    /// </summary>
    internal interface IVulkanTimelineSemaphore : IDisposable
    {
        /// <summary>
        /// The highest value the GPU has reached, as a NON-BLOCKING poll (<c>vkGetSemaphoreCounterValue</c>).
        /// Monotonic: it never goes backwards, and it may lag reality, so a completed signal reads as completed no
        /// earlier than the next poll and never later.
        /// <para>
        /// This is what <c>IGpuFence.Signaled</c> becomes, so it must never wait and must never take a lock. A
        /// device loss observed here is latched at this site and the value returned is not to be trusted, which is
        /// why <see cref="VulkanTimeline"/> re-reads liveness after every call.
        /// </para>
        /// </summary>
        ulong Read();

        /// <summary>
        /// Block until the counter reaches <paramref name="value"/> (<c>vkWaitSemaphores</c>, infinite timeout).
        /// <para>
        /// NO TIMEOUT, on purpose. A GPU that never reaches the value has hung, and the honest behaviour there is
        /// to block, the same as <c>vkQueueWaitIdle</c> and the Metal equivalent. A timeout would turn a hang into
        /// silent forward progress over work that has not happened, which is worse in exactly the way this backend
        /// exists to avoid.
        /// </para>
        /// </summary>
        /// <param name="value">The timeline value to wait for.</param>
        /// <returns>True when the counter reached the value. False when the wait ended because the device was
        /// LOST, which is latched at this site before the false comes back.</returns>
        bool WaitUntil(ulong value);
    }
}
