using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class VulkanStagingSourceVisibilityTests
{
    [Fact]
    public void Setup_upload_releases_pool_writes_for_the_next_staging_owner()
    {
        var fixture = new VulkanResourceFixture();
        using var destination = fixture.Factory.CreateBuffer(new(256, GpuBufferUsage.StructuredBufferReadWrite, 4));
        fixture.Setup.UploadBuffer((IVulkanUploadDestination)destination, 32, new byte[16]);
        Assert.Empty(fixture.SetupSink.MemoryBarriers);
        Assert.Equal(2, fixture.SetupSink.BufferBarriers.Count);
        Assert.Equal(32ul, fixture.SetupSink.BufferBarriers[0].Offset);
        Assert.Equal(16ul, fixture.SetupSink.BufferBarriers[0].Size);
        var copy = Assert.Single(fixture.SetupSink.BufferCopies);
        Assert.NotEqual(0ul, copy.Source);
        Assert.Equal(((VulkanBuffer)destination).Handle, copy.Destination);
        Assert.Equal(0ul, copy.SourceOffset);
        Assert.Equal(32ul, copy.DestinationOffset);
        Assert.Equal(16ul, copy.Size);
        Assert.Empty(fixture.CommandApi.Submissions);
        fixture.Setup.Flush();
        VulkanMemoryReleaseTests.AssertRelease(Assert.Single(fixture.CommandApi.MemoryReleases).Barrier);
    }

    [Fact]
    public void Texture_upload_releases_pool_writes_for_the_next_staging_owner()
    {
        var fixture = new VulkanResourceFixture();
        using var texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(4, 4, GpuTextureUsage.Sampled));
        var native = (VulkanTexture)texture;
        fixture.SetupSink.Clear();
        fixture.Setup.Upload(new VulkanImageUpload(native.Image, false, 0, 0, 0, 0, 4, 4,
            GpuPixelFormat.R8G8B8A8UNorm, native.Resting), new byte[64]);
        Assert.Empty(fixture.SetupSink.MemoryBarriers);
        Assert.Equal(2, fixture.SetupSink.ImageBarriers.Count);
        var copy = Assert.Single(fixture.SetupSink.ImageCopies);
        Assert.NotEqual(0ul, copy.Source);
        Assert.Equal(native.Image, copy.Image);
        Assert.Equal(0ul, copy.BufferOffset);
        Assert.Equal(4u, copy.Width);
        Assert.Equal(4u, copy.Height);
        Assert.Empty(fixture.CommandApi.Submissions);
        fixture.Setup.Flush();
        VulkanMemoryReleaseTests.AssertRelease(Assert.Single(fixture.CommandApi.MemoryReleases).Barrier);
    }

    [Fact]
    public void Recorded_upload_releases_pool_writes_for_the_next_staging_owner()
    {
        var fixture = new VulkanResourceFixture();
        var ring = new VulkanCommandPoolRing(fixture.CommandApi, 3, fixture.Timeline, fixture.Backpressure);
        int slot = ring.Advance();
        var state = new Capture();
        using var arena = new VulkanStagingArena(new Source(), 3, blockBytes: 1024);
        var sink = new Sink(state);
        VulkanBufferUpload.Record(sink, sink, arena, null, new Destination(), 32, new byte[16]);
        Assert.Empty(state.MemoryBarriers);
        Assert.Equal(2, state.BufferBarriers.Count);
        Assert.Equal(32ul, state.BufferBarriers[0].Offset);
        Assert.Equal(16ul, state.BufferBarriers[0].Size);
        Assert.Equal(1, state.Copies);
        Assert.Equal((100ul, 0ul, 200ul, 32ul, 16ul), state.Copy);
        Assert.Equal(2, state.BarrierCalls);
        ring.EndRecording(slot);
        VulkanMemoryReleaseTests.AssertRelease(Assert.Single(fixture.CommandApi.MemoryReleases).Barrier);
    }

    sealed class Capture
    {
        internal int Copies, BarrierCalls;
        internal (ulong, ulong, ulong, ulong, ulong) Copy;
        internal List<(MemoryBarrier2 Barrier, int CopiesBefore)> MemoryBarriers { get; } = new();
        internal List<BufferMemoryBarrier2> BufferBarriers { get; } = new();
    }

    readonly struct Sink(Capture state) : IVkCmdSink, IVulkanUploadSink
    {
        public unsafe void PipelineBarrier(in DependencyInfo dependency)
        {
            state.BarrierCalls++;
            for (uint i = 0; i < dependency.MemoryBarrierCount; i++)
                state.MemoryBarriers.Add((dependency.PMemoryBarriers[i], state.Copies));
            for (uint i = 0; i < dependency.BufferMemoryBarrierCount; i++)
                state.BufferBarriers.Add(dependency.PBufferMemoryBarriers[i]);
        }
        public void CopyBuffer(ulong source, ulong sourceOffsetBytes, ulong destination, ulong destinationOffsetBytes,
            ulong sizeBytes)
        {
            state.Copies++;
            state.Copy = (source, sourceOffsetBytes, destination, destinationOffsetBytes, sizeBytes);
        }
        public void BindDescriptorSets(PipelineBindPoint bindPoint, PipelineLayout layout, uint firstSet,
            ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<uint> dynamicOffsets)
        { }
        public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance) { }
        public void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset,
            uint firstInstance)
        { }
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) { }
    }

    sealed class Source : IVulkanStagingSource
    {
        public VulkanStagingBlock Create(ulong sizeBytes)
            => new(100, Marshal.AllocHGlobal(checked((int)sizeBytes)), sizeBytes);
        public void Destroy(in VulkanStagingBlock block) => Marshal.FreeHGlobal(block.Mapped);
    }

    sealed class Destination : IVulkanUploadDestination
    {
        public ulong DeviceBuffer => 200;
        public GpuBufferUsage UploadUsage => GpuBufferUsage.StructuredBufferReadWrite;
    }
}
