using System;
using System.IO;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerPublicationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Unit_failure_after_successful_stamp_corrects_stored_artifacts(bool cancel, bool writeErrors)
    {
        using var f = new RendererPerfColdLifecycleFixture();
        string directory = Path.Combine(Path.GetTempPath(), "renderer-return-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var files = new RendererPerfPublicationFiles(directory);
            var correction = new IOException("return correction occurrence");
            var recovery = new IOException("return recovery occurrence");
            bool stamped = false;
            int stampChecks = 0, writes = 0;
            RendererPerfReport? initialPartial = null;
            var clock = new RendererPerfClock(() =>
            {
                if (stamped)
                {
                    if (stampChecks++ == 0)
                    {
                        if (cancel) f.Cancellation.Cancel(); else f.Now = 600100;
                        return 100; // stamp.after succeeds, the next enforcement must fail.
                    }
                }
                return f.Now;
            }, 1000);
            var writer = new RendererPerfPublicationWriter((report, final) =>
            {
                writes++; files.Write(report, final);
                if (writes == 1) initialPartial = report;
                if (writeErrors && writes >= 3) throw writes == 3 ? correction : recovery;
            }, report => { files.RecordCompletion(report); stamped = true; }, directory: directory);
            var observed = new ObservePublication(writer, files.SetBudget);
            var outcome = f.Run(observed, clock);
            Assert.True(stamped, string.Join("\n", outcome.Failures.Select(e => e.Stage + " " + e.Error)));
            Assert.NotNull(outcome.Report);
            Assert.Equal(writeErrors ? 3 : 1, outcome.Failures.Length);
            var original = outcome.Failures[0].Error;
            if (cancel) Assert.Equal(f.Cancellation.Token, Assert.IsType<OperationCanceledException>(original).CancellationToken);
            else
            {
                Assert.IsType<TimeoutException>(original);
                Assert.Contains("renderer.publication.return", original.Message, StringComparison.Ordinal);
            }
            Assert.NotNull(observed.Final);
            Assert.Equal(outcome.Failures.Length, observed.Final.Failures.Length);
            Assert.Same(original, observed.Final.Failures[0].Error);
            if (writeErrors)
            {
                Assert.Same(correction, outcome.Failures[1].Error);
                Assert.Same(recovery, outcome.Failures[2].Error);
            }
            Assert.Equal(writeErrors ? new[] { "renderer.publication.final", "renderer.publication.correction", "renderer.publication.recovery" }
                : new[] { "renderer.publication.final" }, outcome.Failures.Select(e => e.Stage));
            string name = f.Template.Manifest.UnitId;
            var stored = RendererPerfReportJson.Parse(File.ReadAllBytes(Path.Combine(directory, name + ".json")));
            Assert.Equal(RendererPerfPublicationState.Failed, stored.Publication.State);
            Assert.False(stored.Publication.FinalUnitPublished);
            Assert.False(stored.Assessment.ValidForPromotion);
            Assert.Equal(outcome.Report.Publication.OriginalFailure, stored.Publication.OriginalFailure);
            Assert.Equal(outcome.Report.Publication.ElapsedMilliseconds, stored.Publication.ElapsedMilliseconds);
            Assert.Equal(outcome.Report.Publication.EstimatedCostUsd, stored.Publication.EstimatedCostUsd);
            Assert.Equal(cancel ? 0d : 600000d, stored.Publication.ElapsedMilliseconds.Value);
            Assert.Equal(cancel ? 0d : .52d, stored.Publication.EstimatedCostUsd.Value!.Value, 12);
            Assert.NotNull(initialPartial);
            Assert.Equal(initialPartial.Blocks, outcome.Report.Blocks);
            Assert.Equal(initialPartial.Pipelines, outcome.Report.Pipelines);
            Assert.Contains("State Failed. Promotion False.", File.ReadAllText(Path.Combine(directory, name + ".md")), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(directory, name + ".partial.json")));
            Assert.NotEmpty(stored.Blocks); Assert.NotEmpty(stored.Pipelines);
            Assert.Equal(outcome.Report.Blocks.Length, stored.Blocks.Length);
            Assert.ThrowsAny<Exception>(() => RendererPerfAnalysis.ReadDirectory(directory, name + ".json"));
            _output.WriteLine("return cancel={0} writeErrors={1} events={2} elapsed={3} cost={4}", cancel, writeErrors,
                outcome.Failures.Length, stored.Publication.ElapsedMilliseconds.Value, stored.Publication.EstimatedCostUsd.Value);
        }
        finally { Directory.Delete(directory, true); }
    }
}
