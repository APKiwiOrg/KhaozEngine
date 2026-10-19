using System;
using System.Collections.Generic;

namespace KhaozEngine.BugReports.Relay;

/// <summary>The outcome of resolving and revoking a report.</summary>
public enum ReportRevocationStatus
{
    /// <summary>The report was tombstoned and its purge was scheduled.</summary>
    Revoked,
    /// <summary>Only the report's retained tombstone remains.</summary>
    AlreadyRevoked,
    /// <summary>No retained report matches the id or reference.</summary>
    NotFound,
    /// <summary>Multiple retained reports match the reference.</summary>
    Ambiguous,
    /// <summary>The input is neither a valid report id nor a valid reference.</summary>
    Invalid,
}

/// <summary>The outcome of redacting and closing a revoked report's issue.</summary>
public enum ReportIssueRevocation
{
    /// <summary>No positive issue number was found.</summary>
    NotCreated,
    /// <summary>The issue was redacted and closed as not planned.</summary>
    Redacted,
    /// <summary>The issue no longer exists or cannot be seen by the GitHub App.</summary>
    Gone,
    /// <summary>The issue moved and was not edited at its new location.</summary>
    Moved,
    /// <summary>The issue update failed and will be retried by the purge.</summary>
    Failed,
}

/// <summary>A report revocation's resolution, immediate cleanup and scheduled purge.</summary>
/// <param name="Status">The resolution and revocation outcome.</param>
/// <param name="ReportId">The resolved canonical report id, when known.</param>
/// <param name="Reference">The normalized report reference, when known.</param>
/// <param name="CandidateIds">The matching report ids when resolution is ambiguous.</param>
/// <param name="DeletedPublicBlobs">The number of public blobs deleted by this call.</param>
/// <param name="Issue">The issue update outcome, or null when no revocation work was required.</param>
/// <param name="IssueNumber">The positive issue number found in the record or a completed claim.</param>
/// <param name="IssueUrl">The issue URL returned by GitHub, including a moved issue's new location.</param>
/// <param name="PurgeAt">When the scheduled purge becomes visible to workers, or null when none was scheduled.</param>
public sealed record ReportRevocationResult(ReportRevocationStatus Status, string? ReportId, string? Reference,
    IReadOnlyList<string> CandidateIds, int DeletedPublicBlobs, ReportIssueRevocation? Issue, int? IssueNumber,
    Uri? IssueUrl, DateTimeOffset? PurgeAt);
