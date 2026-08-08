using System;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE REAL <see cref="IVulkanBarrierRecorder"/>: build the <c>VkDependencyInfo</c>, hand it to
    /// <see cref="IVkCmdSink.PipelineBarrier"/>, and nothing else at all. No guard, no cache, no decision of any
    /// kind, which is the same emptiness <see cref="VulkanCmdSink"/> is built on and for the same reason:
    /// everything a barrier can be wrong about (which layout, which masks, whether one was needed) lives ABOVE
    /// this line in device-free types.
    /// <para>
    /// THE SINK IS A CONCRETE <see cref="VulkanCmdSink"/> AND NOT AN <see cref="IVkCmdSink"/> FIELD, so the call
    /// below inlines straight through to <c>vkCmdPipelineBarrier2</c> with no interface dispatch and nothing
    /// boxed. That is the whole reason this adapter exists rather than the tracker holding the budget seam
    /// directly.
    /// </para>
    /// <para>
    /// STATELESS, so one per list costs nothing and two of them cannot disagree. It is built per list for the
    /// reason <see cref="VulkanRenderApi"/> and <see cref="VulkanPipelineBinder"/> are.
    /// </para>
    /// </summary>
    internal sealed unsafe class VulkanBarrierRecorder : IVulkanBarrierRecorder
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
        {
            if (barriers.Length == 0) return;

            var sink = new VulkanCmdSink(_vk, new CommandBuffer((nint)commandBuffer));

            fixed (ImageMemoryBarrier2* pBarriers = barriers)
            {
                var dependency = new DependencyInfo(
                    sType: StructureType.DependencyInfo,
                    imageMemoryBarrierCount: (uint)barriers.Length,
                    pImageMemoryBarriers: pBarriers);

                sink.PipelineBarrier(in dependency);
            }
        }
    }
}
