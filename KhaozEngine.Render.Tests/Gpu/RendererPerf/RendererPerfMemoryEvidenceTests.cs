using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfMemoryEvidenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Early_memory_high_water_survives_lower_observations_and_failure(bool failed)
    {
        var rig = new RendererPerfDriverTests.DriverRig { ObserveMemory = true, Fault = failed ? "second-observation" : null };
        var report = rig.Run();
        var memory = report.Memory;
        Assert.NotEmpty(memory.Observations);
        var high = Assert.Single(memory.Observations, o => o.Process.TryGetValue("process.workingSet.bytes", out var v) && v.Value == 9000);
        Assert.Equal("conditioning:S3/TotalOnly/0", high.Operation);
        Assert.Equal("batch", high.Stage);
        Assert.Equal(0, high.FirstFrame);
        Assert.Contains(memory.Observations, o => o.Process.TryGetValue("process.workingSet.bytes", out var v) && v.Value == 100);
        Assert.Equal(9000, memory.Peaks["process.workingSet.bytes"].Metric.Value);
        Assert.Equal(6000, memory.Peaks["managed.live.bytes"].Metric.Value);
        Assert.Equal(high, memory.Observations[memory.Peaks["process.workingSet.bytes"].Observation]);
        if (failed) Assert.Contains(memory.Observations, o => o.Reason == "second observation failed");
        var restored = RendererPerfReportJson.Parse(RendererPerfReportJson.Serialize(report)).Memory;
        Assert.Equal(memory.Observations.Length, restored.Observations.Length);
        Assert.Equal(memory.Peaks["managed.live.bytes"], restored.Peaks["managed.live.bytes"]);
        Assert.Equal(failed, !report.Publication.FinalUnitPublished);
    }
}
