using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.Tests.BugReports.Client;

/// <summary>Records every submission and answers with the next scripted outcome, Sent with the reference code by default.</summary>
internal sealed class FakeSink : IBugReportSink
{
    readonly object _sync = new();
    readonly Queue<Func<BugReportSubmission, BugReportSubmitResult>> _outcomes = new();
    readonly List<BugReportSubmission> _submissions = new();

    internal IReadOnlyList<BugReportSubmission> Submissions
    {
        get
        {
            lock (_sync) return _submissions.ToArray();
        }
    }

    /// <summary>Runs inside every <see cref="SubmitAsync"/> call, on the calling thread.</summary>
    internal Action? OnSubmit { get; set; }

    internal FakeSink Respond(BugReportSubmitStatus status, string? error = null)
    {
        lock (_sync)
            _outcomes.Enqueue(s => status == BugReportSubmitStatus.Sent
                ? Sent(s)
                : new BugReportSubmitResult(status, null, error ?? "fake", null));
        return this;
    }

    internal FakeSink Throw(Exception exception)
    {
        lock (_sync) _outcomes.Enqueue(_ => throw exception);
        return this;
    }

    public Task<BugReportSubmitResult> SubmitAsync(BugReportSubmission submission, CancellationToken cancellationToken)
    {
        OnSubmit?.Invoke();
        Func<BugReportSubmission, BugReportSubmitResult> next;
        lock (_sync)
        {
            _submissions.Add(submission);
            next = _outcomes.Count > 0 ? _outcomes.Dequeue() : Sent;
        }
        try
        {
            return Task.FromResult(next(submission));
        }
        catch (Exception exception)
        {
            return Task.FromException<BugReportSubmitResult>(exception);
        }
    }

    static BugReportSubmitResult Sent(BugReportSubmission submission) =>
        new(BugReportSubmitStatus.Sent, ReportIds.ReferenceCode(submission.ReportId), null, null);
}
