using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// What an attachment does with its EXISTING contents when a render pass instance begins, which under a
    /// deferred begin is the whole of how a clear is expressed (V-A2).
    /// <para>
    /// THERE IS NO <c>DONT_CARE</c> ARM, deliberately and in both directions. A load of <c>DONT_CARE</c> leaves the
    /// attachment undefined, and V-A6 rejects the same reasoning applied to a store: undefined contents are not
    /// stable across runs and the goldens require stability on the same rasterizer. An arm that cannot be chosen
    /// is better than one that can be chosen by accident, so the enum does not have it. A measurement that ever
    /// justified one would add it here with its own determinism argument.
    /// </para>
    /// </summary>
    internal enum VulkanLoadOp
    {
        /// <summary><c>VK_ATTACHMENT_LOAD_OP_LOAD</c>: the attachment keeps what it already held.</summary>
        Load,

        /// <summary><c>VK_ATTACHMENT_LOAD_OP_CLEAR</c> with the clear value that travels beside it. What a clear
        /// recorded BEFORE the first draw of a pass folds into, instead of costing a
        /// <c>vkCmdClearAttachments</c>.</summary>
        Clear,
    }

    /// <summary>
    /// A <c>VkViewport</c> as this backend's own value, so the ONE line in the design that renders every golden
    /// upside down when it is wrong is a pure function a device-free test can read (V-A5).
    ///
    /// <para><b>THE HEIGHT IS NEGATIVE AND THAT IS THE POINT.</b> <see cref="ForFramebuffer"/> answers
    /// <c>y = height</c> with <c>height = -height</c>, which is what makes Vulkan's clip space match Direct3D's.
    /// The incumbent reports <c>ClipSpaceYInverted = false</c> for exactly this reason, <c>GpuClip.Correct</c>
    /// negates clip-space Y only when that flag is set, and every matrix the engine builds therefore assumes the
    /// flip has already happened in the viewport. Getting it wrong does not throw and does not fail to render. It
    /// renders every golden upside down.</para>
    ///
    /// <para><b>AND IT NEEDS NO EXTENSION AND NO CONDITIONAL AT THE 1.3 FLOOR.</b> A negative viewport height is
    /// core in Vulkan 1.1. The incumbent still tests for <c>VK_KHR_maintenance1</c> because it targets 1.0, and
    /// this backend's own probe refuses anything below 1.3, so there is no branch here to get wrong either.</para>
    /// </summary>
    /// <param name="X">Left edge in pixels.</param>
    /// <param name="Y">TOP edge plus the height, because the height is negative. Not the top edge.</param>
    /// <param name="Width">Width in pixels.</param>
    /// <param name="Height">NEGATIVE height in pixels. See the type remarks.</param>
    /// <param name="MinDepth">Near depth, 0 for the engine's zero-to-one depth range.</param>
    /// <param name="MaxDepth">Far depth, 1.</param>
    internal readonly record struct VulkanViewportRect(
        float X, float Y, float Width, float Height, float MinDepth, float MaxDepth)
    {
        /// <summary>
        /// The full-framebuffer viewport, WITH THE CLIP-SPACE FLIP. This is the value Veldrid's base
        /// <c>CommandList.SetFramebuffer</c> auto-applies through <c>SetFullViewports</c>, which is the only
        /// reason the engine has a viewport at all: there is no <c>SetViewport</c> on the seam, so a backend that
        /// does not emit this rasterises nothing.
        /// </summary>
        internal static VulkanViewportRect ForFramebuffer(uint width, uint height)
            => new(0f, height, width, -(float)height, 0f, 1f);
    }

    /// <summary>
    /// A <c>VkRect2D</c> as this backend's own value, for the scissor half of the same auto-applied pair.
    /// </summary>
    /// <param name="X">Left edge in pixels.</param>
    /// <param name="Y">Top edge in pixels. NOT flipped: a scissor is a framebuffer-space rectangle and has no
    /// clip space to correct for.</param>
    /// <param name="Width">Width in pixels.</param>
    /// <param name="Height">Height in pixels, POSITIVE, for the same reason.</param>
    internal readonly record struct VulkanScissorRect(int X, int Y, uint Width, uint Height)
    {
        /// <summary>The full-framebuffer scissor, which a framebuffer change applies and which
        /// <c>SetFullScissorRects</c> restores after an explicit rectangle.</summary>
        internal static VulkanScissorRect ForFramebuffer(uint width, uint height) => new(0, 0, width, height);
    }

    /// <summary>
    /// ONE COLOUR ATTACHMENT AS A BEGIN NAMES IT: the view, what happens to its contents, and the value it clears
    /// to when it clears. The store op is not here because it is <c>STORE</c> unconditionally (V-A6) and a field
    /// that only ever holds one value is a field somebody eventually sets to the other one.
    /// </summary>
    /// <param name="View">The attachment's <c>VkImageView</c>, made at TEXTURE creation (V-M11) and borrowed
    /// here.</param>
    /// <param name="LoadOp">Load, or clear with <paramref name="ClearValue"/>.</param>
    /// <param name="ClearValue">The clear colour, meaningful only under <see cref="VulkanLoadOp.Clear"/>.</param>
    internal readonly record struct VulkanColourAttachment(ulong View, VulkanLoadOp LoadOp, Color ClearValue);

    /// <summary>
    /// THE DEPTH ATTACHMENT AS A BEGIN NAMES IT, plus the one thing the colour arm has no analogue for.
    /// <para>
    /// <paramref name="Stencil"/> IS CARRIED SEPARATELY BECAUSE DYNAMIC RENDERING SPLITS THE PLANES. A render pass
    /// instance takes a <c>pDepthAttachment</c> and a <c>pStencilAttachment</c> as two structures over one view,
    /// where a <c>VkRenderPass</c> took one attachment description with one aspect-wide load op. The seam's
    /// <c>ClearDepthStencil</c> carries no stencil VALUE, and the incumbent clears the stencil plane to zero
    /// alongside the depth, so a combined format clears both here too. Leaving the stencil plane out would leave
    /// it holding whatever the last pass left, which is the determinism rule V-A6 states for a store applied to a
    /// load.
    /// </para>
    /// </summary>
    /// <param name="View">The attachment's <c>VkImageView</c>.</param>
    /// <param name="LoadOp">Load, or clear to <paramref name="ClearDepth"/>.</param>
    /// <param name="ClearDepth">The depth value, meaningful only under <see cref="VulkanLoadOp.Clear"/>. The
    /// stencil value that goes with it is always 0.</param>
    /// <param name="Stencil">Whether the format carries a stencil plane, so the begin names a stencil attachment
    /// as well as a depth one.</param>
    internal readonly record struct VulkanDepthAttachment(
        ulong View, VulkanLoadOp LoadOp, float ClearDepth, bool Stencil);
}
