using System.Collections.Generic;

namespace KhaozEngine.Diagnostics;

/// <summary>Captures entries in memory for test assertions. Thread-safe.</summary>
public sealed class InMemorySink : ILogSink
{
    private readonly object gate = new();
    private readonly List<LogEntry> entries = new();

    /// <summary>A point-in-time snapshot of captured entries.</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (gate) { return entries.ToArray(); } }
    }

    /// <inheritdoc />
    public void Emit(in LogEntry entry)
    {
        lock (gate) { entries.Add(entry); }
    }

    /// <inheritdoc />
    public void Flush() { }

    /// <summary>Removes all captured entries.</summary>
    public void Clear()
    {
        lock (gate) { entries.Clear(); }
    }

    /// <inheritdoc />
    public void Dispose() { }
}
