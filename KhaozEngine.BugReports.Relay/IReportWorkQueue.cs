using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay;

/// <summary>What a queued work item asks the worker to do.</summary>
public enum ReportWorkKind
{
    /// <summary>Create the GitHub issue for an accepted report.</summary>
    CreateIssue,

    /// <summary>Add a late server snapshot to the report's issue.</summary>
    AppendServerSnapshot,

    /// <summary>Delete a revoked report's files and private record after active work has settled.</summary>
    PurgeRevoked,
}

/// <summary>One unit of relay work, delivered at least once by the host's queue.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="ReportId">The canonical report id the work is for.</param>
/// <param name="Requeues">How many times the item has come back from the poison queue.</param>
public sealed record ReportWorkItem(ReportWorkKind Kind, string ReportId, int Requeues = 0);

/// <summary>The durable queue that carries work from intake to the worker.</summary>
public interface IReportWorkQueue
{
    /// <summary>Adds <paramref name="item"/> to the queue.</summary>
    Task EnqueueAsync(ReportWorkItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Adds <paramref name="item"/> to the queue, hidden for <paramref name="visibleAfter"/>.
    /// The delay must be between zero and seven days, inclusive. Zero makes the item visible immediately.
    /// </summary>
    Task EnqueueAsync(ReportWorkItem item, TimeSpan visibleAfter, CancellationToken cancellationToken)
        => throw new NotSupportedException("IReportWorkQueue.EnqueueAsync with a visibility delay is not implemented by this queue.");
}
