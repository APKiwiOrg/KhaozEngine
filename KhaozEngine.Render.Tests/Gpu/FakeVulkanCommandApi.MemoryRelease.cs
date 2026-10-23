using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;

namespace KhaozEngine.Tests.Gpu;

internal sealed partial class FakeVulkanCommandApi
{
    internal readonly record struct MemoryRelease(ulong Buffer, int EventsBefore, MemoryBarrier2 Barrier);
    internal List<MemoryRelease> MemoryReleases { get; } = new();

    public void ReleaseMemoryForReuse(ulong commandBuffer)
    {
        lock (_gate) VulkanMemoryRelease.Record(new ReleaseSink(this, commandBuffer));
    }

    readonly struct ReleaseSink(FakeVulkanCommandApi api, ulong buffer) : IVkCmdSink
    {
        public unsafe void PipelineBarrier(in DependencyInfo dependency)
        {
            if (dependency.BufferMemoryBarrierCount != 0 || dependency.ImageMemoryBarrierCount != 0)
                throw new InvalidOperationException("A pooled memory release must cover every resource reference.");
            for (uint i = 0; i < dependency.MemoryBarrierCount; i++)
                api.MemoryReleases.Add(new(buffer, api._log.Count, dependency.PMemoryBarriers[i]));
        }
        public void BindDescriptorSets(PipelineBindPoint bindPoint, PipelineLayout layout, uint firstSet,
            ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<uint> dynamicOffsets) => throw new NotSupportedException();
        public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
            => throw new NotSupportedException();
        public void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset,
            uint firstInstance) => throw new NotSupportedException();
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) => throw new NotSupportedException();
    }
}
