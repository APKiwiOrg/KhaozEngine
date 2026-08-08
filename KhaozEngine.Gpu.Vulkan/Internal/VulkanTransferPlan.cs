using System;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>Which of the four shapes a texture copy is, decided by the two textures' STAGING flags and by
    /// nothing else. A staging texture is a <c>VkBuffer</c> on this backend (V-C7), so each side is either an
    /// image or a buffer and the four combinations are four different <c>vkCmd*</c> calls.</summary>
    internal enum VulkanTransferCase
    {
        /// <summary><c>vkCmdCopyImage</c>: neither side is staging.</summary>
        ImageToImage,

        /// <summary><c>vkCmdCopyBufferToImage</c>: the SOURCE is a staging texture.</summary>
        BufferToImage,

        /// <summary><c>vkCmdCopyImageToBuffer</c>: the DESTINATION is a staging texture. The readback direction
        /// every golden takes.</summary>
        ImageToBuffer,

        /// <summary><c>vkCmdCopyBuffer</c>: both sides are staging textures, so neither has an image and the copy
        /// is between two software-laid-out buffers.</summary>
        BufferToBuffer,
    }

    /// <summary>
    /// THE BUFFER COPY'S TWO ORDERING BARRIERS (V-F6), as pure functions over enums, and the ONE place this
    /// backend deliberately widens the incumbent's own masks.
    ///
    /// <para><b>A BUFFER HAS NO LAYOUT, SO NOTHING ELSE ORDERS THIS COPY.</b> An image copy is ordered by the
    /// layout transitions <see cref="VulkanLayoutTracker"/> emits around it, whose stage and access masks come off
    /// the layouts themselves. A <c>VkBuffer</c> transition does not exist, so a copy out of a buffer a dispatch
    /// just wrote, or into one a draw is about to read, is unordered unless something says otherwise.</para>
    ///
    /// <para><b>THE INCUMBENT EMITS ONE BARRIER, AFTER THE COPY, NAMING <c>VERTEX_INPUT</c> AND
    /// <c>VERTEX_ATTRIBUTE_READ</c> AND NOTHING ELSE</b> (<c>VkCommandList.CopyBufferCore</c>). That orders exactly
    /// one consumer, the vertex fetch, and orders nothing on the source side at all. It happens not to bite there
    /// because the shipped readback path drains the device between the write and the map, which is what compute
    /// rule 2 already requires of a consumer. Reproducing a one-directional barrier here would be reproducing the
    /// gap rather than the behaviour, and this is a synchronisation defect a golden on a software rasterizer
    /// cannot show, which is the class the <c>sync</c> validation job exists for.</para>
    ///
    /// <para><b>SO BOTH SIDES ARE NAMED AND BOTH ARE CONSERVATIVE.</b> A buffer copy is not on any per-draw path:
    /// it happens once per readback and once per bulk upload, so two calls carrying one global barrier each cost
    /// nothing measurable and remove a whole hazard class. V-T2's gated invariant is untouched, because it is a
    /// statement about what a DRAW emits.</para>
    /// </summary>
    internal static unsafe class VulkanTransferBarrier
    {
        /// <summary>Before the copy: everything written by anything becomes available to the transfer read.
        /// </summary>
        internal static MemoryBarrier2 ToTransfer => new(
            sType: StructureType.MemoryBarrier2,
            srcStageMask: PipelineStageFlags2.AllCommandsBit,
            srcAccessMask: AccessFlags2.MemoryWriteBit,
            dstStageMask: PipelineStageFlags2.AllTransferBit,
            dstAccessMask: AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit);

        /// <summary>After the copy: the transfer's write becomes visible to everything that follows, including the
        /// HOST, which is what a staging readback is waiting for.</summary>
        internal static MemoryBarrier2 FromTransfer => new(
            sType: StructureType.MemoryBarrier2,
            srcStageMask: PipelineStageFlags2.AllTransferBit,
            srcAccessMask: AccessFlags2.TransferWriteBit,
            dstStageMask: PipelineStageFlags2.AllCommandsBit,
            dstAccessMask: AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit);

        /// <summary>
        /// ONE <c>vkCmdPipelineBarrier2</c> CARRYING ONE OF THE TWO, through the budget seam like every other
        /// barrier this backend emits.
        /// </summary>
        /// <typeparam name="TSink">The command sink, monomorphized at the call site.</typeparam>
        /// <param name="sink">Where the call is recorded.</param>
        /// <param name="toTransfer">Which of the two shapes.</param>
        internal static unsafe void Emit<TSink>(TSink sink, bool toTransfer)
            where TSink : struct, IVkCmdSink
        {
            MemoryBarrier2 barrier = toTransfer ? ToTransfer : FromTransfer;

            var dependency = new DependencyInfo(
                sType: StructureType.DependencyInfo,
                memoryBarrierCount: 1,
                pMemoryBarriers: &barrier);

            sink.PipelineBarrier(in dependency);
        }
    }

    /// <summary>
    /// EVERY DECISION THE TRANSFER FAMILY MAKES, AS PURE FUNCTIONS: which of the four staging cases a texture copy
    /// is, which subresource range each side has to be transitioned over, and the exact region each
    /// <c>vkCmd*</c> receives. Work-breakdown row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/525).
    ///
    /// <para><b>IT IS A TYPE OF ITS OWN BECAUSE THE ARITHMETIC IS THE PARITY SURFACE.</b> Every golden in the
    /// suite reads back through a copy into a staging texture and consumes <c>MappedData.RowPitch</c>, so a
    /// different region here garbles all 36 at once, silently: the readback succeeds, the pointer is valid, and
    /// the pixels are in the wrong places. Nothing here touches a device, so all of it is a plain <c>[Fact]</c>.
    /// The row and subresource OFFSETS themselves are <see cref="VulkanStagingLayout"/>'s, which row 9 already
    /// reproduced from the incumbent byte for byte and pinned against a checked-in table.</para>
    ///
    /// <para><b>THE LAYOUT SIDE IS EXPRESSED AS <see cref="VulkanTrackedImage"/> VALUES rather than as
    /// barriers.</b> A transfer needs its source in <c>TRANSFER_SRC_OPTIMAL</c> and its destination in
    /// <c>TRANSFER_DST_OPTIMAL</c>, over exactly the subresources it touches and no more, and putting them there
    /// is <see cref="VulkanLayoutTracker"/>'s job because that is the type that knows where they already are and
    /// that owes the restore at <c>End</c> (V-F7). What this type decides is the RANGE, which is the half that can
    /// be wrong: a range wider than the copy would make a mip chain mid-generation look like a partial overlap and
    /// be refused, and a range narrower than it would leave part of the copy in the wrong layout.</para>
    ///
    /// <para><b>THE COPY ASPECT IS ONE BIT AND THE BARRIER ASPECT IS EVERY BIT, and the two genuinely differ.</b>
    /// A <c>vkCmdCopyImage</c> region names exactly one aspect, and a barrier over a combined depth-stencil format
    /// must name both planes. <see cref="VulkanFormats.ToAspect"/> and
    /// <see cref="VulkanFormats.ToBarrierAspect"/> are the two answers and this type uses the first while the
    /// tracker uses the second.</para>
    /// </summary>
    internal static class VulkanTransferPlan
    {
        /// <summary>Which of the four shapes a copy between two textures is.</summary>
        internal static VulkanTransferCase CaseFor(bool sourceIsStaging, bool destinationIsStaging)
            => (sourceIsStaging, destinationIsStaging) switch
            {
                (false, false) => VulkanTransferCase.ImageToImage,
                (true, false) => VulkanTransferCase.BufferToImage,
                (false, true) => VulkanTransferCase.ImageToBuffer,
                _ => VulkanTransferCase.BufferToBuffer,
            };

        /// <summary>One <c>vkCmdCopyBuffer</c> region.</summary>
        internal static BufferCopy BufferRegion(ulong sourceOffset, ulong destinationOffset, ulong sizeBytes)
            => new(sourceOffset, destinationOffset, sizeBytes);

        /// <summary>
        /// One <c>vkCmdCopyImage</c> region: one mip level and one array layer on each side, at the origin, over
        /// <paramref name="width"/> by <paramref name="height"/> texels.
        /// </summary>
        /// <param name="sourceMipLevel">The source level.</param>
        /// <param name="sourceArrayLayer">The source layer.</param>
        /// <param name="destinationMipLevel">The destination level.</param>
        /// <param name="destinationArrayLayer">The destination layer.</param>
        /// <param name="width">Region width in texels.</param>
        /// <param name="height">Region height in texels.</param>
        /// <param name="depthStencil">Whether the copy names the depth aspect rather than the colour one.</param>
        internal static ImageCopy ImageRegion(uint sourceMipLevel, uint sourceArrayLayer,
            uint destinationMipLevel, uint destinationArrayLayer, uint width, uint height, bool depthStencil)
        {
            ImageAspectFlags aspect = VulkanFormats.ToAspect(depthStencil);

            return new ImageCopy(
                srcSubresource: new ImageSubresourceLayers(aspect, sourceMipLevel, sourceArrayLayer, 1),
                srcOffset: default,
                dstSubresource: new ImageSubresourceLayers(aspect, destinationMipLevel, destinationArrayLayer, 1),
                dstOffset: default,
                // DEPTH IS ALWAYS 1, because GpuTextureDescription has no depth at all: the seam expresses 2D
                // textures, 2D arrays and cubemaps and nothing else.
                extent: new Extent3D(width, height, 1));
        }

        /// <summary>
        /// One <c>vkCmdCopyImageToBuffer</c> or <c>vkCmdCopyBufferToImage</c> region, built from the STAGING side's
        /// software subresource layout (V-C7) and the IMAGE side's own level and layer.
        /// <para>
        /// THE STAGING SIDE DECIDES THE BUFFER TERMS AND THE IMAGE SIDE DECIDES THE SUBRESOURCE, which is the split
        /// the incumbent makes at both of its call sites and the one that is easy to get backwards: the row length
        /// and image height are the STAGING mip's dimensions in TEXELS, and the level and layer named in
        /// <c>imageSubresource</c> are the IMAGE's, which need not be the same numbers.
        /// </para>
        /// </summary>
        /// <param name="staging">The region <see cref="VulkanStagingLayout.CopyRegion"/> computed for the staging
        /// side.</param>
        /// <param name="imageMipLevel">The image side's mip level.</param>
        /// <param name="imageArrayLayer">The image side's array layer.</param>
        /// <param name="depthStencil">Whether the copy names the depth aspect.</param>
        internal static BufferImageCopy BufferImageRegion(in VulkanBufferImageCopy staging, uint imageMipLevel,
            uint imageArrayLayer, bool depthStencil)
            => new(
                bufferOffset: staging.BufferOffset,
                bufferRowLength: staging.BufferRowLength,
                bufferImageHeight: staging.BufferImageHeight,
                imageSubresource: new ImageSubresourceLayers(
                    VulkanFormats.ToAspect(depthStencil), imageMipLevel, imageArrayLayer, 1),
                imageOffset: new Offset3D((int)staging.X, (int)staging.Y, 0),
                imageExtent: new Extent3D(staging.Width, staging.Height, 1));

        /// <summary>
        /// One link of the mip chain: a blit of the whole of level <paramref name="level"/> minus one, at
        /// <paramref name="width"/> by <paramref name="height"/>, down into level <paramref name="level"/> at half
        /// those dimensions floored to at least one.
        /// <para>
        /// EVERY ARRAY LAYER IN ONE BLIT, from layer 0, which is what the incumbent does and is what makes a
        /// cubemap's six faces one call per level rather than six. The layer count is the ACTUAL one, six per
        /// logical layer on a cubemap.
        /// </para>
        /// </summary>
        /// <param name="level">The destination level, at least 1.</param>
        /// <param name="width">The SOURCE level's width.</param>
        /// <param name="height">The source level's height.</param>
        /// <param name="layerCount">How many array layers the blit covers.</param>
        internal static ImageBlit MipBlit(uint level, uint width, uint height, uint layerCount)
        {
            var region = new ImageBlit(
                srcSubresource: new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level - 1, 0, layerCount),
                dstSubresource: new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level, 0, layerCount));

            region.SrcOffsets.Element0 = default;
            region.SrcOffsets.Element1 = new Offset3D((int)width, (int)height, 1);
            region.DstOffsets.Element0 = default;
            region.DstOffsets.Element1 = new Offset3D((int)NextMip(width), (int)NextMip(height), 1);

            return region;
        }

        /// <summary>The next level's dimension: half, floored to at least one, which is the incumbent's
        /// <c>Math.Max(dimension &gt;&gt; 1, 1)</c> and is what makes a 1024 by 1 texture's chain end at 1 by 1
        /// rather than at 1 by 0.</summary>
        internal static uint NextMip(uint dimension) => Math.Max(dimension >> 1, 1);

        /// <summary>One <c>vkCmdResolveImage</c> region: mip 0, layer 0, the whole plane (V-C6).</summary>
        /// <param name="width">The resolved width.</param>
        /// <param name="height">The resolved height.</param>
        /// <param name="depthStencil">Whether the resolve names the depth aspect.</param>
        internal static ImageResolve ResolveRegion(uint width, uint height, bool depthStencil)
        {
            var layers = new ImageSubresourceLayers(VulkanFormats.ToAspect(depthStencil), 0, 0, 1);

            return new ImageResolve(
                srcSubresource: layers,
                srcOffset: default,
                dstSubresource: layers,
                dstOffset: default,
                extent: new Extent3D(width, height, 1));
        }

        /// <summary>
        /// The tracker's view of one texture over a subresource RANGE, which is what a transfer transitions. The
        /// range is exactly the subresources the copy touches: a whole-texture copy names every level and every
        /// layer, a subresource copy names one of each, and a mip-chain link names one level across every layer.
        /// </summary>
        /// <param name="texture">The texture, which must have an image.</param>
        /// <param name="baseMipLevel">The first level covered.</param>
        /// <param name="levelCount">How many levels.</param>
        /// <param name="baseArrayLayer">The first ACTUAL array layer covered.</param>
        /// <param name="layerCount">How many actual layers.</param>
        internal static VulkanTrackedImage Tracked(VulkanTexture texture, uint baseMipLevel, uint levelCount,
            uint baseArrayLayer, uint layerCount)
        {
            ArgumentNullException.ThrowIfNull(texture);

            return new VulkanTrackedImage(
                texture.Image, texture.Format, texture.Plan.DepthStencil, texture.Resting,
                new VulkanImageSubrange(baseMipLevel, levelCount, baseArrayLayer, layerCount));
        }

        /// <summary>The whole of a texture: every mip level and every ACTUAL array layer.</summary>
        internal static VulkanTrackedImage TrackedWhole(VulkanTexture texture)
        {
            ArgumentNullException.ThrowIfNull(texture);

            return Tracked(texture, 0, texture.MipLevels, 0, texture.ActualArrayLayers);
        }
    }
}
