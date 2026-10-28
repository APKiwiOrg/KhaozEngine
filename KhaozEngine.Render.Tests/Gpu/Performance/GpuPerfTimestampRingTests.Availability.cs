using System;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.Performance;

public sealed partial class GpuPerfTimestampRingTests
{
    [Fact]
    public void Availability_wait_completes_slots_whose_queries_trail_the_drained_fence()
    {
        using var rig = new Rig(sampleCapacity: 3, notReadyPolls: 2);
        rig.Record(0); rig.Record(1); rig.Record(2);
        rig.Device.WaitForIdle();
        long tick = 0;
        var waits = rig.Ring.AwaitAvailability(() => tick++, long.MaxValue);

        Assert.Equal(new[] { 0, 1, 2 }, waits.Select(w => w.SampleId));
        Assert.All(waits, w => Assert.Equal((true, 2), (w.Wait.Ready, w.Wait.Polls)));
        rig.Ring.PollCompleted();
        Assert.Null(rig.Ring.Failure);
        Assert.All(rig.Results, r => Assert.True(r!.Valid));
        Assert.True(rig.Ring.CompleteBlock());
    }

    [Fact]
    public void Never_available_slot_refuses_with_the_strict_pending_reason_at_the_bound()
    {
        using var rig = new Rig(sampleCapacity: 1, notReadyPolls: int.MaxValue);
        rig.Record(0);
        rig.Device.WaitForIdle();
        long tick = 0;
        var wait = Assert.Single(rig.Ring.AwaitAvailability(() => tick++, 4)).Wait;

        Assert.False(wait.Ready);
        Assert.Equal(3, wait.Polls);
        rig.Ring.PollCompleted();
        Assert.Equal("Pending GPU timestamps remain after the batch drain.", rig.Ring.Failure);
        Assert.Null(rig.Results[0]);
        Assert.False(rig.Ring.CompleteBlock());
    }

    [Fact]
    public void Pools_without_an_availability_wait_keep_the_strict_pending_refusal()
    {
        using var rig = new Rig(sampleCapacity: 1);
        rig.Record(0);
        rig.Device.Pools[0].Hold = true;
        rig.Device.WaitForIdle();

        Assert.Empty(rig.Ring.AwaitAvailability(() => 0, long.MaxValue));
        rig.Ring.PollCompleted();
        Assert.Equal("Pending GPU timestamps remain after the batch drain.", rig.Ring.Failure);
    }

    [Fact]
    public void Availability_wait_failure_quarantines_only_its_slot()
    {
        using var rig = new Rig(sampleCapacity: 2, notReadyPolls: 0);
        rig.Record(0); rig.Record(1);
        var expected = new InvalidOperationException("availability failure");
        ((GpuPerfTimestampAwaitTestPool)rig.Device.Pools[0]).WaitFailure = expected;
        rig.Device.WaitForIdle();

        var failure = Assert.Single(Assert.Throws<GpuDiagnosticFailureException>(() => rig.Ring.AwaitAvailability(() => 0, long.MaxValue)).Failures);
        Assert.Equal("renderer.batch.complete", failure.Stage);
        Assert.Same(expected, failure.Error);
        rig.Ring.PollCompleted();
        Assert.Equal(0, rig.Device.Pools[0].Resolves);
        Assert.Null(rig.Results[0]);
        Assert.True(rig.Results[1]!.Valid);
        Assert.Equal("GPU timestamp resolution or publication failed.", rig.Ring.Failure);
        Assert.True(rig.Ring.HasPending(rig.Device.Fences[0]));
    }
}
