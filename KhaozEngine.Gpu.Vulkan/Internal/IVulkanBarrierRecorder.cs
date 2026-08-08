using System;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// WHERE THE LAYOUT TRACKER PUTS A BATCH OF IMAGE BARRIERS: one <c>vkCmdPipelineBarrier2</c> carrying every
    /// barrier in the span. A line, so the whole tracker above it (V-F6 to V-F8) runs under a plain <c>[Fact]</c>
    /// on a machine with no Vulkan loader.
    ///
    /// <para><b>IT IS NOT <see cref="IVkCmdSink"/> AND IT DOES NOT REPLACE IT.</b> Every implementation is one
    /// call into <see cref="VulkanBarrierBatch"/>, which builds the <c>VkDependencyInfo</c> and hands it to
    /// <see cref="IVkCmdSink.PipelineBarrier"/> on a sink it takes as a generic PARAMETER, so every barrier this
    /// backend emits still passes through the budget seam. What this line adds is the ability to hold the emitter
    /// as a FIELD, which the budget seam deliberately forbids: it is consumed through a
    /// <c>where TSink : struct</c> constraint so the JIT monomorphizes it, and a field of that interface type
    /// would box the struct and pay a dispatch on every draw in the frame.</para>
    ///
    /// <para><b>SO THIS LINE IS ALSO WHERE THE SINK IS SUBSTITUTED, AND THAT IS NOT A CONVENIENCE.</b>
    /// <see cref="VulkanBarrierRecorder"/> drives a <see cref="VulkanCmdSink"/> over the command buffer it is
    /// handed, and <see cref="VulkanCountingBarrierRecorder"/> drives a <see cref="VulkanCountingCmdSink"/> over
    /// the same <see cref="VulkanCmdCallCounts"/> the binds and the staged upload's buffer barrier write into. An
    /// earlier shape had the real recorder build its concrete sink INSIDE its own body, which made the emitter
    /// unsubstitutable and left <see cref="VulkanCmdCallCounts.BarrierCalls"/> structurally unable to see an image
    /// barrier at all: V-T2's "no pipeline barriers on the per-draw path" would then have passed vacuously
    /// forever, which is the failure mode a budget exists to prevent rather than to exhibit.</para>
    ///
    /// <para><b>ONE INTERFACE CALL PER BATCH, AT A PASS BOUNDARY, IS NOT THE COST THAT CONSTRAINT EXISTS TO
    /// AVOID.</b> The tracker emits at a begin, at an <c>End</c>, and at the copy, resolve and dispatch
    /// boundaries. Every one of those is bounded by touched textures per pass and independent of draw count
    /// (V-F7, MV5), so a dispatch there is paid a handful of times per frame rather than per draw. The per-draw
    /// path takes the generic constraint and always will: NO PIPELINE BARRIERS ON THE PER-DRAW PATH is the gated
    /// invariant (V-T2), which is a statement about both the call count and the barrier count.</para>
    ///
    /// <para><b>SILK.NET TYPES ARE NAMED HERE, for the reason <see cref="IVkCmdSink"/> names them:</b> this seam
    /// exists to be a faithful picture of the barrier array a <c>vkCmdPipelineBarrier2</c> receives, and
    /// translating <c>VkImageMemoryBarrier2</c> into an engine-shaped copy would put a second structure between
    /// the tracker and the call it is a tracker for.</para>
    /// </summary>
    internal interface IVulkanBarrierRecorder
    {
        /// <summary>
        /// ONE <c>vkCmdPipelineBarrier2</c> CARRYING EVERY BARRIER IN <paramref name="barriers"/>. Never called
        /// with an empty span: a barrier call for no barriers is a native call bought for nothing, and the tracker
        /// skips it rather than emitting one.
        /// </summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        /// <param name="barriers">The image barriers, already built with both stage masks and both access masks
        /// named (V-F6).</param>
        void Emit(ulong commandBuffer, ReadOnlySpan<ImageMemoryBarrier2> barriers);
    }
}
