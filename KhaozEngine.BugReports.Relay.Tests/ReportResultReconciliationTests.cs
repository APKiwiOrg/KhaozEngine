using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class ReportResultReconciliationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateIssue_ResultScanOverlapsSnapshotInclusion_PreservesInBodyWithoutComment(bool failFinalSave)
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(
            o => o with { IssueFacts = new[] { new IssueFact("Server tick", "server", "tick") } },
            inner => store = new WorkerMediaStore(inner));
        await WorkerFixtures.AcceptAsync(harness);
        Assert.Equal(202, (await WorkerFixtures.SendAsync(harness)).StatusCode);
        var probe = PauseFirstResultProbe(store, "issue");

        Task heldWorker = WorkerFixtures.ProcessAsync(harness);
        await probe.Entered;
        try
        {
            if (failFinalSave)
            {
                int recordWrites = 0;
                store.BeforeWrite = path => path.StartsWith("records/", StringComparison.Ordinal)
                    && ++recordWrites == 2 ? throw new IOException("Final record save unavailable") : Task.CompletedTask;
                await Assert.ThrowsAsync<IOException>(() => WorkerFixtures.ProcessAsync(harness));
                store.BeforeWrite = null;
                Assert.Null((await WorkerFixtures.RecordAsync(harness)).IssueNumber);
            }
            else
            {
                await WorkerFixtures.ProcessAsync(harness);
                Assert.Equal(7, (await WorkerFixtures.RecordAsync(harness)).IssueNumber);
            }
            Assert.Equal(ServerSnapshotState.InBody, (await WorkerFixtures.RecordAsync(harness)).ServerSnapshot);
        }
        finally
        {
            probe.Release();
        }
        await heldWorker;
        Assert.True(harness.Work.TryDequeue(out ReportWorkItem? append));
        Assert.Equal(ReportWorkKind.AppendServerSnapshot, append!.Kind);
        await harness.Relay.ProcessWorkAsync(append, CancellationToken.None);
        Assert.False(harness.Work.TryDequeue(out _));

        ReportRecord record = await WorkerFixtures.RecordAsync(harness);
        Assert.Equal(7, record.IssueNumber);
        Assert.Equal(ServerSnapshotState.InBody, record.ServerSnapshot);
        Assert.Contains("| Server tick | 42 |", Assert.Single(harness.GitHub.Issues).Body, StringComparison.Ordinal);
        Assert.Empty(harness.GitHub.Comments);
    }

    [Fact]
    public async Task AppendSnapshot_ResultScanOverlapsCompletedComment_DoesNotRewriteCompletedRecord()
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        Assert.Equal(202, (await WorkerFixtures.SendAsync(harness)).StatusCode);
        var probe = PauseFirstResultProbe(store, "comment");

        Task heldWorker = WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot);
        await probe.Entered;
        int recordWrites = 0;
        try
        {
            await WorkerFixtures.ProcessAsync(harness, ReportWorkKind.AppendServerSnapshot);
            Assert.Equal(ServerSnapshotState.Commented, (await WorkerFixtures.RecordAsync(harness)).ServerSnapshot);
            store.BeforeWrite = path =>
            {
                if (path.StartsWith("records/", StringComparison.Ordinal)) recordWrites++;
                return Task.CompletedTask;
            };
        }
        finally
        {
            probe.Release();
        }
        await heldWorker;
        await WorkerFixtures.DrainAsync(harness);

        Assert.Equal(0, recordWrites);
        ReportRecord record = await WorkerFixtures.RecordAsync(harness);
        Assert.Equal(7, record.IssueNumber);
        Assert.Equal(ServerSnapshotState.Commented, record.ServerSnapshot);
        Assert.Single(harness.GitHub.Issues);
        var comment = Assert.Single(harness.GitHub.Comments);
        Assert.Equal(7, comment.IssueNumber);
        Assert.Contains("server.json", comment.Body, StringComparison.Ordinal);
    }

    static (Task Entered, Action Release) PauseFirstResultProbe(WorkerMediaStore store, string kind)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string firstClaim = "claims/" + kind + "-" + RelayHarness.Id + "-0.json";
        int probes = 0;
        store.BeforeExists = async path =>
        {
            if (path != firstClaim || Interlocked.Increment(ref probes) != 1) return;
            entered.SetResult();
            await release.Task;
        };
        return (entered.Task, () => release.SetResult());
    }
}
