using System.Collections.Generic;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// WHAT AN ACQUIRE OR A PRESENT DID, as the outcomes the boundary branches on. Decision V-W7: the incumbent
    /// ignores <c>vkQueuePresentKHR</c>'s result entirely, so it can never learn that the surface it is presenting
    /// to has changed underneath it.
    /// </summary>
    internal enum VulkanPresentOutcome
    {
        /// <summary>The call did what it was asked. An acquire holds an image, a present queued one.</summary>
        Success,

        /// <summary>
        /// <c>VK_SUBOPTIMAL_KHR</c>. The operation SUCCEEDED and an acquire under it really did hold an image, but
        /// the swapchain no longer matches the surface properties well. Treated as a recreate request that runs at
        /// the next boundary rather than as a failure, which is why it is a separate outcome from
        /// <see cref="OutOfDate"/>.
        /// </summary>
        Suboptimal,

        /// <summary>
        /// <c>VK_ERROR_OUT_OF_DATE_KHR</c>. The swapchain no longer matches the surface at all and NOTHING
        /// happened: an acquire under it holds no image and signalled no semaphore, which is what makes retiring
        /// the semaphore it was handed safe.
        /// </summary>
        OutOfDate,

        /// <summary>
        /// <c>VK_NOT_READY</c> or <c>VK_TIMEOUT</c> from a zero-timeout acquire probe. No image and no signal, and
        /// it is not an error: it is the answer that says the CPU is about to BLOCK, which is the whole of what
        /// <c>AcquireWaitCount</c> counts.
        /// </summary>
        NotReady,

        /// <summary>
        /// <c>VK_ERROR_SURFACE_LOST_KHR</c>. The surface itself is gone, so recreating the swapchain against it
        /// cannot help and the boundary stops presenting rather than spinning on a recreate that will fail the
        /// same way.
        /// </summary>
        SurfaceLost,

        /// <summary><c>VK_ERROR_DEVICE_LOST</c>, latched at the call's own site before this came back (V-G4).
        /// </summary>
        DeviceLost,

        /// <summary>Any other failure, which in practice is one of the two out-of-memory results.</summary>
        Failed,
    }

    /// <summary>
    /// THE NATIVE CALLS A SWAPCHAIN IS, behind an interface so the acquire ring, the present boundary, the
    /// <c>OUT_OF_DATE</c> state machine and the retirement order are all decidable with no loader:
    /// <c>vkCreateSwapchainKHR</c>, <c>vkGetSwapchainImagesKHR</c>, <c>vkDestroySwapchainKHR</c>,
    /// <c>vkCreateImageView</c> and <c>vkDestroyImageView</c> over swapchain images, <c>vkCreateSemaphore</c> and
    /// <c>vkDestroySemaphore</c> for the binary pair, <c>vkAcquireNextImageKHR</c> in both of MV2's shapes, and
    /// <c>vkQueuePresentKHR</c>.
    /// <para>
    /// <b>THE VIEWS ARE HERE RATHER THAN ON <see cref="IVulkanResourceApi"/>, and the reason is ownership rather
    /// than tidiness.</b> A swapchain image is NOT allocated by this backend: it belongs to the presentation
    /// engine, has no <c>VkDeviceMemory</c> of ours behind it and must not be destroyed. Routing its view through
    /// the resource seam would put it beside images the allocator owns, which is exactly the confusion that ends
    /// with somebody freeing a chunk the driver handed us. Its format is a SURFACE format too, which
    /// <c>GpuPixelFormat</c> cannot always express, so the view call here takes Vulkan's own format value.
    /// </para>
    /// <para>
    /// <b>NEITHER ACQUIRE NOR PRESENT TAKES A LOCK, AND ONLY ONE OF THEM NEEDS ONE.</b> The present goes through
    /// the queue, so its caller holds the device's submit lock (V-W8). The acquire touches no queue at all and its
    /// caller holds nothing, which is what lets the boundary probe, block and retry without serialising against
    /// a submit on another thread.
    /// </para>
    /// </summary>
    internal interface IVulkanSwapchainApi
    {
        /// <summary>
        /// <c>vkCreateSwapchainKHR</c> with <paramref name="spec"/> copied into the create-info field for field.
        /// </summary>
        /// <param name="surface">The surface to present to.</param>
        /// <param name="spec">Everything the create-info says, decided by <see cref="VulkanSwapchainPolicy"/>.</param>
        /// <param name="oldSwapchain">The swapchain being replaced, or 0 on the first creation. Passing it lets
        /// the driver reuse presentable images rather than tearing the whole chain down, and the old handle is
        /// still the caller's to DESTROY afterwards.
        /// <para>
        /// IT IS NOT STILL THE CALLER'S TO USE, AND THAT HOLDS EVEN WHEN THIS CALL FAILS. The specification
        /// retires <paramref name="oldSwapchain"/> as an effect of the call rather than of the call succeeding, and
        /// a retired swapchain may already have had the images nothing had acquired freed underneath it. So a
        /// caller whose creation came back 0 may not carry on acquiring from or presenting to the old one, and may
        /// not hand the same handle to the next attempt either
        /// (VUID-VkSwapchainCreateInfoKHR-oldSwapchain-01933). Retire it and pass 0.
        /// </para></param>
        /// <param name="failure">On a 0 return, the result's own token for the message the caller logs.</param>
        /// <returns>The <c>VkSwapchainKHR</c> handle, or 0 when creation failed.</returns>
        ulong CreateSwapchain(ulong surface, in VulkanSwapchainSpec spec, ulong oldSwapchain, out string? failure);

        /// <summary><c>vkGetSwapchainImagesKHR</c>. The images belong to the presentation engine and are never
        /// destroyed by this backend.</summary>
        IReadOnlyList<ulong> GetImages(ulong swapchain);

        /// <summary><c>vkDestroySwapchainKHR</c>. TERMINAL, and legal only once the queue has drained past every
        /// submission that referenced one of its images.</summary>
        void DestroySwapchain(ulong swapchain);

        /// <summary><c>vkCreateImageView</c> over one swapchain image, 2D, mip 0, layer 0, colour aspect.</summary>
        ulong CreateImageView(ulong image, Silk.NET.Vulkan.Format format);

        /// <summary><c>vkDestroyImageView</c>. Destroys the view only, never the image behind it.</summary>
        void DestroyImageView(ulong view);

        /// <summary><c>vkCreateSemaphore</c> with no type chained on, which is a BINARY semaphore.
        /// <c>VK_KHR_swapchain</c> accepts no timeline semaphore at acquire or present, which is the whole reason
        /// this backend has any binary semaphore at all (V-F5).</summary>
        ulong CreateBinarySemaphore();

        /// <summary><c>vkDestroySemaphore</c>. Legal only on a semaphore with no pending signal, which the
        /// caller establishes by draining first.</summary>
        void DestroySemaphore(ulong semaphore);

        /// <summary>
        /// <c>vkAcquireNextImageKHR</c> signalling <paramref name="semaphore"/> (V-W3). The index comes back
        /// SYNCHRONOUSLY even though the signal is asynchronous, which is what keeps the acquire-at-present-time
        /// timing while moving the wait off the CPU.
        /// </summary>
        /// <param name="swapchain">The swapchain to acquire from.</param>
        /// <param name="semaphore">The binary semaphore this acquire signals, from the acquire ring.</param>
        /// <param name="blockUntilReady">True to pass an infinite timeout, false to pass zero. The zero-timeout
        /// call is the PROBE that establishes whether the CPU is about to block, which is what makes
        /// <c>AcquireWaitCount</c> a reading rather than a count of calls. A probe that comes back
        /// <see cref="VulkanPresentOutcome.NotReady"/> acquired nothing and signalled nothing, so calling again is
        /// legal.</param>
        /// <param name="imageIndex">The acquired image's index, meaningful on
        /// <see cref="VulkanPresentOutcome.Success"/> and <see cref="VulkanPresentOutcome.Suboptimal"/>.</param>
        VulkanPresentOutcome AcquireNextImage(ulong swapchain, ulong semaphore, bool blockUntilReady,
            out uint imageIndex);

        /// <summary>
        /// THE INCUMBENT'S ACQUIRE, RESTORED EXACTLY for <c>KE_VULKAN_ACQUIRE=stall</c>: acquire with a
        /// <c>VkFence</c>, then block the CPU on <c>vkWaitForFences</c> with an infinite timeout. The fence is the
        /// implementation's own and is reset between uses, so nothing above this line has to know a
        /// <c>VkFence</c> exists in a backend whose completion model is one timeline semaphore.
        /// </summary>
        VulkanPresentOutcome AcquireNextImageStalling(ulong swapchain, out uint imageIndex);

        /// <summary>
        /// <c>vkQueuePresentKHR</c> of <paramref name="imageIndex"/>, waiting on
        /// <paramref name="waitSemaphore"/> or on nothing when it is 0.
        /// <para>
        /// A ZERO WAIT SEMAPHORE IS THE STALL MODE'S SHAPE AND IS A SPECIFICATION VIOLATION, reproduced
        /// deliberately for the A/B and rejected by a validation layer, which is why that mode and the validation
        /// knob are not usable together. On the shipped path this is always the image's own render-finished
        /// semaphore.
        /// </para>
        /// <para>THE CALLER HOLDS THE SUBMIT LOCK, because this goes through the one queue.</para>
        /// </summary>
        VulkanPresentOutcome Present(ulong swapchain, uint imageIndex, ulong waitSemaphore);
    }
}
