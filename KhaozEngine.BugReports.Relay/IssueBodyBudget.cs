using System;
using System.Globalization;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Falls back to the report capability when optional issue text cannot fit.</summary>
internal static class IssueBodyBudget
{
    public static string Finish(string body, ReportRecord report, string viewerUrl)
    {
        if (body.Length > IssueLimits.MaxBodyChars)
        {
            int files = report.Manifest.Entries.Count + (report.ServerSnapshot == ServerSnapshotState.InBody ? 1 : 0);
            body = "Report " + ReportIds.ReferenceCode(report.ReportId) + "\n\n- [Viewer](" + viewerUrl + ")\n- "
                + files.ToString(CultureInfo.InvariantCulture) + " files in the report folder\n\n"
                + "Report details omitted for size. Open the viewer.\n";
        }
        if (body.Length > IssueLimits.MaxBodyChars)
            throw new InvalidOperationException("The report viewer link exceeds the issue body limit.");
        return body;
    }
}
