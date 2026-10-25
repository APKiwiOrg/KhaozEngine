using System;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class GpuRetireQueueSubmissionTests
{
    [Fact]
    public void Host_completion_releases_replacements_without_an_extra_submission()
    {
        using var device = new GpuPerfTimestampTestDevice();
        using var queue = GpuRetireQueue.Create(device);
        using var host = new GpuFrameSubmission(device);
        using var commands = device.Factory.CreateCommandList();
        var resource = new Resource();
        host.Begin();
        queue.Retire(resource);
        queue.BeginFrame(host);
        Assert.Equal(0, device.Submissions);
        Assert.False(resource.Disposed);
        host.Submit(commands);
        Assert.Equal(1, device.Submissions);
        host.Begin(); queue.BeginFrame(host);
        Assert.False(resource.Disposed);
        device.Fences[0].Signaled = true;
        host.Abandon();
        host.Begin(); queue.BeginFrame(host);
        Assert.True(resource.Disposed);
        Assert.Equal(0, device.Waits);
        Assert.Equal(0, queue.PendingCount);
        host.Abandon();
    }

    [Fact]
    public void Abandoned_host_keeps_earlier_readers_alive_until_a_later_completion()
    {
        using var device = new GpuPerfTimestampTestDevice();
        using var queue = GpuRetireQueue.Create(device);
        using var host = new GpuFrameSubmission(device);
        using var commands = device.Factory.CreateCommandList();
        var resource = new Resource();
        host.Begin(); queue.Retire(resource); queue.BeginFrame(host); host.Abandon();
        Assert.False(resource.Disposed);
        host.Begin(); queue.BeginFrame(host); host.Submit(commands);
        Assert.Equal(1, device.Submissions);
        Assert.False(resource.Disposed);
        foreach (var fence in device.Fences) fence.Signaled = true;
        host.Begin(); queue.BeginFrame(host); host.Abandon();
        Assert.True(resource.Disposed);
        Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public void Failed_submission_quarantines_the_batch_until_teardown_completion()
    {
        using var device = new GpuPerfTimestampTestDevice();
        using var queue = GpuRetireQueue.Create(device);
        var host = new GpuFrameSubmission(device);
        using var commands = device.Factory.CreateCommandList();
        var resource = new Resource();
        host.Begin(); queue.Retire(resource); queue.BeginFrame(host);
        device.FailSubmit = true;
        Assert.Throws<InvalidOperationException>(() => host.Submit(commands));
        Assert.False(resource.Disposed);
        host.Dispose();
        Assert.True(resource.Disposed);
        Assert.Equal(1, resource.Disposals);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void Capacity_refusal_preserves_both_sealed_and_unsealed_resources()
    {
        using var device = new GpuPerfTimestampTestDevice();
        var first = new Resource(); var second = new Resource();
        using (var queue = GpuRetireQueue.Create(device))
        {
            using var host = new GpuFrameSubmission(device, retainedFrames: 1);
            using var commands = device.Factory.CreateCommandList();
            host.Begin(); queue.Retire(first); queue.BeginFrame(host); host.Submit(commands);
            host.Begin(); queue.Retire(second);
            Assert.Throws<InvalidOperationException>(() => queue.BeginFrame(host));
            Assert.False(first.Disposed); Assert.False(second.Disposed);
            Assert.Equal(1, device.Submissions); Assert.Equal(0, device.Waits);
            host.Abandon(); host.Dispose();
            Assert.True(first.Disposed); Assert.False(second.Disposed);
        }
        Assert.Equal(1, first.Disposals); Assert.Equal(1, second.Disposals);
    }

    [Fact]
    public void Queue_teardown_does_not_reset_or_dispose_the_borrowed_host_fence()
    {
        using var device = new GpuPerfTimestampTestDevice();
        using var host = new GpuFrameSubmission(device);
        using var commands = device.Factory.CreateCommandList();
        var queue = GpuRetireQueue.Create(device);
        host.Begin(); queue.Retire(new Resource()); queue.BeginFrame(host); host.Submit(commands);
        var borrowed = device.Fences[0];
        queue.Dispose();
        Assert.Equal(0, borrowed.ResetCalls); Assert.Equal(0, borrowed.DisposeCalls);
        host.Dispose();
        Assert.Equal(1, borrowed.DisposeCalls);
    }

    [Fact]
    public void Failed_drain_preserves_a_shared_batch_for_a_successful_teardown()
    {
        using var device = new GpuPerfTimestampTestDevice();
        using var queue = GpuRetireQueue.Create(device);
        using var host = new GpuFrameSubmission(device);
        using var commands = device.Factory.CreateCommandList();
        var resource = new Resource();
        host.Begin(); queue.Retire(resource); queue.BeginFrame(host); host.Submit(commands);
        device.Trace = operation => { if (operation == "drain") throw new InvalidOperationException("lost device"); };
        Assert.Throws<InvalidOperationException>(queue.FlushAll);
        Assert.False(resource.Disposed);
        Assert.Throws<GpuDiagnosticFailureException>(host.Dispose);
        Assert.False(resource.Disposed);
        Assert.All(device.Fences, fence => Assert.Equal(0, fence.DisposeCalls));
        device.Trace = null; host.Dispose();
        Assert.True(resource.Disposed); Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public void A_foreign_host_cannot_prove_completion_for_this_device()
    {
        using var device = new GpuPerfTimestampTestDevice();
        using var other = new GpuPerfTimestampTestDevice();
        using var queue = GpuRetireQueue.Create(device);
        using var host = new GpuFrameSubmission(other);
        var resource = new Resource();
        queue.Retire(resource); host.Begin();
        Assert.Throws<ArgumentException>(() => queue.BeginFrame(host));
        Assert.False(resource.Disposed);
        Assert.Equal(0, device.Submissions); Assert.Equal(0, other.Submissions);
        host.Abandon();
    }

    sealed class Resource : IDisposable
    {
        internal int Disposals;
        internal bool Disposed => Disposals > 0;
        public void Dispose() => Disposals++;
    }
}
