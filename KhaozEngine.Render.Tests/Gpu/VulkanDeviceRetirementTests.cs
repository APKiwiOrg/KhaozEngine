using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class VulkanDeviceRetirementTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public void Waiting_for_idle_releases_disposed_payload_without_present(bool texture, int work)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1199
        using var fixture = new VulkanDeviceFixture();
        using var list = fixture.RecordedList();
        using IDisposable resource = texture
            ? fixture.OpenSetupWork()
            : fixture.Device.Factory.CreateBuffer(new(256, GpuBufferUsage.VertexBuffer));

        if (work != 0) fixture.Device.Submit(list);
        if (work == 2) fixture.Semaphore.Completed = fixture.Timeline.LastSubmitted;
        if (work == 3)
        {
            fixture.CommandApi.FailNextSubmit(VulkanSubmitStatus.Failed);
            Assert.Throws<InvalidOperationException>(() => fixture.Device.Submit(list));
        }

        resource.Dispose();
        var held = fixture.Device.ReadMemorySnapshot();
        Assert.Equal(1UL, held.LiveResourceCount.Value!.UInt64);
        Assert.Equal(texture ? 64UL : 256UL,
            (texture ? held.LogicalTextureBytes : held.LogicalBufferBytes).Value!.UInt64);

        if (work == 1)
        {
            // Unfinished work must retain its payload until the wait proves completion.
            fixture.Device.DrainRetiredResources();
            Assert.Equal(1UL, fixture.Device.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        }

        fixture.Device.WaitForIdle();

        var released = fixture.Device.ReadMemorySnapshot();
        Assert.Equal(0UL, released.LiveResourceCount.Value!.UInt64);
        Assert.Equal(0UL, released.LogicalBufferBytes.Value!.UInt64);
        Assert.Equal(0UL, released.LogicalTextureBytes.Value!.UInt64);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Waiting_for_idle_also_frees_memory_retired_by_a_resource_destroy(bool submit)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1199
        using var fixture = new VulkanDeviceFixture();
        using var list = fixture.RecordedList();
        // A device-local allocation at the dedicated threshold has no retained pool chunk.
        using var buffer = fixture.Device.Factory.CreateBuffer(new(
            checked((uint)VulkanMemoryAllocator.DefaultDedicatedThreshold), GpuBufferUsage.VertexBuffer));
        if (submit) fixture.Device.Submit(list);
        buffer.Dispose();
        Assert.NotEmpty(fixture.MemoryApi.LiveHandles);

        fixture.Device.WaitForIdle();

        var memory = fixture.Device.ReadMemorySnapshot();
        Assert.Equal(0UL, memory.LiveResourceCount.Value!.UInt64);
        Assert.Equal(0UL, memory.CommittedBytes.Value!.UInt64);
        Assert.Empty(fixture.MemoryApi.LiveHandles);
    }

    [Fact]
    public void Waiting_for_idle_keeps_resources_retired_against_a_newer_unfinished_submission()
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1199
        using var fixture = new VulkanDeviceFixture();
        using var first = fixture.RecordedList();
        using var later = fixture.RecordedList();
        using var priorBuffer = fixture.Device.Factory.CreateBuffer(new(256, GpuBufferUsage.VertexBuffer));
        using var laterBuffer = fixture.Device.Factory.CreateBuffer(new(512, GpuBufferUsage.VertexBuffer));
        fixture.Device.Submit(first);
        priorBuffer.Dispose();
        fixture.Semaphore.OnWait = () =>
        {
            fixture.Device.Submit(later);
            laterBuffer.Dispose();
        };

        fixture.Device.WaitForIdle();

        Assert.True(fixture.Semaphore.Completed < fixture.Timeline.LastSubmitted);
        var retained = fixture.Device.ReadMemorySnapshot();
        Assert.Equal(1UL, retained.LiveResourceCount.Value!.UInt64);
        Assert.Equal(512UL, retained.LogicalBufferBytes.Value!.UInt64);

        fixture.Semaphore.OnWait = null;
        fixture.Device.WaitForIdle();
        Assert.Equal(0UL, fixture.Device.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Waiting_for_idle_does_not_destroy_retired_native_objects_after_device_loss(bool duringWait)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1199
        using var fixture = new VulkanDeviceFixture();
        using var list = fixture.RecordedList();
        using var buffer = fixture.Device.Factory.CreateBuffer(new(256, GpuBufferUsage.VertexBuffer));
        fixture.Device.Submit(list);
        buffer.Dispose();
        if (duringWait)
        {
            fixture.Semaphore.WaitReachesTheValue = false;
            fixture.Semaphore.OnWait = fixture.Liveness.MarkDead;
        }
        else fixture.Liveness.MarkDead();

        fixture.Device.WaitForIdle();

        Assert.Contains(((VulkanBuffer)buffer).Handle, fixture.ResourceApi.Live);
        Assert.NotEmpty(fixture.MemoryApi.LiveHandles);
        Assert.Equal(1, fixture.Device.Retired.Count);
    }
}
