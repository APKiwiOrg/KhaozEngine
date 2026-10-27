using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfColdFrameLimitTests
{
    [Theory]
    [InlineData("cpu", 10001)]
    [InlineData("cpu", 45000)]
    [InlineData("cpu", 60000)]
    [InlineData("retained-cpu", 10001)]
    [InlineData("retained-cpu", 45000)]
    [InlineData("retained-cpu", 60000)]
    [InlineData("gpu", 10001)]
    [InlineData("gpu", 45000)]
    [InlineData("gpu", 60000)]
    public void First_conditioning_frame_keeps_bounded_cold_work(string interval, long milliseconds)
    {
        var rig = Rig(interval, milliseconds, "conditioning:S3", 0);
        var report = rig.Run();
        Assert.True(report.Publication.FinalUnitPublished, report.Publication.OriginalFailure);
        Assert.Equal(1260, rig.Frames.Count);
        Assert.Equal(300, report.Conditioning.Length);
        var cold = report.Conditioning[0];
        Assert.Equal(milliseconds, interval == "gpu" ? cold.GpuFrame.Value / 1000000d : cold.CpuEndTicks - cold.CpuStartTicks);
        _ = RendererPerfReportJson.Serialize(report);
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("retained-cpu")]
    [InlineData("gpu")]
    public void First_conditioning_frame_beyond_cold_limit_refuses(string interval)
    {
        var rig = Rig(interval, 60001, "conditioning:S3", 0);
        var report = rig.Run();
        Assert.False(report.Publication.FinalUnitPublished);
        Assert.Contains("FrameTimeout", report.Publication.OriginalFailure);
        Assert.Single(rig.Created);
        Assert.NotEmpty(report.Conditioning);
    }

    [Theory]
    [InlineData("cpu", "conditioning:S3", 1)]
    [InlineData("retained-cpu", "conditioning:S3", 1)]
    [InlineData("gpu", "conditioning:S3", 1)]
    [InlineData("cpu", "conditioning:S3", 3)]
    [InlineData("retained-cpu", "conditioning:S3", 3)]
    [InlineData("gpu", "conditioning:S3", 3)]
    [InlineData("cpu", "S3", 0)]
    [InlineData("retained-cpu", "S3", 0)]
    [InlineData("gpu", "S3", 0)]
    [InlineData("cpu", "S3", 64)]
    [InlineData("retained-cpu", "S3", 64)]
    [InlineData("gpu", "S3", 64)]
    [InlineData("cpu", "control:1", 0)]
    [InlineData("retained-cpu", "control:1", 0)]
    [InlineData("gpu", "control:1", 0)]
    [InlineData("cpu", "control:1", 32)]
    [InlineData("retained-cpu", "control:1", 32)]
    [InlineData("gpu", "control:1", 32)]
    public void Every_other_frame_keeps_the_steady_limit(string interval, string operation, int frame)
    {
        var rig = Rig(interval, 10001, operation, frame);
        var report = rig.Run();
        Assert.False(report.Publication.FinalUnitPublished);
        Assert.Contains("FrameTimeout", report.Publication.OriginalFailure);
        Assert.True(rig.Frames.Count < 1260);
    }

    static RendererPerfDriverTests.DriverRig Rig(string interval, long milliseconds, string operation, int frame)
    {
        var rig = new RendererPerfDriverTests.DriverRig();
        if (interval == "cpu") rig.FrameTicks = (block, index) => block.SceneId == operation && index == frame ? milliseconds : 1;
        else rig.RetainFrame = (block, sample) =>
        {
            if (block.SceneId != operation || sample.FrameId != frame) return sample;
            if (interval == "retained-cpu")
            {
                rig.Tick += milliseconds;
                return sample with { CpuEndTicks = sample.CpuStartTicks + milliseconds };
            }
            return sample with { GpuElapsedTicks = (ulong)milliseconds * 1000000, GpuFrame = RendererPerfDriverTests.Value(milliseconds * 1000000, "ns") };
        };
        return rig;
    }
}
