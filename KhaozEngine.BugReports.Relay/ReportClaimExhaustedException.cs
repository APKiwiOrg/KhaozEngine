using System;

namespace KhaozEngine.BugReports.Relay;

/// <summary>
/// Every claim epoch for one operation on a report is used, so redelivery alone cannot finish the work. An operator
/// must inspect and clear the report's claims before it can run again.
/// </summary>
internal sealed class ReportClaimExhaustedException(string reportId, ReportWorkKind kind)
    : Exception("Every work claim is used for " + kind + " on report " + reportId + ". Operator recovery is required.")
{
    public string ReportId { get; } = reportId;
    public ReportWorkKind Kind { get; } = kind;
}
