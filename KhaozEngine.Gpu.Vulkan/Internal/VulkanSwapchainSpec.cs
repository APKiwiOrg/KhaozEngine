using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// EXACTLY WHAT A <c>VkSwapchainCreateInfoKHR</c> WILL SAY, decided above the driver by
    /// <see cref="VulkanSwapchainPolicy"/> and handed down to one seam call that copies it into the create-info
    /// field for field.
    /// <para>
    /// <b>THE POINT OF THE SPLIT IS THAT V-W1'S REPRODUCTION IS ASSERTABLE WITH NO LOADER.</b> Every value here
    /// is a reproduction of the incumbent's, and the reproduction is what the whole row's parity claim rests on:
    /// the present path is visible only to a human eye, so a value that drifted would be found by a tester at a
    /// window or not at all (MV9). Deciding them in a pure function makes each one a plain assertion in a
    /// <c>[Fact]</c> instead.
    /// </para>
    /// </summary>
    /// <param name="ImageCount">How many images the swapchain is asked for:
    /// <c>min(maxImageCount, minImageCount + 1)</c>, with a maximum of 0 read as no limit.</param>
    /// <param name="Format">The image format, <c>B8G8R8A8_UNORM</c> on the shipped path.</param>
    /// <param name="ColourSpace">Its colour space, <c>SRGB_NONLINEAR</c>.</param>
    /// <param name="Extent">The image extent, clamped to the surface's reported minimum and maximum.</param>
    /// <param name="PresentMode">The present mode chosen by the vsync ladder.</param>
    /// <param name="PreTransform">The surface's own <c>currentTransform</c>, which is departure one of V-W2. The
    /// incumbent hardcodes <c>IDENTITY</c>, which is wrong on any surface reporting a rotation.</param>
    /// <param name="CompositeAlpha">Always <c>OPAQUE</c>, reproduced.</param>
    /// <param name="Usage">Always <c>COLOR_ATTACHMENT | TRANSFER_DST</c>, reproduced. Rendering goes DIRECTLY
    /// into the swapchain image, and the transfer bit is what lets a capture copy one out.</param>
    /// <param name="Clipped">Always true, reproduced.</param>
    internal readonly record struct VulkanSwapchainSpec(
        uint ImageCount,
        Format Format,
        ColorSpaceKHR ColourSpace,
        VulkanExtent Extent,
        PresentModeKHR PresentMode,
        SurfaceTransformFlagsKHR PreTransform,
        CompositeAlphaFlagsKHR CompositeAlpha,
        ImageUsageFlags Usage,
        bool Clipped)
    {
        /// <summary>The composite alpha every swapchain this backend creates asks for.</summary>
        internal const CompositeAlphaFlagsKHR Opaque = CompositeAlphaFlagsKHR.OpaqueBitKhr;

        /// <summary>The usage bits every swapchain this backend creates asks for.</summary>
        internal const ImageUsageFlags ColourAndTransferDst =
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit;

        /// <summary>
        /// Whether this spec can actually be handed to <c>vkCreateSwapchainKHR</c>. False on a zero extent, which
        /// a minimised window produces through its surface and which the specification forbids. The present
        /// boundary reads this instead of calling in and getting an error back, which is what makes a minimise
        /// a quiet frame rather than a validation failure.
        /// </summary>
        internal bool IsCreatable => Extent.IsPresentable && ImageCount != 0;
    }
}
