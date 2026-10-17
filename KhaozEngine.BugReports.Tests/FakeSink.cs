using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.Tests.BugReports;

// A scripted IBugReportSink. Each submit records the submission, then dequeues the next outcome.
internal sealed class FakeSink : IBugReportSink
{
    readonly object sync = new();
    readonly Queue<Func<BugReportSubmission, Task<BugReportSubmitResult>>> outcomes = new();
    readonly List<BugReportSubmission> submissions = [];

    public IReadOnlyList<BugReportSubmission> Submissions
    {
        get
        {
            lock (sync)
                return submissions.ToArray();
        }
    }

    public FakeSink Respond(BugReportSubmitStatus status, string? error = null)
    {
        lock (sync)
            outcomes.Enqueue(s => Task.FromResult(Result(s, status, error)));
        return this;
    }

    // Holds the submit until the test completes the gate.
    public FakeSink Block(TaskCompletionSource<BugReportSubmitResult> gate)
    {
        lock (sync)
            outcomes.Enqueue(_ => gate.Task);
        return this;
    }

    // Breaks the sink contract by throwing, as a faulty sink might.
    public FakeSink Throw(Exception exception)
    {
        lock (sync)
            outcomes.Enqueue(_ => Task.FromException<BugReportSubmitResult>(exception));
        return this;
    }

    // Cancels the caller's token, then fails, as a sink racing a cancel might.
    public FakeSink CancelThenThrow(CancellationTokenSource caller, Exception exception)
    {
        lock (sync)
            outcomes.Enqueue(_ =>
            {
                caller.Cancel();
                return Task.FromException<BugReportSubmitResult>(exception);
            });
        return this;
    }

    // Waits until the caller's token is canceled.
    public FakeSink Hang(CancellationToken cancellationToken)
    {
        lock (sync)
            outcomes.Enqueue(async _ =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("Task.Delay(Infinite) returned without cancellation.");
            });
        return this;
    }

    public Task<BugReportSubmitResult> SubmitAsync(BugReportSubmission submission, CancellationToken cancellationToken)
    {
        Func<BugReportSubmission, Task<BugReportSubmitResult>> next;
        lock (sync)
        {
            submissions.Add(submission);
            if (outcomes.Count == 0)
                throw new InvalidOperationException("FakeSink ran out of scripted outcomes.");
            next = outcomes.Dequeue();
        }
        return next(submission);
    }

    public static BugReportSubmitResult Result(BugReportSubmission submission, BugReportSubmitStatus status, string? error = null) =>
        status == BugReportSubmitStatus.Sent
            ? new BugReportSubmitResult(status, ReportIds.ReferenceCode(submission.ReportId), null, null)
            : new BugReportSubmitResult(status, null, error ?? "fake", null);
}
