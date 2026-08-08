using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>A width and a height in pixels, as plain data. Small enough to be a parameter and named so a
    /// swapchain extent, a surface bound and a clamp result cannot be handed to each other by accident.</summary>
    /// <param name="Width">Width in pixels.</param>
    /// <param name="Height">Height in pixels.</param>
    internal readonly record struct VulkanExtent(uint Width, uint Height)
    {
        /// <summary>
        /// The value <c>VkSurfaceCapabilitiesKHR.currentExtent</c> carries when the surface does NOT dictate its
        /// own size and the swapchain's extent decides the window's instead. It is the special
        /// <c>0xFFFFFFFF</c> in both fields, and reading it as a real size is how a swapchain gets created four
        /// billion pixels wide.
        /// </summary>
        internal static VulkanExtent SurfaceDecidesNothing => new(uint.MaxValue, uint.MaxValue);

        /// <summary>Whether this extent is the "surface dictates nothing" sentinel above.</summary>
        internal bool IsUndecided => Width == uint.MaxValue && Height == uint.MaxValue;

        /// <summary>
        /// Whether a swapchain can be created at this size at all. A zero in either dimension cannot:
        /// <c>VkSwapchainCreateInfoKHR.imageExtent</c> must be non-zero in both, and a minimised window reports
        /// exactly that through its surface. This is the reading the present boundary takes instead of calling
        /// <c>vkCreateSwapchainKHR</c> with a size the specification forbids.
        /// </summary>
        internal bool IsPresentable => Width != 0 && Height != 0;

        /// <summary>This extent with each dimension raised to at least 1, which is the shape an offscreen target
        /// standing in for an unpresentable swapchain is created at.</summary>
        internal VulkanExtent AtLeastOnePixel => new(Width == 0 ? 1 : Width, Height == 0 ? 1 : Height);
    }

    /// <summary>One <c>VkSurfaceFormatKHR</c> as plain data.</summary>
    /// <param name="Format">The pixel format.</param>
    /// <param name="ColourSpace">The colour space it is presented in.</param>
    internal readonly record struct VulkanSurfaceFormatPair(Format Format, ColorSpaceKHR ColourSpace);

    /// <summary>
    /// EVERYTHING THE SWAPCHAIN POLICY READS OFF A SURFACE, gathered by one seam call and then never touched
    /// natively again, so every choice made from it is decidable with no loader.
    /// <para>
    /// <b>IT CARRIES LESS THAN <c>VkSurfaceCapabilitiesKHR</c> DOES, and the omissions are the decision.</b> The
    /// supported composite-alpha mask and the supported usage mask are deliberately NOT here, because this
    /// backend passes <c>OPAQUE</c> and <c>COLOR_ATTACHMENT | TRANSFER_DST</c> unconditionally, reproducing the
    /// incumbent (V-W1). Reading those masks and narrowing the request against them would be a THIRD departure
    /// from the reproduction, and the design names exactly two. A surface that supported neither would fail
    /// creation here exactly as it fails creation on the incumbent, which is the parity this row is for.
    /// </para>
    /// </summary>
    /// <param name="MinImageCount">The fewest images the surface will give a swapchain.</param>
    /// <param name="MaxImageCount">The most it will give, or 0 for no limit, which is what the arithmetic below
    /// has to special-case rather than clamping against zero.</param>
    /// <param name="CurrentExtent">The size the surface currently dictates, or
    /// <see cref="VulkanExtent.SurfaceDecidesNothing"/> when it dictates none.</param>
    /// <param name="MinExtent">The smallest swapchain extent the surface accepts.</param>
    /// <param name="MaxExtent">The largest.</param>
    /// <param name="CurrentTransform">The transform the surface reports it is presenting under. Passed straight
    /// back as <c>preTransform</c>, which is departure one of V-W2.</param>
    /// <param name="Formats">Every format and colour space pair the surface supports, in the order the driver
    /// enumerated them. A single entry whose format is <c>VK_FORMAT_UNDEFINED</c> is the legacy signal that the
    /// surface has no preference and any format will do.</param>
    /// <param name="PresentModes">Every present mode the surface supports. <c>FIFO</c> is required by the
    /// specification to be among them on every implementation, which is why the mode ladder can end there without
    /// a failure arm.</param>
    internal readonly record struct VulkanSurfaceReport(
        uint MinImageCount,
        uint MaxImageCount,
        VulkanExtent CurrentExtent,
        VulkanExtent MinExtent,
        VulkanExtent MaxExtent,
        SurfaceTransformFlagsKHR CurrentTransform,
        IReadOnlyList<VulkanSurfaceFormatPair> Formats,
        IReadOnlyList<PresentModeKHR> PresentModes);
}
