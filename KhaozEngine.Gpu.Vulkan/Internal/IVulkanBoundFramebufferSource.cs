using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// WHAT A RECORDER NEEDS FROM ANY FRAMEBUFFER: the flattened <see cref="VulkanBoundFramebuffer"/> it binds.
    /// <para>
    /// TWO TYPES ANSWER IT AND THEY HAVE OPPOSITE LIFETIMES. <see cref="VulkanFramebuffer"/> is an aggregate over
    /// engine textures whose views already exist and never change after construction.
    /// <see cref="VulkanSwapchainFramebuffer"/> wraps images the presentation engine hands back and takes away
    /// again on every recreate, so its attachment is mutable by nature and its identity is what must NOT change
    /// (V-W5). Growing a mode into the first type to cover the second is the shape this interface exists instead
    /// of, and it is the same split the other native backend made for the same reason.
    /// </para>
    /// <para>
    /// IT IS EXPLICITLY IMPLEMENTED ON BOTH, so neither type publishes a second spelling of a property it already
    /// has, and so the recording path names this interface rather than either concrete type.
    /// </para>
    /// </summary>
    internal interface IVulkanBoundFramebufferSource
    {
        /// <summary>Everything a render pass instance needs from this framebuffer, as plain data.</summary>
        VulkanBoundFramebuffer AsBound { get; }

        /// <summary>
        /// Whether this is the device's SWAPCHAIN framebuffer, which is a question about the object rather than
        /// about the attachment it currently points at: an imageless frame's orphan target is still bound through
        /// this wrapper and still answers true.
        /// <para>
        /// IT IS HERE BECAUSE THE PRESENT'S SEMAPHORE PAIR HAS TO RIDE A SUBMIT THAT ORDERED THE SWAPCHAIN IMAGE'S
        /// RENDERING (https://github.com/APKiwiOrg/KhaozEngine/issues/557). The pair used to go to whichever
        /// submit reached the boundary first after the acquire, so a frame that submitted a producer list of its
        /// own first (the ocean's priming pass does exactly that) took the pair on a submission that never touched
        /// the swapchain image, and the present then waited on a semaphore signalled by work that had not rendered
        /// it. A recording knows which framebuffers it bound and nothing else does, so the answer starts here.
        /// </para>
        /// </summary>
        bool IsSwapchain { get; }
    }

    /// <summary>The one place a seam-level <see cref="IGpuFramebuffer"/> becomes something this backend can
    /// bind, so the refusal for a framebuffer another backend made is written once.</summary>
    internal static class VulkanBindableFramebuffer
    {
        /// <summary>The framebuffer as this backend's own, or a named refusal for one another backend made.
        /// </summary>
        /// <param name="framebuffer">What the seam handed in.</param>
        /// <param name="what">What the caller was doing, for the message.</param>
        /// <exception cref="ArgumentException">The framebuffer was made by another backend.</exception>
        internal static IVulkanBoundFramebufferSource Require(IGpuFramebuffer? framebuffer, string what)
            => framebuffer as IVulkanBoundFramebufferSource
                ?? throw new ArgumentException(
                    $"The framebuffer handed to {what} was not created by the native Vulkan backend, so it carries "
                    + "no VkImageView to render into. Create it through the same IGpuDevice.Factory, or use the "
                    + "device's own SwapchainFramebuffer.",
                    nameof(framebuffer));
    }
}
