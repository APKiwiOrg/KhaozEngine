using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.Performance;

public sealed partial class GpuPerfTimestampRingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedOrSoftwareDeviceCannotProveGpu(bool software)
    {
        using var device = new GpuPerfTimestampTestDevice
        {
            Software = software,
            Features = new(false, true, supportsCompletionFences: true) { SupportsTimestamps = software },
        };
        Assert.Throws<NotSupportedException>(() => new GpuPerfTimestampRing(device, 1, 2, (_, _) => { }));
        Assert.Empty(device.Pools);
        Assert.Equal(0, device.Submissions);
    }

    [Fact]
    public void Missing_completion_support_or_end_tail_cannot_begin()
    {
        using var device = new GpuPerfTimestampTestDevice
        { Features = new(false, true) { SupportsTimestamps = true } };
        Assert.Throws<NotSupportedException>(() => new GpuPerfTimestampRing(device, 1, 2, (_, _) => { }));
        using var rig = new Rig();
        using var commands = new NullGpuCommandList();
        rig.Submission.Begin(); commands.Begin();
        Assert.False(rig.Ring.TryBegin(commands, rig.Submission, 0, 2, out _));
        Assert.All(rig.Device.Pools, p => Assert.Equal(0, p.Begins));
        Assert.NotNull(rig.Ring.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolution_or_publication_failure_keeps_fence_and_preserves_other_samples(bool publication)
    {
        using var rig = new Rig(sampleCapacity: 3);
        rig.Record(0); rig.Record(1); rig.Record(2);
        var expected = new InvalidOperationException("first timestamp failure");
        if (publication) rig.OnPublish = (index, _) => { if (index == 0) throw expected; };
        else rig.Device.Pools[0].ResolveFailure = expected;
        rig.Device.WaitForIdle();
        var resolved = Assert.Single(Assert.Throws<GpuDiagnosticFailureException>(rig.Ring.PollCompleted).Failures);
        Assert.Equal("renderer.batch.complete", resolved.Stage);
        Assert.Same(expected, resolved.Error);
        Assert.Null(rig.Results[0]);
        Assert.NotNull(rig.Results[1]); Assert.NotNull(rig.Results[2]);
        Assert.True(rig.Ring.HasPending(rig.Device.Fences[0]));
        rig.Ring.PollCompleted();
        Assert.All(rig.Device.Pools, p => Assert.Equal(1, p.Resolves));
        Assert.Equal(publication ? 1 : 0, rig.Publications[0]);
        Assert.False(rig.Ring.CompleteBlock());
    }

    [Fact]
    public void Reentrant_publication_cannot_resolve_or_publish_the_same_result_twice()
    {
        using var rig = new Rig(sampleCapacity: 1);
        rig.OnPublish = (_, _) => rig.Ring.PollCompleted();
        rig.Record(0); rig.Device.WaitForIdle(); rig.Ring.PollCompleted();
        Assert.Equal(1, rig.Publications[0]);
        Assert.Equal(1, rig.Device.Pools[0].Resolves);
        Assert.True(rig.Ring.CompleteBlock());
    }

    [Fact]
    public void Begin_failure_keeps_the_original_exception_and_quarantines_until_teardown()
    {
        using var rig = new Rig();
        var expected = new InvalidOperationException("timestamp begin failure");
        rig.Device.Pools[0].OnBegin = () => throw expected;
        Assert.Same(expected, Record.Exception(() => rig.Begin(0)));
        Assert.True(rig.Ring.HasPending(rig.Device.Fences[0]));
        Assert.NotNull(rig.Ring.Failure);
        rig.Submission.Abandon();
        rig.Ring.PollCompleted();
        Assert.Equal(0, rig.Device.Pools[0].Resolves);
        var retained = Assert.Single(Assert.Throws<GpuDiagnosticFailureException>(rig.Ring.Dispose).Failures);
        Assert.Equal("renderer.session.dispose.timestamps.retained", retained.Stage);
        Assert.IsType<InvalidOperationException>(retained.Error);
        Assert.Equal(0, rig.Device.Pools[0].Disposals);
        Assert.Equal(1, rig.Device.Pools[1].Disposals);
        Assert.Equal(1, rig.Device.Pools[2].Disposals);
        rig.Submission.Dispose();
        Assert.Equal(1, rig.Device.Pools[0].Disposals);
    }

    [Fact]
    public void Registration_capacity_refusal_does_not_touch_a_pool()
    {
        using var rig = new Rig();
        using var submission = new GpuFrameSubmission(rig.Device, consumerCapacity: 1);
        submission.Begin(); rig.Commands.Begin();
        Assert.True(submission.TryRegister(new GpuFrameSubmissionTests.Participant()));
        Assert.False(rig.Ring.TryBegin(rig.Commands, submission, 0, 2, out _));
        Assert.NotNull(rig.Ring.Failure);
        Assert.All(rig.Device.Pools, p => Assert.Equal(0, p.Begins));
    }
}
