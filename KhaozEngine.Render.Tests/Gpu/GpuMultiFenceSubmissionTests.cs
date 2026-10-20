using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class GpuMultiFenceSubmissionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Legacy_device_default_forwards_only_supported_span_sizes(int count)
    {
        var gpu = new SubmissionTestDevice(0);
        IGpuDevice device = new LegacySubmissionDevice(gpu);
        var fences = new IGpuFence[count];
        for (int i = 0; i < count; i++) fences[i] = gpu.Factory.CreateFence();
        if (count < 2)
        {
            device.Submit(gpu, fences.AsSpan());
            Assert.Equal(1, gpu.SubmitCalls);
            Assert.Same(count == 0 ? null : fences[0], gpu.SubmittedFence);
        }
        else
        {
            Assert.Throws<NotSupportedException>(() => device.Submit(gpu, fences.AsSpan()));
            Assert.Equal(0, gpu.SubmitCalls);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Headless_submit_carries_every_fence_once(int count)
    {
        var gpu = new SubmissionTestDevice(0);
        IGpuDevice device = gpu;
        var fences = new IGpuFence[count];
        for (int i = 0; i < count; i++) fences[i] = device.Factory.CreateFence();
        device.Submit(gpu, fences.AsSpan());
        Assert.Equal(1, gpu.SubmitCalls);
        Assert.Equal(fences, gpu.SubmittedFences);
    }

    [Fact]
    public void Repeated_fence_is_rejected_before_submission()
    {
        var gpu = new SubmissionTestDevice(0);
        IGpuDevice device = gpu;
        using var fence = device.Factory.CreateFence();
        Assert.Throws<ArgumentException>(() => device.Submit(gpu, new IGpuFence[] { fence, fence }.AsSpan()));
        Assert.Equal(0, gpu.SubmitCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frame_submission_carries_caller_fences_with_its_retained_slot(bool retain)
    {
        var gpu = new SubmissionTestDevice(0);
        using var submission = new GpuFrameSubmission(gpu);
        using var caller = gpu.Factory.CreateFence();
        submission.Begin();
        IGpuFence? slot = null;
        if (retain) Assert.True(submission.TryRetain(new Consumer(), out slot));
        submission.Submit(gpu, new IGpuFence[] { caller }.AsSpan());
        Assert.Equal(1, gpu.SubmitCalls);
        Assert.Equal(retain ? new[] { slot!, caller } : new[] { caller }, gpu.SubmittedFences);
    }

    sealed class Consumer : IGpuFrameCompletionConsumer
    {
        public bool HasPending(IGpuFence completion) => false;
        public void DiscardUnsubmitted(IGpuFence completion) { }
        public void ReleaseCompleted(IGpuFence completion) { }
    }
}
