using System;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Unfinished work must be redelivered while another worker holds its claim.</summary>
internal sealed class ReportClaimBusyException(string reportId)
    : Exception("The work claim is busy for report " + reportId + ".")
{
    public string ReportId { get; } = reportId;
}
