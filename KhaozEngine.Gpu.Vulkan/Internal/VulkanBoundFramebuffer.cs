using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// ONE ATTACHMENT AS PLAIN DATA: the view a begin names, the image a transition names, and the format that
    /// decides whether the depth arm carries a stencil plane.
    /// </summary>
    /// <param name="View">The <c>VkImageView</c> at mip 0 layer 0, created at TEXTURE creation (V-M11) and
    /// borrowed here. Never 0 on a real attachment.</param>
    /// <param name="Image">The <c>VkImage</c> behind it. Carried for row 14
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/524), whose barrier tracker transitions every attachment
    /// into its attachment layout at the begin and back to its resting layout at <c>End</c>. Nothing in this row
    /// reads it, and it is here rather than in row 14's own record so that row adds a transition rather than a
    /// second copy of the framebuffer's contents.</param>
    /// <param name="Format">The attachment's pixel format.</param>
    /// <param name="DepthStencil">Whether this is the depth attachment rather than a colour one.</param>
    internal readonly record struct VulkanAttachment(
        ulong View, ulong Image, GpuPixelFormat Format, bool DepthStencil);

    /// <summary>
    /// EVERYTHING A RENDER PASS INSTANCE NEEDS FROM A FRAMEBUFFER, AS PLAIN DATA, and the shape decisions V-M11
    /// and V-D2 oblige this row to hold instead of the framebuffer itself.
    ///
    /// <para><b>A <see cref="VulkanFramebuffer"/> WOULD BE A FIELD THE UNREACHABILITY WALK REFUSES.</b> It is
    /// built from <see cref="VulkanTexture"/>s, and a texture holds <see cref="VulkanResourceOwner"/>, which holds
    /// <see cref="IVulkanResourceApi"/>, which is where <c>vkCreateImageView</c> lives. A recorder with a field of
    /// that type would therefore make a draw-time view expressible and
    /// <c>VulkanRecordingUnreachabilityTests.TheRecordingType_ReachesNoViewFactory</c> would fail, which is
    /// exactly the obligation row 10 wrote down for row 11 and row 11 discharged with
    /// <see cref="VulkanBoundSet"/>. This is that same discipline applied to the other aggregate a recorder
    /// binds: handles, integers and enums, with no route to a factory of any kind.</para>
    ///
    /// <para><b>THE ARRAY IS HELD BY REFERENCE AND NEVER COPIED</b>, so a bind allocates nothing. The framebuffer
    /// writes it once at creation and a bind reads it.</para>
    ///
    /// <para><b>IDENTITY IS AN <see cref="Id"/> RATHER THAN A REFERENCE</b>, which is what the framebuffer-change
    /// guard compares. Veldrid's base <c>CommandList.SetFramebuffer</c> guards on <c>_framebuffer != fb</c>, and
    /// the D3D11 native backend reproduces that with <c>ReferenceEquals</c>, but plain data has no reference to
    /// compare, so each <see cref="VulkanFramebuffer"/> takes a process-unique number at construction and carries
    /// it here. Zero means nothing is bound, which is what a fresh <c>VkCommandBuffer</c> holds.</para>
    /// </summary>
    /// <param name="Id">The bound framebuffer's process-unique identity, or 0 for none.</param>
    /// <param name="Width">Framebuffer width in pixels, which is the render area, the viewport and the full
    /// scissor.</param>
    /// <param name="Height">Framebuffer height in pixels.</param>
    /// <param name="Colour">The colour attachments in order, or null when there are none (a depth-only shadow
    /// pass).</param>
    /// <param name="Depth">The depth attachment, default when the framebuffer declares none.</param>
    internal readonly record struct VulkanBoundFramebuffer(
        ulong Id, uint Width, uint Height, VulkanAttachment[]? Colour, VulkanAttachment Depth)
    {
        /// <summary>Whether a framebuffer is bound at all. False for the state a fresh recording starts in.
        /// </summary>
        internal bool IsBound => Id != 0;

        /// <summary>How many colour attachments a begin names.</summary>
        internal int ColourCount => Colour?.Length ?? 0;

        /// <summary>Whether a begin names a depth attachment. A real attachment always has a view, so the handle
        /// is the answer and there is no second flag to disagree with it.</summary>
        internal bool HasDepth => Depth.View != 0;

        /// <summary>The colour attachments as a span, empty rather than null when there are none, so the begin
        /// path has one shape.</summary>
        internal ReadOnlySpan<VulkanAttachment> ColourAttachments => Colour;
    }
}
