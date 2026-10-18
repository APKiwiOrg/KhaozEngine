using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Wire-compatible responses with no cacheable account or report state.</summary>
internal static class RelayResponses
{
    public static RelayResponse Accepted(string reportId)
        => new(202, Encoding.UTF8.GetString(BugReportJson.SerializeIntakeAccepted(new IntakeAccepted(reportId,
            ReportIds.ReferenceCode(reportId)))), Headers());

    public static RelayResponse Error(int status, string error)
        => new(status, Encoding.UTF8.GetString(BugReportJson.SerializeRelayError(new RelayError(error))), Headers());

    public static RelayResponse Unauthorized()
    {
        RelayResponse response = Error(401, "unauthorized");
        return response with { Headers = Headers("WWW-Authenticate", "Bearer") };
    }

    public static RelayResponse TooManyRequests(TimeSpan retryAfter)
    {
        RelayResponse response = Error(429, "quota");
        string seconds = Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return response with { Headers = Headers("Retry-After", seconds) };
    }

    static IReadOnlyDictionary<string, string> Headers(string? name = null, string? value = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Cache-Control"] = "no-store" };
        if (name is not null) headers.Add(name, value!);
        return headers;
    }
}
