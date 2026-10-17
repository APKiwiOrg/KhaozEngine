using System.Collections.Generic;
using System.Text.Json;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// The <c>log</c> section: <c>{lines:[...]}</c>, one string per kept line, oldest first. A full ring can hold more than
/// the budget, and a structural cut keeps the oldest lines, so the section writes only the newest lines whose escaped
/// size fits <see cref="MaxBytes"/>. The lines just before the report are the ones a reader needs.
/// </summary>
/// <param name="log">The ring of recent formatted log lines.</param>
public sealed class LogSection(RecentLogSink log) : IBugReportSection
{
    /// <summary><c>{"lines":[</c> and <c>]}</c>.</summary>
    const int EnvelopeBytes = 12;

    /// <summary>The quotes and the comma around each line.</summary>
    const int LineOverheadBytes = 3;

    /// <inheritdoc />
    public string Key => "log";

    /// <inheritdoc />
    public int MaxBytes => 256 * 1024;

    /// <inheritdoc />
    public void Write(Utf8JsonWriter writer, BugReportContext context)
    {
        IReadOnlyList<string> lines = log.Snapshot();
        int first = lines.Count;
        long used = EnvelopeBytes;
        while (first > 0)
        {
            long cost = EstimatedBytes(lines[first - 1]);
            if (used + cost > MaxBytes) break;
            used += cost;
            first--;
        }

        writer.WriteStartObject();
        writer.WriteStartArray("lines");
        for (int i = first; i < lines.Count; i++)
            writer.WriteStringValue(lines[i]);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// An upper bound on the bytes <paramref name="line"/> takes as a JSON array element under the section encoder:
    /// six for every ASCII character it may escape and for each surrogate, three for other non-ASCII characters.
    /// </summary>
    static long EstimatedBytes(string line)
    {
        long bytes = LineOverheadBytes;
        foreach (char c in line)
        {
            if (c < 0x80) bytes += c < 0x20 || c is '"' or '\\' or '<' or '>' or '&' or '\'' or '+' or '`' or (char)0x7F ? 6 : 1;
            else if (char.IsSurrogate(c)) bytes += 6;
            else bytes += 3;
        }
        return bytes;
    }
}
