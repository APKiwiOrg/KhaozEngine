using System;

namespace KhaozEngine.BugReports;

/// <summary>What a section may know about the report it writes into.</summary>
/// <param name="ReportId">The report id from <c>ReportIds.NewId</c>.</param>
/// <param name="CapturedAt">When the report was triggered.</param>
/// <param name="Subject">The reporting account, when one is signed in or known to the server.</param>
public sealed record BugReportContext(string ReportId, DateTimeOffset CapturedAt, string? Subject = null);
