using System;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class GpuFrameSubmissionTests
{
    [Fact]
    public void Failed_teardown_drain_preserves_staging_and_fences_until_a_successful_drain()
    {
        var gpu = new SubmissionTestDevice(1);
        using var submission = new GpuFrameSubmission(gpu);
        using var ring = new GpuBufferReadbackRing(gpu, 1, 16);
        using var source = new SubmissionTestBuffer(16);
        submission.Begin();
        Assert.True(submission.TryRetain(ring, out var fence));
        Assert.True(ring.TryRecord(gpu, source, 0, 16, 1, fence!));
        submission.Submit(gpu);
        gpu.FailWait = true;
        Assert.IsType<InvalidOperationException>(Assert.Single(Assert.Throws<GpuDiagnosticFailureException>(() => submission.Dispose()).Failures).Error);
        Assert.True(ring.HasPending(fence!));
        Assert.Equal(0, gpu.Buffers[0].DisposeCalls);
        Assert.All(gpu.Fences, f => Assert.Equal(0, f.DisposeCalls));
        gpu.FailWait = false;
        submission.Dispose();
        Assert.False(ring.HasPending(fence!));
        int waits = gpu.WaitCalls;
        submission.Dispose();
        Assert.Equal(waits, gpu.WaitCalls);
        Assert.All(gpu.Fences, f => Assert.Equal(1, f.DisposeCalls));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Window_callback_exception_abandons_and_recovers_on_the_next_frame(bool prepareFailure)
    {
        using var device = new OpenListTrackingGpuDevice();
        using var commands = device.Factory.CreateCommandList();
        var frame = new Frame { Commands = commands };
        var participant = new Participant();
        Assert.Throws<InvalidOperationException>(() => FramePhases.Run(frame, true, device, commands, Color.Black,
            f =>
            {
                Assert.True(f.Submission.TryRegister(participant));
                if (prepareFailure) throw new InvalidOperationException("prepare callback");
            },
            _ => throw new InvalidOperationException("record callback")));
        Assert.Equal(0, participant.Submissions);
        Assert.Equal(1, participant.Abandons);
        Assert.Equal(0, device.OpenLists);
        Assert.Equal(0, device.Submits);
        FramePhases.Run(frame, true, device, commands, Color.Black,
            f => Assert.True(f.Submission.TryRegister(participant)), _ => { });
        Assert.Equal(1, participant.Submissions);
        Assert.Equal(1, device.Submits);
        frame.DisposeSubmission();
    }

    [Fact]
    public void Disposing_a_completed_consumer_releases_multiple_copies_of_one_frame()
    {
        var gpu = new SubmissionTestDevice(3);
        using var submission = new GpuFrameSubmission(gpu);
        var ring = new GpuBufferReadbackRing(gpu, 3, 16);
        using var source = new SubmissionTestBuffer(16);
        submission.Begin();
        Assert.True(submission.TryRetain(ring, out var fence));
        Assert.True(ring.TryRecord(gpu, source, 0, 16, 1, fence!));
        Assert.True(ring.TryRecord(gpu, source, 0, 16, 2, fence!));
        Assert.True(ring.TryRecord(gpu, source, 0, 16, 3, fence!));
        submission.Submit(gpu);
        ((SubmissionTestFence)fence!).Signaled = true;
        ring.Dispose();
        Assert.False(ring.HasPending(fence));
        Assert.Equal(0, ring.PendingCount);
        Assert.All(gpu.Buffers, b => Assert.Equal(1, b.DisposeCalls));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Callback_failure_notifies_all_and_does_not_abandon_submitted_copies(bool successfulSubmit)
    {
        var gpu = new SubmissionTestDevice(1);
        using var submission = new GpuFrameSubmission(gpu);
        using var ring = new GpuBufferReadbackRing(gpu, 1, 16);
        using var source = new SubmissionTestBuffer(16);
        var first = new GpuFrameSubmissionTests.Participant { ThrowSubmitted = true, ThrowAbandoned = true };
        var second = new GpuFrameSubmissionTests.Participant();
        submission.Begin();
        Assert.True(submission.TryRegister(first)); Assert.True(submission.TryRegister(second));
        Assert.True(submission.TryRetain(ring, out var completion));
        Assert.True(ring.TryRecord(gpu, source, 0, 16, 1, completion!));
        gpu.FailSubmit = !successfulSubmit;
        if (successfulSubmit)
        {
            var notification = Assert.Single(Assert.Throws<GpuDiagnosticFailureException>(() => submission.Submit(gpu)).Failures);
            Assert.Equal("renderer.frame.submission.participant.0", notification.Stage);
            Assert.Equal("submitted callback", notification.Error.Message);
        }
        else
        {
            var failure = Assert.Throws<GpuDiagnosticFailureException>(() => submission.Submit(gpu));
            Assert.Equal(2, failure.Failures.Length);
            Assert.Equal("planned submit failure", failure.Failures[0].Error.Message);
            Assert.Equal("abandoned callback", failure.Failures[1].Error.Message);
        }
        submission.Abandon();
        Assert.Equal(1, ring.PendingCount);
        Assert.Equal(successfulSubmit ? 1 : 0, second.Submissions);
        Assert.Equal(successfulSubmit ? 0 : 1, second.Abandons);
        submission.Begin(); submission.Abandon();
        Assert.Equal(0, ((SubmissionTestFence)completion!).ResetCalls);
        Assert.Equal(1, ring.PendingCount);
        submission.Dispose();
        Assert.Equal(1, gpu.WaitCalls);
        Assert.Equal(0, ring.PendingCount);
    }

    [Fact]
    public void Abandon_callback_failure_still_releases_unsubmitted_copies()
    {
        var gpu = new SubmissionTestDevice(1);
        using var submission = new GpuFrameSubmission(gpu);
        using var ring = new GpuBufferReadbackRing(gpu, 1, 16);
        using var source = new SubmissionTestBuffer(16);
        var participant = new GpuFrameSubmissionTests.Participant { ThrowAbandoned = true };
        submission.Begin();
        Assert.True(submission.TryRegister(participant));
        Assert.True(submission.TryRetain(ring, out var fence));
        Assert.True(ring.TryRecord(gpu, source, 0, 16, 1, fence!));
        var abandoned = Assert.Single(Assert.Throws<GpuDiagnosticFailureException>(() => submission.Abandon()).Failures);
        Assert.Equal("renderer.frame.submission.participant.0", abandoned.Stage);
        Assert.Equal("abandoned callback", abandoned.Error.Message);
        Assert.Equal(0, ring.PendingCount);
        Assert.Equal(0, gpu.SubmitCalls);
        Assert.Equal(1, participant.Abandons);
        submission.Begin(); submission.Submit(gpu);
    }

    [Fact]
    public void Consumer_and_participant_capacity_are_independent()
    {
        var gpu = new SubmissionTestDevice(2);
        using var submission = new GpuFrameSubmission(gpu, consumerCapacity: 1);
        using var first = new GpuBufferReadbackRing(gpu, 1, 16);
        using var second = new GpuBufferReadbackRing(gpu, 1, 16);
        var accepted = new GpuFrameSubmissionTests.Participant();
        var refused = new GpuFrameSubmissionTests.Participant();
        submission.Begin();
        Assert.True(submission.TryRetain(first, out _));
        Assert.False(submission.TryRetain(second, out _));
        Assert.True(submission.TryRegister(accepted));
        Assert.True(submission.TryRegister(accepted));
        Assert.False(submission.TryRegister(refused));
        refused.Abandoned();
        submission.Submit(gpu);
        Assert.Equal(1, accepted.Submissions);
        Assert.Equal(0, refused.Submissions);
        Assert.Equal(1, refused.Abandons);
    }

    [Fact]
    public void Completed_consumer_disposal_releases_fence_ownership()
    {
        var gpu = new SubmissionTestDevice(1);
        using var submission = new GpuFrameSubmission(gpu);
        var ring = new GpuBufferReadbackRing(gpu, 1, 16);
        using var source = new SubmissionTestBuffer(16);
        submission.Begin();
        Assert.True(submission.TryRetain(ring, out var fence));
        Assert.True(ring.TryRecord(gpu, source, 0, 16, 1, fence!));
        submission.Submit(gpu);
        Assert.Throws<InvalidOperationException>(() => ring.Dispose());
        Assert.True(ring.HasPending(fence!));
        Assert.Equal(0, gpu.Buffers[0].DisposeCalls);
        ((SubmissionTestFence)fence!).Signaled = true;
        ring.Dispose();
        Assert.False(ring.HasPending(fence));
        submission.Begin(); submission.Abandon();
        Assert.Equal(1, ((SubmissionTestFence)fence).ResetCalls);
        Assert.Equal(1, gpu.Buffers[0].DisposeCalls);
    }
}
