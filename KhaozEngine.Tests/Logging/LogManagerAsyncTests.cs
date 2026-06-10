using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

public class LogManagerAsyncTests
{
    /// <summary>Parks the writer thread inside Emit until released, making queue backpressure deterministic.</summary>
    private sealed class GatedSink : ILogSink
    {
        public readonly ManualResetEventSlim EmitEntered = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        public void Emit(in LogEntry entry) { EmitEntered.Set(); Release.Wait(); }
        public void Flush() { }
        public void Dispose() { Release.Set(); }   // never leave a parked writer on teardown
    }

    /// <summary>A misbehaving sink that flushes its owner from inside Emit (once), to prove re-entrancy doesn't deadlock.</summary>
    private sealed class ReentrantFlushSink : ILogSink
    {
        public LogManager? Owner;
        private int flushed;
        public void Emit(in LogEntry entry)
        {
            if (Interlocked.Exchange(ref flushed, 1) == 0) Owner?.Flush();
        }
        public void Flush() { }
        public void Dispose() { }
    }

    [Fact]
    public void EntriesAreWrittenAndOrderedAfterFlush()
    {
        var sink = new InMemorySink();
        var options = new LoggerOptions { Synchronous = false, MinimumLevel = LogLevel.Trace };
        options.Sinks.Add(sink);
        using var mgr = new LogManager(options);

        var log = mgr.GetLogger("L");
        for (int i = 0; i < 50; i++) log.Info("m" + i);
        mgr.Flush();

        Assert.Equal(50, sink.Entries.Count);
        Assert.Equal("m0", sink.Entries[0].Message);
        Assert.Equal("m49", sink.Entries[49].Message);
    }

    [Fact]
    public void ShutdownDrainsRemainingEntries()
    {
        var sink = new InMemorySink();
        var options = new LoggerOptions { Synchronous = false };
        options.Sinks.Add(sink);
        var mgr = new LogManager(options);

        var log = mgr.GetLogger("L");
        for (int i = 0; i < 20; i++) log.Info("m" + i);
        mgr.Shutdown();

        Assert.Equal(20, sink.Entries.Count);
    }

    [Fact]
    public void OverflowDropsDeterministicallyAndNeverBlocks()
    {
        var gated = new GatedSink();
        var options = new LoggerOptions { Synchronous = false, QueueCapacity = 2 };
        options.Sinks.Add(gated);
        using var mgr = new LogManager(options);
        var log = mgr.GetLogger("L");

        log.Info("first");                                              // writer dequeues this and parks in Emit
        Assert.True(gated.EmitEntered.Wait(TimeSpan.FromSeconds(5)));   // queue now empty, writer parked

        for (int i = 0; i < 5; i++) log.Info("more" + i);              // 2 fit the queue, 3 overflow (non-blocking)
        Assert.Equal(3, mgr.DroppedCount);

        gated.Release.Set();                                            // unblock writer so dispose can drain
    }

    [Fact]
    public void FlushReportsDroppedCountAsSingleWarning()
    {
        var gated = new GatedSink();
        var observer = new InMemorySink();
        var options = new LoggerOptions { Synchronous = false, QueueCapacity = 2 };
        options.Sinks.Add(gated);
        options.Sinks.Add(observer);
        using var mgr = new LogManager(options);
        var log = mgr.GetLogger("L");

        log.Info("first");
        Assert.True(gated.EmitEntered.Wait(TimeSpan.FromSeconds(5)));
        for (int i = 0; i < 5; i++) log.Info("more" + i);             // 3 dropped
        Assert.Equal(3, mgr.DroppedCount);

        gated.Release.Set();
        mgr.Flush();                                                  // writer reports drops while handling the flush marker

        Assert.Contains(observer.Entries,
            e => e.Level == LogLevel.Warn && e.Category == "Log" && e.Message.Contains("dropped"));
    }

    [Fact]
    public void SynchronousModeWritesInlineWithoutFlush()
    {
        var sink = new InMemorySink();
        var options = new LoggerOptions { Synchronous = true };
        options.Sinks.Add(sink);
        using var mgr = new LogManager(options);
        mgr.GetLogger("L").Info("x");
        Assert.Single(sink.Entries);   // no Flush needed in sync mode
    }

    [Fact]
    public async Task ReentrantFlushFromSinkDoesNotDeadlock()
    {
        var reentrant = new ReentrantFlushSink();
        var observer = new InMemorySink();
        var options = new LoggerOptions { Synchronous = false, MinimumLevel = LogLevel.Trace };
        options.Sinks.Add(reentrant);
        options.Sinks.Add(observer);
        using var mgr = new LogManager(options);
        reentrant.Owner = mgr;

        mgr.GetLogger("L").Info("x");   // writer Emit -> reentrant.Emit -> mgr.Flush() on the writer thread

        // WaitAsync throws TimeoutException if Flush deadlocks; completing normally proves it does not.
        await Task.Run(() => mgr.Flush()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(observer.Entries, e => e.Message == "x");
    }

    [Fact]
    public void LoggingAndFlushAfterShutdownDoNotThrow()
    {
        var sink = new InMemorySink();
        var options = new LoggerOptions { Synchronous = false, MinimumLevel = LogLevel.Trace };
        options.Sinks.Add(sink);
        var mgr = new LogManager(options);
        mgr.GetLogger("L").Info("before");
        mgr.Shutdown();   // completes + disposes the async queue

        // Logging must never throw, including after shutdown (a disposed BlockingCollection).
        var ex = Record.Exception(() =>
        {
            mgr.GetLogger("L").Info("after");
            mgr.Flush();
        });
        Assert.Null(ex);
    }
}
