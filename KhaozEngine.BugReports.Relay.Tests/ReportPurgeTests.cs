using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Diagnostics;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class ReportPurgeTests
{
    const string Id = RelayHarness.Id;
    const string Reference = RelayHarness.Reference;
    static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Purge_DeletesPrivateFilesAndKeepsTombstoneAndIndex()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        await harness.Records.PutPendingSnapshotAsync(Id, "{}"u8.ToArray(), Ct);
        var claims = new WorkClaim(harness.Media, harness.Clock);
        int epoch = (await claims.TryAcquireAsync(ReportWorkKind.AppendServerSnapshot, Id, Ct))!.Value;
        await claims.CompleteAsync(ReportWorkKind.AppendServerSnapshot, Id, epoch, "commented");
        await claims.ReleaseAsync(ReportWorkKind.AppendServerSnapshot, Id, epoch);
        await harness.Relay.RevokeReportAsync(Id, Ct);

        await AdvanceAndDrainAsync(harness);

        AssertPurged(harness);
        Assert.Empty(harness.Media.Paths(ReportArea.Public));
        Assert.Equal(2, harness.GitHub.Updates.Count);
        Assert.Equal(0, harness.Work.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Purge_DeletesLatePublicWrite(bool acceptedOnly)
    {
        var harness = new RelayHarness();
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        await harness.Relay.RevokeReportAsync(Id, Ct);
        if (acceptedOnly) await harness.Media.DeleteAsync(ReportArea.Pending, ReportLayout.ReportRecord(Id), Ct);
        string late = record.Folder + "/server.json";
        string unrelated = record.Folder + "-other/server.json";
        await harness.Media.PutAsync(ReportArea.Public, late, "{}"u8.ToArray(), ReportLayout.HeadersFor(late), false, Ct);
        await harness.Media.PutAsync(ReportArea.Public, unrelated, "{}"u8.ToArray(), ReportLayout.HeadersFor(unrelated), false, Ct);

        await AdvanceAndDrainAsync(harness);

        Assert.Equal(new[] { unrelated }, harness.Media.Paths(ReportArea.Public));
        AssertPurged(harness);
    }

    [Fact]
    public async Task Purge_RedactsIssueCreatedInsideTheLease()
    {
        var harness = new RelayHarness();
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        await harness.Relay.RevokeReportAsync(Id, Ct);
        await harness.Records.SaveReportAsync(record with { IssueNumber = 9 }, Ct);

        await AdvanceAndDrainAsync(harness);

        var update = Assert.Single(harness.GitHub.Updates);
        Assert.Equal(9, update.IssueNumber);
        Assert.Equal("Revoked bug report R-YMWQV0", update.Update.Title);
        Assert.Equal("This report was revoked at the reporter's request. Its files and private record are being deleted.", update.Update.Body);
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Warn
            && entry.Message.Contains(" 9 ", StringComparison.Ordinal) && entry.Message.Contains(Id, StringComparison.Ordinal));
        AssertPurged(harness);
    }

    [Fact]
    public async Task Purge_ReloadsIssueAfterPublicCleanup()
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        await harness.Relay.RevokeReportAsync(Id, Ct);
        string late = record.Folder + "/server.json";
        await harness.Media.PutAsync(ReportArea.Public, late, "{}"u8.ToArray(), ReportLayout.HeadersFor(late), false, Ct);
        int reads = 0;
        store.BeforeRead = async path =>
        {
            if (path != ReportLayout.ReportRecord(Id) || ++reads != 2) return;
            Assert.Empty(harness.Media.Paths(ReportArea.Public));
            await harness.Records.SaveReportAsync(record with { IssueNumber = 9 }, Ct);
        };

        await AdvanceAndDrainAsync(harness);

        Assert.Equal(9, Assert.Single(harness.GitHub.Updates).IssueNumber);
        AssertPurged(harness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Purge_FindsIssueNumberInClaimMarker(bool lateMarker)
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        var claims = new WorkClaim(harness.Media, harness.Clock);
        int epoch = (await claims.TryAcquireAsync(ReportWorkKind.CreateIssue, Id, Ct))!.Value;
        if (!lateMarker) await claims.CompleteAsync(ReportWorkKind.CreateIssue, Id, epoch, "9");
        await harness.Relay.RevokeReportAsync(Id, Ct);
        if (lateMarker) await claims.CompleteAsync(ReportWorkKind.CreateIssue, Id, epoch, "9");

        await AdvanceAndDrainAsync(harness);

        Assert.Equal(lateMarker ? 1 : 2, harness.GitHub.Updates.Count);
        Assert.All(harness.GitHub.Updates, update => Assert.Equal(9, update.IssueNumber));
        AssertPurged(harness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Purge_RedactionFails_ThrowsAndKeepsPrivateFiles(bool apiFailure)
    {
        var harness = new RelayHarness();
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        await harness.Records.PutPendingSnapshotAsync(Id, "{}"u8.ToArray(), Ct);
        await harness.Relay.RevokeReportAsync(Id, Ct);
        string late = record.Folder + "/server.json";
        await harness.Media.PutAsync(ReportArea.Public, late, "{}"u8.ToArray(), ReportLayout.HeadersFor(late), false, Ct);
        string[] pending = harness.Media.Paths(ReportArea.Pending).ToArray();
        Exception failure = apiFailure ? new GitHubApiException(503, false, null, "GitHub unavailable")
            : new HttpRequestException("GitHub unavailable");
        harness.GitHub.OnUpdate = _ =>
        {
            Assert.Empty(harness.Media.Paths(ReportArea.Public));
            Assert.Equal(pending, harness.Media.Paths(ReportArea.Pending));
            throw failure;
        };
        harness.Clock.Advance(TimeSpan.FromMinutes(12));
        Assert.True(harness.Work.TryDequeue(out ReportWorkItem? item));

        Assert.Same(failure, await Record.ExceptionAsync(() => harness.Relay.ProcessWorkAsync(item, Ct)));

        Assert.Equal(pending, harness.Media.Paths(ReportArea.Pending));
        harness.GitHub.OnUpdate = null;
        await harness.Work.EnqueueAsync(item, Ct);
        await WorkerFixtures.DrainAsync(harness);
        AssertPurged(harness);
    }

    [Theory]
    [InlineData(GitHubIssueUpdateOutcome.Gone)]
    [InlineData(GitHubIssueUpdateOutcome.Moved)]
    public async Task Purge_GoneOrMoved_Completes(GitHubIssueUpdateOutcome outcome)
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        await harness.Relay.RevokeReportAsync(Id, Ct);
        harness.GitHub.OnUpdate = _ => Task.FromResult(new GitHubIssueUpdateResult(outcome, null));

        await AdvanceAndDrainAsync(harness);

        AssertPurged(harness);
        Assert.Equal(2, harness.GitHub.Updates.Count);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level == LogLevel.Warn);
    }

    [Fact]
    public async Task Purge_WithoutTombstone_ChangesNothing()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        string[] pending = harness.Media.Paths(ReportArea.Pending).ToArray();
        string[] published = harness.Media.Paths(ReportArea.Public).ToArray();
        await harness.Work.EnqueueAsync(new ReportWorkItem(ReportWorkKind.PurgeRevoked, Id), Ct);

        await WorkerFixtures.DrainAsync(harness);

        Assert.Equal(pending, harness.Media.Paths(ReportArea.Pending));
        Assert.Equal(published, harness.Media.Paths(ReportArea.Public));
        Assert.Empty(harness.GitHub.Updates);
    }

    [Fact]
    public async Task ConcurrentRevokes_TwoPurges_AreSafe()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        harness.GitHub.OnUpdate = async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) await releaseFirst.Task;
            else releaseFirst.SetResult();
            return new GitHubIssueUpdateResult(GitHubIssueUpdateOutcome.Updated, null);
        };
        Task<ReportRevocationResult> first = harness.Relay.RevokeReportAsync(Id, Ct);
        Assert.False(first.IsCompleted);
        byte[] tombstone = (await harness.Media.GetAsync(ReportArea.Pending, ReportLayout.Revoked(Id), 4096, Ct))!.Value.ToArray();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await Task.WhenAll(first, harness.Relay.RevokeReportAsync(Reference, Ct));
        Assert.Equal(2, harness.Work.Count);
        harness.GitHub.OnUpdate = null;
        harness.Clock.Advance(TimeSpan.FromMinutes(12));
        Assert.True(harness.Work.TryDequeue(out ReportWorkItem? firstPurge));

        await harness.Relay.ProcessWorkAsync(firstPurge, Ct);
        AssertPurged(harness);
        string[] pending = harness.Media.Paths(ReportArea.Pending).ToArray();
        await WorkerFixtures.DrainAsync(harness);

        Assert.Equal(pending, harness.Media.Paths(ReportArea.Pending));
        Assert.Equal(tombstone, (await harness.Media.GetAsync(ReportArea.Pending, ReportLayout.Revoked(Id), 4096, Ct))!.Value.ToArray());
        Assert.Equal(3, harness.GitHub.Updates.Count);
        Assert.Equal(0, harness.Work.Count);
    }

    [Fact]
    public async Task Purge_OverlappingDeliveries_AreSafe()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        await harness.Relay.RevokeReportAsync(Id, Ct);
        await harness.Relay.RevokeReportAsync(Id, Ct);
        harness.Clock.Advance(TimeSpan.FromMinutes(12));
        Assert.True(harness.Work.TryDequeue(out ReportWorkItem? first));
        Assert.True(harness.Work.TryDequeue(out ReportWorkItem? second));
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        harness.GitHub.OnUpdate = async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) await releaseFirst.Task;
            return new GitHubIssueUpdateResult(GitHubIssueUpdateOutcome.Updated, null);
        };

        Task firstPurge = harness.Relay.ProcessWorkAsync(first, Ct);
        Assert.False(firstPurge.IsCompleted);
        await harness.Relay.ProcessWorkAsync(second, Ct);
        releaseFirst.SetResult();
        await firstPurge;

        Assert.Equal(4, harness.GitHub.Updates.Count);
        AssertPurged(harness);
        Assert.Equal(0, harness.Work.Count);
    }

    [Theory]
    [InlineData(Id)]
    [InlineData(Reference)]
    public async Task Revoke_AfterPurge_AnswersAlreadyRevoked(string input)
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await harness.Relay.RevokeReportAsync(Id, Ct);
        await AdvanceAndDrainAsync(harness);

        ReportRevocationResult result = await harness.Relay.RevokeReportAsync(input, Ct);

        Assert.Equal(ReportRevocationStatus.AlreadyRevoked, result.Status);
        Assert.Equal(Id, result.ReportId);
        Assert.Equal(0, harness.Work.Count);
        AssertPurged(harness);
    }

    static async Task AdvanceAndDrainAsync(RelayHarness harness)
    {
        harness.Clock.Advance(TimeSpan.FromMinutes(12));
        await WorkerFixtures.DrainAsync(harness);
    }

    static void AssertPurged(RelayHarness harness) => Assert.Equal(new[]
    {
        "references/r-ymwqv0/" + Id + ".json",
        "revoked/" + Id + ".json",
    }, harness.Media.Paths(ReportArea.Pending));
}
