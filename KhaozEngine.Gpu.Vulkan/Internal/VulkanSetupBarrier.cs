using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE BARRIERS AND CLEAR VALUES THE SETUP COMMAND BUFFER RECORDS, as pure functions over plain values.
    /// Decisions V-M10, V-F7 and V-F8, section 9.3.
    ///
    /// <para><b>THREE TRANSITIONS AND NOTHING ELSE.</b> A newly created image's FIRST-EVER transition out of
    /// <c>VK_IMAGE_LAYOUT_UNDEFINED</c>, a transition INTO <c>TRANSFER_DST_OPTIMAL</c> so a clear or an upload can
    /// write it, and the transition back OUT to the resting layout afterwards. The general per-list barrier model
    /// is <see cref="VulkanImageTransition"/> plus <see cref="VulkanLayoutTracker"/>, and this type is
    /// deliberately not folded into it: these three are the setup buffer's own, they run once per resource with a
    /// resource that has no consumer yet, and they are off every hot path by construction.</para>
    ///
    /// <para><b><c>UNDEFINED</c> APPEARS AS AN OLD LAYOUT IN EXACTLY ONE PLACE HERE (V-F8), AND IT IS NAMED SO IT
    /// CANNOT SPREAD.</b> A transition out of <c>UNDEFINED</c> is permitted to DISCARD the image's contents, which
    /// makes it the cheap transition and the tempting one, and using it on an image whose contents are still wanted
    /// produces output that varies by driver and by run. The goldens require stability on the same rasterizer. The
    /// one legitimate site is <see cref="FirstUse"/>, where the image was created microseconds ago and has no
    /// contents to lose. The other legitimate site in the whole backend is a swapchain image being reacquired for a
    /// frame that will fully overwrite it, which is <see cref="VulkanImageTransition.Reacquired"/>.</para>
    ///
    /// <para><b>THE MASKS ARE DELIBERATELY CONSERVATIVE AND THAT IS NOT THE SAME MISTAKE THE INCUMBENT MAKES.</b>
    /// The destination of a resting-layout transition is <c>ALL_COMMANDS</c> with memory read and write, because
    /// the resource has no consumer yet: it was created a moment ago and the backend cannot know whether the first
    /// thing to touch it is a sampler, an attachment write or a compute store. Narrowing that would be guessing.
    /// The incumbent's defect was the opposite shape and worth keeping distinct: its transition helper was a long
    /// if/else over layout PAIRS ending in a debug assertion, and in Release it silently emitted <c>NONE</c> on
    /// both sides for a pair it did not handle, which is not conservative at all. Every barrier below names both
    /// stage masks and both access masks explicitly, and there is no unhandled pair, because there are three.</para>
    ///
    /// <para><b>THE CLEAR VALUES ARE THE INCUMBENT'S, EXACTLY (V-M10).</b> Transparent black for a colour target
    /// and depth 0 with stencil 0 for a depth target, which is what <c>VkTexture.ClearIfRenderTarget</c> passes.
    /// The clear is preserved deliberately rather than dropped with the queue submit that carried it: undefined
    /// contents are not stable across runs, and a render target read before anything writes it would then differ
    /// between two runs of the same golden.</para>
    /// </summary>
    internal static unsafe class VulkanSetupBarrier
    {
        /// <summary>The colour a newly created render target is cleared to: transparent black, matching
        /// <c>VkTexture.ClearIfRenderTarget</c>'s <c>new VkClearColorValue(0, 0, 0, 0)</c>.</summary>
        internal static ClearColorValue TransparentBlack => new(0f, 0f, 0f, 0f);

        /// <summary>The value a newly created depth target is cleared to: depth 0, stencil 0, matching
        /// <c>VkTexture.ClearIfRenderTarget</c>'s <c>new VkClearDepthStencilValue(0, 0)</c>.</summary>
        internal static ClearDepthStencilValue ZeroDepth => new(0f, 0);

        /// <summary>The subresource range covering a whole image: every mip level and every array layer, with
        /// EVERY aspect the format has (<see cref="VulkanFormats.ToBarrierAspect"/>), which on a combined
        /// depth-stencil format is both planes.</summary>
        internal static ImageSubresourceRange WholeImage(bool depthStencil, GpuPixelFormat format, uint mipLevels,
            uint arrayLayers)
            => new(VulkanFormats.ToBarrierAspect(depthStencil, format), 0, mipLevels, 0, arrayLayers);

        /// <summary>The subresource range covering ONE mip level of one array layer, which is what an upload
        /// touches. Both aspects again: the barrier around a copy transitions the whole image's layout even when
        /// the copy itself writes one plane.</summary>
        internal static ImageSubresourceRange OneSubresource(bool depthStencil, GpuPixelFormat format,
            uint mipLevel, uint arrayLayer)
            => new(VulkanFormats.ToBarrierAspect(depthStencil, format), mipLevel, 1, arrayLayer, 1);

        /// <summary>
        /// A newly created image's FIRST-EVER transition, out of <c>UNDEFINED</c> and into
        /// <paramref name="newLayout"/>. See the class note for why this is the only site in this type that may
        /// name <c>UNDEFINED</c> at all.
        /// </summary>
        internal static ImageMemoryBarrier2 FirstUse(ulong image, in ImageSubresourceRange range,
            ImageLayout newLayout)
            => Barrier(image, range, ImageLayout.Undefined, newLayout,
                // NOTHING TO MAKE AVAILABLE. The image was created a moment ago and no access has happened to it,
                // so the source is the top of the pipe with no access mask at all rather than a conservative
                // everything: a source mask over accesses that cannot have happened orders nothing and reads as
                // though it did.
                PipelineStageFlags2.TopOfPipeBit, AccessFlags2.None,
                PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit);

        /// <summary>
        /// Into <c>TRANSFER_DST_OPTIMAL</c> from <paramref name="oldLayout"/>, so a clear or a
        /// <c>vkCmdCopyBufferToImage</c> may write the image.
        /// </summary>
        internal static ImageMemoryBarrier2 ToTransferDestination(ulong image, in ImageSubresourceRange range,
            ImageLayout oldLayout)
            => Barrier(image, range, oldLayout, ImageLayout.TransferDstOptimal,
                PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
                PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit);

        /// <summary>
        /// Out of <c>TRANSFER_DST_OPTIMAL</c> and back to <paramref name="restingLayout"/>, which is where every
        /// command list assumes it will find the texture (V-F7).
        /// </summary>
        internal static ImageMemoryBarrier2 FromTransferDestination(ulong image, in ImageSubresourceRange range,
            ImageLayout restingLayout)
            => Barrier(image, range, ImageLayout.TransferDstOptimal, restingLayout,
                PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit);

        // The one constructor, so the six fields that must always be set together cannot be set apart. No queue
        // family transfer anywhere: this backend creates ONE queue on ONE family (V-N5), so both indexes are
        // IGNORED and an ownership transfer is not expressible.
        static ImageMemoryBarrier2 Barrier(ulong image, in ImageSubresourceRange range, ImageLayout oldLayout,
            ImageLayout newLayout, PipelineStageFlags2 srcStage, AccessFlags2 srcAccess,
            PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
            => new(
                sType: StructureType.ImageMemoryBarrier2,
                srcStageMask: srcStage,
                srcAccessMask: srcAccess,
                dstStageMask: dstStage,
                dstAccessMask: dstAccess,
                oldLayout: oldLayout,
                newLayout: newLayout,
                srcQueueFamilyIndex: Vk.QueueFamilyIgnored,
                dstQueueFamilyIndex: Vk.QueueFamilyIgnored,
                image: new Image(image),
                subresourceRange: range);
    }
}
