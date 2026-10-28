using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfWaterOpticsColdLimitTests
{
    [Theory]
    [InlineData("cpu", "conditioning:S5", 0, 120000, true)]
    [InlineData("retained-cpu", "conditioning:S5", 0, 120000, true)]
    [InlineData("gpu", "conditioning:S5", 0, 120000, true)]
    [InlineData("cpu", "conditioning:S5", 0, 120001, false)]
    [InlineData("retained-cpu", "conditioning:S5", 0, 120001, false)]
    [InlineData("gpu", "conditioning:S5", 0, 120001, false)]
    [InlineData("cpu", "conditioning:S5", 1, 10001, false)]
    [InlineData("retained-cpu", "conditioning:S5", 1, 10001, false)]
    [InlineData("gpu", "conditioning:S5", 1, 10001, false)]
    [InlineData("cpu", "S0", 0, 10001, false)]
    [InlineData("retained-cpu", "S0", 0, 10001, false)]
    [InlineData("gpu", "S0", 0, 10001, false)]
    [InlineData("cpu", "S0", 192, 10001, false)]
    [InlineData("retained-cpu", "S0", 192, 10001, false)]
    [InlineData("gpu", "S0", 192, 10001, false)]
    [InlineData("cpu", "control:1", 0, 10001, false)]
    [InlineData("retained-cpu", "control:1", 0, 10001, false)]
    [InlineData("gpu", "control:1", 0, 10001, false)]
    [InlineData("cpu", "control:1", 64, 10001, false)]
    [InlineData("retained-cpu", "control:1", 64, 10001, false)]
    [InlineData("gpu", "control:1", 64, 10001, false)]
    [InlineData("cpu", "conditioning:S5", 1, 10000, true)]
    [InlineData("retained-cpu", "S0", 192, 10000, true)]
    [InlineData("gpu", "control:1", 64, 10000, true)]
    public void Cold_limit_applies_to_the_v2_conditioning_frame_only(string interval, string operation,
        int frame, long milliseconds, bool accepted)
    {
        var manifest = RendererPerfRehearsalManifestTests.Parse(RendererPerfWaterOpticsPresetTests.ManifestJson());
        var template = RendererPerfProtocolTests.CompleteReport() with { Manifest = manifest };
        var rig = new RendererPerfDriverTests.DriverRig(template: template);
        if (interval == "cpu") rig.FrameTicks = (block, index) => block.SceneId == operation && index == frame ? milliseconds : 1;
        else rig.RetainFrame = (block, sample) =>
        {
            if (block.SceneId != operation || sample.FrameId != frame) return sample;
            if (interval == "retained-cpu")
            {
                rig.Tick += milliseconds;
                return sample with { CpuEndTicks = sample.CpuStartTicks + milliseconds };
            }
            return sample with { GpuElapsedTicks = (ulong)milliseconds * 1000000,
                GpuFrame = RendererPerfDriverTests.Value(milliseconds * 1000000, "ns") };
        };
        var report = rig.Run();
        Assert.Equal(accepted, report.Publication.FinalUnitPublished);
        if (accepted)
        {
            Assert.Null(report.Publication.OriginalFailure);
            Assert.Equal(19632, rig.Frames.Count);
            Assert.Equal("conditioning:S5", rig.Created[0].SceneId);
            Assert.Equal(1200, report.Conditioning.Length);
        }
        else Assert.Contains("FrameTimeout", report.Publication.OriginalFailure);
    }
}
