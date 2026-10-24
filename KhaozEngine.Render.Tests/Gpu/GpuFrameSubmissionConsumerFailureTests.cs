using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class GpuFrameSubmissionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Failed_discard_and_query_quarantine_the_old_fence_before_recovery(int retainedFrames)
    {
        var gpu = new SubmissionTestDevice(3);
        using var submission = new GpuFrameSubmission(gpu, retainedFrames);
        using var old = new GpuBufferReadbackRing(gpu, 1, 16);
        using var valid = new GpuBufferReadbackRing(gpu, 1, 16);
        using var next = new GpuBufferReadbackRing(gpu, 1, 16);
        using var source = new SubmissionTestBuffer(16);
        source.Bytes[0] = 0x6a;
        var faulty = new FaultyConsumer(old) { FailDiscard = true, FailQuery = true };
        try
        {
            submission.Begin();
            Assert.True(submission.TryRetain(faulty, out var oldFence));
            Assert.True(submission.TryRetain(valid, out var shared));
            Assert.Same(oldFence, shared);
            Assert.True(old.TryRecord(gpu, source, 0, 16, 100, oldFence!));
            Assert.True(valid.TryRecord(gpu, source, 0, 16, 100, shared!));
            var failure = Assert.Throws<GpuDiagnosticFailureException>(() => submission.Abandon());
            Assert.Equal(2, failure.Failures.Length);
            Assert.Same(faulty.DiscardError, failure.Failures[0].Error);
            Assert.Same(faulty.QueryError, failure.Failures[1].Error);
            Assert.Equal("renderer.frame.submission.discard.0.0", failure.Failures[0].Stage);
            Assert.Equal("renderer.frame.submission.pending.0.0", failure.Failures[1].Stage);
            Assert.Equal(0, valid.PendingCount);
            Assert.Equal(1, old.PendingCount);
            faulty.FailQuery = false;

            submission.Begin();
            bool retained = submission.TryRetain(next, out var nextFence);
            Assert.Equal(retainedFrames > 1, retained);
            if (retained)
            {
                Assert.NotSame(oldFence, nextFence);
                Assert.True(next.TryRecord(gpu, source, 0, 16, 101, nextFence!));
            }
            submission.Submit(gpu);
            if (nextFence is not null) ((SubmissionTestFence)nextFence).Signaled = true;
            var payload = new byte[16];
            Assert.False(old.TryRead(payload, out _, out _));
            Assert.Equal(0, payload[0]);
            Assert.Equal(0, ((SubmissionTestFence)oldFence!).ResetCalls);
            Assert.Equal(0, gpu.WaitCalls); Assert.Equal(0, gpu.FlushCalls);
            Assert.Equal(1, gpu.SubmitCalls);
            Assert.Equal("discard failure", failure.Failures[0].Error.Message);
            if (retained)
            {
                Assert.True(next.TryRead(payload, out ulong tag, out _));
                Assert.Equal(101ul, tag); Assert.Equal(0x6a, payload[0]);
            }
            submission.Dispose();
            Assert.Equal(0, old.PendingCount);
            Assert.Equal(1, faulty.DiscardCalls);
            Assert.Equal(1, faulty.ReleaseCalls);
            Assert.Equal(1, gpu.WaitCalls);
            Assert.Equal(1, ((SubmissionTestFence)oldFence).DisposeCalls);
        }
        finally
        {
            faulty.FailDiscard = faulty.FailQuery = faulty.FailRelease = false;
            submission.Dispose();
        }
    }

    [Fact]
    public void Completion_query_failure_keeps_ownership_and_still_queries_other_consumers()
    {
        var gpu = new SubmissionTestDevice(2);
        using var submission = new GpuFrameSubmission(gpu);
        using var old = new GpuBufferReadbackRing(gpu, 1, 16);
        using var valid = new GpuBufferReadbackRing(gpu, 1, 16);
        using var source = new SubmissionTestBuffer(16);
        var faulty = new FaultyConsumer(old) { FailQuery = true };
        var other = new FaultyConsumer(valid);
        try
        {
            submission.Begin();
            Assert.True(submission.TryRetain(faulty, out var fence));
            Assert.True(submission.TryRetain(other, out _));
            Assert.True(old.TryRecord(gpu, source, 0, 16, 100, fence!));
            submission.Submit(gpu);
            ((SubmissionTestFence)fence!).Signaled = true;
            var query = Assert.Single(Assert.Throws<GpuDiagnosticFailureException>(() => submission.Begin()).Failures);
            Assert.Same(faulty.QueryError, query.Error);
            Assert.Equal("renderer.frame.submission.pending.0.0", query.Stage);
            Assert.Equal(1, other.QueryCalls);
            Assert.Equal(0, ((SubmissionTestFence)fence).ResetCalls);
            faulty.FailQuery = false;
            submission.Begin();
            Assert.True(submission.TryRetain(other, out var next));
            Assert.NotSame(fence, next);
            submission.Abandon();
            Assert.Equal(0, gpu.WaitCalls); Assert.Equal(0, gpu.FlushCalls);
            submission.Dispose();
        }
        finally
        {
            faulty.FailDiscard = faulty.FailQuery = faulty.FailRelease = false;
            submission.Dispose();
        }
    }

    [Fact]
    public void Teardown_cleanup_failures_preserve_fences_and_release_other_consumers()
    {
        var gpu = new SubmissionTestDevice(2);
        using var submission = new GpuFrameSubmission(gpu);
        using var old = new GpuBufferReadbackRing(gpu, 1, 16);
        using var valid = new GpuBufferReadbackRing(gpu, 1, 16);
        using var source = new SubmissionTestBuffer(16);
        var faulty = new FaultyConsumer(old) { FailRelease = true, FailQuery = true };
        var other = new FaultyConsumer(valid);
        try
        {
            submission.Begin();
            Assert.True(submission.TryRetain(faulty, out var fence));
            Assert.True(submission.TryRetain(other, out _));
            Assert.True(old.TryRecord(gpu, source, 0, 16, 100, fence!));
            Assert.True(valid.TryRecord(gpu, source, 0, 16, 100, fence!));
            submission.Submit(gpu);
            var failure = Assert.Throws<GpuDiagnosticFailureException>(() => submission.Dispose());
            Assert.Equal(2, failure.Failures.Length);
            Assert.Equal("release failure", failure.Failures[0].Error.Message);
            Assert.Equal("query failure", failure.Failures[1].Error.Message);
            Assert.Equal(0, valid.PendingCount);
            Assert.Equal(1, other.ReleaseCalls);
            Assert.Equal(1, old.PendingCount);
            Assert.All(gpu.Fences, item => Assert.Equal(0, item.DisposeCalls));
            Assert.Equal("renderer.session.dispose.submission.consumer.0.0", failure.Failures[0].Stage);
            Assert.Throws<ObjectDisposedException>(() => submission.Begin());
            faulty.FailQuery = faulty.FailRelease = false;
            submission.Dispose();
            Assert.Equal(0, old.PendingCount);
            Assert.All(gpu.Fences, item => Assert.Equal(1, item.DisposeCalls));
        }
        finally
        {
            faulty.FailDiscard = faulty.FailQuery = faulty.FailRelease = false;
            submission.Dispose();
        }
    }

    [Fact]
    public void A_later_submission_never_makes_the_abandoned_payload_readable()
    {
        var gpu = new SubmissionTestDevice(2);
        using var submission = new GpuFrameSubmission(gpu);
        using var old = new GpuBufferReadbackRing(gpu, 1, 16);
        using var next = new GpuBufferReadbackRing(gpu, 1, 16);
        using var source = new SubmissionTestBuffer(16);
        var faulty = new FaultyConsumer(old) { FailDiscard = true, FailQuery = true };
        try
        {
            submission.Begin();
            Assert.True(submission.TryRetain(faulty, out var oldFence));
            source.Bytes[0] = 0x41;
            Assert.True(old.TryRecord(gpu, source, 0, 16, 100, oldFence!));
            var abandoned = Assert.Throws<GpuDiagnosticFailureException>(() => submission.Abandon());
            Assert.Equal(2, abandoned.Failures.Length);
            Assert.Same(faulty.DiscardError, abandoned.Failures[0].Error);
            Assert.Same(faulty.QueryError, abandoned.Failures[1].Error);
            faulty.FailQuery = false;
            submission.Begin();
            Assert.True(submission.TryRetain(next, out var nextFence));
            source.Bytes[0] = 0x43;
            Assert.True(next.TryRecord(gpu, source, 0, 16, 101, nextFence!));
            submission.Submit(gpu);
            ((SubmissionTestFence)nextFence!).Signaled = true;
            var payload = new byte[16];
            Assert.False(old.TryRead(payload, out ulong oldTag, out _));
            Assert.Equal(0ul, oldTag); Assert.Equal(0, payload[0]);
            Assert.True(next.TryRead(payload, out ulong nextTag, out _));
            Assert.Equal(101ul, nextTag); Assert.Equal(0x43, payload[0]);
            Assert.NotSame(oldFence, nextFence);
            Assert.Equal(0, ((SubmissionTestFence)oldFence!).ResetCalls);
            Assert.Equal(0, gpu.WaitCalls); Assert.Equal(0, gpu.FlushCalls);
        }
        finally
        {
            faulty.FailDiscard = faulty.FailQuery = false;
            submission.Dispose();
        }
    }

    sealed class FaultyConsumer(GpuBufferReadbackRing ring) : IGpuFrameCompletionConsumer
    {
        internal bool FailDiscard, FailQuery, FailRelease;
        internal int DiscardCalls, QueryCalls, ReleaseCalls;
        internal readonly Exception DiscardError = new InvalidOperationException("discard failure");
        internal readonly Exception QueryError = new InvalidOperationException("query failure");
        public bool HasPending(IGpuFence completion)
        {
            QueryCalls++;
            if (FailQuery) throw QueryError;
            return ring.HasPending(completion);
        }
        public void DiscardUnsubmitted(IGpuFence completion)
        {
            DiscardCalls++;
            if (FailDiscard) throw DiscardError;
            ring.DiscardUnsubmitted(completion);
        }
        public void ReleaseCompleted(IGpuFence completion)
        {
            ReleaseCalls++;
            if (FailRelease) throw new InvalidOperationException("release failure");
            ring.ReleaseCompleted(completion);
        }
    }
}
