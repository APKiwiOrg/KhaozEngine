using System;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfBatchFailureTests
{
    [Theory]
    [InlineData("second-observation")]
    [InlineData("second-drain")]
    public void Failed_second_batch_keeps_current_evidence_and_device_rows(string fault)
    {
        var rig = new RendererPerfDriverTests.DriverRig { Fault = fault };
        var report = rig.Run();
        Assert.False(report.Publication.FinalUnitPublished);
        Assert.Equal(498, rig.Frames.Count);
        var first = report.Batches[^2];
        var failed = report.Batches[^1];
        Assert.NotNull(first.Drain);
        Assert.NotNull(first.Harvest);
        Assert.Null(failed.Drain);
        Assert.Null(failed.Harvest);
        Assert.Contains(report.Blocks[^1].Warmup, f => f.Passes.Any(p => p.DeviceOnly));
        var restored = RendererPerfReportJson.Parse(RendererPerfReportJson.Serialize(report));
        Assert.Equal(6, restored.Blocks[^1].Warmup.Length);
        Assert.All(restored.Blocks[^1].Warmup, f => Assert.True(Assert.Single(f.Passes).DeviceOnly));
    }

    [Fact]
    public void Native_failed_second_drain_clears_previous_completion_spans()
    {
        using var rig = new RendererPerfFrameBoundaryTests.BoundaryRig(true, count: 6);
        var samples = new RendererPerfFrameSample[6];
        for (int i = 0; i < 3; i++) rig.Session.Frame(i, ref samples[i]);
        rig.Session.CompleteBatch(samples.AsSpan(0, 3));
        Assert.NotNull(rig.Session.ReadInfo().Drain);
        for (int i = 3; i < 6; i++) rig.Session.Frame(i, ref samples[i]);
        rig.Device.Trace = op =>
        {
            if (op == "drain") throw new InvalidOperationException("native drain failed");
        };
        var failure = Assert.Throws<GpuDiagnosticFailureException>(() => rig.Session.CompleteBatch(samples.AsSpan(3, 3)));
        Assert.Equal("native drain failed", Assert.Single(failure.Failures).Error.Message);
        rig.Device.Trace = null;
        var failed = rig.Session.ReadInfo();
        Assert.Null(failed.Drain);
        Assert.Null(failed.Harvest);
        Assert.Equal(6, rig.Device.Submissions);
        Assert.NotEmpty(samples[^1].Metrics);
        Assert.NotEmpty(samples[^1].Passes);
    }
}
