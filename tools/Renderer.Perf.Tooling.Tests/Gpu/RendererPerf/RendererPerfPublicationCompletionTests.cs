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
    // #1547: Release the held ticks only after the initial partial JSON and Markdown exist.
    [Theory]
    [InlineData(5000, false)]
    [InlineData(599000, true)]
    public void Failed_measurement_includes_partial_write_in_elapsed_and_cost(long writeTicks, bool deadlineReached)
    {
        string directory = Path.Combine(Path.GetTempPath(), "renderer-failed-partial-" + Guid.NewGuid().ToString("N"));
        var input = RendererPerfProtocolTests.CompleteReport();
        const string original = "measurement failed";
        input = input with { Publication = input.Publication with
        {
            ElapsedMilliseconds = RendererPerfDriver.Value(1000, "ms", "held unit clock"),
            OriginalFailure = original
        } };
        string stem = Path.Combine(directory, input.Manifest.UnitId);
        var clock = new RendererPerfClock(() => File.Exists(stem + ".partial.md") ? 1000 + writeTicks : 1000, 1000);
        try
        {
            var publication = new RendererPerfEntrypoint.EvidencePublication(directory);
            var report = publication.Publish(input, clock, 600000);
            Assert.Equal(RendererPerfPublicationState.Failed, report.Publication.State);
            Assert.False(report.Publication.FinalUnitPublished);
            Assert.False(File.Exists(stem + ".json"));
            Assert.Equal(original, report.Publication.OriginalFailure);
            if (deadlineReached) Assert.Single(report.Publication.Errors);
            else Assert.Empty(report.Publication.Errors);
            Assert.Equal(1000 + writeTicks, report.Publication.ElapsedMilliseconds.Value);
            Assert.Equal((1000 + writeTicks) / 60000d * (double)input.Manifest.Budget.RatePerMinute,
                report.Publication.EstimatedCostUsd.Value!.Value, 12);
            var persisted = RendererPerfReportJson.Parse(File.ReadAllBytes(stem + ".partial.json"));
            Assert.Equal(report.Publication.OriginalFailure, persisted.Publication.OriginalFailure);
            Assert.Equal(report.Publication.Errors.AsEnumerable(), persisted.Publication.Errors.AsEnumerable());
            Assert.Equal(report.Publication.ElapsedMilliseconds, persisted.Publication.ElapsedMilliseconds);
            Assert.Equal(report.Publication.EstimatedCostUsd, persisted.Publication.EstimatedCostUsd);
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
