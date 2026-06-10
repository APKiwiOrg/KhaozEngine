using System.Globalization;
using System.Text;

namespace KhaozEngine.Diagnostics;

/// <summary>Default text layout for a <see cref="LogEntry"/>: <c>[ts] [LEVEL] [Category] message</c> with any exception appended.</summary>
public static class LogFormatter
{
    /// <summary>Formats an entry as a single string (exception text follows on a new line).</summary>
    public static string Format(in LogEntry entry)
    {
        var sb = new StringBuilder(64);
        sb.Append('[').Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("] [")
          .Append(Tag(entry.Level)).Append("] [").Append(entry.Category).Append("] ").Append(entry.Message);
        if (entry.Exception is not null)
        {
            sb.Append('\n').Append(entry.Exception);
        }
        return sb.ToString();
    }

    /// <summary>The uppercase tag for a level.</summary>
    public static string Tag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info  => "INFO",
        LogLevel.Warn  => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Fatal => "FATAL",
        _ => "INFO"
    };
}
