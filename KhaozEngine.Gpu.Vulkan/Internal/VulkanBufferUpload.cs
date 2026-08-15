using System;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// ONE STAGED BUFFER UPLOAD, END TO END (V-M9, section 9.3): end the pass, take a staging lease, memcpy into
    /// it, barrier the destination against whatever already touched it, record the copy, and barrier it again with
    /// masks narrowed to what actually reads it.
    ///
    /// <para><b>THE ORDER IS THE WHOLE OF IT.</b> The pass ends FIRST, because a copy is illegal inside a
    /// <c>vkCmdBeginRendering</c> scope. Then the copy is BRACKETED by two barriers, and each one closes a
    /// direction the other cannot. The one after it orders the transfer write against the reads that follow. The
    /// one before it orders the write against the reads and the writes that came earlier, INCLUDING the ones in
    /// earlier submissions, which is the half this path shipped without
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/618">#618</see>).</para>
    ///
    /// <para><b>WHY THE FRAME LOOP DOES NOT ALREADY COVER IT.</b> A consumer that re-uploads a vertex buffer every
    /// frame records that copy at the head of the next frame's command buffer, while the previous frame's
    /// <c>vkCmdDrawIndexed</c> is still in flight reading the same bytes as vertex attributes. Two submissions on
    /// one queue are ordered in submission order and nothing else: the second may START its transfer stage before
    /// the first has finished its vertex fetch. The pool ring's fence does not help either, because it waits on the
    /// submission FRAMES-IN-FLIGHT back rather than the immediately preceding one.</para>
    ///
    /// <para><b>A STATIC OVER A GENERIC SINK, deliberately.</b> The barrier goes through
    /// <see cref="IVkCmdSink"/>, which is consumed through a <c>where TSink : struct</c> constraint so the JIT
    /// monomorphizes it and boxes nothing (V-T2). Making this a method on a type that STORED the sink would box it,
    /// which is the one way to spend the cost that seam was shaped to avoid, so the sink is a parameter and the row
    /// that owns a real sink calls in with its own.</para>
    ///
    /// <para><b>BOTH BARRIERS ARE OVER THE WRITTEN RANGE.</b> The incumbent emits a GLOBAL
    /// <c>VkMemoryBarrier</c> instead, one of them, which makes every access of its class wait rather than the one
    /// buffer that was written, and names <c>VertexAttributeRead</c> at <c>VertexInput</c> whatever the destination
    /// is. See <see cref="VulkanUploadBarrier"/> for what that gets wrong in both directions at once.</para>
    ///
    /// <para><b>NO BATCHING, AND THAT IS A CHOICE THIS ROW MAKES CHEAPLY REVERSIBLE.</b> Two uploads in a row
    /// produce two copies and four barriers. Coalescing them into one <c>vkCmdPipelineBarrier2</c> carrying N
    /// buffer barriers is strictly better and needs a pending-upload list on the recorder, which is the recorder's
    /// shape rather than this function's, so it belongs with the row that builds one. The barrier count is visible
    /// through the counting sink either way, so a load that turns out to emit thousands is measurable rather than
    /// invisible.</para>
    /// </summary>
    internal static class VulkanBufferUpload
    {
        /// <summary>
        /// Record one staged upload.
        /// </summary>
        /// <typeparam name="TSink">The command sink, monomorphized at the call site.</typeparam>
        /// <param name="sink">Where the barrier is recorded.</param>
        /// <param name="copies">Where the copy is recorded. Not the budget seam: see
        /// <see cref="IVulkanUploadSink"/>.</param>
        /// <param name="arena">The list's staging arena.</param>
        /// <param name="rendering">The list's rendering state, which is the list itself
        /// (<see cref="VulkanCommandList"/> implements <see cref="IVulkanRenderingScope"/>). Null only on a list
        /// built with no rendering seam, which is only a list a test constructed, and there is no pass to end
        /// then.</param>
        /// <param name="destination">The buffer being written, which supplies both the handle and the usage the
        /// barrier is narrowed to.</param>
        /// <param name="destinationOffsetBytes">Where in that buffer the payload lands.</param>
        /// <param name="data">The payload. An empty one records NOTHING at all, because a zero-byte copy is a
        /// command and a barrier bought for no bytes.</param>
        internal static void Record<TSink>(TSink sink, IVulkanUploadSink copies, VulkanStagingArena arena,
            IVulkanRenderingScope? rendering, IVulkanUploadDestination destination, ulong destinationOffsetBytes,
            ReadOnlySpan<byte> data)
            where TSink : struct, IVkCmdSink
        {
            ArgumentNullException.ThrowIfNull(copies);
            ArgumentNullException.ThrowIfNull(arena);
            ArgumentNullException.ThrowIfNull(destination);

            if (data.Length == 0) return;

            // FIRST. A vkCmdCopyBuffer inside a vkCmdBeginRendering scope is invalid, and this is the split the
            // ring exists so a uniform write never pays.
            rendering?.EndActiveRendering();

            VulkanStagingLease lease = arena.Take((ulong)data.Length);
            lease.Write(data);

            ulong sizeBytes = (ulong)data.Length;

            // BEFORE. Nothing else orders this write against the reads a previous submission is still making out
            // of the same range, and a buffer has no layout for the tracker to have ordered it through (#618).
            RecordBarrier(sink, VulkanUploadBarrier.Before(
                destination.DeviceBuffer, destinationOffsetBytes, sizeBytes, destination.UploadUsage));

            copies.CopyBuffer(lease.Buffer, lease.OffsetBytes, destination.DeviceBuffer, destinationOffsetBytes,
                sizeBytes);

            // AFTER. The write becomes visible to whatever this buffer's usage says reads it.
            RecordBarrier(sink, VulkanUploadBarrier.After(
                destination.DeviceBuffer, destinationOffsetBytes, sizeBytes, destination.UploadUsage));
        }

        // The barrier alone, split out so the fixed statement's scope is one statement rather than the whole
        // upload. A VkDependencyInfo carries raw pointer arrays as a matter of ABI, which is why this package is
        // unsafe by construction (V-P1's note) rather than by choice. The barrier arrives BY VALUE so its address
        // is this frame's rather than the caller's, which is what lets one helper serve both halves.
        static unsafe void RecordBarrier<TSink>(TSink sink, BufferMemoryBarrier2 barrier)
            where TSink : struct, IVkCmdSink
        {
            var dependency = new DependencyInfo(
                sType: StructureType.DependencyInfo,
                bufferMemoryBarrierCount: 1,
                pBufferMemoryBarriers: &barrier);

            sink.PipelineBarrier(in dependency);
        }
    }
}
