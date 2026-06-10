using System;

namespace KhaozEngine.Diagnostics;

/// <summary>A destination for log entries. Implementations must never throw and should be thread-safe.</summary>
public interface ILogSink : IDisposable
{
    /// <summary>Writes one entry. Must swallow its own failures.</summary>
    void Emit(in LogEntry entry);

    /// <summary>Flushes any buffered output.</summary>
    void Flush();
}
