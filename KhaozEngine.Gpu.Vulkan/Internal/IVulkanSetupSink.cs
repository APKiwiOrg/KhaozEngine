using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE FOUR <c>vkCmd*</c> CALLS THE DEVICE-OWNED SETUP COMMAND BUFFER RECORDS (V-M10, section 9.3), behind a
    /// seam so the whole of what a texture creation and a device-level upload APPEND is driven by a plain
    /// <c>[Fact]</c> with no loader.
    ///
    /// <para><b>THIS IS NOT THE BUDGET SEAM.</b> <see cref="IVkCmdSink"/> covers the three call classes that scale
    /// with DRAW COUNT, and nothing here does: a setup barrier is emitted once per resource created, a clear once
    /// per render target created, and a copy once per device-level upload. Counting them into a frozen marginal
    /// would gate on figures nobody should gate on, which is the same distinction <see cref="IVulkanUploadSink"/>
    /// already draws for the record-time copy.</para>
    ///
    /// <para><b>THE COMMAND BUFFER IS A PARAMETER RATHER THAN STATE.</b> The setup buffer advances a slot at every
    /// flush, so a sink that stored one would name a stale buffer after the first. Passing it keeps every
    /// implementation stateless and lets one instance serve every slot.</para>
    ///
    /// <para><b>SILK.NET TYPES ARE NAMED HERE, unlike <see cref="IVulkanResourceApi"/>.</b> It is the same case
    /// <see cref="IVkCmdSink"/> makes: these members exist to be a faithful picture of a <c>vkCmd*</c> argument
    /// list, and translating <c>VkDependencyInfo</c> or <c>VkBufferImageCopy</c> into an engine-shaped copy would
    /// put a second structure between the decision and the call it decides. Every type named is a plain struct that
    /// constructs without a device, so a test that inspects what was recorded stays device-free.</para>
    /// </summary>
    internal interface IVulkanSetupSink
    {
        /// <summary><c>vkCmdPipelineBarrier2</c> (V-F6). The setup path's only barrier call, carrying the
        /// creation-time layout transitions and the upload's pair.</summary>
        void PipelineBarrier(ulong commandBuffer, in DependencyInfo dependency);

        /// <summary><c>vkCmdClearColorImage</c> on an image already in
        /// <c>VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL</c>. The colour half of the creation-time clear the incumbent
        /// issues a whole <c>vkQueueSubmit</c> for and this backend appends (V-M10).</summary>
        void ClearColorImage(ulong commandBuffer, ulong image, in ClearColorValue color,
            in ImageSubresourceRange range);

        /// <summary><c>vkCmdClearDepthStencilImage</c>, the depth half of the same clear.</summary>
        void ClearDepthStencilImage(ulong commandBuffer, ulong image, in ClearDepthStencilValue depthStencil,
            in ImageSubresourceRange range);

        /// <summary><c>vkCmdCopyBuffer</c> of ONE region, from a staging lease to a destination buffer. The
        /// device-level <c>UpdateBuffer</c> on a NON-uniform buffer, which is the buffer half of what off-timeline
        /// means: a ring-backed uniform buffer never comes here, because its write is a memcpy into every segment
        /// with no command recorded at all (9.2).</summary>
        void CopyBuffer(ulong commandBuffer, ulong source, ulong sourceOffsetBytes, ulong destination,
            ulong destinationOffsetBytes, ulong sizeBytes);

        /// <summary><c>vkCmdCopyBufferToImage</c> into an image already in
        /// <c>VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL</c>. The device-level <c>UpdateTexture</c>, whose bytes came
        /// from the device-owned staging arena.</summary>
        void CopyBufferToImage(ulong commandBuffer, ulong source, ulong image, in BufferImageCopy region);
    }
}
