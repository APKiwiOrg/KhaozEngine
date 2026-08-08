using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// EVERY RECORD-TIME CALL A DRAW OR A DISPATCH MAKES, behind one line, so the whole pre-command ordering above
    /// it (<see cref="VulkanDrawRecorder"/>) runs under a plain <c>[Fact]</c> on a machine with no Vulkan loader.
    /// Work-breakdown row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/525).
    ///
    /// <para><b>IT IS NOT <see cref="IVkCmdSink"/> AND IT DOES NOT REPLACE IT.</b> Every draw, dispatch and
    /// dependency barrier below reaches the driver through <see cref="VulkanDrawBatch"/>, which takes an
    /// <see cref="IVkCmdSink"/> as a generic PARAMETER, so the descriptor flush and the command itself still pass
    /// through the seam decision V-T2 freezes a budget over. What this line adds is the ability to HOLD the
    /// emitter as a field, which the budget seam deliberately forbids: it is consumed through a
    /// <c>where TSink : struct</c> constraint so the JIT monomorphizes it, and a field of that interface type
    /// would box the struct. That is the identical split <see cref="IVulkanBarrierRecorder"/> takes, and it is
    /// taken here for the identical reason.</para>
    ///
    /// <para><b>SO THIS LINE IS ALSO WHERE THE SINK IS SUBSTITUTED.</b> <see cref="VulkanDrawEmitter"/> drives a
    /// <see cref="VulkanCmdSink"/> over the command buffer it is handed, and
    /// <see cref="VulkanCountingDrawEmitter"/> drives a <see cref="VulkanCountingCmdSink"/> over the same
    /// <see cref="VulkanCmdCallCounts"/> the binds and the tracker's barriers write into. That is what completes
    /// MV4: <see cref="VulkanCmdCallCounts.DrawCalls"/> read zero by construction while nothing emitted a draw,
    /// and the budget's per-draw marginals are total over the draw path once this exists.</para>
    ///
    /// <para><b>ONE INTERFACE CALL PER DRAW, AND THE ALTERNATIVE IS NOT EXPRESSIBLE.</b> Making the cost zero
    /// would mean making <see cref="VulkanCommandList"/> itself generic in the sink, which would put a type
    /// parameter on the seam implementation the device hands out. What the constraint in V-T2 actually forbids is
    /// BOXING the sink and dispatching every one of its members through the interface, and that does not happen
    /// here: the body behind this call is monomorphized, so the descriptor flush (which is the call that scales
    /// with the run count) and the <c>vkCmd*</c> inline exactly as they do on the other backend's emitter.</para>
    ///
    /// <para><b>THE VERTEX AND INDEX BINDS ARE HERE AND ARE NOT ON THE BUDGET SEAM, deliberately.</b> V-T2 covers
    /// exactly three call classes and neither of these is one of them: adding them would widen what the frozen
    /// marginals gate on, which
    /// <c>VulkanBindBudgetTests.TheSeam_CannotSeeTheViewportHalfOfTheGate</c> exists to keep a decision somebody
    /// makes deliberately. They still need a device-free line, for the reason
    /// <see cref="IVulkanRenderApi"/> needed one: the schedule that decides how a run of dirty vertex slots is cut
    /// is a decision that can be wrong, and Silk.NET's generated bindings are non-virtual, so an emission is
    /// observable only where there is something to interpose on.</para>
    ///
    /// <para><b>HANDLES ARE <c>ulong</c> HERE</b>, the same split <see cref="IVulkanRenderApi"/> and
    /// <see cref="IVulkanCommandApi"/> take, so the schedule above names no Silk.NET type and a fake invents plain
    /// numbers. The <see cref="IVkCmdSink"/> arguments that DO need to be a faithful picture of a <c>vkCmd*</c>
    /// argument list are on that seam, one hop below.</para>
    /// </summary>
    internal interface IVulkanDrawEmitter
    {
        /// <summary>
        /// <c>vkCmdBindVertexBuffers</c> over a CONTIGUOUS RUN of bindings starting at
        /// <paramref name="firstBinding"/>.
        /// <para>
        /// THERE IS NO SINGLE-SLOT OVERLOAD, and that is the same rule <see cref="IVkCmdSink"/> states for
        /// descriptor sets: the law is one call per contiguous run of dirty slots, so a per-slot entry point would
        /// be the fan-out defect available as an API.
        /// </para>
        /// </summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        /// <param name="firstBinding">The run's first binding number.</param>
        /// <param name="buffers">The <c>VkBuffer</c> per binding in the run, never empty.</param>
        /// <param name="offsets">The byte offset per binding, positionally against
        /// <paramref name="buffers"/>.</param>
        void BindVertexBuffers(ulong commandBuffer, uint firstBinding, ReadOnlySpan<ulong> buffers,
            ReadOnlySpan<ulong> offsets);

        /// <summary><c>vkCmdBindIndexBuffer</c>. One index buffer is bound at a time, because the seam binds one
        /// and Vulkan has one slot for it.</summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        /// <param name="buffer">The <c>VkBuffer</c>, non-zero.</param>
        /// <param name="offsetBytes">Where the first index lives.</param>
        /// <param name="sixteenBit">True for <c>VK_INDEX_TYPE_UINT16</c>, false for <c>UINT32</c>. A
        /// <c>bool</c> rather than an enum because the seam's <see cref="GpuIndexFormat"/> has exactly two members
        /// and a wider parameter would have unreachable values.</param>
        void BindIndexBuffer(ulong commandBuffer, ulong buffer, ulong offsetBytes, bool sixteenBit);

        /// <summary>The descriptor flush and then <c>vkCmdDraw</c>, in that order, through
        /// <see cref="VulkanDrawBatch"/>.</summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        /// <param name="binds">The graphics bind schedule, flushed immediately before the command.</param>
        /// <param name="call">The draw's four counts.</param>
        void Draw(ulong commandBuffer, VulkanBindRecords binds, in VulkanDrawCall call);

        /// <summary>The descriptor flush and then <c>vkCmdDrawIndexed</c>.</summary>
        void DrawIndexed(ulong commandBuffer, VulkanBindRecords binds, in VulkanIndexedDrawCall call);

        /// <summary>The COMPUTE descriptor flush and then <c>vkCmdDispatch</c>. Separate records, separate bind
        /// point, separate flush (V-C1).</summary>
        void Dispatch(ulong commandBuffer, VulkanBindRecords binds, uint groupCountX, uint groupCountY,
            uint groupCountZ);

        /// <summary>
        /// THE DEPENDENT-DISPATCH READ-AFTER-WRITE BARRIER (V-C2), as one <c>vkCmdPipelineBarrier2</c> carrying one
        /// GLOBAL memory barrier. Emitted only when a dispatch binds a resource an earlier dispatch in this
        /// recording wrote: see <see cref="VulkanComputeHazards"/> for the set that decides it and
        /// <see cref="VulkanDispatchBarrier"/> for the masks.
        /// </summary>
        void DependencyBarrier(ulong commandBuffer);
    }

    /// <summary>The four counts a <c>vkCmdDraw</c> carries, as one value so an ordering test can compare a whole
    /// draw rather than four arguments.</summary>
    /// <param name="VertexCount">Vertices per instance.</param>
    /// <param name="InstanceCount">Instances. Eight of these is still ONE draw, which is the trace identity the
    /// budget freezes.</param>
    /// <param name="FirstVertex">First vertex index.</param>
    /// <param name="FirstInstance">First instance index.</param>
    internal readonly record struct VulkanDrawCall(
        uint VertexCount, uint InstanceCount, uint FirstVertex, uint FirstInstance);

    /// <summary>The five counts a <c>vkCmdDrawIndexed</c> carries.</summary>
    /// <param name="IndexCount">Indices per instance.</param>
    /// <param name="InstanceCount">Instances.</param>
    /// <param name="FirstIndex">First index into the bound index buffer.</param>
    /// <param name="VertexOffset">Added to every index before the vertex buffer is read. SIGNED, which is the one
    /// argument in either draw that is, and the seam carries it as an <c>int</c> for the same reason.</param>
    /// <param name="FirstInstance">First instance index.</param>
    internal readonly record struct VulkanIndexedDrawCall(
        uint IndexCount, uint InstanceCount, uint FirstIndex, int VertexOffset, uint FirstInstance);
}
