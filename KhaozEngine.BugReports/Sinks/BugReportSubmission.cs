using System;

namespace KhaozEngine.BugReports;

/// <summary>A finished bundle ready to hand to an <see cref="IBugReportSink"/>.</summary>
/// <param name="ReportId">The bundle's version 7 report id. A relay uses it as the idempotency key.</param>
/// <param name="Subject">The signed-in account the report belongs to, or null when it binds at the next flush.</param>
/// <param name="Bundle">The bundle bytes from <see cref="BugReportBundleWriter.Write"/>.</param>
public sealed record BugReportSubmission(string ReportId, string? Subject, ReadOnlyMemory<byte> Bundle);

/// <summary>What became of one submission.</summary>
public enum BugReportSubmitStatus
{
    /// <summary>The destination accepted the bundle. The caller deletes its queued copy.</summary>
    Sent,

    /// <summary>The bundle may succeed later. The caller keeps its queued copy and tries again.</summary>
    Queued,

    /// <summary>The destination refused the bundle for good. The caller deletes its queued copy.</summary>
    Rejected,
}

/// <summary>The outcome of <see cref="IBugReportSink.SubmitAsync"/>.</summary>
/// <param name="Status">Whether the bundle was sent, should be queued or was refused.</param>
/// <param name="Reference">The reference code to show the player when <paramref name="Status"/> is Sent, otherwise null.</param>
/// <param name="Error">A machine-readable reason when the bundle was not sent, otherwise null.</param>
/// <param name="RetryAfter">How long the destination asked the caller to wait before trying again, when it said.</param>
public sealed record BugReportSubmitResult(BugReportSubmitStatus Status, string? Reference, string? Error, TimeSpan? RetryAfter);
