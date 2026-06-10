using System;
using System.Collections.Generic;
using System.Threading;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// The instance core of the logging service. Owns sinks, the runtime-settable minimum level, and the
/// write path. Create category loggers via <see cref="GetLogger(string)"/>. Injectable and testable.
/// </summary>
public sealed class LogManager : IDisposable
{
    private readonly object sinkGate = new();
    private readonly List<ILogSink> sinks;
    private readonly IClock clock;
    private readonly string defaultCategory;
    private int minimumLevel;

    /// <summary>Creates a manager from <paramref name="options"/>.</summary>
    public LogManager(LoggerOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        sinks = new List<ILogSink>(options.Sinks);
        clock = options.Clock ?? SystemClock.Instance;
        defaultCategory = string.IsNullOrEmpty(options.DefaultCategory) ? "App" : options.DefaultCategory;
        minimumLevel = (int)options.MinimumLevel;
    }

    /// <summary>Entries below this level are dropped. Safe to set from any thread.</summary>
    public LogLevel MinimumLevel
    {
        get => (LogLevel)Volatile.Read(ref minimumLevel);
        set => Volatile.Write(ref minimumLevel, (int)value);
    }

    /// <summary>Number of entries dropped because the async queue was full. Always 0 in Task 4 (no queue yet).</summary>
    public long DroppedCount => 0;

    /// <summary>The default category used by convenience methods.</summary>
    public string DefaultCategory => defaultCategory;

    /// <summary>The current timestamp from the configured clock.</summary>
    internal DateTimeOffset Now => clock.Now;

    /// <summary>Returns a logger for <paramref name="category"/>.</summary>
    public ILogger GetLogger(string category) => new CategoryLogger(this, string.IsNullOrEmpty(category) ? defaultCategory : category);

    /// <summary>Returns a logger whose category is <c>typeof(T).Name</c>.</summary>
    public ILogger GetLogger<T>() => GetLogger(typeof(T).Name);

    /// <summary>True when entries at <paramref name="level"/> pass the global filter.</summary>
    internal bool IsEnabled(LogLevel level) => (int)level >= Volatile.Read(ref minimumLevel);

    /// <summary>Adds a sink at runtime (thread-safe).</summary>
    public void AddSink(ILogSink sink)
    {
        if (sink is null) return;
        lock (sinkGate) { sinks.Add(sink); }
    }

    /// <summary>Submits an entry to the write path. Writes inline regardless of Synchronous flag (Task 5 adds the async branch).</summary>
    internal void Submit(in LogEntry entry)
    {
        if (!IsEnabled(entry.Level)) return;
        WriteToSinks(entry);
    }

    private void WriteToSinks(in LogEntry entry)
    {
        ILogSink[] snapshot;
        lock (sinkGate) { snapshot = sinks.ToArray(); }
        foreach (var sink in snapshot)
        {
            try { sink.Emit(entry); }
            catch { /* logging never throws */ }
        }
    }

    private void FlushSinks()
    {
        ILogSink[] snapshot;
        lock (sinkGate) { snapshot = sinks.ToArray(); }
        foreach (var sink in snapshot)
        {
            try { sink.Flush(); }
            catch { /* best-effort */ }
        }
    }

    private void DisposeSinks()
    {
        ILogSink[] snapshot;
        lock (sinkGate) { snapshot = sinks.ToArray(); sinks.Clear(); }
        foreach (var sink in snapshot)
        {
            try { sink.Dispose(); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Flushes all sinks. (Task 5 makes this also drain the async queue.)</summary>
    public void Flush() => FlushSinks();

    /// <summary>Flushes and disposes all sinks. (Task 5 makes this also stop the writer thread.)</summary>
    public void Shutdown()
    {
        FlushSinks();
        DisposeSinks();
    }

    /// <inheritdoc />
    public void Dispose() => Shutdown();
}
