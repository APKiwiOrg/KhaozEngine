using System;

namespace KhaozEngine.BugReports.Contracts;

public static class RelayRoutes
{
    public const string Intake = "bug-reports";

    public static string ServerSnapshot(string reportId)
    {
        if (!ReportIds.IsValid(reportId))
            throw new ArgumentException("A canonical version 7 report id is required.", nameof(reportId));
        return Intake + "/" + reportId + "/server";
    }
}

public static class RelayHeaders
{
    public const string IdempotencyKey = "Idempotency-Key";
    public const string ServiceKey = "X-Report-Service-Key";
}
