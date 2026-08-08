using System;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE REAL <see cref="IVulkanTransferSink"/>: six <c>vkCmd*</c> calls against whatever command buffer the
    /// caller hands in, and nothing else at all. No guard, no cache and no decision of any kind, which is the same
    /// emptiness <see cref="VulkanCmdSink"/> and <see cref="VulkanSetupSink"/> are built on and for the same
    /// reason: everything a copy can be wrong about (which case, which region, which layout) lives above this line
    /// in device-free types.
    /// <para>
    /// THE LAYOUTS ARE CONSTANTS HERE RATHER THAN PARAMETERS, deliberately. A transfer names exactly two image
    /// layouts and the seam above documents both as the caller's obligation, so passing them would offer a caller
    /// a choice that has one correct answer and would let a wrong one reach the driver silently.
    /// </para>
    /// <para>
    /// A CLASS RATHER THAN A READONLY STRUCT, like <see cref="VulkanSetupSink"/>. Nothing here is on the per-draw
    /// path, so there is no monomorphization to buy, and holding it as a plain field on the command list is what
    /// keeps the list's own shape simple.
    /// </para>
    /// <para>
    /// NO RESULT TO CHECK ANYWHERE. Every <c>vkCmd*</c> returns void: recording errors are reported by
    /// <c>vkEndCommandBuffer</c> (which <see cref="VulkanCommandApi"/> checks) or by the validation layer, not per
    /// call.
    /// </para>
    /// </summary>
    internal sealed unsafe class VulkanTransferSink : IVulkanTransferSink
    {
        readonly Vk _vk;

        /// <param name="vk">The instance's loaded API.</param>
        internal VulkanTransferSink(Vk vk)
        {
            ArgumentNullException.ThrowIfNull(vk);

            _vk = vk;
        }

        /// <inheritdoc/>
        public void CopyBuffer(ulong commandBuffer, ulong source, ulong destination, in BufferCopy region)
            => _vk.CmdCopyBuffer(Cmd(commandBuffer), new Buffer(source), new Buffer(destination), 1, in region);

        /// <inheritdoc/>
        public void MemoryBarrier(ulong commandBuffer, bool toTransfer)
            => VulkanTransferBarrier.Emit(new VulkanCmdSink(_vk, Cmd(commandBuffer)), toTransfer);

        /// <inheritdoc/>
        public void CopyImage(ulong commandBuffer, ulong source, ulong destination, in ImageCopy region)
            => _vk.CmdCopyImage(Cmd(commandBuffer), new Image(source), ImageLayout.TransferSrcOptimal,
                new Image(destination), ImageLayout.TransferDstOptimal, 1, in region);

        /// <inheritdoc/>
        public void CopyImageToBuffer(ulong commandBuffer, ulong image, ulong buffer, in BufferImageCopy region)
            => _vk.CmdCopyImageToBuffer(Cmd(commandBuffer), new Image(image), ImageLayout.TransferSrcOptimal,
                new Buffer(buffer), 1, in region);

        /// <inheritdoc/>
        public void CopyBufferToImage(ulong commandBuffer, ulong buffer, ulong image, in BufferImageCopy region)
            => _vk.CmdCopyBufferToImage(Cmd(commandBuffer), new Buffer(buffer), new Image(image),
                ImageLayout.TransferDstOptimal, 1, in region);

        /// <inheritdoc/>
        public void BlitImage(ulong commandBuffer, ulong image, in ImageBlit region, bool linear)
            => _vk.CmdBlitImage(Cmd(commandBuffer), new Image(image), ImageLayout.TransferSrcOptimal,
                new Image(image), ImageLayout.TransferDstOptimal, 1, in region,
                linear ? Filter.Linear : Filter.Nearest);

        /// <inheritdoc/>
        public void ResolveImage(ulong commandBuffer, ulong source, ulong destination, in ImageResolve region)
            => _vk.CmdResolveImage(Cmd(commandBuffer), new Image(source), ImageLayout.TransferSrcOptimal,
                new Image(destination), ImageLayout.TransferDstOptimal, 1, in region);

        // A DISPATCHABLE handle, so it is a pointer rather than a 64-bit integer on the native side. The
        // conversion happens at this line and nowhere above it, exactly as it does in VulkanCommandApi.
        static CommandBuffer Cmd(ulong handle) => new((nint)handle);
    }
}
