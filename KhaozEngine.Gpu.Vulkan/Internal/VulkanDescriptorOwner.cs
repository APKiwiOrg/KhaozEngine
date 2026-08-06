using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// WHAT THE DESCRIPTOR SUBSYSTEM NEEDS FROM ITS DEVICE, in one object, for the same reason
    /// <see cref="VulkanResourceOwner"/> exists: three things that must always travel together and must always be
    /// the SAME three.
    ///
    /// <para><b>IT IS DELIBERATELY NOT <see cref="VulkanResourceOwner"/>, AND THAT SEPARATION IS DECISION V-D2's
    /// (6.3).</b> The recording type's field graph legitimately reaches a <see cref="VulkanResourceOwner"/>,
    /// through the staging block lifetime edge that
    /// <c>VulkanRecordingUnreachabilityTests</c> names and allows. Hanging the descriptor pool off that record
    /// would put it on the far side of that allowance, where the unreachability walk could never see it, and the
    /// architecture test would keep passing while a draw could allocate a descriptor set. So the descriptor
    /// subsystem gets its own owner, held by the device and by the resource factory and by nothing a recorder can
    /// reach.</para>
    ///
    /// <para><b>IT CARRIES NO MEMORY ALLOCATOR</b>, because nothing here allocates device memory: a descriptor
    /// pool's storage is the driver's own and is not visible through <c>vkAllocateMemory</c> at all.</para>
    /// </summary>
    /// <param name="Api">The nine native descriptor calls.</param>
    /// <param name="Timeline">The device's ONE completion timeline, whose current value a deferred free is
    /// recorded at (V-F9).</param>
    /// <param name="Retired">The device's ONE deferred-disposal list.</param>
    internal sealed record VulkanDescriptorOwner(
        IVulkanDescriptorApi Api,
        VulkanTimeline Timeline,
        VulkanRetireList Retired)
    {
        /// <summary>
        /// HOLD ONE TERMINAL FREE behind the timeline (V-F9): record the value the device has handed out most
        /// recently, and run <paramref name="release"/> once the GPU has passed it.
        ///
        /// <para><b>A DESCRIPTOR SET FREED UNDER A SUBMISSION THAT BINDS IT IS UNDEFINED BEHAVIOUR</b>, and it is
        /// the quiet kind: the driver reads a recycled slot and draws something. So a set's disposal is deferred
        /// exactly as a buffer's or a texture's is, through the same list, and the pool's per-type budget is
        /// restored at the deferred free rather than at <c>Dispose</c>, because the budget is only genuinely free
        /// once the descriptors are.</para>
        ///
        /// <para><b>TERMINAL, like every other entry in that list.</b> One entry restores the budget and makes the
        /// one <c>vkFreeDescriptorSets</c> call, and retires nothing further, so the retirement depth stays at the
        /// generation the device's two teardown drains already cover.</para>
        /// </summary>
        internal void RetireTerminal(Action release)
        {
            ArgumentNullException.ThrowIfNull(release);
            Retired.Retire(Timeline.LastAllocated, release);
        }
    }
}
