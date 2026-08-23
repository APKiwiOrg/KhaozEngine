namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>What a <c>vkQueueSubmit</c> did, as the three answers the submit path branches on.</summary>
    internal enum VulkanSubmitStatus
    {
        /// <summary>The submission was accepted by the queue. The timeline value it carries WILL be signalled.
        /// </summary>
        Success,

        /// <summary>The device was LOST, latched at the submit's own site before this came back. Nothing was
        /// submitted, nothing will ever signal, and every fence on the device now reads signalled (V-F10).
        /// </summary>
        DeviceLost,

        /// <summary>A non-loss failure, which in practice is one of the two out-of-memory results. The spec
        /// requires the implementation to leave every referenced synchronisation primitive UNAFFECTED in that
        /// case, so the value this submission took will never be signalled by anything.</summary>
        Failed,
    }

    /// <summary>
    /// THE SEVEN NATIVE CALLS THE COMMAND PATH IS, behind an interface so everything above them is device-free and
    /// testable: <c>vkCreateCommandPool</c>, <c>vkAllocateCommandBuffers</c>, <c>vkResetCommandPool</c>,
    /// <c>vkBeginCommandBuffer</c>, <c>vkEndCommandBuffer</c>, <c>vkQueueSubmit</c> and
    /// <c>vkDestroyCommandPool</c>.
    /// <para>
    /// The same split <see cref="IVulkanTimelineSemaphore"/> and <see cref="IVulkanDeviceMemoryApi"/> take, and for
    /// the same reason. What is left below this line is seven driver calls with no ordering logic in them. What
    /// sits above it is the part that can be WRONG: the slot advance and its wrap, the backpressure accounting,
    /// the recording state machine, the disposal-while-in-flight handover, and above all the SUBMIT ORDER (which
    /// timeline value is allocated where, which submit signals it, and what happens to it when the submit fails).
    /// All of that runs under <c>dotnet test</c> on a machine with no Vulkan loader.
    /// </para>
    /// <para>
    /// HANDLES ARE <c>ulong</c>, not <c>VkCommandPool</c> and not <c>VkCommandBuffer</c>, so this interface and
    /// every type above it name no Silk.NET type at all. A fake in a test therefore invents plain numbers rather
    /// than binding handles it has no device to make. <c>VkCommandBuffer</c> is a DISPATCHABLE handle and is a
    /// pointer rather than a 64-bit integer on the native side, which the implementation converts at this line and
    /// nowhere above it.
    /// </para>
    /// <para>
    /// <b><see cref="CreatePool"/> TAKES NO FLAGS, AND THAT IS THE STRUCTURAL ASSERTION</b> that decision V-R2
    /// asked for. The incumbent created one pool per list with <c>VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT</c>,
    /// which tells the driver every buffer must be individually resettable and pushes it onto the slower per-buffer
    /// allocator. This backend resets the WHOLE POOL instead, which is the documented fast path and returns memory
    /// to the pool's arena in one operation. There is no parameter through which that flag could be asked for, so
    /// the decision cannot be undone by a call site: it can only be undone by editing this seam, which is where a
    /// reader will find the reason.
    /// </para>
    /// <para>
    /// THERE IS NO RESET-BUFFER MEMBER AND NO SECOND SUBMIT, for the same shape of reason.
    /// <c>vkResetCommandBuffer</c> is unreachable from here, and <see cref="Submit"/> is ONE
    /// <c>vkQueueSubmit</c> per submission (V-F3): the incumbent's second empty submit signalling an internal
    /// tracking fence has nowhere to be expressed.
    /// </para>
    /// </summary>
    internal interface IVulkanCommandApi
    {
        /// <summary>
        /// <c>vkCreateCommandPool</c> on the device's one graphics queue family, with NO flags. One of these per
        /// slot per list.
        /// </summary>
        /// <returns>The <c>VkCommandPool</c> handle. Never 0 on success.</returns>
        ulong CreatePool();

        /// <summary>
        /// <c>vkAllocateCommandBuffers</c> for exactly ONE <c>VK_COMMAND_BUFFER_LEVEL_PRIMARY</c> buffer out of
        /// <paramref name="pool"/>, at pool creation time. There is no secondary level here and no sub-list
        /// concept anywhere in this backend (section 6.4).
        /// </summary>
        /// <param name="pool">The pool this buffer belongs to and is freed with.</param>
        /// <returns>The <c>VkCommandBuffer</c> handle as an integer. Never 0 on success.</returns>
        ulong AllocatePrimaryBuffer(ulong pool);

        /// <summary>
        /// <c>vkResetCommandPool</c> over the WHOLE pool, with no
        /// <c>VK_COMMAND_POOL_RESET_RELEASE_RESOURCES_BIT</c>, so the memory the last record used stays in the
        /// pool's arena for the next one. Called at every <c>Begin</c>, on the slot the ring has just advanced
        /// onto and only after that slot's last submission has completed.
        /// </summary>
        void ResetPool(ulong pool);

        /// <summary>
        /// <c>vkBeginCommandBuffer</c> with <c>VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT</c>, which is exactly
        /// what a slot ring makes true: every record is submitted at most once and the pool is reset before the
        /// buffer is recorded into again.
        /// </summary>
        void BeginOneTimeSubmit(ulong commandBuffer);

        /// <summary><c>vkEndCommandBuffer</c>, sealing the record for submission.</summary>
        void EndRecording(ulong commandBuffer);

        /// <summary>
        /// ONE <c>vkQueueSubmit</c> (V-F3) of one command buffer, signalling the device's timeline semaphore at
        /// <paramref name="signalValue"/> through the <c>VkTimelineSemaphoreSubmitInfo</c> chained onto the
        /// submit info. No fence, because this backend has no <c>VkFence</c> on any path the completion model
        /// uses, and no wait semaphore at all on the headless path.
        /// <para>
        /// A WINDOWED FRAME'S FIRST SUBMIT ALSO WAITS AND ALSO SIGNALS (V-W3): it waits on the acquire's binary
        /// semaphore at <c>COLOR_ATTACHMENT_OUTPUT</c> and signals the acquired image's render-finished
        /// semaphore, which the present of that image waits on. That is the whole of what replaces the
        /// incumbent's per-frame CPU stall.
        /// </para>
        /// <para>
        /// THE CALLER HOLDS THE SUBMIT LOCK. This member does not take one: the ordering guarantee that a
        /// timeline's signals strictly increase comes from the value being allocated and submitted under one
        /// lock, and a lock down here could not provide it because the allocation happens above.
        /// </para>
        /// </summary>
        /// <param name="commandBuffer">The sealed buffer to execute.</param>
        /// <param name="signalValue">The timeline value this submission signals on completion.</param>
        /// <param name="frame">The swapchain's binary pair for this frame, or the default for none, which is
        /// every headless submit, every submit after the first in one frame, and every submit under
        /// <c>KE_VULKAN_ACQUIRE=stall</c>. See <see cref="VulkanFrameSemaphores"/>.</param>
        /// <param name="failure">On <see cref="VulkanSubmitStatus.Failed"/>, the result's own token for the
        /// message the caller throws. Null otherwise.</param>
        VulkanSubmitStatus Submit(ulong commandBuffer, ulong signalValue, in VulkanFrameSemaphores frame,
            out string? failure);

        /// <summary>
        /// <c>vkDestroyCommandPool</c>, which also frees every buffer allocated from it. TERMINAL: it retires
        /// nothing and allocates nothing, so it is legal in the retire list's teardown drain, which runs between
        /// <c>vkDeviceWaitIdle</c> and <c>vkDestroyDevice</c>. Skipped by the implementation when the device is
        /// dead, because <c>vkDestroyDevice</c> (or the loss that killed it) already destroyed every object made
        /// from it and calling in afterwards aborts the process through the Vulkan loader.
        /// </summary>
        void DestroyPool(ulong pool);
    }
}
