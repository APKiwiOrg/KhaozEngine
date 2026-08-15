using System;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// DECISION V-T2's INTERPOSITION POINT: the narrow seam the device-free native-call BUDGET test counts
    /// through, covering ONLY the three call classes that scale with draw count. Descriptor binds, draws and
    /// dispatches, and barriers. Nothing else.
    ///
    /// <para><b>WHY A SEAM AT ALL.</b> Silk.NET's generated bindings are non-virtual, so there is no way to
    /// observe what a recorder asked the driver for without a line to interpose on. The Direct3D 11 backend
    /// answered the same question with <c>ID3D11BindSink</c>, and this is that shape aimed at a different animal
    /// (see below).</para>
    ///
    /// <para><b>CONSUMED THROUGH A GENERIC CONSTRAINT (<c>where TSink : struct, IVkCmdSink</c>) AND NEVER THROUGH
    /// THE INTERFACE.</b> The JIT monomorphizes each implementation, so a recorder written against this seam
    /// carries no interface dispatch and boxes nothing: the call site inlines straight through to the
    /// <c>vkCmd*</c> it names, exactly as the D3D11 emitter does. That is the whole of V-T2's "generic-constrained
    /// to a struct so the JIT monomorphizes it away". A caller that stores this as an <c>IVkCmdSink</c> field
    /// boxes the struct and pays a dispatch on every draw in the frame, which is the one way to spend the cost
    /// this design took the trouble to avoid. Every implementation is a READONLY struct whose mutable state (if
    /// any) sits behind a class reference, which is the emitter rule the other backend enforces with a reflection
    /// test and which holds here for the same reason: a sink is copied into whatever recorder drives it.</para>
    ///
    /// <para><b>AIMING THIS AT DIRECT3D 11's CALL CLASSES WOULD HAVE BEEN THE MISTAKE.</b> That API's #418 defect
    /// was one native call per resource per stage, because Direct3D 11 binds RESOURCES. Vulkan binds SETS, and the
    /// resources went into the set at creation, so a full activation of the engine's four-set shapes is ONE call.
    /// The Vulkan fan-out class is a completely different animal: per-draw descriptor set ALLOCATION, per-draw
    /// <c>vkUpdateDescriptorSets</c>, and per-draw barrier emission. A budget ported from Direct3D 11 would pass
    /// green while a Vulkan backend allocated a descriptor set per draw.</para>
    ///
    /// <para><b>AND THE SINK CANNOT GATE THE INVARIANT THAT MATTERS MOST, WHICH IS WHY V-D2 EXISTS.</b> "Zero
    /// <c>vkAllocateDescriptorSets</c> and zero <c>vkUpdateDescriptorSets</c> between <c>Begin</c> and
    /// <c>End</c>" is the Vulkan #418 protection, and NEITHER of those is a sink call, so no counting seam can see
    /// them. That enforcement is STRUCTURAL and landed in rows 10 and 11: the descriptor pool is not reachable from
    /// the recording type, asserted by an architecture test over the type graph, plus a fake pool whose allocate
    /// and write counters must both read zero. Do not add either call to this interface to make it countable: a
    /// call that cannot be made is a stronger guarantee than a call that is counted and found to be zero.</para>
    ///
    /// <para><b>WHAT DELIBERATELY GOES STRAIGHT TO <c>vkCmd*</c> WITH NO INDIRECTION:</b> clears, copies, mip
    /// generation, resolves, and the <c>vkCmdBeginRendering</c> / <c>vkCmdEndRendering</c> pair. Nothing about any
    /// of them scales per draw, and freezing numbers over them would gate on figures nobody should gate on. There
    /// is also no secondary-command-buffer concept and no <c>vkCmdDrawIndirect</c> here, because the engine seam
    /// has neither and adding one would have no consumer (section 6.4).</para>
    ///
    /// <para><b>WHAT CALLS IT.</b> Row 7 (https://github.com/APKiwiOrg/KhaozEngine/issues/517) landed the seam,
    /// the real sink and the counting sink. The bind flush that drives the descriptor member and the BUDGET TEST
    /// itself are row 11 (https://github.com/APKiwiOrg/KhaozEngine/issues/521). The barrier member has two
    /// callers: the staged upload's buffer barrier (row 9, https://github.com/APKiwiOrg/KhaozEngine/issues/519)
    /// and the layout tracker's image barriers (row 14,
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/524, which reaches it through
    /// <see cref="IVulkanBarrierRecorder"/>, because the tracker HOLDS its emitter and this one is consumed
    /// through a struct constraint that a field would box). The draw and dispatch members are row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525). The seam was here first on purpose: a budget seam
    /// retrofitted after the recorder exists is a seam shaped by the recorder rather than by what needs counting,
    /// and phase 2 records exactly that outcome.</para>
    ///
    /// <para><b>SILK.NET TYPES ARE NAMED HERE, unlike every other seam in this package.</b>
    /// <see cref="IVulkanCommandApi"/>, <see cref="IVulkanTimelineSemaphore"/> and
    /// <see cref="IVulkanDeviceMemoryApi"/> all speak <c>ulong</c> handles so the ordering logic above them names
    /// no binding type. This one is the opposite case: it exists to be a FAITHFUL picture of a <c>vkCmd*</c>
    /// argument list, and translating <c>VkDependencyInfo</c> into an engine-shaped copy would put a second
    /// structure between the budget and the call it is a budget for, which is the drift the seam exists to
    /// prevent. Every type named here is a plain struct that constructs without a device, so the device-free
    /// budget test stays device-free.</para>
    /// </summary>
    internal interface IVkCmdSink
    {
        /// <summary>
        /// <c>vkCmdBindDescriptorSets</c> over a CONTIGUOUS RUN of sets starting at
        /// <paramref name="firstSet"/>, carrying <paramref name="dynamicOffsets"/> for every dynamic descriptor in
        /// those sets in set-then-binding order.
        /// <para>
        /// THERE IS NO SINGLE-SET OVERLOAD, deliberately, and it is the same rule <c>ID3D11BindSink</c> expresses
        /// by having only array calls. The law is one call per contiguous run of dirty slots, so a per-slot entry
        /// point would be the fan-out defect available as an API.
        /// </para>
        /// <para>
        /// <paramref name="dynamicOffsets"/> IS POSITIONAL AND COVERS SETS THE CALLER NEVER NAMED. It carries one
        /// entry for every dynamic descriptor in every set in the run, including ring bases for uniform buffers
        /// nobody asked about. Row 11 composes it and owns the device-free test that pins it for every shipped
        /// layout shape, because an off-by-one there reads the wrong slice of the right buffer, which renders
        /// plausible garbage rather than throwing.
        /// </para>
        /// </summary>
        void BindDescriptorSets(PipelineBindPoint bindPoint, PipelineLayout layout, uint firstSet,
            ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<uint> dynamicOffsets);

        /// <summary><c>vkCmdDraw</c>.</summary>
        void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance);

        /// <summary><c>vkCmdDrawIndexed</c>.</summary>
        void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset,
            uint firstInstance);

        /// <summary><c>vkCmdDispatch</c>.</summary>
        void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ);

        /// <summary>
        /// <c>vkCmdPipelineBarrier2</c>, with explicit source and destination stage and access masks per barrier
        /// (V-F6). The whole barrier model is this one call: there is no <c>vkCmdPipelineBarrier</c> and no
        /// if/else over layout pairs, which is what the incumbent has and what silently emits <c>NONE</c> masks in
        /// Release when it meets a pair it does not handle.
        /// <para>
        /// ONE CALL CARRYING N BARRIERS is the unit that matters here, and both numbers are countable off the
        /// dependency info: a budget that froze only the call count would pass a recorder that put a barrier per
        /// draw into one batch. NO PIPELINE BARRIERS ON THE PER-DRAW PATH is the gated invariant (V-T2), which is
        /// a statement about both.
        /// </para>
        /// </summary>
        void PipelineBarrier(in DependencyInfo dependency);
    }
}
