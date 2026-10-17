using System;
using System.Text.Json;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

/// <summary>
/// The queue message form of a <see cref="ReportWorkItem"/>:
/// <c>{"kind":"createIssue","reportId":"&lt;32 hex&gt;","requeues":0}</c>.
/// </summary>
public static class ReportWorkItemJson
{
    const string CreateIssue = "createIssue";
    const string AppendServerSnapshot = "appendServerSnapshot";

    /// <summary>Writes <paramref name="item"/> as a queue message. Refuses an item that <see cref="Parse"/> would refuse.</summary>
    public static string Serialize(ReportWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        string kind = item.Kind switch
        {
            ReportWorkKind.CreateIssue => CreateIssue,
            ReportWorkKind.AppendServerSnapshot => AppendServerSnapshot,
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Kind, "Unknown work kind."),
        };
        if (!ReportIds.IsValid(item.ReportId))
            throw new ArgumentException("A canonical version 7 report id is required.", nameof(item));
        ArgumentOutOfRangeException.ThrowIfNegative(item.Requeues, nameof(item));
        return JsonSerializer.Serialize(new WorkItemDocument(kind, item.ReportId, item.Requeues), RelayJson.Options);
    }

    /// <summary>
    /// Reads a queue message. A missing <c>requeues</c> reads as 0. Throws <see cref="FormatException"/> on malformed
    /// JSON, a missing or unknown kind, an invalid report id or a negative requeue count.
    /// </summary>
    public static ReportWorkItem Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        WorkItemDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<WorkItemDocument>(json, RelayJson.Options);
        }
        catch (JsonException e)
        {
            throw new FormatException("The work item is not valid JSON.", e);
        }

        if (document is null)
            throw new FormatException("The work item is null.");
        ReportWorkKind kind = document.Kind switch
        {
            CreateIssue => ReportWorkKind.CreateIssue,
            AppendServerSnapshot => ReportWorkKind.AppendServerSnapshot,
            _ => throw new FormatException("The work item has no known kind."),
        };
        if (document.ReportId is null || !ReportIds.IsValid(document.ReportId))
            throw new FormatException("The work item has no canonical report id.");
        int requeues = document.Requeues ?? 0;
        if (requeues < 0)
            throw new FormatException("The work item has a negative requeue count.");
        return new ReportWorkItem(kind, document.ReportId, requeues);
    }

    sealed record WorkItemDocument(string? Kind, string? ReportId, int? Requeues);
}
