using System;

namespace KhaozEngine.Diagnostics;

/// <summary>One immutable log record: when, how severe, which component, the message, and an optional exception.</summary>
public readonly struct LogEntry
{
    /// <summary>When the event occurred (captured on the calling thread, not when written).</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Severity of the entry.</summary>
    public LogLevel Level { get; }

    /// <summary>Component/category tag (for example a type name).</summary>
    public string Category { get; }

    /// <summary>The message text.</summary>
    public string Message { get; }

    /// <summary>Associated exception, or <c>null</c>.</summary>
    public Exception? Exception { get; }

    /// <summary>Creates a log entry. Null <paramref name="category"/> and <paramref name="message"/> are coerced to <see cref="string.Empty"/>.</summary>
    public LogEntry(DateTimeOffset timestamp, LogLevel level, string category, string message, Exception? exception = null)
    {
        Timestamp = timestamp;
        Level = level;
        Category = category ?? string.Empty;
        Message = message ?? string.Empty;
        Exception = exception;
    }
}
