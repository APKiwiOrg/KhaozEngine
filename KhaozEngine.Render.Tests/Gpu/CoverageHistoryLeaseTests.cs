using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageHistoryLeaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Older_signaled_retirement_cannot_free_the_source_before_the_copy_lease(bool abandonedRetry)
    {
        var device = new SubmissionTestDevice(32);
        var barrier = new HeldBarrier();
        int drains = 0;
        using var retired = new GpuRetireQueue(() => drains++, barrier);
        using var original = Batch(device);
        using var replacement = Batch(device);
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var upload = CoverageGpuUpload.Host(topology, 2);
        var actor = new KhaozEngine.Render3D.CoverageGpuActor(new(topology, upload, new(fixture.Source)))
        { FrameHistoryLease = new(original, retired) };
        actor.CommitHistory(original, default);
        var oldCurrent = Assert.IsType<SubmissionTestBuffer>(original.Current);
        retired.Retire(original); retired.BeginFrame();
        var oldCompletion = barrier.Fences[0];
        actor.FrameHistoryLease = new(replacement, retired);
        if (abandonedRetry)
        {
            actor.CopyHistory(device, replacement, default);
            oldCompletion.Signaled = true; retired.BeginFrame();
            Assert.Same(original, actor.HistoryBatch);
            Assert.Equal(0, oldCurrent.DisposeCalls);
            actor.CopyHistory(device, replacement, default);
            Assert.Equal(2, device.CopyCalls);
        }
        actor.CommitHistory(replacement, default);
        oldCompletion.Signaled = true;
        retired.BeginFrame();
        Assert.Equal(0, oldCurrent.DisposeCalls);
        Assert.Equal(2, barrier.Fences.Count);
        Assert.False(barrier.Fences[1].Signaled);
        Assert.Equal(0, drains);
        barrier.Fences[1].Signaled = true; retired.BeginFrame();
        Assert.Equal(1, oldCurrent.DisposeCalls);
        original.Dispose();
        Assert.Equal(1, oldCurrent.DisposeCalls);
        actor.ReleaseHistory(); actor.ReleaseHistory();
        replacement.Dispose();
        retired.FlushAll();
        Assert.Equal(1, Assert.IsType<SubmissionTestBuffer>(replacement.Current).DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scene_and_submission_owner_teardown_keep_leases_until_the_drain(bool ownerFirst)
    {
        var device = new SubmissionTestDevice(16);
        using var owner = new GpuFrameSubmission(device);
        using var retired = new GpuRetireQueue(device.WaitForIdle);
        using var batch = Batch(device);
        var lease = new CoverageGpuHistoryLease(batch, retired);
        lease.Activate();
        var current = Assert.IsType<SubmissionTestBuffer>(batch.Current);
        if (ownerFirst) owner.Dispose();
        lease.Retire(); lease.Retire(); batch.Dispose();
        Assert.Equal(0, current.DisposeCalls);
        if (!ownerFirst) owner.Dispose();
        retired.FlushAll(); lease.Dispose(); batch.Dispose();
        Assert.Equal(1, current.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Submission_or_callback_failure_cannot_release_a_copy_source_early(bool submitFailure)
    {
        var device = new SubmissionTestDevice(32) { FailSubmit = submitFailure };
        var barrier = new HeldBarrier();
        using var retired = new GpuRetireQueue(device.WaitForIdle, barrier);
        using var original = Batch(device); using var replacement = Batch(device);
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var actor = new KhaozEngine.Render3D.CoverageGpuActor(new(topology, CoverageGpuUpload.Host(topology, 2), new(fixture.Source)))
        { FrameHistoryLease = new(original, retired) };
        actor.CommitHistory(original, default);
        retired.Retire(original); retired.BeginFrame(); barrier.Fences[0].Signaled = true;
        actor.FrameHistoryLease = new(replacement, retired);
        using var owner = new GpuFrameSubmission(device);
        owner.Begin();
        if (!submitFailure) Assert.True(owner.TryRegister(new ThrowingParticipant()));
        Assert.True(owner.TryRegister(new CommitParticipant(() => actor.CommitHistory(replacement, default))));
        Assert.Throws<InvalidOperationException>(() => owner.Submit(device));
        retired.BeginFrame();
        Assert.Equal(0, Assert.IsType<SubmissionTestBuffer>(original.Current).DisposeCalls);
        if (submitFailure) Assert.Same(original, actor.HistoryBatch);
        else Assert.Same(replacement, actor.HistoryBatch);
        device.FailSubmit = false;
        actor.ReleaseHistory();
        retired.FlushAll();
        original.Dispose(); replacement.Dispose();
    }

    sealed class CommitParticipant(Action commit) : IGpuFrameSubmissionParticipant
    {
        public void Submitted() => commit();
        public void Abandoned() { }
    }
    sealed class ThrowingParticipant : IGpuFrameSubmissionParticipant
    {
        public void Submitted() => throw new InvalidOperationException("planned callback failure");
        public void Abandoned() { }
    }

    static CoverageGpuBatch Batch(SubmissionTestDevice device)
    {
        var batch = new CoverageGpuBatch(device);
        batch.Reserve(1, 32, 64, 64);
        return batch;
    }

    sealed class HeldBarrier : IRetireBarrier
    {
        internal readonly List<SubmissionTestFence> Fences = new();
        public IGpuFence Submit() { var fence = new SubmissionTestFence(); Fences.Add(fence); return fence; }
        public void Release(IGpuFence completion) { }
        public void Dispose() { }
    }
}
