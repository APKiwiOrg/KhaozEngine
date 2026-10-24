using System;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.Performance;

[Collection("AllocSensitive")]
public sealed partial class GpuPerfTimestampRingTests
{
    [Fact]
    public void PendingNeverMutatesPublishedSamplesOrResetsFence()
    {
        using var rig = new Rig();
        rig.Record(0);
        rig.Device.WaitForIdle();
        rig.Ring.PollCompleted();
        var published = Assert.IsType<GpuPerfTimestampResult>(rig.Results[0]);
        rig.Record(1);
        var pending = rig.Device.Pools[0];
        pending.Hold = true;
        rig.Device.WaitForIdle();
        rig.Ring.PollCompleted();
        rig.Submission.Begin(); rig.Submission.Abandon();

        Assert.Same(published, rig.Results[0]);
        Assert.Equal(new ulong[] { 0, 10 }, published.Ticks);
        Assert.Null(rig.Results[1]);
        Assert.Contains("Pending", rig.Ring.Failure, StringComparison.Ordinal);
        Assert.Equal(1, rig.Device.Fences[0].ResetCalls);
        Assert.True(rig.Ring.HasPending(rig.Device.Fences[0]));
        Assert.Equal(2, pending.Begins);
        Assert.Equal(2, pending.Resolves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidAndDisjointInvalidateUnit(bool disjoint)
    {
        using var rig = new Rig();
        rig.Record(0);
        rig.Device.Pools[0].Invalid = !disjoint;
        rig.Device.Pools[0].Disjoint = disjoint;
        rig.Device.WaitForIdle(); rig.Ring.PollCompleted();

        Assert.False(Assert.IsType<GpuPerfTimestampResult>(rig.Results[0]).Valid);
        Assert.Contains("Invalid or disjoint", rig.Ring.Failure, StringComparison.Ordinal);
        Assert.True(rig.Ring.HasPending(rig.Device.Fences[0]));
        rig.Submission.Begin(); rig.Commands.Begin();
        Assert.False(rig.Ring.TryBegin(rig.Commands, rig.Submission, 1, 2u, out _));
        Assert.Equal(1, rig.Device.Pools[0].Begins);
    }

    [Fact]
    public void FullRingDoesNotAllocateWaitOrDrop()
    {
        using var rig = new Rig();
        for (int i = 0; i < 3; i++) rig.Record(i);
        rig.Device.WaitForIdle(); rig.Ring.PollCompleted();
        for (int i = 3; i < 6; i++) rig.Record(i);
        rig.Submission.Begin(); rig.Commands.Begin();
        bool began = true;
        int waits = rig.Device.Waits, submissions = rig.Device.Submissions;
        AllocationMeasurement.NoPerCallAllocation("full timestamp ring refusal", () =>
            began = rig.Ring.TryBegin(rig.Commands, rig.Submission, 6, 2u, out _));

        Assert.False(began);
        Assert.NotNull(rig.Ring.Failure);
        Assert.Equal(waits, rig.Device.Waits);
        Assert.Equal(submissions, rig.Device.Submissions);
        Assert.Equal(3, rig.Device.Pools.Count);
        Assert.All(rig.Device.Pools, p => { Assert.Equal(2u, p.Capacity); Assert.Equal(2, p.Begins); });
        Assert.All(rig.Results.Take(3), r => Assert.NotNull(r));
        Assert.All(rig.Results.Skip(3), r => Assert.Null(r));
    }

    [Fact]
    public void RecordingAndRetentionDoNotAllocate()
    {
        using var rig = new Rig(5u);
        for (int i = 0; i < 3; i++) rig.Record(i, 5u);
        rig.Device.WaitForIdle(); rig.Ring.PollCompleted();
        bool began = false;
        AllocationMeasurement.NoPerCallAllocation("timestamp retain and recording", () =>
        {
            rig.Submission.Begin(); rig.Commands.Begin();
            began = rig.Ring.TryBegin(rig.Commands, rig.Submission, 3, 5u, out _);
            if (began) { rig.Finish(5u); rig.Submission.Submit(rig.Commands); }
        });
        Assert.True(began);
        Assert.Equal(1, rig.Device.Waits);
        Assert.Equal(4, rig.Device.Submissions);
    }

    [Fact]
    public void ResolveUsesExactSubmittedFenceAndConsumersResetIndependently()
    {
        using var rig = new Rig();
        using var history = new GpuBufferReadbackRing(rig.Device, 3, 16);
        using var source = new SubmissionTestBuffer(16);
        using var caller = rig.Device.Factory.CreateFence();
        rig.Begin(0);
        Assert.True(rig.Submission.TryRetain(history, out var shared));
        Assert.True(history.TryRecord(rig.Commands, source, 0, 16, 100, shared!));
        rig.Finish(2u);
        rig.Submission.Submit(rig.Commands, [caller]);
        Assert.Equal(2, rig.Device.CompletionCount);
        Assert.Same(shared, rig.Device.SubmittedFences[0]);
        Assert.Same(caller, rig.Device.SubmittedFences[1]);
        rig.Device.WaitForIdle(); rig.Ring.PollCompleted();
        Assert.Same(shared, rig.Device.Pools[0].ResolvedFence);
        rig.Submission.Begin(); rig.Submission.Abandon();
        Assert.Equal(0, ((SubmissionTestFence)shared!).ResetCalls);
        Assert.True(history.TryRead(new byte[16], out var tag, out _));
        Assert.Equal(100ul, tag);
        rig.Submission.Begin(); rig.Submission.Abandon();
        Assert.Equal(1, ((SubmissionTestFence)shared).ResetCalls);
        Assert.Equal(0, ((SubmissionTestFence)caller).ResetCalls);
        Assert.True(caller.Signaled);
        Assert.Equal(1, rig.Device.Pools[0].Resolves);
        Assert.Null(rig.Ring.Failure);
    }

    [Fact]
    public void OutOfOrderCompletionKeepsEachSampleAndPollsEachSlotExactlyOnce()
    {
        using var rig = new Rig();
        rig.Record(0); rig.Record(1); rig.Record(2);
        rig.Device.Fences[2].Signaled = rig.Device.Fences[1].Signaled = true;
        rig.Ring.PollCompleted();
        Assert.Null(rig.Results[0]);
        Assert.NotNull(rig.Results[1]); Assert.NotNull(rig.Results[2]);
        Assert.All(rig.Device.Pools, p => Assert.Equal(1, p.Resolves));
        Assert.All(rig.Device.Fences, f => Assert.Equal(0, f.ResetCalls));
        Assert.Equal(0, rig.Device.Waits);
        Assert.Equal(3, rig.Device.Submissions);
        Assert.NotNull(rig.Ring.Failure);
    }

    [Fact]
    public void SubmitFailureQuarantinesUntilTeardown()
    {
        using var rig = new Rig();
        rig.Begin(0); rig.Finish(2u);
        rig.Device.FailSubmit = true;
        var submitted = Assert.Throws<InvalidOperationException>(() => rig.Submission.Submit(rig.Commands));
        Assert.Equal("submit failure", submitted.Message);
        rig.Device.Fences[0].Signaled = true;
        rig.Ring.PollCompleted();
        rig.Submission.Begin(); rig.Commands.Begin();
        Assert.False(rig.Ring.TryBegin(rig.Commands, rig.Submission, 1, 2u, out _));
        Assert.True(rig.Ring.HasPending(rig.Device.Fences[0]));
        Assert.Equal(0, rig.Device.Fences[0].ResetCalls);
        Assert.Equal(0, rig.Device.Pools[0].Resolves);
        Assert.Equal(0, rig.Device.Pools[0].Disposals);
        rig.Submission.Dispose(); rig.Ring.Dispose();
        Assert.Equal(1, rig.Device.Waits);
        Assert.All(rig.Device.Pools, p => Assert.Equal(1, p.Disposals));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbandonmentAndCallbackErrorsKeepQuarantineUntilTeardown(bool ended)
    {
        using var rig = new Rig();
        rig.Begin(0);
        var participant = new GpuFrameSubmissionTests.Participant { ThrowAbandoned = true };
        Assert.True(rig.Submission.TryRegister(participant));
        if (ended) rig.Finish(2u);
        var abandoned = Assert.Single(Assert.Throws<GpuDiagnosticFailureException>(rig.Submission.Abandon).Failures);
        Assert.Equal("renderer.frame.submission.participant.1", abandoned.Stage);
        Assert.Equal("abandoned callback", abandoned.Error.Message);
        rig.Ring.PollCompleted();
        Assert.True(rig.Ring.HasPending(rig.Device.Fences[0]));
        Assert.Equal(0, rig.Device.Pools[0].Resolves);
        Assert.Equal(0, rig.Device.Pools[0].Disposals);
        rig.Submission.Dispose(); rig.Ring.Dispose();
        Assert.False(rig.Ring.HasPending(rig.Device.Fences[0]));
        Assert.Equal(1, rig.Device.Pools[0].Disposals);
    }

    [Fact]
    public void ThrowingSubmittedConsumerDoesNotHideTheRingsSubmissionDecision()
    {
        using var rig = new Rig();
        rig.Submission.Begin(); rig.Commands.Begin();
        Assert.True(rig.Submission.TryRegister(new GpuFrameSubmissionTests.Participant { ThrowSubmitted = true }));
        Assert.True(rig.Ring.TryBegin(rig.Commands, rig.Submission, 0, 2u, out _));
        rig.Finish(2u);
        var submitted = Assert.Single(Assert.Throws<GpuDiagnosticFailureException>(() => rig.Submission.Submit(rig.Commands)).Failures);
        Assert.Equal("renderer.frame.submission.participant.0", submitted.Stage);
        Assert.Equal("submitted callback", submitted.Error.Message);
        rig.Device.WaitForIdle(); rig.Ring.PollCompleted();
        Assert.True(Assert.IsType<GpuPerfTimestampResult>(rig.Results[0]).Valid);
        Assert.Same(rig.Device.SubmittedFences[0], rig.Device.Pools[0].ResolvedFence);
    }

    [Fact]
    public void ConsumerCallbackErrorsDoNotReleaseOrResetTheTimestampFence()
    {
        using var rig = new Rig();
        var consumer = new ThrowingConsumer();
        rig.Begin(0);
        Assert.True(rig.Submission.TryRetain(consumer, out var fence));
        rig.Finish(2u); rig.Submission.Submit(rig.Commands);
        ((SubmissionTestFence)fence!).Signaled = true;
        var query = Assert.Single(Assert.Throws<GpuDiagnosticFailureException>(rig.Submission.Begin).Failures);
        Assert.Equal("renderer.frame.submission.pending.0.1", query.Stage);
        Assert.Same(consumer.Error, query.Error);
        Assert.True(rig.Ring.HasPending(fence));
        Assert.Equal(0, ((SubmissionTestFence)fence).ResetCalls);
        consumer.Fail = false;
        rig.Ring.PollCompleted();
        rig.Submission.Begin(); rig.Submission.Abandon();
        Assert.Equal(1, ((SubmissionTestFence)fence).ResetCalls);
        Assert.True(rig.Results[0]!.Valid);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    public void ReadyWithoutUsablePeriodCannotPublishProof(double period)
    {
        using var rig = new Rig();
        rig.Record(0); rig.Device.Pools[0].Period = period;
        rig.Device.WaitForIdle(); rig.Ring.PollCompleted();
        Assert.False(Assert.IsType<GpuPerfTimestampResult>(rig.Results[0]).Valid);
        Assert.Null(rig.Ring.FirstReadyPeriod);
        Assert.NotNull(rig.Ring.Failure);
    }

    sealed class ThrowingConsumer : IGpuFrameCompletionConsumer
    {
        internal bool Fail = true;
        internal readonly Exception Error = new InvalidOperationException("consumer callback");
        public bool HasPending(IGpuFence completion)
            => Fail ? throw Error : false;
        public void DiscardUnsubmitted(IGpuFence completion) { }
        public void ReleaseCompleted(IGpuFence completion) { }
    }

}
