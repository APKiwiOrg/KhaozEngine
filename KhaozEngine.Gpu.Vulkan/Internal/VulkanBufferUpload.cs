using System;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// ONE STAGED BUFFER UPLOAD, END TO END (V-M9, section 9.3): end the pass, take a staging lease, memcpy into
    /// it, record the copy, and barrier the destination with masks narrowed to what actually reads it.
    ///
    /// <para><b>THE ORDER IS THE WHOLE OF IT.</b> The pass ends FIRST, because a copy is illegal inside a
    /// <c>vkCmdBeginRendering</c> scope. The barrier comes LAST, because it orders the transfer write against the
    /// reads that follow, and a barrier emitted before the copy would order nothing.</para>
    ///
    /// <para><b>A STATIC OVER A GENERIC SINK, deliberately.</b> The barrier goes through
    /// <see cref="IVkCmdSink"/>, which is consumed through a <c>where TSink : struct</c> constraint so the JIT
    /// monomorphizes it and boxes nothing (V-T2). Making this a method on a type that STORED the sink would box it,
    /// which is the one way to spend the cost that seam was shaped to avoid, so the sink is a parameter and the row
    /// that owns a real sink calls in with its own.</para>
    ///
    /// <para><b>ONE BARRIER PER UPLOAD, OVER THE WRITTEN RANGE.</b> The incumbent emits a GLOBAL
    /// <c>VkMemoryBarrier</c> instead, which makes every access of its class wait rather than the one buffer that
    /// was written, and names <c>VertexAttributeRead</c> at <c>VertexInput</c> whatever the destination is. See
    /// <see cref="VulkanUploadBarrier"/> for what that gets wrong in both directions at once.</para>
    ///
    /// <para><b>NO BATCHING, AND THAT IS A CHOICE THIS ROW MAKES CHEAPLY REVERSIBLE.</b> Two uploads in a row
    /// produce two copies and two barriers. Coalescing them into one <c>vkCmdPipelineBarrier2</c> carrying N
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

            copies.CopyBuffer(lease.Buffer, lease.OffsetBytes, destination.DeviceBuffer, destinationOffsetBytes,
                (ulong)data.Length);

            RecordBarrier(sink, destination, destinationOffsetBytes, (ulong)data.Length);
        }

        // The barrier alone, split out so the fixed statement's scope is one statement rather than the whole
        // upload. A VkDependencyInfo carries raw pointer arrays as a matter of ABI, which is why this package is
        // unsafe by construction (V-P1's note) rather than by choice.
        static unsafe void RecordBarrier<TSink>(TSink sink, IVulkanUploadDestination destination,
            ulong destinationOffsetBytes, ulong sizeBytes)
            where TSink : struct, IVkCmdSink
        {
            BufferMemoryBarrier2 barrier = VulkanUploadBarrier.For(
                destination.DeviceBuffer, destinationOffsetBytes, sizeBytes, destination.UploadUsage);

            var dependency = new DependencyInfo(
                sType: StructureType.DependencyInfo,
                bufferMemoryBarrierCount: 1,
                pBufferMemoryBarriers: &barrier);

            sink.PipelineBarrier(in dependency);
        }
    }
}
