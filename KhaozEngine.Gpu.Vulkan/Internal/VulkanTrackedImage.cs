using System;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE SUBRESOURCE RANGE A TRANSITION COVERS, as plain numbers, because tracking is PER SUBRESOURCE RANGE
    /// (V-F6) rather than per image. A mip chain being generated transitions one level at a time, and a whole
    /// texture being sampled transitions every level at once, so the range is part of what identifies a tracked
    /// entry rather than a detail of the barrier.
    /// </summary>
    /// <param name="BaseMipLevel">The first mip level covered.</param>
    /// <param name="LevelCount">How many mip levels, never zero.</param>
    /// <param name="BaseArrayLayer">The first array layer covered.</param>
    /// <param name="LayerCount">How many array layers, never zero.</param>
    internal readonly record struct VulkanImageSubrange(
        uint BaseMipLevel, uint LevelCount, uint BaseArrayLayer, uint LayerCount)
    {
        /// <summary>Mip 0, layer 0, one of each: what an ATTACHMENT is. The attachment view is created at mip 0
        /// layer 0 (V-M11) because <c>CreateFramebuffer</c> carries no mip or layer parameter, so a pass can
        /// transition nothing wider than the plane it renders into.</summary>
        internal static VulkanImageSubrange Attachment => new(0, 1, 0, 1);

        /// <summary>Every mip level and every array layer, which is what a sampled bind and a copy of a whole
        /// texture cover.</summary>
        internal static VulkanImageSubrange Whole(uint mipLevels, uint arrayLayers)
            => new(0, mipLevels, 0, arrayLayers);

        /// <summary>Whether this range and <paramref name="other"/> share any subresource. Two ranges that overlap
        /// without one containing the other cannot both be tracked, because a transition of one would silently
        /// leave the other entry claiming a layout the image no longer has.</summary>
        internal bool Overlaps(in VulkanImageSubrange other)
            => BaseMipLevel < other.BaseMipLevel + other.LevelCount
                && other.BaseMipLevel < BaseMipLevel + LevelCount
                && BaseArrayLayer < other.BaseArrayLayer + other.LayerCount
                && other.BaseArrayLayer < BaseArrayLayer + LayerCount;

        /// <summary>Whether every subresource of <paramref name="other"/> is also in this range. The relation a
        /// WIDER transition keys on: a whole-chain sampled bind contains every per-level entry a mip chain left
        /// behind, and containment is what makes that answerable, because each contained piece can be transitioned
        /// from its own layout and the pieces cannot be split.</summary>
        internal bool Contains(in VulkanImageSubrange other)
            => BaseMipLevel <= other.BaseMipLevel
                && other.BaseMipLevel + other.LevelCount <= BaseMipLevel + LevelCount
                && BaseArrayLayer <= other.BaseArrayLayer
                && other.BaseArrayLayer + other.LayerCount <= BaseArrayLayer + LayerCount;

        /// <summary>How many subresources this range covers, which is levels times layers. Used to tell a set of
        /// contained pieces that TILES a wider range from one that leaves a gap, without subtracting rectangles:
        /// the pieces a tracker holds are pairwise non-overlapping, so equal areas mean an exact tiling.</summary>
        internal ulong SubresourceCount => (ulong)LevelCount * LayerCount;

        /// <summary>The range as the barrier names it, with <paramref name="aspect"/> from
        /// <see cref="VulkanFormats.ToBarrierAspect"/>: every aspect the format has, which on a combined
        /// depth-stencil format is both planes.</summary>
        internal ImageSubresourceRange ToRange(ImageAspectFlags aspect)
            => new(aspect, BaseMipLevel, LevelCount, BaseArrayLayer, LayerCount);
    }

    /// <summary>
    /// ONE IMAGE THE LAYOUT TRACKER CAN TRANSITION, AS PLAIN DATA: the handle, the range, enough format
    /// information to compute the barrier's aspect mask, and the CANONICAL RESTING LAYOUT the list restores it to
    /// before <c>End</c> (V-F7).
    ///
    /// <para><b>IT IS A VALUE RATHER THAN A <see cref="VulkanTexture"/> FOR THE REASON
    /// <see cref="VulkanBoundFramebuffer"/> IS.</b> A texture holds <see cref="VulkanResourceOwner"/>, which holds
    /// <see cref="IVulkanResourceApi"/>, which is where <c>vkCreateImageView</c> lives, so a recorder with a field
    /// of that type would make a draw-time view expressible and
    /// <c>VulkanRecordingUnreachabilityTests.TheRecordingType_ReachesNoViewFactory</c> would fail. The tracker is
    /// list state, so it is under the same obligation: handles, integers and enums, with no route to a factory of
    /// any kind.</para>
    ///
    /// <para><b>THE RESTING LAYOUT TRAVELS WITH THE IMAGE RATHER THAN BEING LOOKED UP.</b> There is nothing to
    /// look it up in: the tracker is LIST-LOCAL and holds no device-wide map by construction (V-F7, section 2.5),
    /// which is the whole reason two lists cannot disagree about a texture's layout.</para>
    /// </summary>
    /// <param name="Image">The <c>VkImage</c>. Never 0: a staging texture is a <c>VkBuffer</c> with no image at
    /// all (V-C7).</param>
    /// <param name="Format">The pixel format, which decides the barrier's aspect mask.</param>
    /// <param name="DepthStencil">Whether the image carries a depth aspect rather than a colour one.</param>
    /// <param name="Resting">The canonical resting layout assigned at creation (V-F7), which is where a list
    /// assumes it will find this image and where <c>End</c> puts it back.</param>
    /// <param name="Range">The subresource range this entry covers.</param>
    internal readonly record struct VulkanTrackedImage(
        ulong Image, GpuPixelFormat Format, bool DepthStencil, VulkanRestingLayout Resting,
        VulkanImageSubrange Range)
    {
        /// <summary>An ATTACHMENT as the tracker sees it: mip 0, layer 0, and the resting layout the texture was
        /// created with. Built from a bound framebuffer's flattened attachment record, which carries the image and
        /// the resting layout for exactly this.</summary>
        internal static VulkanTrackedImage ForAttachment(in VulkanAttachment attachment)
            => new(attachment.Image, attachment.Format, attachment.DepthStencil, attachment.Resting,
                VulkanImageSubrange.Attachment);

        /// <summary>The resting layout as a real <c>VkImageLayout</c>, which is both where a list assumes this
        /// image starts and where it puts it back.</summary>
        /// <exception cref="ArgumentOutOfRangeException">A staging texture, which has no image and no layout.
        /// </exception>
        internal ImageLayout RestingLayout => VulkanFormats.ToImageLayout(Resting);

        /// <summary>The barrier's subresource range, with every aspect the format has.</summary>
        internal ImageSubresourceRange SubresourceRange
            => Range.ToRange(VulkanFormats.ToBarrierAspect(DepthStencil, Format));

        /// <summary>The layout an image of this kind takes while it is an ATTACHMENT, which is the transition the
        /// design's table asks for at a begin.</summary>
        internal ImageLayout AttachmentLayout => DepthStencil
            ? ImageLayout.DepthStencilAttachmentOptimal
            : ImageLayout.ColorAttachmentOptimal;
    }
}
