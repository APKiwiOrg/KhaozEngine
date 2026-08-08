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
