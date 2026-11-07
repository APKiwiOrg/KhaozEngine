using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// The batch completion drain includes the query availability wait of backends that need one (issue 1635).
public sealed class RendererPerfCompletionWaitTests
{
    [Fact]
    public void Query_availability_wait_is_retained_completion_with_its_evidence()
    {
        using var rig = new RendererPerfFrameBoundaryTests.BoundaryRig(false, notReadyPolls: 2);
        var samples = new RendererPerfFrameSample[1];
        rig.Session.Frame(0, ref samples[0]);
        rig.Session.CompleteBatch(samples);

        Assert.Empty(samples[0].Errors);
        Assert.NotNull(samples[0].GpuElapsedTicks);
        RendererPerfSessionInfo info = rig.Session.ReadInfo();
        Assert.Null(info.Failure);
        Assert.Equal(50 + 14, info.Drain!.EndTicks - info.Drain.StartTicks);
        Assert.True(info.Harvest!.StartTicks >= info.Drain.EndTicks);
        var completion = info.Completion!;
        Assert.Equal(1, completion["completion.queryWait.0.ready"].Value);
        Assert.Equal(2, completion["completion.queryWait.0.polls"].Value);
        Assert.Equal(14, completion["completion.queryWait.0.elapsedTicks"].Value);
        Assert.Equal(1, completion["completion.queryWait.0.fenceTarget"].Value);
        Assert.Equal(1, completion["completion.queryWait.0.fenceCompleted"].Value);
        Assert.Equal(1, completion["completion.queryWait.0.drainTarget"].Value);
        Assert.Equal(2, completion["completion.queryWait.0.timestamp.0.notReady"].Value);
        Assert.Equal(0, completion["completion.queryWait.0.timestamp.0.finalHresult"].Value);
        Assert.All(completion.Values, metric => RendererPerfReportJson.Metric(metric));
    }

    [Fact]
    public void Query_still_unavailable_at_the_unit_deadline_keeps_the_strict_refusal()
    {
        using var rig = new RendererPerfFrameBoundaryTests.BoundaryRig(false, notReadyPolls: int.MaxValue, completionDeadline: 0);
        var samples = new RendererPerfFrameSample[1];
        rig.Session.Frame(0, ref samples[0]);
        rig.Session.CompleteBatch(samples);

        RendererPerfSessionInfo info = rig.Session.ReadInfo();
        Assert.Equal("Pending GPU timestamps remain after the batch drain.", info.Failure);
        Assert.Null(samples[0].GpuElapsedTicks);
        Assert.Equal(0, info.Completion!["completion.queryWait.0.ready"].Value);
        Assert.Equal(0, info.Completion["completion.queryWait.0.polls"].Value);
        Assert.Equal(1, info.Completion["completion.queryWait.0.timestamp.0.finalHresult"].Value);
    }

    [Fact]
    public void Pools_without_an_availability_wait_record_no_completion_evidence()
    {
        using var rig = new RendererPerfFrameBoundaryTests.BoundaryRig(false);
        var samples = new RendererPerfFrameSample[1];
        rig.Session.Frame(0, ref samples[0]);
        rig.Session.CompleteBatch(samples);

        Assert.Empty(rig.Session.ReadInfo().Completion!);
    }
}
