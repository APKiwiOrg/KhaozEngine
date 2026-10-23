using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class VulkanMemoryReleaseTests
{
    [Fact]
    public void Setup_releases_device_and_host_aliases_before_completion_can_retire_memory()
    {
        var fixture = new VulkanResourceFixture();
        using var destination = fixture.Factory.CreateBuffer(new(256, GpuBufferUsage.StructuredBufferReadWrite, 4));
        fixture.Setup.UploadBuffer((IVulkanUploadDestination)destination, 0, new byte[16]);
        Assert.Empty(fixture.CommandApi.MemoryReleases);
        fixture.Setup.Flush();
        var release = Assert.Single(fixture.CommandApi.MemoryReleases);
        Assert.StartsWith("End(", fixture.CommandApi.Events[release.EventsBefore], StringComparison.Ordinal);
        Assert.Equal(release.Buffer, Assert.Single(fixture.CommandApi.Submissions).Buffer);
        AssertRelease(release.Barrier);
    }

    [Fact]
    public void Every_seal_keeps_its_release_even_when_another_recording_is_abandoned()
    {
        var fixture = new VulkanResourceFixture();
        using var abandoned = fixture.CreateList();
        using var submitted = fixture.CreateList();
        abandoned.Begin(); abandoned.End();
        submitted.Begin(); submitted.End();
        abandoned.Begin(); abandoned.End();
        Assert.Equal(3, fixture.CommandApi.MemoryReleases.Count);
        fixture.Submits.Submit(submitted, null);
        var release = fixture.CommandApi.MemoryReleases[1];
        Assert.Equal(Assert.Single(fixture.CommandApi.Submissions).Buffer, release.Buffer);
        foreach (var item in fixture.CommandApi.MemoryReleases)
        {
            Assert.StartsWith("End(", fixture.CommandApi.Events[item.EventsBefore], StringComparison.Ordinal);
            AssertRelease(item.Barrier);
        }
    }

    [Fact]
    public void Completed_retirement_can_reuse_the_exact_storage_range_for_a_coherent_uniform_ring()
    {
        var fixture = new VulkanResourceFixture();
        var facts = new VulkanMemoryFacts(
            [new(0, 0, VulkanMemoryTrait.DeviceLocal | VulkanMemoryTrait.HostVisible | VulkanMemoryTrait.HostCoherent)],
            NonCoherentAtomSize: 64, MaxAllocationCount: 4096);
        using var memory = new VulkanMemoryAllocator(new FakeVulkanDeviceMemoryApi(), facts,
            new VulkanTimelineRetirement(fixture.Timeline, fixture.Retired), chunkSize: 4096, dedicatedThreshold: 8192);
        var prior = memory.Allocate(new(256, 16, 1, VulkanMemoryUsage.DeviceLocal, VulkanMemoryTiling.Linear));
        using var list = fixture.CreateList();
        list.Begin(); list.End();
        ulong value = fixture.Submits.Submit(list, null);
        fixture.Retired.Retire(value, () => memory.Free(prior));
        Assert.Equal(0, fixture.Retired.Drain(fixture.Timeline.CompletedValue));
        fixture.Semaphore.Completed = value;
        Assert.Equal(1, fixture.Retired.Drain(fixture.Timeline.CompletedValue));
        var ring = memory.Allocate(new(256, 16, 1, VulkanMemoryUsage.Ring, VulkanMemoryTiling.Linear));
        Assert.Equal(prior.Memory, ring.Memory);
        Assert.Equal(prior.Offset, ring.Offset);
        Assert.Equal(prior.MappedPointer, ring.MappedPointer);
        AssertRelease(Assert.Single(fixture.CommandApi.MemoryReleases).Barrier);
        Assert.Equal(0, fixture.Semaphore.WaitCount);
        memory.Free(ring);
    }

    [Fact]
    public void Empty_dead_and_disposed_setup_batches_publish_no_release()
    {
        var fixture = new VulkanResourceFixture();
        Assert.Equal(0ul, fixture.Setup.Flush());
        using var destination = fixture.Factory.CreateBuffer(new(256, GpuBufferUsage.StructuredBufferReadWrite, 4));
        fixture.Setup.UploadBuffer((IVulkanUploadDestination)destination, 0, new byte[16]);
        fixture.Liveness.Kill();
        Assert.Equal(0ul, fixture.Setup.Flush());
        fixture.Setup.Dispose();
        Assert.Empty(fixture.CommandApi.MemoryReleases);
        Assert.Empty(fixture.CommandApi.Submissions);
    }

    [Fact]
    public void Setup_flush_after_list_seal_keeps_both_release_boundaries()
    {
        var fixture = new VulkanResourceFixture();
        using var list = fixture.CreateList();
        list.Begin();
        using var allocatedDuringRecording = fixture.Factory.CreateBuffer(new(256, GpuBufferUsage.StructuredBufferReadWrite, 4));
        fixture.Setup.UploadBuffer((IVulkanUploadDestination)allocatedDuringRecording, 0, new byte[16]);
        list.End();
        fixture.Setup.Flush();
        fixture.Submits.Submit(list, null);
        Assert.Equal(2, fixture.CommandApi.MemoryReleases.Count);
        Assert.Equal(2, fixture.CommandApi.Submissions.Count);
        Assert.Equal(fixture.CommandApi.MemoryReleases[1].Buffer, fixture.CommandApi.Submissions[0].Buffer);
        Assert.Equal(fixture.CommandApi.MemoryReleases[0].Buffer, fixture.CommandApi.Submissions[1].Buffer);
        foreach (var release in fixture.CommandApi.MemoryReleases) AssertRelease(release.Barrier);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disposal_discards_an_open_recording_without_publishing_its_release(bool setup)
    {
        var fixture = new VulkanResourceFixture();
        if (setup)
        {
            using var destination = fixture.Factory.CreateBuffer(new(256, GpuBufferUsage.StructuredBufferReadWrite, 4));
            fixture.Setup.UploadBuffer((IVulkanUploadDestination)destination, 0, new byte[16]);
            fixture.Setup.Retire(fixture.Retired);
        }
        else
        {
            var list = fixture.CreateList();
            list.Begin();
            list.Dispose();
        }
        Assert.Empty(fixture.CommandApi.MemoryReleases);
        Assert.Empty(fixture.CommandApi.Submissions);
    }

    internal static void AssertRelease(MemoryBarrier2 barrier)
    {
        Assert.Equal(StructureType.MemoryBarrier2, barrier.SType);
        Assert.Equal(PipelineStageFlags2.AllCommandsBit, barrier.SrcStageMask);
        Assert.Equal(AccessFlags2.MemoryWriteBit, barrier.SrcAccessMask);
        Assert.Equal(PipelineStageFlags2.AllCommandsBit | PipelineStageFlags2.HostBit, barrier.DstStageMask);
        Assert.Equal(AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit
            | AccessFlags2.HostReadBit | AccessFlags2.HostWriteBit, barrier.DstAccessMask);
    }
}
