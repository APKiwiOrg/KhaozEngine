using System;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Where the server snapshot has been included in the GitHub issue.</summary>
internal enum ServerSnapshotState
{
    None,
    InBody,
    Commented,
}

/// <summary>The relay-private report metadata and durable issue creation state.</summary>
internal sealed record ReportRecord(int FormatVersion, string ReportId, string Reference, string Folder,
    DateTimeOffset ReceivedAt, ReporterIdentity Reporter, BugReportManifest Manifest, int? IssueNumber,
    ServerSnapshotState ServerSnapshot);

/// <summary>The durable mapping from a report id to its capability folder and authenticated owner.</summary>
internal sealed record AcceptedReport(string Folder, string Subject, DateTimeOffset ReceivedAt);
