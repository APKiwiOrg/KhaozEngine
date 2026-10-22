using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class GpuFrameSubmissionTests
{
    [Fact]
    public void Held_completion_keeps_frame_actor_and_fence_identity()
    {
        var gpu = new SubmissionTestDevice(6);
        using var submission = new GpuFrameSubmission(gpu);
        using var first = new GpuBufferReadbackRing(gpu, 3, 16);
        using var second = new GpuBufferReadbackRing(gpu, 3, 16);
        using var source = new SubmissionTestBuffer(16);
        submission.Begin();
        Assert.True(submission.TryRetain(first, out var fence));
        Assert.True(submission.TryRetain(second, out var shared));
        Assert.Same(fence, shared);
        Assert.True(first.TryRecord(gpu, source, 0, 16, 100, fence!));
        Assert.True(second.TryRecord(gpu, source, 0, 16, 100, shared!));
        submission.Submit(gpu);
        Assert.Equal(1, gpu.SubmitCalls);
        Assert.Same(fence, gpu.SubmittedFence);
        Assert.False(first.TryRead(new byte[16], out _, out _));
        submission.Begin(); submission.Abandon();
        Assert.Equal(0, ((SubmissionTestFence)fence!).ResetCalls);
        ((SubmissionTestFence)fence).Signaled = true;
        Assert.True(first.TryRead(new byte[16], out var tag, out _));
        Assert.Equal(100ul, tag);
        submission.Begin(); submission.Abandon();
        Assert.Equal(0, ((SubmissionTestFence)fence).ResetCalls);
        Assert.True(second.TryRead(new byte[16], out _, out _));
        submission.Begin(); submission.Abandon();
        Assert.Equal(1, ((SubmissionTestFence)fence).ResetCalls);
        Assert.Equal(0, gpu.WaitCalls);
        Assert.Equal(0, gpu.FlushCalls);
    }

    [Fact]
    public void Full_ring_drops_only_diagnostics()
    {
        var gpu = new SubmissionTestDevice(3);
        using var submission = new GpuFrameSubmission(gpu);
        using var ring = new GpuBufferReadbackRing(gpu, 3, 16);
        using var source = new SubmissionTestBuffer(16);
        var participant = new Participant();
        for (int i = 0; i < 3; i++)
        {
            submission.Begin();
            Assert.True(submission.TryRegister(participant));
            Assert.True(submission.TryRetain(ring, out var completion));
            Assert.True(ring.TryRecord(gpu, source, 0, 16, (ulong)i, completion!));
            submission.Submit(gpu);
        }
        submission.Begin();
        Assert.True(submission.TryRegister(participant));
        Assert.False(submission.TryRetain(ring, out _));
        submission.Submit(gpu);
        Assert.Equal(4, participant.Submissions);
        Assert.Equal(4, gpu.SubmitCalls);
        Assert.Equal(0, participant.Abandons);
        Assert.Equal(0, gpu.WaitCalls);
        submission.Dispose();
        Assert.Equal(0, ring.PendingCount);
        Assert.Equal(1, gpu.WaitCalls);
    }

    [Fact]
    public void Abandon_discards_only_the_unsubmitted_recording()
    {
        var gpu = new SubmissionTestDevice(3);
        using var submission = new GpuFrameSubmission(gpu);
        using var ring = new GpuBufferReadbackRing(gpu, 3, 16);
        using var source = new SubmissionTestBuffer(16);
        var participant = new Participant();
        submission.Begin();
        Assert.True(submission.TryRetain(ring, out var first));
        Assert.True(ring.TryRecord(gpu, source, 0, 16, 100, first!));
        submission.Submit(gpu);
        submission.Begin();
        Assert.True(submission.TryRegister(participant));
        Assert.True(submission.TryRetain(ring, out var second));
        Assert.True(ring.TryRecord(gpu, source, 0, 16, 101, second!));
        submission.Abandon(); submission.Abandon();
        Assert.Equal(1, participant.Abandons);
        Assert.Equal(1, ring.PendingCount);
        Assert.True(ring.HasPending(first!));
        Assert.False(ring.HasPending(second!));
        submission.Dispose();
    }

    internal sealed class Participant : IGpuFrameSubmissionParticipant
    {
        internal int Submissions, Abandons;
        internal bool ThrowSubmitted, ThrowAbandoned;
        public void Submitted() { Submissions++; if (ThrowSubmitted) throw new InvalidOperationException("submitted callback"); }
        public void Abandoned() { Abandons++; if (ThrowAbandoned) throw new InvalidOperationException("abandoned callback"); }
    }
}
