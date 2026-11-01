using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// ONE <c>vkCmdBindDescriptorSets</c> AS THE DRIVER WOULD HAVE RECEIVED IT. The counting sink in the backend
    /// tallies how many, which is what a BUDGET is over. This records WHAT, which is what the positional
    /// dynamic-offset array needs: a count alone cannot tell a correctly composed array from one whose entries
    /// landed on the wrong descriptors.
    /// </summary>
    /// <param name="BindPoint">Graphics or compute.</param>
    /// <param name="PipelineLayout">The <c>VkPipelineLayout</c> the run was bound under.</param>
    /// <param name="FirstSet">The run's starting set number.</param>
    /// <param name="Sets">The <c>VkDescriptorSet</c> handles, in slot order.</param>
    /// <param name="DynamicOffsets">The positional array, in set-then-binding order.</param>
    internal readonly record struct VulkanRecordedBind(
        PipelineBindPoint BindPoint, ulong PipelineLayout, uint FirstSet, ulong[] Sets, uint[] DynamicOffsets);

    /// <summary>
    /// AN <see cref="IVkCmdSink"/> THAT KEEPS THE ARGUMENTS. A readonly struct over one list, for the same reason
    /// every sink in the backend is one: it is copied into whatever recorder drives it, so its state sits behind a
    /// reference and two copies still append to one log.
    /// </summary>
    internal readonly struct VulkanCapturingCmdSink : IVkCmdSink
    {
        readonly List<VulkanRecordedBind> _binds;

        internal VulkanCapturingCmdSink(List<VulkanRecordedBind> binds) => _binds = binds;

        /// <inheritdoc/>
        public void BindDescriptorSets(PipelineBindPoint bindPoint, PipelineLayout layout, uint firstSet,
            ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<uint> dynamicOffsets)
        {
            var handles = new ulong[sets.Length];
            for (int i = 0; i < sets.Length; i++) handles[i] = sets[i].Handle;

            _binds.Add(new VulkanRecordedBind(bindPoint, layout.Handle, firstSet, handles,
                dynamicOffsets.ToArray()));
        }

        /// <inheritdoc/>
        public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
        {
        }

        /// <inheritdoc/>
        public void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset,
            uint firstInstance)
        {
        }

        /// <inheritdoc/>
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
        }

        /// <inheritdoc/>
        public void PipelineBarrier(in DependencyInfo dependency)
        {
        }
    }
}
