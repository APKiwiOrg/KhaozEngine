using System;

namespace KhaozEngine.BugReports.Relay;

/// <summary>A late snapshot must wait for the report's issue number to be recorded.</summary>
public sealed class ReportNotReadyException : Exception
{
    /// <summary>Creates a retryable failure for an accepted report whose issue is not ready.</summary>
    public ReportNotReadyException(string reportId)
        : base("The issue is not ready for report " + reportId + ".") => ReportId = reportId;

    /// <summary>The report whose work should be redelivered.</summary>
    public string ReportId { get; }
}
