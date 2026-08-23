using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE SIX <c>vkCmd*</c> CALLS THE TRANSFER FAMILY IS: the buffer copy, both directions of the buffer-image
    /// copy, the image-to-image copy, the mip chain's blit and the multisample resolve. Behind a seam so the whole
    /// of what a copy DECIDES (which of the four staging cases it is, which subresource range, which region
    /// arithmetic, which layout each image has to be in) is driven by a plain <c>[Fact]</c> with no loader.
    /// Work-breakdown row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/525).
    ///
    /// <para><b>THIS IS NOT THE BUDGET SEAM.</b> <see cref="IVkCmdSink"/> covers the three call classes that scale
    /// with DRAW COUNT, and its own note names copies, mip generation and resolves as going straight to
    /// <c>vkCmd*</c> with no indirection precisely so freezing numbers over the budget cannot end up gating on
    /// figures nobody should gate on (V-T2). Nothing here is counted by
    /// <see cref="VulkanCountingCmdSink"/> and nothing here appears in a frozen marginal. What the seam buys is
    /// testability of the region arithmetic, which is the highest-risk parity surface in the backend: every golden
    /// reads back through a copy into a staging texture and a different arithmetic garbles all 36 at once.</para>
    ///
    /// <para><b>THE LAYOUT TRANSITIONS DO NOT COME THROUGH HERE.</b> They are
    /// <see cref="VulkanLayoutTracker"/>'s, so they reach the driver through
    /// <see cref="IVulkanBarrierRecorder"/> and therefore through the budget seam, which is what keeps every
    /// barrier this backend emits countable. Each member below is documented with the layout it REQUIRES its
    /// images to already be in, and putting them there is the caller's obligation rather than this seam's
    /// (V-F7).</para>
    ///
    /// <para><b>SILK.NET REGION TYPES ARE NAMED HERE, exactly as <see cref="IVulkanSetupSink"/> names them.</b>
    /// These members exist to be a faithful picture of a <c>vkCmd*</c> argument list, and translating
    /// <c>VkBufferImageCopy</c> into an engine-shaped copy would put a second structure between the arithmetic and
    /// the call that arithmetic is for. Every type named is a plain struct that constructs without a device, so a
    /// test that inspects what was recorded stays device-free. The HANDLES stay <c>ulong</c>, as everywhere else
    /// in this package.</para>
    /// </summary>
    internal interface IVulkanTransferSink
    {
        /// <summary><c>vkCmdCopyBuffer</c> of ONE region. Buffers have no layout, so there is nothing to
        /// transition on either side and the ordering is <see cref="MemoryBarrier"/>'s instead.</summary>
        void CopyBuffer(ulong commandBuffer, ulong source, ulong destination, in BufferCopy region);

        /// <summary>
        /// THE BUFFER COPY'S ORDERING, as one <c>vkCmdPipelineBarrier2</c> carrying one GLOBAL memory barrier. A
        /// <c>VkBuffer</c> has no layout, so nothing the layout tracker does orders a buffer copy against the work
        /// on either side of it: <see cref="VulkanTransferBarrier"/> carries both shapes and the argument for
        /// widening the incumbent's.
        /// </summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        /// <param name="toTransfer">True for the barrier BEFORE the copy, which makes earlier writes available to
        /// the transfer read. False for the one after, which makes the transfer's write visible to everything
        /// later.</param>
        void MemoryBarrier(ulong commandBuffer, bool toTransfer);

        /// <summary><c>vkCmdCopyImage</c> of ONE region, with <paramref name="source"/> already in
        /// <c>TRANSFER_SRC_OPTIMAL</c> and <paramref name="destination"/> already in
        /// <c>TRANSFER_DST_OPTIMAL</c>.</summary>
        void CopyImage(ulong commandBuffer, ulong source, ulong destination, in ImageCopy region);

        /// <summary><c>vkCmdCopyImageToBuffer</c> of ONE region, with <paramref name="image"/> already in
        /// <c>TRANSFER_SRC_OPTIMAL</c>. This is the readback direction every golden takes.</summary>
        void CopyImageToBuffer(ulong commandBuffer, ulong image, ulong buffer, in BufferImageCopy region);

        /// <summary><c>vkCmdCopyBufferToImage</c> of ONE region, with <paramref name="image"/> already in
        /// <c>TRANSFER_DST_OPTIMAL</c>. The list-level upload direction, distinct from the device-level one on
        /// <see cref="IVulkanSetupSink"/>, which records into the setup buffer instead.</summary>
        void CopyBufferToImage(ulong commandBuffer, ulong buffer, ulong image, in BufferImageCopy region);

        /// <summary>
        /// <c>vkCmdBlitImage</c> of ONE region from an image to ITSELF, which is what a mip chain is: level N-1 in
        /// <c>TRANSFER_SRC_OPTIMAL</c> down into level N in <c>TRANSFER_DST_OPTIMAL</c>. Both layouts are the
        /// caller's obligation, and both name the same <c>VkImage</c>, which is legal precisely because the two
        /// subresource ranges are disjoint.
        /// </summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        /// <param name="image">The image, on both sides of the blit.</param>
        /// <param name="region">The source and destination levels and their extents.</param>
        /// <param name="linear">The filter. <c>VK_FILTER_LINEAR</c> is what averages four texels into one and is
        /// what the incumbent selected for every format this seam can express.</param>
        void BlitImage(ulong commandBuffer, ulong image, in ImageBlit region, bool linear);

        /// <summary><c>vkCmdResolveImage</c> of ONE region, with the multisampled
        /// <paramref name="source"/> already in <c>TRANSFER_SRC_OPTIMAL</c> and the single-sample
        /// <paramref name="destination"/> already in <c>TRANSFER_DST_OPTIMAL</c> (V-C6).</summary>
        void ResolveImage(ulong commandBuffer, ulong source, ulong destination, in ImageResolve region);
    }
}
