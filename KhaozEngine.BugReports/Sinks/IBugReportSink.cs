using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports;

/// <summary>A destination for finished report bundles.</summary>
public interface IBugReportSink
{
    /// <summary>
    /// Delivers one bundle. Every delivery outcome is a result, never an exception. Only the caller's own
    /// cancellation throws.
    /// </summary>
    /// <exception cref="System.OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task<BugReportSubmitResult> SubmitAsync(BugReportSubmission submission, CancellationToken cancellationToken);
}
