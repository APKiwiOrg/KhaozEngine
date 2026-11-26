using System;
using System.IO;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_final_correction_and_recovery_failures_reach_unit_outcome(bool exhaustText)
    {
        using var fixture = new RendererPerfColdLifecycleFixture();
        var final = new InvalidOperationException(exhaustText ? new string('\u0001', 3000000) : "actual final write");
        var correction = new InvalidOperationException("actual correction write");
        var recovery = new InvalidOperationException("actual partial recovery write");
        string directory = Path.Combine(Path.GetTempPath(), "renderer-publication-failures-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var files = new RendererPerfPublicationFiles(directory);
        int writes = 0;
        var writer = new RendererPerfPublicationWriter((report, isFinal) =>
        {
            writes++;
            if (writes == 1) { Assert.False(isFinal); files.Write(report, false); return; }
            throw writes == 2 ? final : writes == 3 ? correction : recovery;
        }, _ => throw new InvalidOperationException("No stamp follows a failed final."), directory: directory);
        var outcome = fixture.Run(Assert.IsAssignableFrom<IRendererPerfUnitPublication>(writer));
        Assert.NotNull(outcome.Report);
        Assert.Equal(4, writes);
        Assert.Equal(new[] { "renderer.publication.final", "renderer.publication.correction", "renderer.publication.recovery" },
            outcome.Failures.Select(f => f.Stage));
        Assert.Same(final, outcome.Failures[0].Error);
        Assert.Same(correction, outcome.Failures[1].Error);
        Assert.Same(recovery, outcome.Failures[2].Error);
        Assert.False(outcome.Report.Publication.FinalUnitPublished);
        Assert.False(outcome.Report.Blocks.IsEmpty);
        Assert.NotEmpty(outcome.Report.Pipelines);
        Assert.True(File.Exists(Path.Combine(directory, fixture.Template.Manifest.UnitId + ".partial.json")));
        if (exhaustText)
        {
            Assert.Equal(RendererPerfFailureText.OmittedOriginal, outcome.Report.Publication.OriginalFailure);
            Assert.False(outcome.Report.Publication.FailureText.Complete);
            Assert.Equal(3, outcome.Report.Publication.FailureText.OmittedFailures);
            Assert.True(outcome.Report.Publication.FailureText.RequiredUtf8Bytes > 16777216);
        }
        else Assert.Equal("actual final write", outcome.Report.Publication.OriginalFailure);
        _output.WriteLine("actual final/correction/recovery events=3 partialExists=true textComplete={0} required={1}",
            outcome.Report.Publication.FailureText.Complete, outcome.Report.Publication.FailureText.RequiredUtf8Bytes);
        System.IO.Directory.Delete(directory, true);
    }
    // #1547: The typed unit must count the real partial write even when its workload failed first.
    [Theory]
    [InlineData(5000, false)]
    [InlineData(600000, true)]
    public void Failed_unit_includes_partial_write_in_elapsed_and_cost(long writeTicks, bool deadlineReached)
    {
        using var fixture = new RendererPerfColdLifecycleFixture();
        string directory = Path.Combine(Path.GetTempPath(), "renderer-failed-unit-partial-" + Guid.NewGuid().ToString("N"));
        string stem = Path.Combine(directory, fixture.Template.Manifest.UnitId);
        var original = new InvalidOperationException("measurement failed");
        fixture.Boundary = stage => { if (stage == "frame") throw original; };
        var clock = new RendererPerfClock(() => File.Exists(stem + ".partial.md") ? fixture.Now + writeTicks : fixture.Now, 1000);
        try
        {
            var outcome = fixture.Run(RendererPerfPublicationWriter.Files(directory), clock);
            Assert.False(outcome.Certified);
            Assert.NotNull(outcome.Report);
            Assert.Same(original, outcome.Failures[0].Error);
            Assert.Same(original, outcome.Report.Publication.OriginalFailureException);
            Assert.Equal(RendererPerfPublicationState.Failed, outcome.Report.Publication.State);
            Assert.False(outcome.Report.Publication.FinalUnitPublished);
            if (deadlineReached)
            {
                Assert.Equal(2, outcome.Failures.Length);
                var timeout = Assert.IsType<RendererPerfUnitBudgetTimeoutException>(outcome.Failures[1].Error);
                Assert.Equal(RendererPerfUnitBudgetRefusal.DeadlineReached, timeout.Reason);
            }
            else Assert.Single(outcome.Failures);
            Assert.False(File.Exists(stem + ".json"));
            Assert.Equal(writeTicks, outcome.Report.Publication.ElapsedMilliseconds.Value);
            Assert.Equal(writeTicks / 60000d * (double)fixture.Template.Manifest.Budget.RatePerMinute,
                outcome.Report.Publication.EstimatedCostUsd.Value!.Value, 12);
            var persisted = RendererPerfReportJson.Parse(File.ReadAllBytes(stem + ".partial.json"));
            Assert.Equal("measurement failed", persisted.Publication.OriginalFailure);
            Assert.Equal(outcome.Report.Publication.Errors.AsEnumerable(), persisted.Publication.Errors.AsEnumerable());
            Assert.Equal(outcome.Report.Publication.ElapsedMilliseconds, persisted.Publication.ElapsedMilliseconds);
            Assert.Equal(outcome.Report.Publication.EstimatedCostUsd, persisted.Publication.EstimatedCostUsd);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
    // #1547: Keep a complete accounting snapshot when deadline recovery cannot replace the partial.
    [Fact]
    public void Failed_partial_keeps_coherent_accounting_when_the_update_and_recovery_fail()
    {
        using var fixture = new RendererPerfCompilerPublicationFixture();
        var input = fixture.Report with { Publication = fixture.Report.Publication with { OriginalFailure = "measurement failed" } };
        string stem = Path.Combine(fixture.Directory, input.Manifest.UnitId);
        string temporary = stem + ".partial.json.tmp";
        bool expired = false;
        var budget = fixture.Budget with
        {
            Clock = new(() => expired ? fixture.Budget.Deadline
                : File.Exists(stem + ".partial.md") ? fixture.Now + 5000 : fixture.Now, fixture.Budget.Clock.Frequency),
            Boundary = stage =>
            {
                if (expired || !File.Exists(stem + ".partial.md")) return;
                if (stage is not ("renderer.publication.slot.after" or "renderer.publication.rename.before")) return;
                expired = true;
                if (File.Exists(temporary)) File.Delete(temporary);
                Directory.CreateDirectory(temporary);
            }
        };
        var result = RendererPerfPublicationWriter.Files(fixture.Directory).Publish(input, fixture.Inventory, budget);
        Assert.True(expired);
        Assert.Equal(2, result.Failures.Length);
        var timeout = Assert.IsType<RendererPerfUnitBudgetTimeoutException>(result.Failures[0].Error);
        Assert.Equal(RendererPerfUnitBudgetRefusal.DeadlineReached, timeout.Reason);
        Assert.Equal("renderer.publication.recovery", result.Failures[1].Stage);
        var persisted = RendererPerfReportJson.Parse(File.ReadAllBytes(stem + ".partial.json"));
        Assert.Equal("measurement failed", persisted.Publication.OriginalFailure);
        Assert.Equal(0d, persisted.Publication.ElapsedMilliseconds.Value);
        Assert.Equal(0d, persisted.Publication.EstimatedCostUsd.Value);
    }
    [Fact]
    public void Previously_omitted_original_text_keeps_exact_required_bytes()
    {
        using var fixture = new RendererPerfColdLifecycleFixture();
        string directory = Path.Combine(Path.GetTempPath(), "renderer-omitted-cause-" + Guid.NewGuid().ToString("N"));
        var original = new InvalidOperationException(new string('\u0001', 3000000));
        fixture.Boundary = stage => { if (stage == "frame") throw original; };
        try
        {
            var outcome = fixture.Run(RendererPerfPublicationWriter.Files(directory));
            Assert.Single(outcome.Failures); Assert.Same(original, outcome.Failures[0].Error);
            Assert.NotNull(outcome.Report);
            Assert.False(outcome.Report.Publication.FailureText.Complete);
            Assert.Equal(1, outcome.Report.Publication.FailureText.OmittedFailures);
            Assert.Equal(18000004, outcome.Report.Publication.FailureText.RequiredUtf8Bytes);
            Assert.Equal(RendererPerfFailureText.OmittedOriginal, outcome.Report.Publication.OriginalFailure);
            Assert.Equal(0, fixture.Diagnostic.CompilerReads);
        }
        finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
    }

}
