using System;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE REAL <see cref="IVkCmdSink"/>: five <c>vkCmd*</c> calls against one command buffer, and nothing else at
    /// all. No guard, no cache, no dirty tracking and no decision of any kind.
    /// <para>
    /// THAT EMPTINESS IS THE DESIGN. Everything a budget could be wrong about (which slots are dirty, how a run is
    /// cut, what the positional dynamic-offset array contains, whether a redundant bind is skipped, whether a
    /// barrier was needed) lives ABOVE this line in device-free types, so the budget test taken over
    /// <see cref="VulkanCountingCmdSink"/> measures the SHIPPED schedule rather than a second copy of it. What can
    /// still drift between the two sinks is nothing: the members below have no branch in them.
    /// </para>
    /// <para>
    /// A READONLY STRUCT over two handle-sized values, so the JIT monomorphizes it into whatever recorder drives
    /// it and a copy of it still names the same buffer. It carries no mutable state at all, which is the strongest
    /// form of the emitter rule the other backend enforces by reflection.
    /// </para>
    /// <para>
    /// NO RESULT TO CHECK ANYWHERE. Every <c>vkCmd*</c> returns void: recording errors are reported by
    /// <c>vkEndCommandBuffer</c> (which <see cref="VulkanCommandApi"/> checks) or by the validation layer, not per
    /// call. That is why this type has no loss latch, and why it is the one place in this package where a native
    /// call is made with nothing examined afterwards.
    /// </para>
    /// </summary>
    internal readonly unsafe struct VulkanCmdSink : IVkCmdSink
    {
        readonly Vk _vk;
        readonly CommandBuffer _buffer;

        /// <param name="vk">The instance's loaded API.</param>
        /// <param name="buffer">The command buffer being recorded into, which the caller has begun and has not yet
        /// ended.</param>
        internal VulkanCmdSink(Vk vk, CommandBuffer buffer)
        {
            ArgumentNullException.ThrowIfNull(vk);

            _vk = vk;
            _buffer = buffer;
        }

        /// <inheritdoc/>
        public void BindDescriptorSets(PipelineBindPoint bindPoint, PipelineLayout layout, uint firstSet,
            ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<uint> dynamicOffsets)
        {
            fixed (DescriptorSet* pSets = sets)
            fixed (uint* pOffsets = dynamicOffsets)
            {
                _vk.CmdBindDescriptorSets(_buffer, bindPoint, layout, firstSet, (uint)sets.Length, pSets,
                    (uint)dynamicOffsets.Length, pOffsets);
            }
        }

        /// <inheritdoc/>
        public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
            => _vk.CmdDraw(_buffer, vertexCount, instanceCount, firstVertex, firstInstance);

        /// <inheritdoc/>
        public void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset,
            uint firstInstance)
            => _vk.CmdDrawIndexed(_buffer, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);

        /// <inheritdoc/>
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
            => _vk.CmdDispatch(_buffer, groupCountX, groupCountY, groupCountZ);

        /// <inheritdoc/>
        public void PipelineBarrier(in DependencyInfo dependency)
            => _vk.CmdPipelineBarrier2(_buffer, in dependency);
    }
}
