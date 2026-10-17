using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Diagnostics;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class ReportWorkerTests
{
    const string Id = RelayHarness.Id;
    static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task CreateIssue_ComposesAndRecordsIssueNumber()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        GitHubIssueDraft issue = Assert.Single(harness.GitHub.Issues);
        Assert.Equal("[Grimhollow 0.12.3] Fell through the floor", issue.Title);
        Assert.Contains("| Account | `discord:42` |", issue.Body, StringComparison.Ordinal);
        Assert.Contains("tick 42", issue.Body, StringComparison.Ordinal);
        Assert.Contains("clip/000.jpg", issue.Body, StringComparison.Ordinal);
        Assert.Equal(new[] { "kind/bug" }, issue.Labels);
        Assert.Equal(7, (await WorkerFixtures.RecordAsync(harness)).IssueNumber);
    }

    [Fact]
    public async Task CreateIssue_Redelivered_IsNoOp()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        harness.Clock.Advance(TimeSpan.FromDays(1));
        await WorkerFixtures.ProcessAsync(harness);
        Assert.Single(harness.GitHub.Issues);
    }

    [Fact]
    public async Task CreateIssue_UnknownReport_ReturnsAndLogsError()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.ProcessAsync(harness);
        Assert.Empty(harness.GitHub.Issues);
        var log = Assert.Single(harness.Logger.Entries);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.Contains(Id, log.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReportWorkKind.CreateIssue)]
    [InlineData(ReportWorkKind.AppendServerSnapshot)]
    public async Task Worker_AcceptedWithoutPrivateRecord_LogsAndReturns(ReportWorkKind kind)
    {
        var harness = new RelayHarness();
        await harness.Records.TryCreateAcceptedAsync(Id, new AcceptedReport(IssueFixtures.Folder, "discord:42", harness.Clock.GetUtcNow()), Ct);
        await WorkerFixtures.ProcessAsync(harness, kind);
        Assert.Empty(harness.GitHub.Issues);
        Assert.Empty(harness.GitHub.Comments);
        var log = Assert.Single(harness.Logger.Entries);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.Contains(Id, log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateIssue_PendingSnapshotMatches_InBodyAndServerJsonAndViewerRerendered()
    {
        var harness = new RelayHarness(o => o with { IssueFacts = new[] { new IssueFact("Server tick", "server", "tick") } });
        await WorkerFixtures.SendAsync(harness);
        await WorkerFixtures.AcceptAsync(harness);
        string before = await WorkerFixtures.PublicTextAsync(harness, "viewer.html");
        await WorkerFixtures.ProcessAsync(harness);
        ReportRecord record = await WorkerFixtures.RecordAsync(harness);
        Assert.Equal(ServerSnapshotState.InBody, record.ServerSnapshot);
        Assert.Equal(7, record.IssueNumber);
        Assert.Contains("| Server tick | 42 |", Assert.Single(harness.GitHub.Issues).Body, StringComparison.Ordinal);
        Assert.Contains("server.json", harness.GitHub.Issues[0].Body, StringComparison.Ordinal);
        Assert.Empty(harness.GitHub.Comments);
        Assert.NotEqual(before, await WorkerFixtures.PublicTextAsync(harness, "viewer.html"));
        using JsonDocument viewer = await WorkerFixtures.ViewerAsync(harness);
        Assert.Equal(JsonValueKind.Null, viewer.RootElement.GetProperty("reporter").ValueKind);
        Assert.Contains("tick", viewer.RootElement.GetProperty("server").GetString(), StringComparison.Ordinal);
        Assert.Equal(new ReportBlobHeaders("application/json; charset=utf-8", "no-cache"),
            harness.Media.HeadersOf(ReportArea.Public, record.Folder + "/server.json"));
    }

    [Theory]
    [InlineData(ReportWorkKind.CreateIssue)]
    [InlineData(ReportWorkKind.AppendServerSnapshot)]
    public async Task Worker_SnapshotIsReserializedBeforePublicWrite(ReportWorkKind kind)
    {
        var harness = new RelayHarness();
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        if (kind == ReportWorkKind.AppendServerSnapshot)
            await harness.Records.SaveReportAsync(record with { IssueNumber = 7 }, Ct);
        string raw = "  " + Encoding.UTF8.GetString(BugReportJson.SerializeServerSnapshot(WorkerFixtures.Snapshot())) + " \n";
        await harness.Records.PutPendingSnapshotAsync(Id, Encoding.UTF8.GetBytes(raw), Ct);
        await WorkerFixtures.ProcessAsync(harness, kind);
        Assert.Equal(Encoding.UTF8.GetString(PublicServerSnapshot.Serialize(WorkerFixtures.Snapshot())),
            await WorkerFixtures.PublicTextAsync(harness, "server.json"));
    }

    [Fact]
    public async Task CreateIssue_PendingSnapshotSubjectMismatch_IgnoredAndWarned()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.SendAsync(harness, WorkerFixtures.Snapshot(subject: "discord:7"));
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        Assert.Equal(ServerSnapshotState.None, (await WorkerFixtures.RecordAsync(harness)).ServerSnapshot);
        Assert.False(await harness.Media.ExistsAsync(ReportArea.Public, record.Folder + "/server.json", Ct));
        Assert.DoesNotContain("server.json", Assert.Single(harness.GitHub.Issues).Body, StringComparison.Ordinal);
        AssertPrivateWarning(harness);
    }

    [Theory]
    [InlineData(ReportWorkKind.CreateIssue, "../secret")]
    [InlineData(ReportWorkKind.CreateIssue, "Server")]
    [InlineData(ReportWorkKind.AppendServerSnapshot, "../secret")]
    [InlineData(ReportWorkKind.AppendServerSnapshot, "")]
    public async Task Worker_InvalidSnapshotSectionKey_DropsSnapshot(ReportWorkKind kind, string key)
    {
        var harness = new RelayHarness();
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        if (kind == ReportWorkKind.AppendServerSnapshot)
            await harness.Records.SaveReportAsync(record with { IssueNumber = 7 }, Ct);
        await harness.Records.PutPendingSnapshotAsync(Id, BugReportJson.SerializeServerSnapshot(WorkerFixtures.Snapshot(key: key)), Ct);
        await WorkerFixtures.ProcessAsync(harness, kind);
        Assert.False(await harness.Media.ExistsAsync(ReportArea.Public, record.Folder + "/server.json", Ct));
        Assert.Empty(harness.GitHub.Comments);
        Assert.Equal(ServerSnapshotState.None, (await WorkerFixtures.RecordAsync(harness)).ServerSnapshot);
        AssertPrivateWarning(harness);
    }

    [Fact]
    public async Task CreateIssue_RateLimited_Throws_IssueNumberStaysNull()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        var failure = new GitHubApiException(429, true, TimeSpan.FromMinutes(1), "Rate limited");
        harness.GitHub.OnCreate = () => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<GitHubApiException>(() => WorkerFixtures.ProcessAsync(harness)));
        Assert.Null((await WorkerFixtures.RecordAsync(harness)).IssueNumber);
    }

    [Fact]
    public async Task AppendSnapshot_BeforeIssue_ThrowsReportNotReady()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        var error = await Assert.ThrowsAsync<ReportNotReadyException>(() => WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot));
        Assert.Equal(Id, error.ReportId);
    }

    [Fact]
    public async Task AppendSnapshot_AfterIssue_PostsOneComment_StateCommented()
    {
        var harness = await ReadyForAppendAsync();
        await WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot);
        var comment = Assert.Single(harness.GitHub.Comments);
        Assert.Equal(7, comment.IssueNumber);
        Assert.Contains("server.json", comment.Body, StringComparison.Ordinal);
        Assert.Equal(ServerSnapshotState.Commented, (await WorkerFixtures.RecordAsync(harness)).ServerSnapshot);
        using JsonDocument viewer = await WorkerFixtures.ViewerAsync(harness);
        Assert.Equal(JsonValueKind.Null, viewer.RootElement.GetProperty("reporter").ValueKind);
        Assert.Contains("tick", viewer.RootElement.GetProperty("server").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppendSnapshot_AlreadyInBody_IsNoOp()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.SendAsync(harness);
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        await WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot);
        Assert.Empty(harness.GitHub.Comments);
        Assert.Equal(ServerSnapshotState.InBody, (await WorkerFixtures.RecordAsync(harness)).ServerSnapshot);
    }

    [Fact]
    public async Task AppendSnapshot_Redelivered_AfterCommented_IsNoOp()
    {
        var harness = await ReadyForAppendAsync();
        await WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot);
        harness.Clock.Advance(TimeSpan.FromDays(1));
        await WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot);
        Assert.Single(harness.GitHub.Comments);
    }

    [Fact]
    public async Task AppendSnapshot_UnknownReport_Returns()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot);
        Assert.Empty(harness.GitHub.Comments);
    }

    [Fact]
    public async Task AppendSnapshot_MissingSnapshot_WarnsAndReturns()
    {
        var harness = new RelayHarness();
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        await harness.Records.SaveReportAsync(record with { IssueNumber = 7 }, Ct);
        await WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot);
        Assert.Empty(harness.GitHub.Comments);
        AssertPrivateWarning(harness);
    }

    [Fact]
    public async Task AppendSnapshot_SubjectMismatch_WarnsAndReturns()
    {
        var harness = new RelayHarness();
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        await harness.Records.SaveReportAsync(record with { IssueNumber = 7 }, Ct);
        await harness.Records.PutPendingSnapshotAsync(Id, BugReportJson.SerializeServerSnapshot(WorkerFixtures.Snapshot(subject: "discord:7")), Ct);
        await WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot);
        Assert.Empty(harness.GitHub.Comments);
        AssertPrivateWarning(harness);
    }

    [Fact]
    public async Task SnapshotRace_ArrivesDuringIssueCreation_EndsInExactlyOneComment()
    {
        var harness = new RelayHarness(o => o with { IssueFacts = new[] { new IssueFact("Server tick", "server", "tick") } });
        await WorkerFixtures.AcceptAsync(harness);
        harness.GitHub.OnCreate = async () => Assert.Equal(202, (await WorkerFixtures.SendAsync(harness)).StatusCode);
        await WorkerFixtures.ProcessAsync(harness);
        await WorkerFixtures.DrainAsync(harness);
        Assert.DoesNotContain("Server tick", Assert.Single(harness.GitHub.Issues).Body, StringComparison.Ordinal);
        Assert.Contains("| Server tick | 42 |", Assert.Single(harness.GitHub.Comments).Body, StringComparison.Ordinal);
        Assert.Equal(ServerSnapshotState.Commented, (await WorkerFixtures.RecordAsync(harness)).ServerSnapshot);
    }

    [Fact]
    public Task CreateIssue_TwoMessagesConcurrently_OneIssue()
        => ConcurrentAsync(ReportWorkKind.CreateIssue, "issue");

    [Fact]
    public Task AppendSnapshot_TwoMessagesConcurrently_OneComment()
        => ConcurrentAsync(ReportWorkKind.AppendServerSnapshot, "comment");

    [Fact]
    public async Task CreateIssue_StaleClaim_IsTakenOver()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.SeedClaimAsync(harness, "issue", 0, harness.Clock.GetUtcNow());
        harness.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<ReportClaimBusyException>(() => WorkerFixtures.ProcessAsync(harness));
        Assert.Empty(harness.GitHub.Issues);
        harness.Clock.Advance(TimeSpan.FromTicks(2));
        await WorkerFixtures.ProcessAsync(harness);
        Assert.Single(harness.GitHub.Issues);
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/issue-" + Id + "-1.json", Ct));
    }

    [Fact]
    public async Task CreateIssue_IntakeIndexRecoveryInterleaving_OneIssue()
    {
        var harness = new RelayHarness();
        using var bundle = new MemoryStream(TestBundles.Valid(Id));
        ReportRecord record = await new ReportBundleStorer(harness.Media, new ReportViewerWriter(harness.Media))
            .StoreAsync(bundle, BundleValidator.Validate(bundle).Manifest!, new ReporterIdentity("discord:42", "Ash", null), harness.Clock.GetUtcNow(), Ct);
        await harness.Records.CreateReportAsync(record, Ct);
        harness.Clock.Advance(TimeSpan.FromMinutes(16));
        harness.GitHub.OnCreate = async () =>
        {
            Assert.Equal(202, (await harness.IntakeAsync()).StatusCode);
            await Assert.ThrowsAsync<ReportClaimBusyException>(() => WorkerFixtures.DrainAsync(harness));
        };
        // Index recovery queues one item, while the owner of that claim has not saved its issue number yet.
        Assert.Equal(202, (await harness.IntakeAsync()).StatusCode);
        await WorkerFixtures.DrainAsync(harness);
        Assert.Single(harness.GitHub.Issues);
        Assert.Equal(7, (await WorkerFixtures.RecordAsync(harness)).IssueNumber);
    }

    [Fact]
    public async Task CreateIssue_RecordReadBeforeAnotherHolderFinishes_DoesNotCreateAgainAfterLease()
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        await WorkerFixtures.AcceptAsync(harness);
        var firstAtClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int probes = 0;
        store.BeforeExists = async path =>
        {
            if (!path.StartsWith("claims/issue-", StringComparison.Ordinal) || Interlocked.Increment(ref probes) != 1) return;
            firstAtClaim.SetResult();
            await release.Task;
        };
        Task heldWorker = WorkerFixtures.ProcessAsync(harness);
        await firstAtClaim.Task;
        try
        {
            await WorkerFixtures.ProcessAsync(harness);
            harness.Clock.Advance(TimeSpan.FromMinutes(11));
        }
        finally
        {
            release.SetResult();
        }
        await heldWorker;
        Assert.Single(harness.GitHub.Issues);
        Assert.Equal(7, (await WorkerFixtures.RecordAsync(harness)).IssueNumber);
    }

    [Fact]
    public async Task AppendSnapshot_StaleClaim_IsTakenOver()
    {
        var harness = await ReadyForAppendAsync();
        await WorkerFixtures.SeedClaimAsync(harness, "comment", 0, harness.Clock.GetUtcNow());
        Assert.Equal(ServerSnapshotState.None, (await WorkerFixtures.RecordAsync(harness)).ServerSnapshot);
        harness.Clock.Advance(TimeSpan.FromMinutes(11));
        await WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot);
        Assert.Single(harness.GitHub.Comments);
        Assert.Equal(ServerSnapshotState.Commented, (await WorkerFixtures.RecordAsync(harness)).ServerSnapshot);
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/comment-" + Id + "-1.json", Ct));
    }

    [Theory]
    [InlineData(ReportWorkKind.CreateIssue, "issue")]
    [InlineData(ReportWorkKind.AppendServerSnapshot, "comment")]
    public async Task Worker_SixteenStaleClaims_AllowsTakeover(ReportWorkKind kind, string claimKind)
    {
        var harness = new RelayHarness();
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        if (kind == ReportWorkKind.AppendServerSnapshot)
        {
            await harness.Records.SaveReportAsync(record with { IssueNumber = 7 }, Ct);
            await WorkerFixtures.SendAsync(harness);
        }
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { claimedAt = harness.Clock.GetUtcNow().AddMinutes(-11) });
        for (int epoch = 0; epoch < 16; epoch++)
        {
            string path = "claims/" + claimKind + "-" + Id + "-" + epoch + ".json";
            await harness.Media.PutAsync(ReportArea.Pending, path, json, ReportLayout.HeadersFor(path), false, Ct);
        }
        await WorkerFixtures.ProcessAsync(harness, kind);
        if (kind == ReportWorkKind.CreateIssue) Assert.Single(harness.GitHub.Issues);
        else Assert.Single(harness.GitHub.Comments);
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/" + claimKind + "-" + Id + "-16.json", Ct));
    }

    [Fact]
    public async Task Worker_ClaimStoreFault_Propagates()
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        await WorkerFixtures.AcceptAsync(harness);
        var failure = new IOException("Claims unavailable");
        store.BeforeExists = _ => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => WorkerFixtures.ProcessAsync(harness)));
        Assert.Empty(harness.GitHub.Issues);
    }

    [Fact]
    public async Task Worker_RecordSaveFault_Propagates()
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        await WorkerFixtures.AcceptAsync(harness);
        var failure = new IOException("Record unavailable");
        store.BeforeWrite = path => path.StartsWith("records/", StringComparison.Ordinal) ? throw failure : Task.CompletedTask;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => WorkerFixtures.ProcessAsync(harness)));
        Assert.Single(harness.GitHub.Issues);
        Assert.Null((await WorkerFixtures.RecordAsync(harness)).IssueNumber);
    }

    static async Task ConcurrentAsync(ReportWorkKind kind, string claimKind)
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        if (kind == ReportWorkKind.AppendServerSnapshot)
        {
            await harness.Records.SaveReportAsync(record with { IssueNumber = 7 }, Ct);
            await WorkerFixtures.SendAsync(harness);
        }
        var bothAtClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrivals = 0;
        store.BeforeWrite = async path =>
        {
            if (path != "claims/" + claimKind + "-" + Id + "-0.json") return;
            if (Interlocked.Increment(ref arrivals) == 2) bothAtClaim.SetResult();
            await bothAtClaim.Task;
        };
        var secondWorker = new BugReportRelay(harness.Options, harness.Authenticator, store, harness.Work,
            harness.Quotas, harness.GitHub, harness.Clock);
        Exception?[] results = await Task.WhenAll(
            Record.ExceptionAsync(() => WorkerFixtures.ProcessAsync(harness, kind)),
            Record.ExceptionAsync(() => secondWorker.ProcessWorkAsync(new ReportWorkItem(kind, Id), Ct)));
        foreach (Exception? error in results)
            if (error is not null) Assert.IsType<ReportClaimBusyException>(error);
        if (kind == ReportWorkKind.CreateIssue) Assert.Single(harness.GitHub.Issues);
        else Assert.Single(harness.GitHub.Comments);
        Assert.Equal(7, (await WorkerFixtures.RecordAsync(harness)).IssueNumber);
        await WorkerFixtures.ProcessAsync(harness, kind);
        if (kind == ReportWorkKind.CreateIssue) Assert.Single(harness.GitHub.Issues);
        else Assert.Single(harness.GitHub.Comments);
    }

    static async Task<RelayHarness> ReadyForAppendAsync()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        await WorkerFixtures.SendAsync(harness);
        return harness;
    }

    static void AssertPrivateWarning(RelayHarness harness)
    {
        var log = Assert.Single(harness.Logger.Entries);
        Assert.Equal(LogLevel.Warn, log.Level);
        Assert.Contains(Id, log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("discord:", log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Ash", log.Message, StringComparison.Ordinal);
    }
}
