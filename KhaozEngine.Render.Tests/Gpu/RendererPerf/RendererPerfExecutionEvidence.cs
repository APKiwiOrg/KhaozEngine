using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfExecutionEvidence
{
    internal static void Validate(RendererPerfReport report)
    {
        bool complete = report.Publication.FinalUnitPublished;
        Require(report.Conditioning.Length <= report.Manifest.ConditioningFrames
            && (!complete || report.Conditioning.Length == report.Manifest.ConditioningFrames), "Conditioning frames are missing or excessive.");
        var ids = new HashSet<int>();
        for (int i = 0; i < report.Conditioning.Length; i++)
            RendererPerfReportJson.ValidateFrame(report.Conditioning[i], i % 256, ids);
        var operations = new List<(string Id, int Count)> { ("conditioning:S3/TotalOnly/0", report.Conditioning.Length) };
        operations.AddRange(report.Blocks.Select(b => ($"{b.Block.SceneId}/{b.Block.Layout}/{b.Block.Iteration}", b.Warmup.Length + b.Measured.Length)));
        operations.AddRange(report.Controls.Select(c => ($"control:{c.Control.Id}/TotalOnly/0", c.Warmup.Length + c.Measured.Length)));
        foreach (var startup in report.Startup)
        {
            Require(!string.IsNullOrWhiteSpace(startup.Operation) && startup.Frequency > 0
                && startup.Wall.EndTicks >= startup.Wall.StartTicks, "Cold startup evidence is invalid.");
            foreach (var metric in startup.Metrics.Values) RendererPerfReportJson.Metric(metric);
        }
        int batch = 0;
        foreach (var operation in operations)
        {
            int offset = 0;
            while (offset < operation.Count)
            {
                Require(batch < report.Batches.Length, "A retained frame has no batch completion evidence.");
                var row = report.Batches[batch++];
                Require(row.Operation == operation.Id && row.FirstFrame == offset && row.Count is >= 1 and <= 3
                    && row.Count <= operation.Count - offset, "A batch is duplicated, foreign, missing a frame or outside the three-frame pacing.");
                Require(row.Frequency > 0 && row.EndTicks >= row.StartTicks, "Batch wall evidence is invalid.");
                foreach (var metric in row.Telemetry.Values) RendererPerfReportJson.Metric(metric);
                foreach (var span in new[] { row.Drain, row.Harvest })
                    Require(span is null || span.StartTicks >= row.StartTicks && span.EndTicks >= span.StartTicks && span.EndTicks <= row.EndTicks,
                        "Drain or harvest is outside batch wall time.");
                Require(row.Drain is null || row.Harvest is null || row.Harvest.StartTicks >= row.Drain.EndTicks,
                    "Harvest overlaps completion drain.");
                offset += row.Count;
            }
        }
        Require(batch == report.Batches.Length, "An extra measurement batch was published.");
        if (report.Assessment.ValidForPromotion)
            Require(report.Conditioning.All(f => f.Errors.IsEmpty && f.GpuFrame.Status == RendererPerfMetricStatus.Available),
                "Invalid conditioning cannot support promotion.");
    }
    static void Require(bool condition, string reason)
    {
        if (!condition) throw new RendererPerfProtocolException("Publication", reason);
    }
}
