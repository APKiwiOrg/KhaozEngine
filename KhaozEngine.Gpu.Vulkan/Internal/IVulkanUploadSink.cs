namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE COPY SIDE OF A STAGED UPLOAD, as a seam this row can drive against a fake. One member today:
    /// <c>vkCmdCopyBuffer</c>.
    ///
    /// <para><b>THIS IS NOT THE BUDGET SEAM, AND THE DISTINCTION IS DELIBERATE.</b>
    /// <see cref="IVkCmdSink"/> covers the three call classes that SCALE WITH DRAW COUNT and nothing else, and its
    /// own note names copies as going straight to <c>vkCmd*</c> with no indirection precisely so that freezing
    /// numbers over the budget cannot end up gating on figures nobody should gate on (V-T2). Nothing about that
    /// changes here: a copy recorded through this interface is not counted by
    /// <see cref="VulkanCountingCmdSink"/> and does not appear in any frozen marginal. What this seam buys is
    /// testability of the ARENA, which is where the policy that can be wrong lives, and it stops the staging path
    /// having to wait for a real device before any of it can be driven.</para>
    ///
    /// <para><b>THE BARRIER DOES GO THROUGH THE BUDGET SEAM</b>, as
    /// <see cref="IVkCmdSink.PipelineBarrier"/>, because a barrier is one of the three classes that budget exists
    /// to watch. An upload barrier is not on the per-draw path, so it does not violate the gated invariant, and
    /// counting it is exactly how a recorder that started emitting one per draw would be caught.</para>
    ///
    /// <para><b>THE TEXTURE HALF LANDED SOMEWHERE ELSE, and the reason is worth a sentence.</b>
    /// <c>vkCmdCopyBufferToImage</c> was expected here when this interface was written. Row 9
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/519) put it on <see cref="IVulkanSetupSink"/> instead,
    /// because the only texture upload that exists is the DEVICE-level one, which records into the device-owned
    /// setup command buffer rather than into a list. A record-time <c>UpdateTexture</c> is not on the seam at all,
    /// so a member here would have had no caller.</para>
    /// </summary>
    internal interface IVulkanUploadSink
    {
        /// <summary>
        /// <c>vkCmdCopyBuffer</c> of ONE region, from a staging lease to a destination buffer.
        /// </summary>
        /// <param name="source">The staging <c>VkBuffer</c>.</param>
        /// <param name="sourceOffsetBytes">The lease's offset inside it.</param>
        /// <param name="destination">The destination <c>VkBuffer</c>.</param>
        /// <param name="destinationOffsetBytes">Where in the destination the payload lands, which is the caller's
        /// own offset.</param>
        /// <param name="sizeBytes">How many bytes to copy.</param>
        void CopyBuffer(ulong source, ulong sourceOffsetBytes, ulong destination, ulong destinationOffsetBytes,
            ulong sizeBytes);
    }
}
