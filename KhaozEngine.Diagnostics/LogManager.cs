using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// The instance core of the logging service. Owns sinks, the runtime-settable minimum level, and the
/// write path. In async mode a single background thread drains a bounded queue so logging never blocks
/// the caller; in synchronous mode writes happen inline (used by tests). Injectable and testable.
/// </summary>
public sealed class LogManager : IDisposable
{
    private readonly struct WorkItem
    {
        public readonly LogEntry Entry;
        public readonly bool IsFlush;
        public readonly ManualResetEventSlim? FlushDone;

        public WorkItem(in LogEntry entry) { Entry = entry; IsFlush = false; FlushDone = null; }
        public WorkItem(ManualResetEventSlim flushDone) { Entry = default; IsFlush = true; FlushDone = flushDone; }
    }

    private readonly object sinkGate = new();
    private readonly List<ILogSink> sinks;
    private readonly IClock clock;
    private readonly string defaultCategory;
    private int minimumLevel;

    private readonly bool synchronous;
    private readonly BlockingCollection<WorkItem>? queue;
    private readonly Thread? worker;
    private int workerThreadId;
    private long dropped;
    private long reportedDropped;
    private bool shutdown;

    /// <summary>Creates a manager from <paramref name="options"/>.</summary>
    public LogManager(LoggerOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        sinks = new List<ILogSink>(options.Sinks);
        clock = options.Clock ?? SystemClock.Instance;
        defaultCategory = string.IsNullOrEmpty(options.DefaultCategory) ? "App" : options.DefaultCategory;
        minimumLevel = (int)options.MinimumLevel;
        synchronous = options.Synchronous;

        if (!synchronous)
        {
            int capacity = options.QueueCapacity > 0 ? options.QueueCapacity : 1;
            queue = new BlockingCollection<WorkItem>(new ConcurrentQueue<WorkItem>(), capacity);
            worker = new Thread(WriterLoop) { IsBackground = true, Name = "KhaozEngine.Log" };
            worker.Start();
            workerThreadId = worker.ManagedThreadId;
        }
    }

    /// <summary>Entries below this level are dropped. Safe to set from any thread.</summary>
    public LogLevel MinimumLevel
    {
        get => (LogLevel)Volatile.Read(ref minimumLevel);
        set => Volatile.Write(ref minimumLevel, (int)value);
    }

    /// <summary>Number of entries dropped because the async queue was full.</summary>
    public long DroppedCount => Interlocked.Read(ref dropped);

    /// <summary>The default category used by the static <c>Log</c> facade's convenience methods.</summary>
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

    /// <summary>Submits an entry. Async: enqueue (drop-on-full, never blocks). Sync: write inline.</summary>
    internal void Submit(in LogEntry entry)
    {
        if (!IsEnabled(entry.Level)) return;
        if (synchronous)
        {
            WriteToSinks(entry);
            return;
        }
        try
        {
            if (!queue!.TryAdd(new WorkItem(entry)))
            {
                Interlocked.Increment(ref dropped);
            }
        }
        // ObjectDisposedException derives from InvalidOperationException, so this covers both the
        // adding-completed and queue-disposed (shutting down) cases: drop, never throw.
        catch (InvalidOperationException) { }
    }

    private void WriterLoop()
    {
        foreach (var item in queue!.GetConsumingEnumerable())
        {
            if (item.IsFlush)
            {
                ReportDropsIfAny();
                FlushSinks();
                item.FlushDone!.Set();
            }
            else
            {
                WriteToSinks(item.Entry);
            }
        }
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

    private void ReportDropsIfAny()
    {
        long total = Interlocked.Read(ref dropped);
        long since = total - reportedDropped;
        if (since <= 0) return;
        reportedDropped = total;
        WriteToSinks(new LogEntry(clock.Now, LogLevel.Warn, "Log", $"{since} log entries dropped (queue full); {total} total"));
    }

    /// <summary>Drains the async queue (if any) and flushes all sinks. Blocks until done.</summary>
    public void Flush()
    {
        if (synchronous)
        {
            ReportDropsIfAny();
            FlushSinks();
            return;
        }

        // Re-entrancy guard: if a sink's Emit/Flush calls back into Flush on the writer thread, we cannot
        // enqueue a marker and wait for ourselves (the writer would block waiting on a marker only it can
        // process). The writer is already mid-drain, so flushing sinks inline is the correct, deadlock-free
        // behaviour.
        if (Thread.CurrentThread.ManagedThreadId == workerThreadId)
        {
            ReportDropsIfAny();
            FlushSinks();
            return;
        }

        // Push a flush marker and wait for the writer to reach it. The writer reports any drops and
        // flushes sinks while handling the marker, so all entries queued before this call are written
        // first. The drop warning is written by the writer thread, never re-enqueued (so it can't itself
        // be dropped when the queue is full). If the queue is already completed/disposed (shutting down,
        // possibly concurrently), flush inline instead. Logging never throws, including after shutdown.
        try
        {
            if (!queue!.IsAddingCompleted)
            {
                using var done = new ManualResetEventSlim(false);
                queue.Add(new WorkItem(done));   // flush markers must not be dropped; brief block is acceptable off the hot path
                done.Wait();
                return;
            }
        }
        // ObjectDisposedException derives from InvalidOperationException, so this covers both the
        // adding-completed and queue-disposed (shutting down, possibly concurrently) cases.
        catch (InvalidOperationException) { }

        ReportDropsIfAny();
        FlushSinks();
    }

    /// <summary>Flushes and disposes all sinks; in async mode stops and joins the writer thread first.</summary>
    public void Shutdown()
    {
        lock (sinkGate)
        {
            if (shutdown) return;
            shutdown = true;
        }

        if (!synchronous)
        {
            try
            {
                if (!queue!.IsAddingCompleted) queue.CompleteAdding();
            }
            catch (ObjectDisposedException) { }
            worker?.Join();   // writer has drained the queue and exited; safe to touch sinks from here
        }

        ReportDropsIfAny();
        FlushSinks();
        DisposeSinks();

        if (!synchronous)
        {
            try { queue!.Dispose(); } catch { }
        }
    }

    /// <inheritdoc />
    public void Dispose() => Shutdown();
}
