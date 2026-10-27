using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfExecutionEvidenceTests
{
    [Theory]
    [InlineData("missing-conditioning")]
    [InlineData("extra-conditioning")]
    [InlineData("missing-batch")]
    [InlineData("duplicate-batch")]
    [InlineData("missing-frame")]
    [InlineData("foreign-operation")]
    [InlineData("invalid-drain")]
    public void Completed_publication_requires_all_conditioning_and_batch_evidence(string fault)
    {
        var report = WithTrace(RendererPerfProtocolTests.CompleteReport());
        switch (fault)
        {
            case "missing-conditioning": report = report with { Conditioning = [] }; break;
            case "extra-conditioning": report = report with { Conditioning = report.Conditioning.Add(report.Conditioning[0]) }; break;
            case "missing-batch": report = report with { Batches = report.Batches.RemoveAt(0) }; break;
            case "duplicate-batch": report = report with { Batches = report.Batches.Insert(1, report.Batches[0]) }; break;
            case "missing-frame": report = report with { Batches = report.Batches.SetItem(0, report.Batches[0] with { Count = 2 }) }; break;
            case "foreign-operation": report = report with { Batches = report.Batches.SetItem(0, report.Batches[0] with { Operation = "other" }) }; break;
            case "invalid-drain":
                report = report with
                {
                    Batches = report.Batches.SetItem(0, report.Batches[0] with
                    { Drain = new("batch.completionDrain", null, -1, 0) })
                }; break;
        }
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report));
    }

    internal static RendererPerfReport WithTrace(RendererPerfReport report)
    {
        var sample = report.Blocks.SelectMany(b => b.Measured).Concat(report.Controls.SelectMany(c => c.Measured)).First();
        var conditioning = Enumerable.Range(0, report.Manifest.ConditioningFrames)
            .Select(i => sample with { FrameId = i, SequenceIndex = i % 256 }).ToImmutableArray();
        var batches = new List<RendererPerfBatchReport>();
        void Add(string operation, int count)
        {
            for (int first = 0; first < count; first += 3)
                batches.Add(new(operation, first, Math.Min(3, count - first), 0, 10, 1000,
                    new("batch.completionDrain", null, 5, 7), new("batch.harvest", null, 7, 10),
                    ImmutableDictionary<string, RendererPerfMetric>.Empty));
        }
        Add("conditioning:S3/TotalOnly/0", conditioning.Length);
        foreach (var block in report.Blocks)
            Add($"{block.Block.SceneId}/{block.Block.Layout}/{block.Block.Iteration}", block.Warmup.Length + block.Measured.Length);
        foreach (var control in report.Controls)
            Add($"control:{control.Control.Id}/TotalOnly/0", control.Warmup.Length + control.Measured.Length);
        return RendererPerfGpuClockAssessmentTests.WithClocks(report with { Conditioning = conditioning, Batches = [.. batches] });
    }
}
