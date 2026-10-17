using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class ServerSnapshotRecoveryTests
{
    const string Id = RelayHarness.Id;
    static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Snapshot_ResendAfterLostEnqueue_RepairsAppend()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        var queue = new FailOnceQueue(harness.Work);
        var relay = new BugReportRelay(harness.Options, harness.Authenticator, harness.Media, queue, harness.Quotas, harness.GitHub, harness.Clock);
        await Assert.ThrowsAsync<IOException>(() => relay.ServerSnapshotIntakeAsync(Id, WorkerFixtures.Request(), Ct));
        Assert.NotNull(await harness.Records.LoadPendingSnapshotAsync(Id, Ct));
        Assert.Equal(0, harness.Work.Count);

        byte[] resend = BugReportJson.SerializeServerSnapshot(WorkerFixtures.Snapshot(tick: 99));
        Assert.Equal(202, (await relay.ServerSnapshotIntakeAsync(Id, WorkerFixtures.Request(resend), Ct)).StatusCode);
        Assert.Equal(202, (await relay.ServerSnapshotIntakeAsync(Id, WorkerFixtures.Request(resend), Ct)).StatusCode);
        Assert.Equal(2, harness.Work.Count);
        await WorkerFixtures.DrainAsync(harness);

        Assert.Single(harness.GitHub.Issues);
        Assert.Single(harness.GitHub.Comments);
        Assert.Equal(ServerSnapshotState.Commented, (await WorkerFixtures.RecordAsync(harness)).ServerSnapshot);
        using var saved = System.Text.Json.JsonDocument.Parse(await WorkerFixtures.PublicTextAsync(harness, "server.json"));
        Assert.Equal(42, saved.RootElement.GetProperty("sections").GetProperty("server").GetProperty("tick").GetInt32());
    }

    [Theory]
    [InlineData(null, (int)ServerSnapshotState.None)]
    [InlineData(7, (int)ServerSnapshotState.InBody)]
    [InlineData(7, (int)ServerSnapshotState.Commented)]
    public async Task Snapshot_ResendWithoutUnappendedIssue_QueuesNothing(int? issueNumber, int state)
    {
        var harness = new RelayHarness();
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        await harness.Records.SaveReportAsync(record with { IssueNumber = issueNumber, ServerSnapshot = (ServerSnapshotState)state }, Ct);
        await harness.Records.PutPendingSnapshotAsync(Id, BugReportJson.SerializeServerSnapshot(WorkerFixtures.Snapshot()), Ct);

        Assert.Equal(202, (await WorkerFixtures.SendAsync(harness)).StatusCode);

        Assert.Equal(0, harness.Work.Count);
    }

    [Fact]
    public async Task Snapshot_LostPutReloadsNewAcceptedIndex_RepairsAppend()
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        await harness.Records.PutPendingSnapshotAsync(Id, BugReportJson.SerializeServerSnapshot(WorkerFixtures.Snapshot()), Ct);
        store.BeforeWrite = async path =>
        {
            if (!path.StartsWith("snapshots/", StringComparison.Ordinal)) return;
            ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
            await harness.Records.SaveReportAsync(record with { IssueNumber = 7 }, Ct);
        };

        Assert.Equal(202, (await WorkerFixtures.SendAsync(harness)).StatusCode);
        Assert.True(harness.Work.TryDequeue(out ReportWorkItem? item));
        Assert.Equal(new ReportWorkItem(ReportWorkKind.AppendServerSnapshot, Id), item);
        Assert.Equal(0, harness.Work.Count);
    }

    sealed class FailOnceQueue(IReportWorkQueue inner) : IReportWorkQueue
    {
        bool failed;

        public Task EnqueueAsync(ReportWorkItem item, CancellationToken cancellationToken)
        {
            if (failed) return inner.EnqueueAsync(item, cancellationToken);
            failed = true;
            throw new IOException("Queue unavailable");
        }
    }
}
