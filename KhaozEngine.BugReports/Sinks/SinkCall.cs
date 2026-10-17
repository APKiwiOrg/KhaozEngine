using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports;

// Holds the queue and the submitter to the sink contract when a sink breaks it.
internal static class SinkCall
{
    // Null when the sink threw or returned no result. Only an OperationCanceledException while the caller's token
    // is canceled is rethrown, so a sink failure that races the caller's cancel is still a failure, not a throw.
    public static async Task<BugReportSubmitResult?> TrySubmitAsync(IBugReportSink sink, BugReportSubmission submission, CancellationToken cancellationToken)
    {
        try
        {
            BugReportSubmitResult? result = await sink.SubmitAsync(submission, cancellationToken).ConfigureAwait(false);
            if (result is null)
                Log.Warn("Bug report sink " + sink.GetType().Name + " returned no result for report " + submission.ReportId + ".");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warn("Bug report sink " + sink.GetType().Name + " failed for report " + submission.ReportId + ".", ex);
            return null;
        }
    }
}
