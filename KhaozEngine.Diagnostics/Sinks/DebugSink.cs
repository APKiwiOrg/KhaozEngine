using System.Diagnostics;

namespace KhaozEngine.Diagnostics;

/// <summary>Writes formatted entries to <see cref="System.Diagnostics.Trace"/> (IDE Output window / attached listeners).</summary>
public sealed class DebugSink : ILogSink
{
    private readonly LogLevel? minimumLevel;

    /// <summary>Creates a debug/trace sink with an optional per-sink threshold.</summary>
    public DebugSink(LogLevel? minimumLevel = null)
    {
        this.minimumLevel = minimumLevel;
    }

    /// <inheritdoc />
    public void Emit(in LogEntry entry)
    {
        if (minimumLevel.HasValue && entry.Level < minimumLevel.Value) return;
        try { Trace.WriteLine(LogFormatter.Format(entry)); }
        catch { /* never throw */ }
    }

    /// <inheritdoc />
    public void Flush()
    {
        try { Trace.Flush(); }
        catch { /* best-effort */ }
    }

    /// <inheritdoc />
    public void Dispose() { }
}
