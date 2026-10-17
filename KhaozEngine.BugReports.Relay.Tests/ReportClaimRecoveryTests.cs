using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class ReportClaimRecoveryTests
{
    const string Id = RelayHarness.Id;
    static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task CreateIssue_HolderGitHubFailure_RedeliveryCreatesTheIssue()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        var failure = new IOException("GitHub unavailable");
        harness.GitHub.OnCreate = () => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => WorkerFixtures.ProcessAsync(harness)));
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/issue-" + Id + "-0.released", Ct));

        harness.GitHub.OnCreate = null;
        await WorkerFixtures.ProcessAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);

        Assert.Single(harness.GitHub.Issues);
        Assert.Equal(7, (await WorkerFixtures.RecordAsync(harness)).IssueNumber);
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/issue-" + Id + "-1.json", Ct));
    }

    [Fact]
    public async Task CreateIssue_ActiveLease_Throws_Busy_NotAcknowledged()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.SeedClaimAsync(harness, "issue", 0, harness.Clock.GetUtcNow());

        var error = await Assert.ThrowsAsync<ReportClaimBusyException>(() => WorkerFixtures.ProcessAsync(harness));

        Assert.Equal(Id, error.ReportId);
        Assert.Empty(harness.GitHub.Issues);
        Assert.Null((await WorkerFixtures.RecordAsync(harness)).IssueNumber);
        Assert.False(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/issue-" + Id + "-1.json", Ct));
    }

    [Fact]
    public async Task CreateIssue_CrashWithoutRelease_TakenOverAfterLease()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.SeedClaimAsync(harness, "issue", 0, harness.Clock.GetUtcNow());
        harness.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<ReportClaimBusyException>(() => WorkerFixtures.ProcessAsync(harness));
        Assert.Empty(harness.GitHub.Issues);
        Assert.False(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/issue-" + Id + "-0.released", Ct));

        harness.Clock.Advance(TimeSpan.FromTicks(1));
        await WorkerFixtures.ProcessAsync(harness);

        Assert.Single(harness.GitHub.Issues);
        Assert.Equal(7, (await WorkerFixtures.RecordAsync(harness)).IssueNumber);
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/issue-" + Id + "-1.json", Ct));
    }

    [Fact]
    public async Task AppendSnapshot_HolderFailure_RedeliveryPostsOneComment()
    {
        var harness = new RelayHarness();
        await PrepareAsync(harness, ReportWorkKind.AppendServerSnapshot);
        var failure = new IOException("GitHub unavailable");
        harness.GitHub.OnComment = () => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot)));
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/comment-" + Id + "-0.released", Ct));

        harness.GitHub.OnComment = null;
        await WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot);
        await WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot);

        Assert.Equal(7, Assert.Single(harness.GitHub.Comments).IssueNumber);
        Assert.Equal(ServerSnapshotState.Commented, (await WorkerFixtures.RecordAsync(harness)).ServerSnapshot);
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/comment-" + Id + "-1.json", Ct));
    }

    [Fact]
    public async Task AppendSnapshot_ActiveLease_Throws_Busy_NotAcknowledged()
    {
        var harness = new RelayHarness();
        await PrepareAsync(harness, ReportWorkKind.AppendServerSnapshot);
        await WorkerFixtures.SeedClaimAsync(harness, "comment", 0, harness.Clock.GetUtcNow());

        var error = await Assert.ThrowsAsync<ReportClaimBusyException>(() => WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot));

        Assert.Equal(Id, error.ReportId);
        Assert.Empty(harness.GitHub.Comments);
        Assert.Equal(ServerSnapshotState.None, (await WorkerFixtures.RecordAsync(harness)).ServerSnapshot);
    }

    [Theory]
    [InlineData((int)ServerSnapshotState.InBody)]
    [InlineData((int)ServerSnapshotState.Commented)]
    public async Task AppendSnapshot_AlreadyIncludedWithoutIssueNumber_Returns(int state)
    {
        var harness = new RelayHarness();
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        await harness.Records.SaveReportAsync(record with { ServerSnapshot = (ServerSnapshotState)state }, Ct);

        await WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot);

        Assert.Empty(harness.GitHub.Comments);
        Assert.False(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/comment-" + Id + "-0.json", Ct));
    }

    [Theory]
    [InlineData(ReportWorkKind.CreateIssue, "issue", "7")]
    [InlineData(ReportWorkKind.AppendServerSnapshot, "comment", "commented")]
    public async Task Worker_RecordSaveFailureAfterGitHubSuccess_RedeliveryReconcilesWithoutSecondEffect(
        ReportWorkKind kind, string claimKind, string result)
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        await PrepareAsync(harness, kind);
        var failure = new IOException("Record unavailable");
        store.BeforeWrite = path => path.StartsWith("records/", StringComparison.Ordinal) ? throw failure : Task.CompletedTask;

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => WorkerFixtures.ProcessAsync(harness, kind)));
        Assert.False(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/" + claimKind + "-" + Id + "-0.released", Ct));
        Assert.Equal(result, await ClaimTextAsync(harness, "claims/" + claimKind + "-" + Id + "-0.done"));

        store.BeforeWrite = null;
        await WorkerFixtures.ProcessAsync(harness, kind);
        await WorkerFixtures.ProcessAsync(harness, kind);

        ReportRecord record = await WorkerFixtures.RecordAsync(harness);
        Assert.Equal(7, record.IssueNumber);
        if (kind == ReportWorkKind.CreateIssue)
        {
            Assert.Single(harness.GitHub.Issues);
            Assert.Empty(harness.GitHub.Comments);
        }
        else
        {
            Assert.Single(harness.GitHub.Comments);
            Assert.Equal(ServerSnapshotState.Commented, record.ServerSnapshot);
        }
        Assert.False(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/" + claimKind + "-" + Id + "-1.json", Ct));
    }

    [Theory]
    [InlineData(ReportWorkKind.CreateIssue, "issue")]
    [InlineData(ReportWorkKind.AppendServerSnapshot, "comment")]
    public async Task Worker_ResultMarkerWriteFailure_RethrowsWithoutReleaseMarker(ReportWorkKind kind, string claimKind)
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        await PrepareAsync(harness, kind);
        var failure = new IOException("Marker unavailable");
        store.BeforeWrite = path => path.EndsWith(".done", StringComparison.Ordinal) ? throw failure : Task.CompletedTask;

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => WorkerFixtures.ProcessAsync(harness, kind)));

        Assert.False(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/" + claimKind + "-" + Id + "-0.released", Ct));
        Assert.False(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/" + claimKind + "-" + Id + "-0.done", Ct));
        Assert.Equal(kind == ReportWorkKind.CreateIssue ? 1 : 0, harness.GitHub.Issues.Count);
        Assert.Equal(kind == ReportWorkKind.CreateIssue ? 0 : 1, harness.GitHub.Comments.Count);
        store.BeforeWrite = null;
        await Assert.ThrowsAsync<ReportClaimBusyException>(() => WorkerFixtures.ProcessAsync(harness, kind));
        Assert.Equal(kind == ReportWorkKind.CreateIssue ? 1 : 0, harness.GitHub.Issues.Count);
        Assert.Equal(kind == ReportWorkKind.CreateIssue ? 0 : 1, harness.GitHub.Comments.Count);
    }

    [Fact]
    public async Task CreateIssue_FailureBeforeGitHub_ReleasesClaim()
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        await WorkerFixtures.SendAsync(harness);
        await WorkerFixtures.AcceptAsync(harness);
        var failure = new IOException("Record unavailable");
        store.BeforeWrite = path => path.StartsWith("records/", StringComparison.Ordinal) ? throw failure : Task.CompletedTask;

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => WorkerFixtures.ProcessAsync(harness)));
        Assert.Empty(harness.GitHub.Issues);
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/issue-" + Id + "-0.released", Ct));

        store.BeforeWrite = null;
        await WorkerFixtures.ProcessAsync(harness);

        Assert.Single(harness.GitHub.Issues);
        ReportRecord record = await WorkerFixtures.RecordAsync(harness);
        Assert.Equal(7, record.IssueNumber);
        Assert.Equal(ServerSnapshotState.InBody, record.ServerSnapshot);
    }

    [Theory]
    [InlineData(ReportWorkKind.CreateIssue, "issue")]
    [InlineData(ReportWorkKind.AppendServerSnapshot, "comment")]
    public async Task Worker_CancelledHolder_ReleasesClaim(ReportWorkKind kind, string claimKind)
    {
        var harness = new RelayHarness();
        await PrepareAsync(harness, kind);
        using var cancellation = new CancellationTokenSource();
        Task Cancel()
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
        harness.GitHub.OnCreate = Cancel;
        harness.GitHub.OnComment = Cancel;

        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.Relay.ProcessWorkAsync(new ReportWorkItem(kind, Id), cancellation.Token));
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/" + claimKind + "-" + Id + "-0.released", Ct));

        harness.GitHub.OnCreate = null;
        harness.GitHub.OnComment = null;
        await WorkerFixtures.ProcessAsync(harness, kind);
        if (kind == ReportWorkKind.CreateIssue) Assert.Single(harness.GitHub.Issues);
        else Assert.Single(harness.GitHub.Comments);
    }

    [Theory]
    [InlineData(ReportWorkKind.CreateIssue, "issue", 63)]
    [InlineData(ReportWorkKind.CreateIssue, "issue", 64)]
    [InlineData(ReportWorkKind.AppendServerSnapshot, "comment", 63)]
    [InlineData(ReportWorkKind.AppendServerSnapshot, "comment", 64)]
    public async Task Claims_CapIs64(ReportWorkKind kind, string claimKind, int existingClaims)
    {
        var harness = new RelayHarness();
        await PrepareAsync(harness, kind);
        for (int epoch = 0; epoch < existingClaims; epoch++)
            await WorkerFixtures.SeedClaimAsync(harness, claimKind, epoch, harness.Clock.GetUtcNow().AddMinutes(-11));

        if (existingClaims == 64)
        {
            await Assert.ThrowsAsync<ReportClaimBusyException>(() => WorkerFixtures.ProcessAsync(harness, kind));
            Assert.Empty(harness.GitHub.Issues);
            Assert.Empty(harness.GitHub.Comments);
        }
        else
        {
            await WorkerFixtures.ProcessAsync(harness, kind);
            if (kind == ReportWorkKind.CreateIssue) Assert.Single(harness.GitHub.Issues);
            else Assert.Single(harness.GitHub.Comments);
            Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/" + claimKind + "-" + Id + "-63.json", Ct));
        }
        Assert.False(await harness.Media.ExistsAsync(ReportArea.Pending, "claims/" + claimKind + "-" + Id + "-64.json", Ct));
    }

    static async Task<string> ClaimTextAsync(RelayHarness harness, string path)
    {
        ReadOnlyMemory<byte>? bytes = await harness.Media.GetAsync(ReportArea.Pending, path, 1024, Ct);
        Assert.NotNull(bytes);
        return System.Text.Encoding.UTF8.GetString(bytes.Value.Span);
    }

    static async Task PrepareAsync(RelayHarness harness, ReportWorkKind kind)
    {
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        if (kind != ReportWorkKind.AppendServerSnapshot) return;
        await harness.Records.SaveReportAsync(record with { IssueNumber = 7 }, Ct);
        await WorkerFixtures.SendAsync(harness);
    }
}
