using System;
using System.IO;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfPublicationCompletionTests
{
    [Fact]
    public void File_completion_metadata_matches_the_returned_report()
    {
        string parent = Environment.GetEnvironmentVariable("KE_RENDERER_PERF_FIX_EVIDENCE")
            ?? Path.Combine(Path.GetTempPath(), "khaozengine-renderer-perf-task-8-fix-r1");
        string directory = Path.Combine(parent, "publication-" + Guid.NewGuid().ToString("N"));
        try
        {
            long tick = 0;
            var input = RendererPerfProtocolTests.CompleteReport();
            var report = RendererPerfPublicationWriter.Files(directory).Publish(input, new(() => tick += 10, 1000), 600000);
            Assert.True(report.Publication.FinalUnitPublished, report.Publication.OriginalFailure);
            var bytes = File.ReadAllBytes(Path.Combine(directory, input.Manifest.UnitId + ".json"));
            var persisted = RendererPerfReportJson.Parse(bytes);
            Assert.Equal(report.Publication, persisted.Publication);
            Assert.True(File.Exists(Path.Combine(directory, input.Manifest.UnitId + ".partial.json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public void Successful_final_write_is_in_returned_and_persisted_elapsed_and_cost()
    {
        var rig = new RendererPerfDriverTests.DriverRig { Fault = "slow-final" };
        var report = rig.Run();
        Assert.True(report.Publication.FinalUnitPublished);
        Assert.Equal(6680, report.Publication.ElapsedMilliseconds.Value);
        Assert.Equal(0.005789333333333333, report.Publication.EstimatedCostUsd.Value!.Value, 12);
        var persisted = rig.Writes.Last(w => w.Final).Report;
        Assert.Equal(report.Publication.ElapsedMilliseconds, persisted.Publication.ElapsedMilliseconds);
        Assert.Equal(report.Publication.EstimatedCostUsd, persisted.Publication.EstimatedCostUsd);
        Assert.Equal(1260, rig.Frames.Count);
        _ = RendererPerfReportJson.Serialize(persisted);
    }
}
