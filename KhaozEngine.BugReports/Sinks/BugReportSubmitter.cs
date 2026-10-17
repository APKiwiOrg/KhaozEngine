using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports;

/// <summary>
/// Delivers reports write-ahead: each bundle is queued on disk before its first send and deleted only when the sink
/// returns Sent or Rejected, so a crash or an unreachable relay keeps it for a later <see cref="FlushAsync"/>.
/// A signed-out submission is queued without a send. When the queue cannot be written the bundle is sent directly
/// and any result other than Sent carries the <c>queue_failed</c> error.
/// </summary>
public sealed class BugReportSubmitter
{
    /// <summary>The error on a submission queued because no account was signed in.</summary>
    public const string SignedOutError = "signed_out";

    /// <summary>The error on an unsent submission that could not be kept on disk.</summary>
    public const string QueueFailedError = "queue_failed";

    readonly IBugReportSink sink;
    readonly BugReportOfflineQueue queue;

    /// <summary>Creates a submitter that sends through <paramref name="sink"/> and keeps bundles in <paramref name="queue"/>.</summary>
    public BugReportSubmitter(IBugReportSink sink, BugReportOfflineQueue queue)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(queue);
        this.sink = sink;
        this.queue = queue;
    }

    /// <summary>
    /// Queues <paramref name="submission"/>, then sends it when it has a subject. A null or blank subject returns
    /// Queued with <see cref="SignedOutError"/>. Every outcome is a result. Only the caller's own cancellation throws,
    /// and it leaves the bundle queued.
    /// </summary>
    /// <exception cref="ArgumentException">The report id is not a canonical version 7 id.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<BugReportSubmitResult> SubmitAsync(BugReportSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!ReportIds.IsValid(submission.ReportId))
            throw new ArgumentException("A canonical version 7 report id is required.", nameof(submission));
        cancellationToken.ThrowIfCancellationRequested();

        bool signedIn = !string.IsNullOrWhiteSpace(submission.Subject);
        bool kept = TryEnqueue(submission, inFlight: signedIn);
        if (!signedIn)
            return new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, kept ? SignedOutError : QueueFailedError, null);

        if (!kept)
        {
            BugReportSubmitResult direct = await sink.SubmitAsync(submission, cancellationToken).ConfigureAwait(false);
            return direct.Status == BugReportSubmitStatus.Sent ? direct : direct with { Error = QueueFailedError };
        }

        BugReportSubmitResult? result = null;
        try
        {
            result = await sink.SubmitAsync(submission, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            queue.Complete(submission.ReportId, result);
        }
    }

    /// <summary>Sends the queued reports of <paramref name="subject"/> and every unbound report, binding them to it.</summary>
    /// <returns>The number of reports the sink accepted.</returns>
    public Task<int> FlushAsync(string subject, CancellationToken cancellationToken) =>
        queue.FlushAsync(subject, sink, cancellationToken);

    bool TryEnqueue(BugReportSubmission submission, bool inFlight)
    {
        try
        {
            if (inFlight)
                queue.EnqueueInFlight(submission);
            else
                queue.Enqueue(submission);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Bug report " + submission.ReportId + " could not be queued on disk.", ex);
            return false;
        }
    }
}
