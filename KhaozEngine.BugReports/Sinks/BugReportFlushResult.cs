namespace KhaozEngine.BugReports;

/// <summary>What one flush of the offline queue delivered.</summary>
/// <param name="Sent">The queued reports the sink accepted.</param>
/// <param name="Rejected">The queued reports the sink refused for good. Each was deleted and logged.</param>
public readonly record struct BugReportFlushResult(int Sent, int Rejected);
