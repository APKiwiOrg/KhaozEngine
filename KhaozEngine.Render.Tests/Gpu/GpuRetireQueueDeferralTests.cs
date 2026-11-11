using System;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class GpuRetireQueueDeferralTests
{
    // #1765. Shared retirement must keep the standard host running when its completion pool is full.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deferred_tail_waits_for_a_later_submitted_fence_even_after_abandon(bool abandon)
    {
        using var device = new GpuPerfTimestampTestDevice();
        using var queue = GpuRetireQueue.Create(device);
        using var host = new GpuFrameSubmission(device, retainedFrames: 1);
        using var commands = device.Factory.CreateCommandList();
        var first = new Resource(); var tail = new Resource(); var later = new Resource();
        host.Begin(); queue.Retire(first); queue.BeginFrame(host); host.Submit(commands);
        host.Begin(); queue.Retire(tail); queue.BeginFrame(host);
        Assert.Equal(1, queue.SharedSealDeferrals);
        Assert.Equal(2, queue.PendingCount);
        Assert.Equal(0, first.Disposals); Assert.Equal(0, tail.Disposals);
        if (abandon) host.Abandon(); else host.Submit(commands);
        device.Fences[0].Signaled = true;
        host.Begin(); queue.Retire(later); queue.BeginFrame(host);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(0, tail.Disposals); Assert.Equal(0, later.Disposals);
        host.Submit(commands);
        Assert.Equal(0, tail.Disposals);
        device.Fences[0].Signaled = true;
        host.Begin(); queue.BeginFrame(host); host.Abandon();
        Assert.Equal(1, tail.Disposals); Assert.Equal(1, later.Disposals);
        Assert.Equal(0, queue.PendingCount); Assert.Equal(0, queue.ValveDrains);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sustained_deferral_is_bounded_by_one_permitted_drain(bool consumerFull)
    {
        using var device = new GpuPerfTimestampTestDevice();
        using var queue = GpuRetireQueue.Create(device, maxSealedBatches: 2);
        using var host = new GpuFrameSubmission(device, retainedFrames: 1, consumerCapacity: 1);
        using var commands = device.Factory.CreateCommandList();
        var first = new Resource(); var child = new Resource();
        var tail = new Resource(() => queue.Retire(child));
        host.Begin();
        if (consumerFull) Assert.True(host.TryRetain(new OccupiedConsumer(), out _));
        queue.Retire(first); queue.BeginFrame(host); host.Submit(commands);
        host.Begin(); queue.Retire(tail); queue.BeginFrame(host); host.Submit(commands);
        Assert.Equal(0, first.Disposals); Assert.Equal(0, tail.Disposals);
        host.Begin(); queue.BeginFrame(host);
        Assert.Equal(1, queue.ValveDrains); Assert.Equal(1, device.Waits);
        Assert.Equal(1, first.Disposals); Assert.Equal(1, tail.Disposals);
        Assert.Equal(1, child.Disposals);
        Assert.Equal(0, queue.PendingCount);
        Assert.NotEmpty(device.Fences);
        Assert.All(device.Fences, f => { Assert.Equal(0, f.ResetCalls); Assert.Equal(0, f.DisposeCalls); });
        host.Abandon();
    }

    [Fact]
    public void Frame_count_only_policy_never_drains_for_shared_deferrals()
    {
        using var device = new GpuPerfTimestampTestDevice();
        using var queue = GpuRetireQueue.CreateFrameCounted(device, frameDelay: 1);
        using var host = new GpuFrameSubmission(device, retainedFrames: 1);
        using var commands = device.Factory.CreateCommandList();
        var first = new Resource(); var tail = new Resource();
        host.Begin(); queue.Retire(first); queue.BeginFrame(host); host.Submit(commands);
        for (int i = 0; i <= queue.MaxSealedBatches; i++)
        {
            host.Begin();
            if (i == 0) queue.Retire(tail);
            queue.BeginFrame(host); host.Submit(commands);
        }
        Assert.Equal(queue.MaxSealedBatches + 1, queue.SharedSealDeferrals);
        Assert.Equal(0, device.Waits); Assert.Equal(0, queue.ValveDrains);
        Assert.Equal(0, first.Disposals); Assert.Equal(0, tail.Disposals);
        device.Fences[0].Signaled = true;
        host.Begin(); queue.BeginFrame(host); host.Submit(commands);
        Assert.Equal(1, first.Disposals); Assert.Equal(0, tail.Disposals);
        device.Fences[0].Signaled = true;
        host.Begin(); queue.BeginFrame(host); host.Abandon();
        Assert.Equal(1, tail.Disposals);
    }

    sealed class Resource(Action? release = null) : IDisposable
    {
        internal int Disposals;
        public void Dispose() { Disposals++; release?.Invoke(); }
    }

    sealed class OccupiedConsumer : IGpuFrameCompletionConsumer
    {
        public bool HasPending(IGpuFence completion) => false;
        public void DiscardUnsubmitted(IGpuFence completion) { }
        public void ReleaseCompleted(IGpuFence completion) { }
    }
}
