using System;

namespace KhaozEngine.Diagnostics;

/// <summary>Writes formatted entries to the console. Errors and fatals optionally go to stderr.</summary>
public sealed class ConsoleSink : ILogSink
{
    private readonly LogLevel? minimumLevel;
    private readonly bool useStdErrForErrors;

    /// <summary>Creates a console sink.</summary>
    /// <param name="minimumLevel">Optional per-sink threshold; entries below it are skipped.</param>
    /// <param name="useStdErrForErrors">When true, <see cref="LogLevel.Error"/> and above are written to stderr.</param>
    public ConsoleSink(LogLevel? minimumLevel = null, bool useStdErrForErrors = true)
    {
        this.minimumLevel = minimumLevel;
        this.useStdErrForErrors = useStdErrForErrors;
    }

    /// <inheritdoc />
    public void Emit(in LogEntry entry)
    {
        if (minimumLevel.HasValue && entry.Level < minimumLevel.Value) return;
        try
        {
            var writer = (useStdErrForErrors && entry.Level >= LogLevel.Error) ? Console.Error : Console.Out;
            writer.WriteLine(LogFormatter.Format(entry));
        }
        catch { /* never throw */ }
    }

    /// <inheritdoc />
    public void Flush()
    {
        try { Console.Out.Flush(); Console.Error.Flush(); }
        catch { /* best-effort */ }
    }

    /// <inheritdoc />
    public void Dispose() { }
}
