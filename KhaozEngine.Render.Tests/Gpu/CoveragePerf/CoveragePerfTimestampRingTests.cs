using System;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("AllocSensitive")]
public sealed partial class CoveragePerfTimestampRingTests
{
    [Fact]
    public void PendingNeverMutatesPublishedSamplesOrResetsFence()
    {
        using var rig = new Rig();
        rig.Record(0);
        rig.Device.WaitForIdle();
        rig.Ring.PollCompleted();
        var published = Assert.IsType<CoveragePerfGpuResult>(rig.Results[0]);
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

        Assert.False(Assert.IsType<CoveragePerfGpuResult>(rig.Results[0]).Valid);
        Assert.Contains("Invalid or disjoint", rig.Ring.Failure, StringComparison.Ordinal);
        Assert.True(rig.Ring.HasPending(rig.Device.Fences[0]));
        rig.Submission.Begin(); rig.Commands.Begin();
        Assert.False(rig.Ring.TryBegin(rig.Commands, rig.Submission, 1, CoveragePerfTimerLayout.Total));
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
            began = rig.Ring.TryBegin(rig.Commands, rig.Submission, 6, CoveragePerfTimerLayout.Total));

        Assert.False(began);
        Assert.NotNull(rig.Ring.Failure);
        Assert.Equal(waits, rig.Device.Waits);
        Assert.Equal(submissions, rig.Device.Submissions);
        Assert.Equal(3, rig.Device.Pools.Count);
        Assert.All(rig.Device.Pools, p => { Assert.Equal(5u, p.Capacity); Assert.Equal(2, p.Begins); });
        Assert.All(rig.Results.Take(3), r => Assert.NotNull(r));
        Assert.All(rig.Results.Skip(3), r => Assert.Null(r));
    }

    [Fact]
    public void RecordingAndRetentionDoNotAllocate()
    {
        using var rig = new Rig(CoveragePerfTimerLayout.Phases);
        for (int i = 0; i < 3; i++) rig.Record(i, CoveragePerfTimerLayout.Phases);
        rig.Device.WaitForIdle(); rig.Ring.PollCompleted();
        bool began = false;
        AllocationMeasurement.NoPerCallAllocation("timestamp retain and recording", () =>
        {
            rig.Submission.Begin(); rig.Commands.Begin();
            began = rig.Ring.TryBegin(rig.Commands, rig.Submission, 3, CoveragePerfTimerLayout.Phases);
            if (began) { rig.Finish(CoveragePerfTimerLayout.Phases); rig.Submission.Submit(rig.Commands); }
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
        rig.Finish(CoveragePerfTimerLayout.Total);
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
        rig.Begin(0); rig.Finish(CoveragePerfTimerLayout.Total);
        rig.Device.FailSubmit = true;
        Assert.Throws<InvalidOperationException>(() => rig.Submission.Submit(rig.Commands));
        rig.Device.Fences[0].Signaled = true;
        rig.Ring.PollCompleted();
        rig.Submission.Begin(); rig.Commands.Begin();
        Assert.False(rig.Ring.TryBegin(rig.Commands, rig.Submission, 1, CoveragePerfTimerLayout.Total));
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
        if (ended) rig.Finish(CoveragePerfTimerLayout.Total);
        Assert.Throws<InvalidOperationException>(rig.Submission.Abandon);
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
        Assert.True(rig.Ring.TryBegin(rig.Commands, rig.Submission, 0, CoveragePerfTimerLayout.Total));
        rig.Finish(CoveragePerfTimerLayout.Total);
        Assert.Throws<InvalidOperationException>(() => rig.Submission.Submit(rig.Commands));
        rig.Device.WaitForIdle(); rig.Ring.PollCompleted();
        Assert.True(Assert.IsType<CoveragePerfGpuResult>(rig.Results[0]).Valid);
        Assert.Same(rig.Device.SubmittedFences[0], rig.Device.Pools[0].ResolvedFence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedOrSoftwareDeviceCannotProveGpu(bool software)
    {
        var run = new CoveragePerfFakeRun("G1");
        run.Setup = s => s.Info = s.Info with { TimestampSupported = software, SoftwareAdapter = software };
        var report = run.Run();
        Assert.Equal(CoveragePerfRunStatus.Refused, report.Status);
        Assert.Equal(0, Assert.Single(run.Sessions).Frames);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, report.GpuStatus);
    }

    [Fact]
    public void ConsumerCallbackErrorsDoNotReleaseOrResetTheTimestampFence()
    {
        using var rig = new Rig();
        var consumer = new ThrowingConsumer();
        rig.Begin(0);
        Assert.True(rig.Submission.TryRetain(consumer, out var fence));
        rig.Finish(CoveragePerfTimerLayout.Total); rig.Submission.Submit(rig.Commands);
        ((SubmissionTestFence)fence!).Signaled = true;
        Assert.Throws<InvalidOperationException>(rig.Submission.Begin);
        Assert.True(rig.Ring.HasPending(fence));
        Assert.Equal(0, ((SubmissionTestFence)fence).ResetCalls);
        consumer.Fail = false;
        rig.Ring.PollCompleted();
        rig.Submission.Begin(); rig.Submission.Abandon();
        Assert.Equal(1, ((SubmissionTestFence)fence).ResetCalls);
        Assert.True(rig.Results[0]!.Valid);
    }

    [Fact]
    public void FactoryWrapperForwardsTimestampCreation()
    {
        using var device = new CoveragePerfTimestampTestDevice();
        using var wrapper = new CoveragePerfDevice(device);
        int before = wrapper.Creations;
        using var pool = wrapper.Factory.CreateTimestampPool(5);
        Assert.Same(Assert.Single(device.Pools), pool);
        Assert.Equal(5u, pool.Capacity);
        Assert.Equal(before + 1, wrapper.Creations);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    public void ReadyWithoutUsablePeriodCannotPublishProof(double period)
    {
        using var rig = new Rig();
        rig.Record(0); rig.Device.Pools[0].Period = period;
        rig.Device.WaitForIdle(); rig.Ring.PollCompleted();
        Assert.False(Assert.IsType<CoveragePerfGpuResult>(rig.Results[0]).Valid);
        Assert.Null(rig.Ring.FirstReadyPeriod);
        Assert.NotNull(rig.Ring.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdenticalMarkerLayoutsBracketUnconditionalResolutionInBothConditions(bool phases)
    {
        var layout = phases ? CoveragePerfTimerLayout.Phases : CoveragePerfTimerLayout.Total;
        using var off = CoveragePerfSceneProbe.Open(1, false, completeOutcomes: true, layout: layout);
        using var on = CoveragePerfSceneProbe.Open(1, true, completeOutcomes: true, layout: layout);
        var offSample = off.Frame(0); var onSample = on.Frame(0);
        string[] expected = phases ? ["timestamp-0", "timestamp-1", "timestamp-2", "timestamp-3", "timestamp-4"]
            : ["timestamp-0", "timestamp-1"];
        Assert.Equal(expected, off.Events.Where(e => e.StartsWith("timestamp-", StringComparison.Ordinal)));
        Assert.Equal(expected, on.Events.Where(e => e.StartsWith("timestamp-", StringComparison.Ordinal)));
        foreach (var probe in new[] { off, on })
        {
            Assert.True(probe.Events.IndexOf("timestamp-0") < probe.Events.IndexOf("resolution-start"));
            if (phases)
            {
                Assert.Equal("timestamp-1", probe.Events[probe.Events.IndexOf("resolution-start") - 1]);
                Assert.Equal("timestamp-2", probe.Events[probe.Events.IndexOf("resolution-stop") - 1]);
                Assert.True(probe.Events.IndexOf("timestamp-3") > probe.Events.IndexOf("readbacks"));
            }
            int frameEnd = probe.Events.LastIndexOf("commands-end");
            Assert.True(frameEnd > probe.Events.IndexOf("readbacks"));
            Assert.Equal(expected[^1], probe.Events[frameEnd - 1]);
        }
        Assert.Equal(0, off.Session.Typical.Scene.DispatchedHostPreparationCount);
        Assert.Equal(1, on.Session.Typical.Scene.DispatchedHostPreparationCount);
        CoveragePerfFrameSample[] a = [offSample], b = [onSample];
        off.Session.CompleteBatch(a); on.Session.CompleteBatch(b);
        Assert.True(a[0].Gpu!.Valid); Assert.True(b[0].Gpu!.Valid);
        Assert.Equal(layout, a[0].Gpu!.Layout);
        Assert.Equal(phases ? 5 : 2, a[0].Gpu!.Ticks.Length);
        Assert.Equal<ulong>(a[0].Gpu!.Ticks, b[0].Gpu!.Ticks);
    }

    [Fact]
    public void ZeroRowHistoryParticipantSharesTheTimestampSubmission()
    {
        using var probe = CoveragePerfSceneProbe.Open(0, false, completeOutcomes: true, layout: CoveragePerfTimerLayout.Total);
        CoveragePerfFrameSample[] samples = [probe.Frame(0)];
        Assert.Equal(2, probe.ParticipantsAtReadback);
        probe.Session.CompleteBatch(samples);
        Assert.True(samples[0].Gpu!.Valid);
        Assert.True(samples[0].DeviceComplete);
        Assert.Null(samples[0].Error);
    }

    sealed class ThrowingConsumer : IGpuFrameCompletionConsumer
    {
        internal bool Fail = true;
        public bool HasPending(IGpuFence completion)
            => Fail ? throw new InvalidOperationException("consumer callback") : false;
        public void DiscardUnsubmitted(IGpuFence completion) { }
        public void ReleaseCompleted(IGpuFence completion) { }
    }

    sealed class Rig : IDisposable
    {
        internal readonly CoveragePerfTimestampTestDevice Device = new();
        internal readonly CoveragePerfTimestampTestCommands Commands = new();
        internal readonly GpuFrameSubmission Submission;
        internal readonly CoveragePerfTimestampRing Ring;
        internal readonly CoveragePerfGpuResult?[] Results = new CoveragePerfGpuResult?[12];
        internal Rig(CoveragePerfTimerLayout layout = CoveragePerfTimerLayout.Total)
        { Submission = new(Device); Ring = new(Device, Results.Length, layout, (index, result) => Results[index] = result); }
        internal void Begin(int index, CoveragePerfTimerLayout layout = CoveragePerfTimerLayout.Total)
        {
            Submission.Begin(); Commands.Begin();
            Assert.True(Ring.TryBegin(Commands, Submission, index, layout));
        }
        internal void Finish(CoveragePerfTimerLayout layout)
        {
            var pool = Ring.ActivePool!;
            if (layout == CoveragePerfTimerLayout.Phases)
                for (uint i = 1; i < 4; i++) Commands.WriteTimestamp(pool, i);
            Commands.FinishTimestampsOnEnd(pool, layout == CoveragePerfTimerLayout.Total ? 1u : 4u);
            Commands.End();
        }
        internal void Record(int index, CoveragePerfTimerLayout layout = CoveragePerfTimerLayout.Total)
        { Begin(index, layout); Finish(layout); Submission.Submit(Commands); }
        public void Dispose() { Submission.Dispose(); Ring.Dispose(); Commands.Dispose(); Device.Dispose(); }
    }
}
