using System;
using System.Text.Json;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// The <c>session</c> section: <c>{uptimeSeconds, sessionLog, previousCrash:{file, writtenAt}|null}</c>. Only file names
/// are written, never the directories they sit in.
/// <para>
/// The facts delegate runs in the trigger frame and must do no file or directory I/O. The host finds the previous crash
/// once at startup with <c>ClientFacts.FindPreviousCrash</c> and the delegate returns that cached result.
/// </para>
/// </summary>
/// <param name="facts">Returns the session facts, from cached values only.</param>
public sealed class SessionSection(Func<SessionFacts> facts) : IBugReportSection
{
    /// <inheritdoc />
    public string Key => "session";

    /// <inheritdoc />
    public int MaxBytes => 4 * 1024;

    /// <inheritdoc />
    public void Write(Utf8JsonWriter writer, BugReportContext context)
    {
        SessionFacts session = facts();
        writer.WriteStartObject();
        writer.WriteNumber("uptimeSeconds", session.Uptime.TotalSeconds);
        writer.WriteString("sessionLog", FileNameOf(session.SessionLogName));
        if (session.PreviousCrashFile is null)
        {
            writer.WriteNull("previousCrash");
        }
        else
        {
            writer.WriteStartObject("previousCrash");
            writer.WriteString("file", FileNameOf(session.PreviousCrashFile));
            if (session.PreviousCrashAt is DateTimeOffset writtenAt) writer.WriteString("writtenAt", writtenAt.ToUniversalTime());
            else writer.WriteNull("writtenAt");
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    /// <summary>The text after the last <c>/</c> or <c>\</c>, so a path from any OS gives its file name.</summary>
    static string? FileNameOf(string? path)
    {
        if (path is null) return null;
        int separator = path.AsSpan().LastIndexOfAny('/', '\\');
        return separator < 0 ? path : path[(separator + 1)..];
    }
}
