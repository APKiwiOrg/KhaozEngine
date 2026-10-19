using System;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

[Collection("LoggingSerial")]
public sealed class LogScopeTests
{
    private sealed class ScopeSink : ILogSink
    {
        internal readonly InMemorySink Entries = new();
        internal int Flushes, Disposals;
        public void Emit(in LogEntry entry) => Entries.Emit(entry);
        public void Flush() => Flushes++;
        public void Dispose() => Disposals++;
    }

    private static (LogManager manager, ScopeSink sink) Manager(
        string category, LogLevel level = LogLevel.Trace, IClock? clock = null)
    {
        var sink = new ScopeSink();
        var options = new LoggerOptions { Synchronous = true, DefaultCategory = category, MinimumLevel = level };
        if (clock is not null) options.Clock = clock;
        options.Sinks.Add(sink);
        return (new LogManager(options), sink);
    }

    [Fact]
    public void CachedLoggersFollowNestedScopesWithFilteringMetadataAndFlush()
    {
        Log.Shutdown();
        var typed = Log.For<LogScopeTests>();
        var named = Log.Get("named");
        var defaults = Log.Get("");
        var (process, processSink) = Manager("process");
        var (outer, outerSink) = Manager("outer", LogLevel.Warn);
        var timestamp = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var (inner, innerSink) = Manager("inner", clock: new FakeClock { Now = timestamp });
        using (outer)
        using (inner)
            try
            {
                Log.Configure(process);
                var pinned = process.GetLogger("pinned");
                using (Log.Use(outer))
                {
                    Assert.Same(outer, Log.Manager);
                    Assert.True(Log.IsConfigured);
                    Assert.Equal(LogLevel.Warn, Log.MinimumLevel);
                    Assert.False(typed.IsEnabled(LogLevel.Info));
                    typed.Info("filtered");
                    Assert.Equal("outer", defaults.Category);
                    defaults.Warn("outer");
                    using (Log.Use(inner))
                    {
                        Assert.True(typed.IsEnabled(LogLevel.Info));
                        typed.Info("typed");
                        Assert.Equal(timestamp, innerSink.Entries.Entries[0].Timestamp);
                        var error = new InvalidOperationException("metadata");
                        named.Error("named", error);
                        Assert.Same(error, innerSink.Entries.Entries[1].Exception);
                        Log.Info("convenience");
                        defaults.Info("default");
                        Assert.Equal("inner", defaults.Category);
                        Log.Flush();
                        pinned.Info("injected");
                        Assert.Equal(1, innerSink.Flushes);
                        Assert.Equal(0, outerSink.Flushes);
                        Assert.Equal(0, processSink.Flushes);
                    }
                    Assert.Equal("outer", defaults.Category);
                    typed.Warn("restored");
                }
                typed.Info("process");
                Assert.Equal(new[] { "outer", "restored" },
                    System.Linq.Enumerable.Select(outerSink.Entries.Entries, e => e.Message));
                Assert.Equal(new[] { nameof(LogScopeTests), "named", "inner", "inner" },
                    System.Linq.Enumerable.Select(innerSink.Entries.Entries, e => e.Category));
                Assert.Equal(new[] { "injected", "process" },
                    System.Linq.Enumerable.Select(processSink.Entries.Entries, e => e.Message));
                Assert.Equal(0, outerSink.Disposals);
                Assert.Equal(0, innerSink.Disposals);
                outer.GetLogger("borrowed").Warn("still alive");
                Assert.Equal(3, outerSink.Entries.Entries.Count);
            }
            finally { Log.Shutdown(); }
    }

