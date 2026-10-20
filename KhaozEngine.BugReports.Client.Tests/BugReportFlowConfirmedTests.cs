using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

/// <summary>The Send confirmation that releases a game's captured server evidence.</summary>
[Collection("AmbientLocalization")]
public sealed class BugReportFlowConfirmedTests : BugReportFlowHarness
{
    [Fact]
    public async Task Send_raises_ReportConfirmed_once_with_the_captured_id()
    {
        BugReportFlow flow = await SignedIn();
        var confirmed = new List<(string Id, bool OnFrameThread, bool BeforeBuild)>();
        flow.ReportConfirmed += id => confirmed.Add((id, OnFrameThread(), flow.Builds.WaitAll(TimeSpan.Zero)));
        string[] confirmedAtSubmit = [];
        _sink.OnSubmit = () => confirmedAtSubmit = confirmed.Select(entry => entry.Id).ToArray();
        Open(flow);
        string capturedId = Assert.Single(_captured);
        Assert.Empty(confirmed);
        Assert.Empty(_sink.Submissions);

        Assert.True(Send(flow));

        Assert.Equal((capturedId, true, true), Assert.Single(confirmed));
        await Settle(flow);
        Step(flow);
        Assert.Single(confirmed);
        Assert.Equal([capturedId], confirmedAtSubmit);
        Assert.Equal(capturedId, Assert.Single(_sink.Submissions).ReportId);
    }

    [Fact]
    public async Task Cancel_never_raises_ReportConfirmed()
    {
        BugReportFlow flow = await SignedIn();
        var confirmed = new List<string>();
        flow.ReportConfirmed += confirmed.Add;
        Open(flow);
        Step(flow, Frame(text: "Fell through"));

        Assert.True(Step(flow, Frame(pressed: [Key.Escape])));
        await Settle(flow);

        Assert.Empty(confirmed);
        Assert.Empty(_sink.Submissions);
        Assert.Equal(BugReportFlowState.Idle, flow.State);
    }

    [Fact]
    public async Task A_throwing_ReportConfirmed_handler_does_not_stop_the_send()
    {
        BugReportFlow flow = await SignedIn();
        flow.ReportConfirmed += _ => throw new InvalidOperationException("game confirmation hook");
        using IDisposable logs = CaptureLogs();
        Open(flow);

        Assert.True(Send(flow));
        await Settle(flow);

        Assert.Equal(Assert.Single(_captured), Assert.Single(_sink.Submissions).ReportId);
        Assert.Equal(BugReportSubmitStatus.Sent, Assert.Single(_submitted).Status);
        Assert.Contains("ReportConfirmed", Assert.Single(Warnings).Message, StringComparison.Ordinal);
        Assert.Equal(BugReportFlowState.Idle, flow.State);
        Assert.Equal(1, _capture.ThawCount);
    }

    [Fact]
    public async Task Two_overlapping_reports_confirm_their_own_ids()
    {
        BugReportFlow flow = await SignedIn();
        var confirmed = new List<string>();
        flow.ReportConfirmed += confirmed.Add;
        using var releaseFirstBuild = new ManualResetEventSlim();
        var firstBuildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int builds = 0;
        flow.FinishStarting = () =>
        {
            if (Interlocked.Increment(ref builds) != 1) return;
            firstBuildStarted.TrySetResult();
            if (!releaseFirstBuild.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("The test did not release the first report build.");
        };
        Task firstSend;
        try
        {
            Open(flow);
            string firstId = Assert.Single(_captured);
            Assert.True(Send(flow));
            firstSend = flow.PendingWork!;
            await firstBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(firstSend.IsCompleted);

            Open(flow);
            string secondId = _captured[1];
            Assert.NotEqual(firstId, secondId);
            Assert.True(Send(flow));
            Assert.Equal([firstId, secondId], confirmed);
            Assert.False(firstSend.IsCompleted);
            await Settle(flow);
            Assert.False(firstSend.IsCompleted);
            Assert.Equal(secondId, Assert.Single(_sink.Submissions).ReportId);
        }
        finally
        {
            releaseFirstBuild.Set();
        }

        await firstSend.WaitAsync(TimeSpan.FromSeconds(30));
        await Settle(flow);
        Assert.Equal(_captured, confirmed);
        Assert.Equal(2, _sink.Submissions.Count);
        Assert.Contains(_sink.Submissions, submission => submission.ReportId == _captured[0]);
    }
}
