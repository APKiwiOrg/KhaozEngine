using System;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE REAL <see cref="IVulkanSetupSink"/>: four <c>vkCmd*</c> calls against whatever command buffer the setup
    /// path hands in, and nothing else at all. No guard, no cache and no decision of any kind, which is the same
    /// emptiness <see cref="VulkanCmdSink"/> is built on and for the same reason: everything that could be wrong
    /// (which barriers, which layouts, which clear values, which copy region) lives above this line in device-free
    /// types.
    /// <para>
    /// A CLASS RATHER THAN A READONLY STRUCT, unlike <see cref="VulkanCmdSink"/>. That type is consumed through a
    /// generic constraint on the per-draw path so the JIT monomorphizes it, and this one is held as a field by the
    /// device's setup buffer and called a handful of times per resource created. Paying an interface dispatch on a
    /// creation-time call to keep the field simple is the right side of that trade, and taking the other side
    /// would put a generic parameter on the device itself.
    /// </para>
    /// <para>
    /// NO RESULT TO CHECK ANYWHERE. Every <c>vkCmd*</c> returns void: recording errors are reported by
    /// <c>vkEndCommandBuffer</c> (which <see cref="VulkanCommandApi"/> checks) or by the validation layer, not per
    /// call.
    /// </para>
    /// </summary>
    internal sealed unsafe class VulkanSetupSink : IVulkanSetupSink
    {
        readonly Vk _vk;

        /// <param name="vk">The instance's loaded API.</param>
        internal VulkanSetupSink(Vk vk)
        {
            ArgumentNullException.ThrowIfNull(vk);
            _vk = vk;
        }

        /// <inheritdoc/>
        public void PipelineBarrier(ulong commandBuffer, in DependencyInfo dependency)
            => _vk.CmdPipelineBarrier2(Buffer(commandBuffer), in dependency);

        /// <inheritdoc/>
        public void ClearColorImage(ulong commandBuffer, ulong image, in ClearColorValue color,
            in ImageSubresourceRange range)
            => _vk.CmdClearColorImage(Buffer(commandBuffer), new Image(image), ImageLayout.TransferDstOptimal,
                in color, 1, in range);

        /// <inheritdoc/>
        public void ClearDepthStencilImage(ulong commandBuffer, ulong image,
            in ClearDepthStencilValue depthStencil, in ImageSubresourceRange range)
            => _vk.CmdClearDepthStencilImage(Buffer(commandBuffer), new Image(image),
                ImageLayout.TransferDstOptimal, in depthStencil, 1, in range);

        /// <inheritdoc/>
        public void CopyBuffer(ulong commandBuffer, ulong source, ulong sourceOffsetBytes, ulong destination,
            ulong destinationOffsetBytes, ulong sizeBytes)
        {
            var region = new BufferCopy(sourceOffsetBytes, destinationOffsetBytes, sizeBytes);
            _vk.CmdCopyBuffer(Buffer(commandBuffer), new Buffer(source), new Buffer(destination), 1, in region);
        }

        /// <inheritdoc/>
        public void CopyBufferToImage(ulong commandBuffer, ulong source, ulong image, in BufferImageCopy region)
            => _vk.CmdCopyBufferToImage(Buffer(commandBuffer), new Buffer(source), new Image(image),
                ImageLayout.TransferDstOptimal, 1, in region);

        // A DISPATCHABLE handle, so it is a pointer rather than a 64-bit integer on the native side. The
        // conversion happens at this line and nowhere above it, exactly as it does in VulkanCommandApi.
        static CommandBuffer Buffer(ulong handle) => new((nint)handle);
    }
}
