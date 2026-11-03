using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfFrameLimitTests
{
    [Theory]
    [InlineData(4000, 1, 0)]
    [InlineData(1, 11000, 0)]
    [InlineData(1, 1, 11000)]
    public void Individually_valid_frames_do_not_charge_batch_work_to_one_frame(long frame, long drain, long readback)
    {
        var rig = new RendererPerfDriverTests.DriverRig
        { FirstBatchFrameTicks = frame, FirstDrainTicks = drain, ColdReadbackTicks = readback };
        var report = rig.Run();
        Assert.True(report.Publication.FinalUnitPublished, report.Publication.OriginalFailure);
        Assert.Equal(1260, rig.Frames.Count);
        Assert.Equal(frame, report.Conditioning[0].CpuEndTicks - report.Conditioning[0].CpuStartTicks);
        Assert.Equal(drain, report.Batches[0].Drain!.EndTicks - report.Batches[0].Drain!.StartTicks);
        _ = RendererPerfReportJson.Serialize(report);
    }

    [Fact]
    public void Individual_retained_gpu_duration_still_enforces_the_frame_limit()
    {
        var rig = new RendererPerfDriverTests.DriverRig { Fault = "gpu-timeout" };
        var report = rig.Run();
        Assert.False(report.Publication.FinalUnitPublished);
        Assert.Contains("FrameTimeout", report.Publication.OriginalFailure);
        Assert.Equal(3, rig.Frames.Count);
        _ = RendererPerfReportJson.Serialize(report);
    }
}