    [Fact]
    public void ProcessMutationsInsideScopeLeaveBorrowedManagerAliveAndChangeTheFallback()
    {
        var (first, firstSink) = Manager("first");
        var (second, secondSink) = Manager("second");
        var (scoped, scopedSink) = Manager("scope");
        using (scoped)
            try
            {
                Log.Configure(first);
                var cached = Log.Get("");
                using (Log.Use(scoped))
                {
                    Log.MinimumLevel = LogLevel.Error;
                    Assert.Equal(LogLevel.Error, first.MinimumLevel);
                    Assert.Equal(LogLevel.Trace, scoped.MinimumLevel);
                    Assert.Equal(LogLevel.Trace, Log.MinimumLevel);
                    Log.Configure(second);
                    Assert.Equal(1, firstSink.Disposals);
                    cached.Info("scope after reconfigure");
                    Log.Shutdown();
                    Assert.Equal(1, secondSink.Disposals);
                    Assert.Same(scoped, Log.Manager);
                    Assert.True(Log.IsConfigured);
                    Log.MinimumLevel = LogLevel.Fatal;
                    Assert.Equal(LogLevel.Trace, scoped.MinimumLevel);
                    cached.Info("scope after shutdown");
                    Log.Configure(new LoggerOptions { Synchronous = true, DefaultCategory = "replacement" });
                }
                Assert.Equal("replacement", cached.Category);
                Assert.Equal(0, scopedSink.Disposals);
                Assert.Equal(2, scopedSink.Entries.Entries.Count);
            }
            finally { Log.Shutdown(); second.Dispose(); }
    }

    [Fact]
    public async Task ChildDisposalAndSiblingScopesCannotPoisonTheParentToken()
    {
        Log.Shutdown();
        var (parentManager, _) = Manager("parent");
        var (childManager, childSink) = Manager("child");
        using (parentManager)
        using (childManager)
        {
            var cached = Log.For<LogScopeTests>();
            using var parent = Log.Use(parentManager);
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task child = DedicatedThread.Run(async () =>
            {
                Assert.Same(parentManager, Log.Manager);
                parent.Dispose();
                parent.Dispose();
                Assert.Null(Log.Manager);
                using var own = Log.Use(childManager);
                changed.SetResult();
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cached.Info("child");
            });
            Task sibling = DedicatedThread.Run(async () =>
            {
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Same(parentManager, Log.Manager);
                observed.SetResult();
            });
            await Task.WhenAll(child, sibling);
            Assert.Same(parentManager, Log.Manager);
            parent.Dispose();
            Assert.Null(Log.Manager);
            Assert.False(Log.IsConfigured);
            Assert.Equal("child", Assert.Single(childSink.Entries.Entries).Message);
        }
    }

    [Fact]
    public void ExceptionUnwindRestoresAndOutOfOrderDisposeDoesNotChangeTheScope()
    {
        Log.Shutdown();
        var (outerManager, _) = Manager("outer");
        var (innerManager, _) = Manager("inner");
        using (outerManager)
        using (innerManager)
        {
            using var outer = Log.Use(outerManager);
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var inner = Log.Use(innerManager);
                Assert.Throws<InvalidOperationException>(outer.Dispose);
                Assert.Same(innerManager, Log.Manager);
                throw new InvalidOperationException();
            }));
            Assert.Same(outerManager, Log.Manager);
        }
        Assert.Null(Log.Manager);
    }

    [Fact]
    public void NullManagerIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Log.Use(null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrashReportsIgnoreConflictingScopes(bool injected)
    {
        var (process, processSink) = Manager("process");
        var (pinned, pinnedSink) = Manager("pinned");
        var (scoped, scopedSink) = Manager("scope");
        using (pinned)
        using (scoped)
            try
            {
                Log.Configure(process);
                if (injected) CrashHandler.Install(pinned);
                else CrashHandler.Install();
                using (Log.Use(scoped))
                {
                    CrashHandler.Report("failure", new InvalidOperationException("crash"), null);
                    Assert.Same(scoped, Log.Manager);
                }
                var target = injected ? pinnedSink : processSink;
                Assert.Equal("Crash", Assert.Single(target.Entries.Entries).Category);
                Assert.Equal(1, target.Flushes);
                Assert.Empty(scopedSink.Entries.Entries);
                Assert.Equal(0, scopedSink.Flushes);
                Assert.Equal(0, scopedSink.Disposals);
                Assert.Empty((injected ? processSink : pinnedSink).Entries.Entries);
            }
            finally { CrashHandler.Uninstall(); Log.Shutdown(); }
    }
}
