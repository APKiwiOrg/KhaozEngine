using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE READ-AFTER-WRITE DEPENDENCY BETWEEN TWO DISPATCHES IN ONE LIST (V-C2), as a pure function over enums,
    /// so what it synchronises is a plain <c>[Fact]</c> rather than a thing only a validation layer on a real
    /// device can see. The same shape <see cref="VulkanImageTransition"/> takes for the layout half.
    ///
    /// <para><b>A GLOBAL MEMORY BARRIER RATHER THAN A BUFFER OR IMAGE ONE, AND THAT IS THE DECISION.</b> The
    /// hazard this closes is "some resource an earlier dispatch wrote is bound to this one", and the set that
    /// answers it (<see cref="VulkanComputeHazards"/>) carries handles rather than ranges: a storage buffer has no
    /// tracked sub-range here and a storage image is already in <c>GENERAL</c> at both ends, so a per-resource
    /// barrier would name the same two stage masks N times and buy nothing a single global one does not. One
    /// barrier per DEPENDENCY, never one per dispatch, which is the clause the design states as "driven by a set
    /// of resources written by earlier dispatches rather than by a barrier per dispatch".</para>
    ///
    /// <para><b>THIS IS NOT A CONTRACT CHANGE AND MUST NOT BE WRITTEN AS ONE.</b> The seam's compute rule 2 is
    /// honoured AS WRITTEN and no seam member is added: chaining dependent dispatches inside one list still needs
    /// <c>End</c>, <c>Submit</c> and <c>WaitForIdle</c> on the PORTABLE contract, because rule 2 is cross-backend
    /// and the drain is what the seam guarantees rather than what any one backend needs. All three engine-owned
    /// backends order a dependent chain natively by different mechanisms, so a consumer that drops the drain is
    /// relying on a backend property the seam never promised (see <see cref="VulkanComputeHazards"/>). What this
    /// barrier is, is EVIDENCE for the automatic-hazard seam capability
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/461), which after this phase has two of three backends able
    /// to answer yes.</para>
    ///
    /// <para><b>COMPUTE TO COMPUTE ON BOTH SIDES.</b> The graphics direction of the same hazard is rule 1 and is a
    /// LAYOUT transition rather than a memory barrier: a storage texture a graphics pass samples goes
    /// <c>GENERAL</c> to <c>SHADER_READ_ONLY_OPTIMAL</c> through <see cref="VulkanLayoutTracker"/>, whose
    /// <see cref="VulkanImageTransition"/> already names <c>ALL_COMMANDS</c> and <c>MEMORY_WRITE</c> as the source
    /// side of a <c>GENERAL</c> image. So this one is only ever the dispatch-to-dispatch edge.</para>
    /// </summary>
    internal static unsafe class VulkanDispatchBarrier
    {
        /// <summary>
        /// The barrier that makes an earlier dispatch's shader writes visible to a later dispatch's shader reads.
        /// Both stage masks and both access masks named explicitly (V-F6), like every other barrier in this
        /// backend.
        /// </summary>
        internal static MemoryBarrier2 ReadAfterWrite => new(
            sType: StructureType.MemoryBarrier2,
            srcStageMask: PipelineStageFlags2.ComputeShaderBit,
            srcAccessMask: AccessFlags2.ShaderWriteBit,
            dstStageMask: PipelineStageFlags2.ComputeShaderBit,
            // BOTH READ AND WRITE ON THE DESTINATION SIDE, because the classic ping-pong reads stage N-1's output
            // and writes its own, and a write-after-write on the same resource is the same hazard with the same
            // answer. Naming only the read would order the read and leave a second writer racing the first.
            dstAccessMask: AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit);
    }

    /// <summary>
    /// THE DESCRIPTOR FLUSH AND THE COMMAND, AS ONE FUNCTION PER COMMAND, over a generic
    /// <see cref="IVkCmdSink"/>. The whole body of every <see cref="IVulkanDrawEmitter"/> there is, so the two
    /// implementations differ ONLY in which sink they drive.
    ///
    /// <para><b>A STATIC OVER A GENERIC SINK, WHICH IS EXACTLY <see cref="VulkanBarrierBatch"/>'s SHAPE AND FOR
    /// THE SAME REASON.</b> The sink is consumed through a <c>where TSink : struct</c> constraint so the JIT
    /// monomorphizes it and boxes nothing (V-T2), and a type that STORED it in a field of the interface type would
    /// box it. So the sink is a PARAMETER here and the emitter that owns a real one calls in with its own.</para>
    ///
    /// <para><b>THE FLUSH AND THE COMMAND ARE ONE FUNCTION RATHER THAN TWO CALLS FROM ABOVE, and that ordering is
    /// the point.</b> Every dirty descriptor slot has to be bound BEFORE the command that reads it, on the same
    /// buffer, with nothing between them. Splitting the pair would let a caller emit them in the wrong order or
    /// emit one without the other, and it would put a second interface hop on the per-draw path. Both
    /// implementations reach the driver through these three functions, so the device-free budget counts the
    /// SHIPPED pairing rather than a second copy of it.</para>
    ///
    /// <para><b>NO DECISION OF ANY KIND LIVES HERE.</b> Whether the pass is open, which slots are dirty, which
    /// images needed a transition and whether a dependency barrier was owed are all settled above this line in
    /// device-free types. This is the emptiness <see cref="VulkanCmdSink"/> is built on, one layer up.</para>
    /// </summary>
    internal static class VulkanDrawBatch
    {
        /// <summary>The graphics bind flush and then <c>vkCmdDraw</c>.</summary>
        /// <typeparam name="TSink">The command sink, monomorphized at the call site.</typeparam>
        /// <param name="sink">Where the calls are recorded.</param>
        /// <param name="binds">The graphics bind schedule.</param>
        /// <param name="call">The draw's four counts.</param>
        internal static void Draw<TSink>(ref TSink sink, VulkanBindRecords binds, in VulkanDrawCall call)
            where TSink : struct, IVkCmdSink
        {
            binds.Flush(ref sink);
            sink.Draw(call.VertexCount, call.InstanceCount, call.FirstVertex, call.FirstInstance);
        }

        /// <summary>The graphics bind flush and then <c>vkCmdDrawIndexed</c>.</summary>
        internal static void DrawIndexed<TSink>(ref TSink sink, VulkanBindRecords binds,
            in VulkanIndexedDrawCall call)
            where TSink : struct, IVkCmdSink
        {
            binds.Flush(ref sink);
            sink.DrawIndexed(call.IndexCount, call.InstanceCount, call.FirstIndex, call.VertexOffset,
                call.FirstInstance);
        }

        /// <summary>The COMPUTE bind flush and then <c>vkCmdDispatch</c>.</summary>
        internal static void Dispatch<TSink>(ref TSink sink, VulkanBindRecords binds, uint groupCountX,
            uint groupCountY, uint groupCountZ)
            where TSink : struct, IVkCmdSink
        {
            binds.Flush(ref sink);
            sink.Dispatch(groupCountX, groupCountY, groupCountZ);
        }

        /// <summary>
        /// ONE <c>vkCmdPipelineBarrier2</c> CARRYING <see cref="VulkanDispatchBarrier.ReadAfterWrite"/>, and
        /// nothing else. It goes through the budget seam like every other barrier this backend emits, which is
        /// what makes "no pipeline barriers on the per-draw path" a statement a device-free test can check about
        /// the dispatch path too.
        /// </summary>
        internal static unsafe void Dependency<TSink>(ref TSink sink)
            where TSink : struct, IVkCmdSink
        {
            MemoryBarrier2 barrier = VulkanDispatchBarrier.ReadAfterWrite;

            var dependency = new DependencyInfo(
                sType: StructureType.DependencyInfo,
                memoryBarrierCount: 1,
                pMemoryBarriers: &barrier);

            sink.PipelineBarrier(in dependency);
        }
    }
}
