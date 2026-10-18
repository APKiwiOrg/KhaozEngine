namespace KhaozEngine.BugReports.Relay.Azure;

internal static class AzureStatus
{
    public static bool IsConflict(int status) => status is 409 or 412;
    public static bool IsAbsent(int status) => status == 404;
}
