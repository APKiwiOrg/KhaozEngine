using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfSidecarAccountingTests
{
    [Theory]
    [InlineData(5000, true, false)]
    [InlineData(600000, false, false)]
    [InlineData(5000, false, true)]
    public void Sidecar_preparation_counts_in_elapsed_cost_and_absolute_deadline(long preparationTicks, bool completed, bool failedMeasurement)
    {
        string directory = Path.Combine(Path.GetTempPath(), "khaozengine-renderer-perf-task-13-fix-r1", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = RendererPerfProtocolTests.CompleteReport();
            input = input with { Publication = input.Publication with { ElapsedMilliseconds = RendererPerfDriver.Value(1000, "ms", "held unit clock") } };
            if (failedMeasurement) input = input with { Publication = input.Publication with { OriginalFailure = "measurement failed" } };
            long tick = 1000;
            bool prepared = false;
            var clock = new RendererPerfClock(() =>
            {
                // The held clock releases these ticks when real sidecar preparation has written its model.
                if (!prepared && File.Exists(Path.Combine(directory, "work-models.json")))
                {
                    prepared = true;
                    tick += preparationTicks;
                }
                return tick;
            }, 1000);
            var type = typeof(RendererPerfEntrypoint).GetNestedType("EvidencePublication", BindingFlags.NonPublic)!;
            var publication = (IRendererPerfPublication)Activator.CreateInstance(type, directory)!;
            var report = publication.Publish(input, clock, 600000);
            Assert.True(prepared);
            Assert.Equal(completed, report.Publication.FinalUnitPublished);
            Assert.Equal(1000 + preparationTicks, report.Publication.ElapsedMilliseconds.Value);
            Assert.Equal((1000 + preparationTicks) / 60000d * (double)input.Manifest.Budget.RatePerMinute,
                report.Publication.EstimatedCostUsd.Value!.Value, 12);
            Assert.Contains(report.Sidecars, s => s.Path == "work-models.json");
            string suffix = completed ? ".json" : ".partial.json";
            var persisted = RendererPerfReportJson.Parse(File.ReadAllBytes(Path.Combine(directory, input.Manifest.UnitId + suffix)));
            Assert.Equal(report.Publication, persisted.Publication);
            if (!completed) Assert.Contains(failedMeasurement ? "measurement failed" : "UnitTimeout", report.Publication.OriginalFailure, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
