using System;
using System.Collections.Generic;
using System.Text.Json;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports;

/// <summary>Reads the newest log lines from a captured log section.</summary>
public static class BugReportLogTail
{
    /// <summary>Returns up to <paramref name="maxLines"/> string lines, oldest first, ignoring non-string elements.</summary>
    /// <remarks>A missing, malformed or non-array lines property gives an empty list.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLines"/> is negative.</exception>
    public static IReadOnlyList<string> FromLogSection(ReadOnlyMemory<byte> json, int maxLines = 50)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLines);
        if (maxLines == 0) return [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, BugReportJson.SectionDocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("lines", out JsonElement lines) || lines.ValueKind != JsonValueKind.Array)
                return [];
            var tail = new List<string>();
            foreach (JsonElement line in lines.EnumerateArray())
                if (line.ValueKind == JsonValueKind.String) tail.Add(line.GetString()!);
            return tail.Count > maxLines ? tail.GetRange(tail.Count - maxLines, maxLines) : tail;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return [];
        }
    }
}
