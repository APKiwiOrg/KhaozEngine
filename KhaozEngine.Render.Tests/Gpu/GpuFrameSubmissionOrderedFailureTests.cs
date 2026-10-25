using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Tests;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("AllocSensitive")]
public sealed class GpuFrameSubmissionOrderedFailureTests
{
    [Fact]
    public void Begin_preserves_queries_across_retained_slots_and_repeated_originals()
    {
        var gpu = new SubmissionTestDevice(2);
        using var submission = new GpuFrameSubmission(gpu);
        var same = new InvalidOperationException("same query independently thrown");
        var tail = new InvalidOperationException("known later event");
        var first = new QueryConsumer(same); var second = new QueryConsumer(new GpuDiagnosticFailureException([new("native.query.origin", same), new("native.query.tail", tail)]));
        submission.Begin(); Assert.True(submission.TryRetain(first, out var a)); submission.Submit(gpu);
        submission.Begin(); Assert.True(submission.TryRetain(second, out var b)); submission.Submit(gpu);
        ((SubmissionTestFence)a!).Signaled = ((SubmissionTestFence)b!).Signaled = true;
        var actual = Assert.Throws<GpuDiagnosticFailureException>(submission.Begin).Failures;
        Assert.Equal(new[] { "renderer.frame.submission.pending.0.0", "native.query.origin", "native.query.tail" }, actual.Select(f => f.Stage));
        Assert.Same(same, actual[0].Error); Assert.Same(same, actual[1].Error); Assert.Same(tail, actual[2].Error);
        Assert.Equal(1, first.Queries); Assert.Equal(1, second.Queries);
        Assert.All(gpu.Fences, fence => Assert.Equal(0, fence.ResetCalls));
        first.Error = second.Error = null;
    }

    [Fact]
    public void Warmed_successful_begin_abandon_notify_and_pending_poll_allocate_nothing()
    {
        var gpu = new SubmissionTestDevice(2);
        using var submission = new GpuFrameSubmission(gpu);
        var consumer = new QueryConsumer(null); var participant = new QuietParticipant();
        void Cycle()
        {
            submission.Begin(); submission.TryRegister(participant); submission.TryRetain(consumer, out _); submission.Submit(gpu);
            foreach (var fence in gpu.Fences) fence.Signaled = true;
            submission.Begin(); submission.TryRegister(participant); submission.TryRetain(consumer, out _); submission.Abandon();
        }
        Cycle(); Cycle();
        long bytes;
        using (AllocationRegion.Start("submission successful owner paths"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Cycle();
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, bytes);
    }

    sealed class QueryConsumer(Exception? error) : IGpuFrameCompletionConsumer
    {
        internal Exception? Error = error;
        internal int Queries;
        public bool HasPending(IGpuFence completion) { Queries++; if (Error is { } e) throw e; return false; }
        public void DiscardUnsubmitted(IGpuFence completion) { }
        public void ReleaseCompleted(IGpuFence completion) { }
    }
    sealed class QuietParticipant : IGpuFrameSubmissionParticipant
    { public void Submitted() { } public void Abandoned() { } }
}
