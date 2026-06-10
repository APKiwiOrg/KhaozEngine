using System;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

public class LogManagerSyncTests
{
    private sealed class ThrowingSink : ILogSink
    {
        public void Emit(in LogEntry entry) => throw new InvalidOperationException("sink boom");
        public void Flush() => throw new InvalidOperationException("flush boom");
        public void Dispose() { }
    }

    private static (LogManager mgr, InMemorySink sink, FakeClock clock) NewManager(LogLevel min = LogLevel.Trace)
    {
        var sink = new InMemorySink();
        var clock = new FakeClock();
        var options = new LoggerOptions { Synchronous = true, MinimumLevel = min, Clock = clock };
        options.Sinks.Add(sink);
        return (new LogManager(options), sink, clock);
    }

    [Fact]
    public void InfoBuildsEntryWithLevelCategoryMessageAndClockTimestamp()
    {
        var (mgr, sink, clock) = NewManager();
        clock.Now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        mgr.GetLogger("Boot").Info("hello");

        var e = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Info, e.Level);
        Assert.Equal("Boot", e.Category);
        Assert.Equal("hello", e.Message);
        Assert.Equal(clock.Now, e.Timestamp);
    }

    [Fact]
    public void GetLoggerGenericUsesTypeName()
    {
        var (mgr, sink, _) = NewManager();
        mgr.GetLogger<LogManagerSyncTests>().Warn("w");
        Assert.Equal(nameof(LogManagerSyncTests), sink.Entries[0].Category);
    }

    [Fact]
    public void EachLevelMethodTagsItsLevel()
    {
        var (mgr, sink, _) = NewManager();
        var log = mgr.GetLogger("L");
        log.Trace("t"); log.Debug("d"); log.Info("i");
        log.Warn("w"); log.Error("e"); log.Fatal("f");

        Assert.Collection(sink.Entries,
            e => Assert.Equal(LogLevel.Trace, e.Level),
            e => Assert.Equal(LogLevel.Debug, e.Level),
            e => Assert.Equal(LogLevel.Info, e.Level),
            e => Assert.Equal(LogLevel.Warn, e.Level),
            e => Assert.Equal(LogLevel.Error, e.Level),
            e => Assert.Equal(LogLevel.Fatal, e.Level));
    }

    [Fact]
    public void EntriesBelowMinimumLevelAreDropped()
    {
        var (mgr, sink, _) = NewManager(min: LogLevel.Warn);
        var log = mgr.GetLogger("L");
        log.Info("skipped");
        log.Error("kept");
        var e = Assert.Single(sink.Entries);
        Assert.Equal("kept", e.Message);
    }

    [Fact]
    public void MinimumLevelIsSettableAtRuntime()
    {
        var (mgr, sink, _) = NewManager(min: LogLevel.Error);
        var log = mgr.GetLogger("L");
        log.Info("before");        // dropped
        mgr.MinimumLevel = LogLevel.Info;
        log.Info("after");         // kept
        var e = Assert.Single(sink.Entries);
        Assert.Equal("after", e.Message);
    }

    [Fact]
    public void IsEnabledReflectsMinimumLevel()
    {
        var (mgr, _, _) = NewManager(min: LogLevel.Warn);
        var log = mgr.GetLogger("L");
        Assert.False(log.IsEnabled(LogLevel.Info));
        Assert.True(log.IsEnabled(LogLevel.Warn));
        Assert.True(log.IsEnabled(LogLevel.Fatal));
    }

    [Fact]
    public void AllSinksReceiveEntry()
    {
        var a = new InMemorySink();
        var b = new InMemorySink();
        var options = new LoggerOptions { Synchronous = true };
        options.Sinks.Add(a);
        options.Sinks.Add(b);
        var mgr = new LogManager(options);

        mgr.GetLogger("L").Info("x");
        Assert.Single(a.Entries);
        Assert.Single(b.Entries);
    }

    [Fact]
    public void AddSinkAtRuntimeReceivesSubsequentEntries()
    {
        var (mgr, _, _) = NewManager();
        var late = new InMemorySink();
        mgr.AddSink(late);
        mgr.GetLogger("L").Info("x");
        Assert.Single(late.Entries);
    }

    [Fact]
    public void ThrowingSinkNeverSurfacesAndDoesNotStopOtherSinks()
    {
        var good = new InMemorySink();
        var options = new LoggerOptions { Synchronous = true };
        options.Sinks.Add(new ThrowingSink());
        options.Sinks.Add(good);
        var mgr = new LogManager(options);

        var ex = Record.Exception(() => mgr.GetLogger("L").Error("boom"));
        Assert.Null(ex);
        Assert.Single(good.Entries);
    }

    [Fact]
    public void FormatterProducesTimestampLevelCategoryMessage()
    {
        var ts = new DateTimeOffset(2026, 6, 10, 13, 14, 15, 678, TimeSpan.Zero);
        var line = LogFormatter.Format(new LogEntry(ts, LogLevel.Warn, "Boot", "started"));
        Assert.Equal("[2026-06-10 13:14:15.678] [WARN] [Boot] started", line);
    }

    [Fact]
    public void FormatterAppendsExceptionOnNewLine()
    {
        var ts = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var line = LogFormatter.Format(new LogEntry(ts, LogLevel.Error, "X", "failed", new InvalidOperationException("disk gone")));
        Assert.Contains("[ERROR] [X] failed", line);
        Assert.Contains("disk gone", line);
        Assert.Contains("\n", line);
    }
}
