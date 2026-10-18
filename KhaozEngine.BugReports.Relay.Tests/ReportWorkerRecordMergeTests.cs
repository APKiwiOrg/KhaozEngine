using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class ReportWorkerRecordMergeTests
{
    [Fact]
    public async Task CreateIssue_FinalSaveAfterReconcileAndLateComment_PreservesCompletedRecord()
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        await WorkerFixtures.AcceptAsync(harness);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.AfterWrite = async path =>
        {
            if (path != "claims/issue-" + RelayHarness.Id + "-0.done") return;
            entered.SetResult();
            await release.Task;
        };

        Task heldWorker = WorkerFixtures.ProcessAsync(harness);
        await entered.Task;
        try
        {
            Assert.Null((await WorkerFixtures.RecordAsync(harness)).IssueNumber);
            await WorkerFixtures.ProcessAsync(harness);
            Assert.Equal(7, (await WorkerFixtures.RecordAsync(harness)).IssueNumber);
            Assert.Equal(202, (await WorkerFixtures.SendAsync(harness)).StatusCode);
            await WorkerFixtures.DrainAsync(harness);
            Assert.Equal(ServerSnapshotState.Commented, (await WorkerFixtures.RecordAsync(harness)).ServerSnapshot);
        }
        finally
        {
            release.SetResult();
        }
        await heldWorker;

        ReportRecord record = await WorkerFixtures.RecordAsync(harness);
        Assert.Equal(7, record.IssueNumber);
        Assert.Equal(ServerSnapshotState.Commented, record.ServerSnapshot);
        Assert.Single(harness.GitHub.Issues);
        Assert.Equal(7, Assert.Single(harness.GitHub.Comments).IssueNumber);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(null, 1)]
    [InlineData(null, 2)]
    [InlineData(41, 0)]
    [InlineData(41, 1)]
    [InlineData(41, 2)]
    public async Task CreateIssue_FinalSave_MergesOnlyMissingIssueAndSkipsUnchanged(int? issueNumber, int snapshotValue)
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        ReportRecord accepted = await WorkerFixtures.AcceptAsync(harness);
        ReportRecord current = accepted with
        {
            Reference = "R-NEW123",
            IssueNumber = issueNumber,
            ServerSnapshot = (ServerSnapshotState)snapshotValue,
        };
        int writes = 0;
        harness.GitHub.OnCreate = async () =>
        {
            await harness.Records.SaveReportAsync(current, CancellationToken.None);
            store.BeforeWrite = path =>
            {
                if (path.StartsWith("records/", StringComparison.Ordinal)) writes++;
                return Task.CompletedTask;
            };
        };

        await WorkerFixtures.ProcessAsync(harness);

        ReportRecord saved = await WorkerFixtures.RecordAsync(harness);
        Assert.Equal(issueNumber ?? 7, saved.IssueNumber);
        Assert.Equal(current.ServerSnapshot, saved.ServerSnapshot);
        Assert.Equal(current.Reference, saved.Reference);
        Assert.Equal(issueNumber is null ? 1 : 0, writes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AppendSnapshot_FinalSave_PreservesIssueAndCompletedSnapshotAndSkipsUnchanged(int snapshotValue)
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        Assert.Equal(202, (await WorkerFixtures.SendAsync(harness)).StatusCode);
        ReportRecord current = (await WorkerFixtures.RecordAsync(harness)) with
        {
            Reference = "R-NEW123",
            IssueNumber = 41,
            ServerSnapshot = (ServerSnapshotState)snapshotValue,
        };
        int writes = 0;
        harness.GitHub.OnComment = async () =>
        {
            await harness.Records.SaveReportAsync(current, CancellationToken.None);
            store.BeforeWrite = path =>
            {
                if (path.StartsWith("records/", StringComparison.Ordinal)) writes++;
                return Task.CompletedTask;
            };
        };

        await WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot);

        ReportRecord saved = await WorkerFixtures.RecordAsync(harness);
        Assert.Equal(41, saved.IssueNumber);
        Assert.Equal(snapshotValue == 0 ? ServerSnapshotState.Commented : current.ServerSnapshot, saved.ServerSnapshot);
        Assert.Equal(current.Reference, saved.Reference);
        Assert.Equal(snapshotValue == 0 ? 1 : 0, writes);
    }
}
