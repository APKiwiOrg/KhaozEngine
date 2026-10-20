using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Candidate_is_never_finalized_and_only_the_returned_report_survives(bool late)
    {
        using var files = new Files();
        using var publication = new CoveragePerfPublication(files.Path, "inline-driver");
        var run = new CoveragePerfFakeRun();
        run.Publisher.OnUnit = candidate =>
        {
            publication.StageCandidate(candidate);
            Assert.False(File.Exists(files.Json));
            Assert.False(File.Exists(files.Markdown));
            if (late) run.Time.Advance(301000);
        };
        var returned = run.Run();
        var published = publication.PublishReturned(returned, run.Time.Clock(), 0);
        var reloaded = CoveragePerfReportJson.Deserialize(File.ReadAllText(files.Json));
        Assert.Equal(returned.Status, reloaded.Status);
        Assert.Equal(returned.Reason, reloaded.Reason);
        Assert.Equal(returned.FinalUnitPublished, reloaded.FinalUnitPublished);
        Assert.Equal(published.Status, reloaded.Status);
        Assert.True(reloaded.ReturnedByRun);
        Assert.Equal(late ? CoveragePerfRunStatus.Failed : CoveragePerfRunStatus.Completed, reloaded.Status);
        Assert.True(new FileInfo(files.Markdown).Length > 0);
    }

    [Fact]
    public void A_deadline_crossed_while_finalizing_cannot_inherit_a_pass()
    {
        using var files = new Files();
        using var publication = new CoveragePerfPublication(files.Path, "inline-driver");
        var run = new CoveragePerfFakeRun();
        var returned = run.Run();
        run.Time.Advance(301000);
        var report = publication.PublishReturned(returned, run.Time.Clock(), 0);
        Assert.Equal(CoveragePerfRunStatus.Failed, report.Status);
        Assert.False(report.FinalUnitPublished);
        Assert.Contains("publication exceeded", report.Reason, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(File.ReadAllText(files.Json));
        Assert.Equal("Failed", json.RootElement.GetProperty("report").GetProperty("status").GetString());
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, report.CpuStatus);
    }

    [Fact]
    public void Existing_reports_and_nonreturned_candidates_are_refused()
    {
        using var files = new Files();
        var run = new CoveragePerfFakeRun();
        var report = run.Run();
        using (var publication = new CoveragePerfPublication(files.Path, "inline-driver"))
        {
            Assert.Throws<InvalidOperationException>(() => publication.PublishReturned(report with { ReturnedByRun = false }, run.Time.Clock(), 0));
            publication.PublishReturned(report, run.Time.Clock(), 0);
        }
        Assert.Throws<IOException>(() => new CoveragePerfPublication(files.Path, "inline-driver"));
        Assert.Empty(Directory.EnumerateFiles(files.Path, "*.tmp"));
    }

    sealed class Files : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "khaozengine-ec-perf-8b", Guid.NewGuid().ToString("N"));
        internal string Json => System.IO.Path.Combine(Path, "inline-driver.json");
        internal string Markdown => System.IO.Path.Combine(Path, "inline-driver.md");
        public void Dispose() => Directory.Delete(Path, true);
    }
}
