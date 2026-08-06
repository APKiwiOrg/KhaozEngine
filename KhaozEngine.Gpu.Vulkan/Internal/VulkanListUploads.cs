using System;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE REAL <see cref="IVulkanUploadSink"/>: one <c>vkCmdCopyBuffer</c> of one region, and nothing else.
    /// <para>
    /// A READONLY STRUCT over two handle-sized values, like <see cref="VulkanCmdSink"/>, so it costs nothing to
    /// build one per upload and a copy of it still names the same buffer. It is deliberately NOT the budget seam:
    /// a copy is not one of the three call classes that scale with draw count, and counting it would gate on a
    /// figure nobody should gate on.
    /// </para>
    /// </summary>
    internal readonly struct VulkanCopySink : IVulkanUploadSink
    {
        readonly Vk _vk;
        readonly CommandBuffer _buffer;

        /// <param name="vk">The instance's loaded API.</param>
        /// <param name="buffer">The command buffer being recorded into.</param>
        internal VulkanCopySink(Vk vk, CommandBuffer buffer)
        {
            ArgumentNullException.ThrowIfNull(vk);

            _vk = vk;
            _buffer = buffer;
        }

        /// <inheritdoc/>
        public void CopyBuffer(ulong source, ulong sourceOffsetBytes, ulong destination,
            ulong destinationOffsetBytes, ulong sizeBytes)
        {
            var region = new BufferCopy(sourceOffsetBytes, destinationOffsetBytes, sizeBytes);
            _vk.CmdCopyBuffer(_buffer, new Buffer(source), new Buffer(destination), 1, in region);
        }
    }

    /// <summary>
    /// ONE COMMAND LIST'S STAGING ARENA AND ITS COPY RECORDER: the implementation of
    /// <see cref="IVulkanRecordUploads"/> that row 8 shaped and left for this row to fill, now that a NON-UNIFORM
    /// buffer can exist at all. Section 9.3.
    ///
    /// <para><b>IT HOLDS THE LIST'S POOL RING RATHER THAN A COMMAND BUFFER.</b> The buffer changes with every slot
    /// advance, so a field would name a stale one after the first <c>Begin</c>. Reading it through the ring at the
    /// moment of the copy is what keeps this type correct across a wrap, and it is why row 8 gave the list an
    /// interface to hold instead of the arena itself.</para>
    ///
    /// <para><b>THE TWO SINKS ARE BUILT PER UPLOAD, ON THE STACK.</b> Both are readonly structs over the API and
    /// the current buffer, and <see cref="VulkanBufferUpload.Record"/> takes the barrier sink through a
    /// <c>where TSink : struct</c> constraint so the JIT monomorphizes it and boxes nothing (V-T2). Storing either
    /// as an interface-typed field would box it and pay a dispatch, which is the one way to spend the cost that
    /// seam was shaped to avoid.</para>
    ///
    /// <para><b>THE RENDERING SCOPE IS NULL UNTIL ROW 12</b>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/522), and null is correct rather than unbuilt: with no
    /// dynamic rendering there is no pass to end, and a <c>vkCmdCopyBuffer</c> outside one is legal. The moment
    /// that row lands, a bulk upload mid-frame ends the pass, copies, barriers and lets the next draw begin it
    /// again, which is the split the uniform ring exists so a per-frame write never pays.</para>
    ///
    /// <para><b>THE ARENA IS DISPOSED WITH THE LIST</b>, which is what <see cref="IVulkanRecordUploads"/> being
    /// <see cref="IDisposable"/> is for. Its blocks go through <see cref="IVulkanStagingSource.Destroy"/>, which
    /// defers the native free behind the timeline, so a block an in-flight submission is still reading outlives the
    /// list that filled it.</para>
    /// </summary>
    internal sealed class VulkanListUploads : IVulkanRecordUploads
    {
        readonly Vk _vk;
        readonly VulkanCommandPoolRing _ring;
        readonly VulkanStagingArena _arena;
        readonly IVulkanRenderingScope? _rendering;

        /// <param name="vk">The instance's loaded API.</param>
        /// <param name="ring">The owning list's pools, which supply the buffer being recorded into.</param>
        /// <param name="arena">This list's own staging arena.</param>
        /// <param name="rendering">The list's rendering state, or null while there is no rendering to end.</param>
        internal VulkanListUploads(Vk vk, VulkanCommandPoolRing ring, VulkanStagingArena arena,
            IVulkanRenderingScope? rendering = null)
        {
            ArgumentNullException.ThrowIfNull(vk);
            ArgumentNullException.ThrowIfNull(ring);
            ArgumentNullException.ThrowIfNull(arena);

            _vk = vk;
            _ring = ring;
            _arena = arena;
            _rendering = rendering;
        }

        /// <inheritdoc/>
        public void Upload(IVulkanUploadDestination destination, ulong destinationOffsetBytes,
            ReadOnlySpan<byte> data)
        {
            int slot = _ring.Slot;
            if (slot < 0)
            {
                throw new InvalidOperationException(
                    "A native Vulkan command list recorded a buffer upload before Begin. There is no command "
                    + "buffer to record the copy into until the pool ring has advanced onto a slot, and the "
                    + "staging arena has not been told which slot's blocks to sub-allocate out of either.");
            }

            var buffer = new CommandBuffer((nint)_ring.BufferAt(slot));

            VulkanBufferUpload.Record(
                new VulkanCmdSink(_vk, buffer),
                new VulkanCopySink(_vk, buffer),
                _arena,
                _rendering,
                destination,
                destinationOffsetBytes,
                data);
        }

        /// <inheritdoc/>
        public void BeginSlot(int slot) => _arena.BeginSlot(slot);

        /// <inheritdoc/>
        public void Dispose() => _arena.Dispose();
    }
}
