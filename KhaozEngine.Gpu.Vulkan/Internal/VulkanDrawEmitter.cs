using System;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE REAL <see cref="IVulkanDrawEmitter"/>: two <c>vkCmd*</c> binds of its own, and everything else handed
    /// to <see cref="VulkanDrawBatch"/> over the command buffer's <see cref="VulkanCmdSink"/>. No guard, no cache
    /// and no decision of any kind, which is the emptiness <see cref="VulkanCmdSink"/> is built on and for the
    /// same reason: everything a draw can be wrong about (whether the pass is open, which slots are dirty, which
    /// image needed a transition, whether a dependency barrier was owed) lives ABOVE this line in device-free
    /// types.
    /// <para>
    /// THE SINK IS BUILT PER CALL RATHER THAN HELD, exactly as <see cref="VulkanBarrierRecorder"/> builds its own:
    /// a <see cref="VulkanCmdSink"/> names one command buffer and the buffer changes with the ring slot every time
    /// the list is re-begun. It is a concrete struct at the point of use, so the descriptor flush and the
    /// <c>vkCmd*</c> inline straight through with no interface dispatch and nothing boxed.
    /// </para>
    /// <para>
    /// STATELESS, so one per list costs nothing and two of them cannot disagree. Built per list for the reason
    /// <see cref="VulkanRenderApi"/>, <see cref="VulkanPipelineBinder"/> and <see cref="VulkanBarrierRecorder"/>
    /// are.
    /// </para>
    /// <para>
    /// NO RESULT TO CHECK ANYWHERE. Every <c>vkCmd*</c> returns void: recording errors are reported by
    /// <c>vkEndCommandBuffer</c> (which <see cref="VulkanCommandApi"/> checks) or by the validation layer, not per
    /// call.
    /// </para>
    /// </summary>
    internal sealed unsafe class VulkanDrawEmitter : IVulkanDrawEmitter
    {
        // The widest contiguous vertex run this backend will ever bind without touching the heap. The bind
        // schedule caps a vertex slot at VulkanGeometryBinds.MaxSlot, so this is the whole possible run, and a
        // stackalloc of it is 16 buffers plus 16 offsets on a frame's hottest path.
        const int MaxRun = (int)VulkanGeometryBinds.MaxSlot + 1;

        readonly Vk _vk;

        /// <param name="vk">The instance's loaded API.</param>
        internal VulkanDrawEmitter(Vk vk)
        {
            ArgumentNullException.ThrowIfNull(vk);

            _vk = vk;
        }

        /// <inheritdoc/>
        public void BindVertexBuffers(ulong commandBuffer, uint firstBinding, ReadOnlySpan<ulong> buffers,
            ReadOnlySpan<ulong> offsets)
        {
            if (buffers.Length == 0) return;

            // COPIED INTO A NATIVE-SHAPED ARRAY at this line and nowhere above it, which is what lets the whole
            // schedule hold plain ulong handles and lets a fake invent them. A stackalloc rather than a field,
            // because a field would make this type stateful and two lists share one emitter's scratch.
            Buffer* handles = stackalloc Buffer[MaxRun];
            for (int i = 0; i < buffers.Length; i++) handles[i] = new Buffer(buffers[i]);

            fixed (ulong* pOffsets = offsets)
            {
                _vk.CmdBindVertexBuffers(new CommandBuffer((nint)commandBuffer), firstBinding,
                    (uint)buffers.Length, handles, pOffsets);
            }
        }

        /// <inheritdoc/>
        public void BindIndexBuffer(ulong commandBuffer, ulong buffer, ulong offsetBytes, bool sixteenBit)
            => _vk.CmdBindIndexBuffer(new CommandBuffer((nint)commandBuffer), new Buffer(buffer), offsetBytes,
                sixteenBit ? IndexType.Uint16 : IndexType.Uint32);

        /// <inheritdoc/>
        public void Draw(ulong commandBuffer, VulkanBindRecords binds, in VulkanDrawCall call)
        {
            var sink = Sink(commandBuffer);
            VulkanDrawBatch.Draw(ref sink, binds, in call);
        }

        /// <inheritdoc/>
        public void DrawIndexed(ulong commandBuffer, VulkanBindRecords binds, in VulkanIndexedDrawCall call)
        {
            var sink = Sink(commandBuffer);
            VulkanDrawBatch.DrawIndexed(ref sink, binds, in call);
        }

        /// <inheritdoc/>
        public void Dispatch(ulong commandBuffer, VulkanBindRecords binds, uint groupCountX, uint groupCountY,
            uint groupCountZ)
        {
            var sink = Sink(commandBuffer);
            VulkanDrawBatch.Dispatch(ref sink, binds, groupCountX, groupCountY, groupCountZ);
        }

        /// <inheritdoc/>
        public void DependencyBarrier(ulong commandBuffer)
        {
            var sink = Sink(commandBuffer);
            VulkanDrawBatch.Dependency(ref sink);
        }

        VulkanCmdSink Sink(ulong commandBuffer) => new(_vk, new CommandBuffer((nint)commandBuffer));
    }

    /// <summary>
    /// THE DEVICE-FREE <see cref="IVulkanDrawEmitter"/>: the same <see cref="VulkanDrawBatch"/> calls over a
    /// <see cref="VulkanCountingCmdSink"/>, so every descriptor bind, draw, dispatch and dependency barrier a
    /// recording emits is tallied into a <see cref="VulkanCmdCallCounts"/> and nothing else happens.
    /// <para>
    /// IT EXISTS BECAUSE MV4's BUDGET IS OTHERWISE BLIND TO HALF OF WHAT IT FREEZES. The per-draw marginals were
    /// asserted over the bind classes alone while <c>vkCmdDraw</c> was emitted by nothing, and
    /// <see cref="VulkanCmdCallCounts.DrawCalls"/> read zero BY CONSTRUCTION rather than as a finding. Driving the
    /// real list through this emitter is what makes those assertions total over the draw path, which is the
    /// condition MV4's freeze was waiting on.
    /// </para>
    /// <para>
    /// IT TALLIES INTO THE SAME COUNTS OBJECT the binds and the layout tracker's barriers do, deliberately, so one
    /// budget test reads one set of numbers for a whole recording rather than reconciling three tallies that could
    /// drift apart.
    /// </para>
    /// <para>
    /// THE VERTEX AND INDEX BINDS ARE NOT TALLIED, and that is the seam's own rule rather than an omission here.
    /// Neither is a member of <see cref="IVkCmdSink"/>, because V-T2 covers exactly three call classes, so
    /// counting them would put a number into the budget that nobody agreed to freeze.
    /// <c>FakeVulkanDrawEmitter</c> in the test project records them, which is the assertion this type
    /// deliberately does not make.
    /// </para>
    /// <para>
    /// THE COMMAND BUFFER IS IGNORED, because there is no buffer: the counting sink records which calls were made
    /// and with what shape, and where they would have gone is the one thing a device-free tally cannot check.
    /// </para>
    /// </summary>
    internal sealed class VulkanCountingDrawEmitter : IVulkanDrawEmitter
    {
        readonly VulkanCountingCmdSink _sink;

        /// <param name="counts">The tallies to write into, shared with whatever else drives this recording.</param>
        internal VulkanCountingDrawEmitter(VulkanCmdCallCounts counts)
        {
            ArgumentNullException.ThrowIfNull(counts);

            _sink = new VulkanCountingCmdSink(counts);
        }

        /// <summary>The tallies this emitter writes into.</summary>
        internal VulkanCmdCallCounts Counts => _sink.Counts;

        /// <inheritdoc/>
        public void BindVertexBuffers(ulong commandBuffer, uint firstBinding, ReadOnlySpan<ulong> buffers,
            ReadOnlySpan<ulong> offsets)
        {
            // Nothing. See the class note: a vertex bind is not one of V-T2's three classes.
        }

        /// <inheritdoc/>
        public void BindIndexBuffer(ulong commandBuffer, ulong buffer, ulong offsetBytes, bool sixteenBit)
        {
            // Nothing, for the same reason.
        }

        /// <inheritdoc/>
        public void Draw(ulong commandBuffer, VulkanBindRecords binds, in VulkanDrawCall call)
        {
            VulkanCountingCmdSink sink = _sink;
            VulkanDrawBatch.Draw(ref sink, binds, in call);
        }

        /// <inheritdoc/>
        public void DrawIndexed(ulong commandBuffer, VulkanBindRecords binds, in VulkanIndexedDrawCall call)
        {
            VulkanCountingCmdSink sink = _sink;
            VulkanDrawBatch.DrawIndexed(ref sink, binds, in call);
        }

        /// <inheritdoc/>
        public void Dispatch(ulong commandBuffer, VulkanBindRecords binds, uint groupCountX, uint groupCountY,
            uint groupCountZ)
        {
            VulkanCountingCmdSink sink = _sink;
            VulkanDrawBatch.Dispatch(ref sink, binds, groupCountX, groupCountY, groupCountZ);
        }

        /// <inheritdoc/>
        public void DependencyBarrier(ulong commandBuffer)
        {
            VulkanCountingCmdSink sink = _sink;
            VulkanDrawBatch.Dependency(ref sink);
        }
    }
}
