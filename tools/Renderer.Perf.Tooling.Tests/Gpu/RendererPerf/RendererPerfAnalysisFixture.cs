using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Synthetic durations and counters only. No device, stopwatch or workload is opened.
internal static class RendererPerfAnalysisFixture
{
    internal static RendererPerfMetric Value(double value, string unit = "ns", string source = "synthetic outer pair")
        => new(value, unit, source, RendererPerfMetricStatus.Available, null);

    internal static RendererPerfReport Report(bool full = false)
    {
        var report = RendererPerfProtocolTests.CompleteReport();
        var manifest = RendererPerfManifest.Parse(Encoding.UTF8.GetBytes(RendererPerfProtocolTests.ManifestJson(full ? "full" : "pilot").ToJsonString()));
        ImmutableArray<RendererPerfFrameSample> Frames(int count, int sequence, int id) => [.. Enumerable.Range(0, count)
            .Select(i => new RendererPerfFrameSample(id + i, (sequence + i) % 256, 0, 100, 1000,
                100, 1, Value(100), [], [], ImmutableDictionary<string, RendererPerfMetric>.Empty, []))];
        report = report with
        {
            Manifest = manifest,
            Recipes = [.. manifest.Recipes.Select(r => new RendererPerfRecipeReport(r, "public synthetic fixture", new string('a', 64),
                ImmutableDictionary<string, long>.Empty, ImmutableDictionary<string, long>.Empty, ImmutableDictionary<string, RendererPerfMetric>.Empty))],
            Blocks = [.. manifest.Blocks.Select(b => new RendererPerfBlockReport(b,
                Frames(b.WarmupFrames, (256 - b.WarmupFrames) % 256, 0), Frames(b.MeasuredFrames, 0, b.WarmupFrames), []))],
            Controls = [.. manifest.Controls.Select(c => new RendererPerfControlReport(c, new string('a', 64),
                Frames(c.WarmupFrames, (256 - c.WarmupFrames) % 256, 0), Frames(c.MeasuredFrames, 0, c.WarmupFrames),
                ImmutableDictionary<string, RendererPerfMetric>.Empty))],
            Publication = report.Publication with
            {
                ElapsedMilliseconds = Value(1234, "ms", "synthetic publication clock"),
                EstimatedCostUsd = Value(.001, "USD", "synthetic accounting")
            }
        };
        return RendererPerfExecutionEvidenceTests.WithTrace(report);
    }

    internal static RendererPerfSummary VerifiedAnalyze(RendererPerfReport report,
        ImmutableDictionary<string, RendererPerfReferenceRates>? references = null)
    {
        // Failed documents remain descriptive. A successful arithmetic fixture gets real capture,
        // admission, native-reader fake, files and directory verification, never an unchecked flag.
        if (report.Publication.State != RendererPerfPublicationState.Completed || report.Publication.OriginalFailure is not null
            || !report.Assessment.ValidForPromotion) return RendererPerfAnalysis.Analyze(report, references);
        _ = RendererPerfReportJson.Serialize(report);
        using var fixture = new RendererPerfCompilerPublicationFixture(report);
        var published = fixture.Publish(RendererPerfPublicationWriter.Files(fixture.Directory));
        if (!published.Failures.IsEmpty) throw new KhaozEngine.Gpu.GpuDiagnosticFailureException(published.Failures);
        return RendererPerfAnalysis.ReadDirectory(fixture.Directory, report.Manifest.UnitId + ".json", references);
    }
    internal static RendererPerfComparisonResult VerifiedCompare(RendererPerfReport baseline, RendererPerfReport candidate)
    {
        try { return RendererPerfComparison.Compare(VerifiedAnalyze(baseline), VerifiedAnalyze(candidate)); }
        catch (RendererPerfProtocolException) { return RendererPerfComparison.Compare(baseline, candidate); }
    }

    internal static RendererPerfReport Map(RendererPerfReport report, Func<RendererPerfBlock, RendererPerfFrameSample, RendererPerfFrameSample> change)
        => report with { Blocks = [.. report.Blocks.Select(b => b with { Measured = [.. b.Measured.Select(f => change(b.Block, f))] })] };

    internal static RendererPerfFrameSample Duration(RendererPerfFrameSample frame, double ns)
        => frame with { GpuFrame = Value(ns), GpuElapsedTicks = (ulong)ns };

    internal static RendererPerfReport Failed(RendererPerfReport report, string reason = "partial write accounting gap #1547")
        => report with
        {
            Assessment = report.Assessment with { ValidForPromotion = false, Reasons = [reason] },
            Publication = report.Publication with { State = RendererPerfPublicationState.Failed, FinalUnitPublished = false, OriginalFailure = reason }
        };
}
