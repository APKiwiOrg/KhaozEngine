using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports;

// Holds the queue and the submitter to the sink contract when a sink breaks it.
internal static class SinkCall
{
    // Null when the sink threw for any reason other than the caller's cancellation, or returned no result.
    // The caller's own cancellation still throws.
    public static async Task<BugReportSubmitResult?> TrySubmitAsync(IBugReportSink sink, BugReportSubmission submission, CancellationToken cancellationToken)
    {
        try
        {
            BugReportSubmitResult? result = await sink.SubmitAsync(submission, cancellationToken).ConfigureAwait(false);
            if (result is null)
                Log.Warn("Bug report sink " + sink.GetType().Name + " returned no result for report " + submission.ReportId + ".");
            return result;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warn("Bug report sink " + sink.GetType().Name + " failed for report " + submission.ReportId + ".", ex);
            return null;
        }
    }
}
