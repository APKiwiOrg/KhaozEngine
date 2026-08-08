using System;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE REAL <see cref="IVulkanBarrierRecorder"/>: name the command buffer's <see cref="VulkanCmdSink"/> and
    /// hand it to <see cref="VulkanBarrierBatch"/>, and nothing else at all. No guard, no cache, no decision
    /// of any kind, which is the same emptiness <see cref="VulkanCmdSink"/> is built on and for the same reason:
    /// everything a barrier can be wrong about (which layout, which masks, whether one was needed) lives ABOVE
    /// this line in device-free types.
    /// <para>
    /// THE SINK IS BUILT PER CALL RATHER THAN HELD, because a <see cref="VulkanCmdSink"/> names one command
    /// buffer and the buffer changes with the ring slot every time the list is re-begun. It is a concrete struct
    /// at the point of use, so the call inlines straight through to <c>vkCmdPipelineBarrier2</c> with no interface
    /// dispatch and nothing boxed.
    /// </para>
    /// <para>
    /// THE COUNTING TWIN IS <see cref="VulkanCountingBarrierRecorder"/>, and the two exist so the tracker's
    /// emitter is SUBSTITUTABLE. Both bodies are one call into <see cref="VulkanBarrierBatch"/>, so what a
    /// device-free budget counts is the shipped batching rather than a second copy of it.
    /// </para>
    /// <para>
    /// STATELESS, so one per list costs nothing and two of them cannot disagree. It is built per list for the
    /// reason <see cref="VulkanRenderApi"/> and <see cref="VulkanPipelineBinder"/> are.
    /// </para>
    /// </summary>
    internal sealed class VulkanBarrierRecorder : IVulkanBarrierRecorder
    {
        readonly Vk _vk;

        /// <param name="vk">The instance's loaded API.</param>
        internal VulkanBarrierRecorder(Vk vk)
        {
            ArgumentNullException.ThrowIfNull(vk);

            _vk = vk;
        }

        /// <inheritdoc/>
        public void Emit(ulong commandBuffer, ReadOnlySpan<ImageMemoryBarrier2> barriers)
            => VulkanBarrierBatch.Emit(new VulkanCmdSink(_vk, new CommandBuffer((nint)commandBuffer)), barriers);
    }

    /// <summary>
    /// THE DEVICE-FREE <see cref="IVulkanBarrierRecorder"/>: the same <see cref="VulkanBarrierBatch"/> call
    /// over a <see cref="VulkanCountingCmdSink"/>, so every image barrier the layout tracker emits is tallied into
    /// a <see cref="VulkanCmdCallCounts"/> and nothing else happens.
    /// <para>
    /// IT EXISTS BECAUSE V-T2's BUDGET IS OTHERWISE BLIND TO HALF OF WHAT IT BOUNDS. "No pipeline barriers on the
    /// per-draw path" is a statement about the tracker's image barriers as much as about the staged upload's
    /// buffer barrier, and an emitter that could only ever be the real one would leave
    /// <see cref="VulkanCmdCallCounts.BarrierCalls"/> at zero between two draws no matter what the tracker did.
    /// A budget that cannot fail is worse than no budget, because it reads as evidence.
    /// </para>
    /// <para>
    /// IT TALLIES INTO THE SAME COUNTS OBJECT THE BINDS DO, deliberately, so one budget test reads one set of
    /// numbers for a whole recording rather than reconciling two tallies that could drift apart.
    /// </para>
    /// <para>
    /// THE COMMAND BUFFER IS IGNORED, because there is no buffer: the counting sink records which calls were made
    /// and with what shape, and where they would have gone is the one thing a device-free tally cannot check.
    /// <c>FakeVulkanBarrierRecorder</c> in the test project keeps the buffer and the barriers themselves,
    /// which is the assertion this type deliberately does not make.
    /// </para>
    /// </summary>
    internal sealed class VulkanCountingBarrierRecorder : IVulkanBarrierRecorder
    {
        readonly VulkanCountingCmdSink _sink;

        /// <param name="counts">The tallies to write into, shared with whatever else drives this recording.</param>
        internal VulkanCountingBarrierRecorder(VulkanCmdCallCounts counts)
        {
            ArgumentNullException.ThrowIfNull(counts);

            _sink = new VulkanCountingCmdSink(counts);
        }

        /// <summary>The tallies this recorder writes into.</summary>
        internal VulkanCmdCallCounts Counts => _sink.Counts;

        /// <inheritdoc/>
        public void Emit(ulong commandBuffer, ReadOnlySpan<ImageMemoryBarrier2> barriers)
            => VulkanBarrierBatch.Emit(_sink, barriers);
    }
}
