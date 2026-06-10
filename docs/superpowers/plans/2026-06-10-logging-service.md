# KhaozEngine Logging Service Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the minimal `KhaozEngine.Diagnostics.FileLogger` with a full logging service (levels, pluggable sinks, category loggers, a static `Log` facade over an instance `LogManager` core, non-blocking background writes, a crash-hook helper, and a promoted `AppDataPaths`), and release it as engine 4.0.0.

**Architecture:** A `LogManager` instance owns the runtime-settable minimum level, an injected `IClock`, and a list of `ILogSink`s. Category loggers (`ILogger` via `GetLogger<T>()`/`GetLogger(string)`) build a `LogEntry` on the calling thread, apply the level filter, then submit it to the manager. In async mode (default) the manager enqueues to a bounded queue drained by a single background writer thread; in synchronous mode (tests) it writes inline. Sinks (`FileSink`, `ConsoleSink`, `DebugSink`, `InMemorySink`) each apply their own optional threshold and never throw. A static `Log` facade wraps one ambient manager so games call `Log.For<T>().Info(...)`.

**Tech Stack:** C# / net10.0, `Nullable enable`, `ImplicitUsings disable` (every file needs explicit `using`s), pure `System.*` (no MonoGame), xUnit.

**Scope:** This plan covers the engine package + tests + `FileLogger` removal + `AppDataPaths` promotion + docs + the 4.0.0 release. The three consumer migrations (SpaceGame, Nullwake, Hardpoint) are **separate follow-on plans** written after 4.0.0 is packed to `local-feed`, because none of them can compile against the new API until the nupkg exists. See "Follow-on plans" at the end.

**Working tree:** All work happens in the existing worktree `/Users/antonio/KhaozEngine/.claude/worktrees/logging-service` (branch `worktree-logging-service`, off `main` @ 3.0.0). Do **not** edit `KhaozEngine.slnx`, `KhaozEngine.Tests.csproj`, `Directory.Build.props`, `CHANGELOG.md`, or `docs/CONSUMERS.md`'s version line until Task 13 (release), to avoid colliding with the in-flight `worktree-batch1-promote` tree. New `.cs` files are globbed automatically and need no project edits.

**Test command (run from the worktree root):**
`dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~<ClassName>"`
Filtering by class keeps each task's run fast. The Tests project references `KhaozEngine.Diagnostics` by ProjectReference, so no `local-feed` is needed until packing.

---

## Task 0: Verify baseline green

**Files:** none (verification only).

- [ ] **Step 1: Build the Diagnostics + Tests projects**

Run: `cd /Users/antonio/KhaozEngine/.claude/worktrees/logging-service && dotnet build KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: build succeeds (restores MonoGame/xUnit on first run).

- [ ] **Step 2: Run the existing FileLogger tests to confirm a clean baseline**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~FileLoggerTests"`
Expected: PASS (8 tests). This is the behavior the new service must subsume before `FileLogger` is deleted in Task 11.

---

## Task 1: Foundational types (`LogLevel`, `LogEntry`)

**Files:**
- Create: `KhaozEngine.Diagnostics/LogLevel.cs`
- Create: `KhaozEngine.Diagnostics/LogEntry.cs`
- Test: `KhaozEngine.Tests/Logging/LogEntryTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// KhaozEngine.Tests/Logging/LogEntryTests.cs
using System;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

public class LogEntryTests
{
    [Fact]
    public void ConstructorStoresAllFields()
    {
        var ts = new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);
        var ex = new InvalidOperationException("x");
        var entry = new LogEntry(ts, LogLevel.Warn, "Boot", "started", ex);

        Assert.Equal(ts, entry.Timestamp);
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Equal("Boot", entry.Category);
        Assert.Equal("started", entry.Message);
        Assert.Same(ex, entry.Exception);
    }

    [Fact]
    public void ExceptionDefaultsToNull()
    {
        var entry = new LogEntry(DateTimeOffset.UnixEpoch, LogLevel.Info, "App", "hi");
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void LevelsAreOrderedTraceLowToFatalHigh()
    {
        Assert.True(LogLevel.Trace < LogLevel.Debug);
        Assert.True(LogLevel.Debug < LogLevel.Info);
        Assert.True(LogLevel.Info < LogLevel.Warn);
        Assert.True(LogLevel.Warn < LogLevel.Error);
        Assert.True(LogLevel.Error < LogLevel.Fatal);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LogEntryTests"`
Expected: FAIL to compile ("LogLevel does not exist", "LogEntry does not exist").

- [ ] **Step 3: Write the implementation**

```csharp
// KhaozEngine.Diagnostics/LogLevel.cs
namespace KhaozEngine.Diagnostics;

/// <summary>Severity of a log entry, ordered from most verbose (<see cref="Trace"/>) to most severe (<see cref="Fatal"/>).</summary>
public enum LogLevel
{
    /// <summary>Very fine-grained diagnostic detail.</summary>
    Trace,
    /// <summary>Debugging detail useful during development.</summary>
    Debug,
    /// <summary>Normal operational message.</summary>
    Info,
    /// <summary>Something unexpected but recoverable.</summary>
    Warn,
    /// <summary>A failure that affected an operation.</summary>
    Error,
    /// <summary>An unrecoverable failure, typically a crash.</summary>
    Fatal
}
```

```csharp
// KhaozEngine.Diagnostics/LogEntry.cs
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

    /// <summary>Creates a log entry.</summary>
    public LogEntry(DateTimeOffset timestamp, LogLevel level, string category, string message, Exception? exception = null)
    {
        Timestamp = timestamp;
        Level = level;
        Category = category ?? string.Empty;
        Message = message ?? string.Empty;
        Exception = exception;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LogEntryTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Diagnostics/LogLevel.cs KhaozEngine.Diagnostics/LogEntry.cs KhaozEngine.Tests/Logging/LogEntryTests.cs
git commit -m "feat(diagnostics): add LogLevel and LogEntry"
```

---

## Task 2: Clock abstraction (`IClock`, `SystemClock`) + test fake

**Files:**
- Create: `KhaozEngine.Diagnostics/IClock.cs`
- Create: `KhaozEngine.Diagnostics/SystemClock.cs`
- Create: `KhaozEngine.Tests/Logging/FakeClock.cs`
- Test: `KhaozEngine.Tests/Logging/SystemClockTests.cs`

- [ ] **Step 1: Write the failing test (and the reusable FakeClock helper)**

```csharp
// KhaozEngine.Tests/Logging/FakeClock.cs
using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Tests.Logging;

/// <summary>Deterministic clock for tests. Returns <see cref="Now"/> until set otherwise.</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Advances <see cref="Now"/> by the given span and returns the new value.</summary>
    public DateTimeOffset Advance(TimeSpan by) => Now += by;
}
```

```csharp
// KhaozEngine.Tests/Logging/SystemClockTests.cs
using System;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

public class SystemClockTests
{
    [Fact]
    public void NowIsCloseToDateTimeOffsetNow()
    {
        var before = DateTimeOffset.Now;
        var now = SystemClock.Instance.Now;
        var after = DateTimeOffset.Now;
        Assert.True(now >= before.AddSeconds(-1) && now <= after.AddSeconds(1));
    }

    [Fact]
    public void InstanceIsSingleton()
    {
        Assert.Same(SystemClock.Instance, SystemClock.Instance);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SystemClockTests"`
Expected: FAIL to compile ("IClock does not exist", "SystemClock does not exist").

- [ ] **Step 3: Write the implementation**

```csharp
// KhaozEngine.Diagnostics/IClock.cs
using System;

namespace KhaozEngine.Diagnostics;

/// <summary>Supplies the current wall-clock time for log timestamps. Injectable so tests stay deterministic.</summary>
public interface IClock
{
    /// <summary>The current time.</summary>
    DateTimeOffset Now { get; }
}
```

```csharp
// KhaozEngine.Diagnostics/SystemClock.cs
using System;

namespace KhaozEngine.Diagnostics;

/// <summary>Default <see cref="IClock"/> backed by <see cref="DateTimeOffset.Now"/>.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>Shared instance.</summary>
    public static readonly SystemClock Instance = new();

    private SystemClock() { }

    /// <inheritdoc />
    public DateTimeOffset Now => DateTimeOffset.Now;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SystemClockTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Diagnostics/IClock.cs KhaozEngine.Diagnostics/SystemClock.cs KhaozEngine.Tests/Logging/FakeClock.cs KhaozEngine.Tests/Logging/SystemClockTests.cs
git commit -m "feat(diagnostics): add IClock + SystemClock and test FakeClock"
```

---

## Task 3: Sink interface + in-memory sink (`ILogSink`, `InMemorySink`)

**Files:**
- Create: `KhaozEngine.Diagnostics/ILogSink.cs`
- Create: `KhaozEngine.Diagnostics/Sinks/InMemorySink.cs`
- Test: `KhaozEngine.Tests/Logging/InMemorySinkTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// KhaozEngine.Tests/Logging/InMemorySinkTests.cs
using System;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

public class InMemorySinkTests
{
    private static LogEntry Entry(string msg, LogLevel level = LogLevel.Info) =>
        new(DateTimeOffset.UnixEpoch, level, "Test", msg);

    [Fact]
    public void EmitCapturesEntriesInOrder()
    {
        var sink = new InMemorySink();
        sink.Emit(Entry("a"));
        sink.Emit(Entry("b"));

        Assert.Collection(sink.Entries,
            e => Assert.Equal("a", e.Message),
            e => Assert.Equal("b", e.Message));
    }

    [Fact]
    public void EntriesIsASnapshotNotLiveView()
    {
        var sink = new InMemorySink();
        sink.Emit(Entry("a"));
        var snapshot = sink.Entries;
        sink.Emit(Entry("b"));
        Assert.Single(snapshot);
    }

    [Fact]
    public void ClearRemovesAllEntries()
    {
        var sink = new InMemorySink();
        sink.Emit(Entry("a"));
        sink.Clear();
        Assert.Empty(sink.Entries);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~InMemorySinkTests"`
Expected: FAIL to compile ("ILogSink/InMemorySink do not exist").

- [ ] **Step 3: Write the implementation**

```csharp
// KhaozEngine.Diagnostics/ILogSink.cs
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
```

```csharp
// KhaozEngine.Diagnostics/Sinks/InMemorySink.cs
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~InMemorySinkTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Diagnostics/ILogSink.cs KhaozEngine.Diagnostics/Sinks/InMemorySink.cs KhaozEngine.Tests/Logging/InMemorySinkTests.cs
git commit -m "feat(diagnostics): add ILogSink and InMemorySink"
```

---

## Task 4: Synchronous core (`ILogger`, `CategoryLogger`, `LogFormatter`, `LoggerOptions`, `LogManager` sync path)

**Files:**
- Create: `KhaozEngine.Diagnostics/ILogger.cs`
- Create: `KhaozEngine.Diagnostics/LogFormatter.cs`
- Create: `KhaozEngine.Diagnostics/LoggerOptions.cs`
- Create: `KhaozEngine.Diagnostics/CategoryLogger.cs`
- Create: `KhaozEngine.Diagnostics/LogManager.cs`
- Test: `KhaozEngine.Tests/Logging/LogManagerSyncTests.cs`

Note: this task builds the **synchronous** write path only. `Submit` writes inline regardless of the `Synchronous` flag; Task 5 adds the background-thread async path. All tests here set `Synchronous = true` explicitly so they remain green after Task 5.

- [ ] **Step 1: Write the failing test**

```csharp
// KhaozEngine.Tests/Logging/LogManagerSyncTests.cs
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LogManagerSyncTests"`
Expected: FAIL to compile (`ILogger`, `LogFormatter`, `LoggerOptions`, `LogManager` do not exist).

- [ ] **Step 3: Write the implementations**

```csharp
// KhaozEngine.Diagnostics/ILogger.cs
using System;

namespace KhaozEngine.Diagnostics;

/// <summary>Logs messages under a fixed category. Obtain one from <see cref="LogManager.GetLogger(string)"/> or the static <c>Log</c> facade.</summary>
public interface ILogger
{
    /// <summary>The category/component tag this logger stamps on every entry.</summary>
    string Category { get; }

    /// <summary>True when entries at <paramref name="level"/> would be recorded.</summary>
    bool IsEnabled(LogLevel level);

    /// <summary>Logs a message at an explicit level.</summary>
    void Log(LogLevel level, string message, Exception? exception = null);

    /// <summary>Logs at <see cref="LogLevel.Trace"/>.</summary>
    void Trace(string message, Exception? exception = null);

    /// <summary>Logs at <see cref="LogLevel.Debug"/>.</summary>
    void Debug(string message, Exception? exception = null);

    /// <summary>Logs at <see cref="LogLevel.Info"/>.</summary>
    void Info(string message, Exception? exception = null);

    /// <summary>Logs at <see cref="LogLevel.Warn"/>.</summary>
    void Warn(string message, Exception? exception = null);

    /// <summary>Logs at <see cref="LogLevel.Error"/>.</summary>
    void Error(string message, Exception? exception = null);

    /// <summary>Logs at <see cref="LogLevel.Fatal"/>.</summary>
    void Fatal(string message, Exception? exception = null);
}
```

```csharp
// KhaozEngine.Diagnostics/LogFormatter.cs
using System.Globalization;
using System.Text;

namespace KhaozEngine.Diagnostics;

/// <summary>Default text layout for a <see cref="LogEntry"/>: <c>[ts] [LEVEL] [Category] message</c> with any exception appended.</summary>
public static class LogFormatter
{
    /// <summary>Formats an entry as a single string (exception text follows on a new line).</summary>
    public static string Format(in LogEntry entry)
    {
        var sb = new StringBuilder(64);
        sb.Append('[').Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("] [")
          .Append(Tag(entry.Level)).Append("] [").Append(entry.Category).Append("] ").Append(entry.Message);
        if (entry.Exception is not null)
        {
            sb.Append('\n').Append(entry.Exception);
        }
        return sb.ToString();
    }

    /// <summary>The uppercase tag for a level.</summary>
    public static string Tag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info  => "INFO",
        LogLevel.Warn  => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Fatal => "FATAL",
        _ => "INFO"
    };
}
```

```csharp
// KhaozEngine.Diagnostics/LoggerOptions.cs
using System.Collections.Generic;

namespace KhaozEngine.Diagnostics;

/// <summary>Construction-time configuration for a <see cref="LogManager"/>.</summary>
public sealed class LoggerOptions
{
    /// <summary>Entries below this level are dropped. Runtime-adjustable via <see cref="LogManager.MinimumLevel"/>.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>When true, writes happen inline on the calling thread (deterministic; used by tests). When false, a background writer thread drains a queue.</summary>
    public bool Synchronous { get; set; }

    /// <summary>Bounded async queue capacity. When full, entries are dropped (counted in <see cref="LogManager.DroppedCount"/>).</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>Clock used for entry timestamps.</summary>
    public IClock Clock { get; set; } = SystemClock.Instance;

    /// <summary>Category used by the convenience <see cref="Log"/> methods.</summary>
    public string DefaultCategory { get; set; } = "App";

    /// <summary>Sinks attached at construction. More can be added later via <see cref="LogManager.AddSink"/>.</summary>
    public IList<ILogSink> Sinks { get; } = new List<ILogSink>();
}
```

```csharp
// KhaozEngine.Diagnostics/CategoryLogger.cs
using System;

namespace KhaozEngine.Diagnostics;

/// <summary>An <see cref="ILogger"/> bound to one category, delegating to its owning <see cref="LogManager"/>.</summary>
internal sealed class CategoryLogger : ILogger
{
    private readonly LogManager owner;

    public string Category { get; }

    public CategoryLogger(LogManager owner, string category)
    {
        this.owner = owner;
        Category = category ?? string.Empty;
    }

    public bool IsEnabled(LogLevel level) => owner.IsEnabled(level);

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (!owner.IsEnabled(level)) return;
        owner.Submit(new LogEntry(owner.Now, level, Category, message ?? string.Empty, exception));
    }

    public void Trace(string message, Exception? exception = null) => Log(LogLevel.Trace, message, exception);
    public void Debug(string message, Exception? exception = null) => Log(LogLevel.Debug, message, exception);
    public void Info(string message, Exception? exception = null)  => Log(LogLevel.Info,  message, exception);
    public void Warn(string message, Exception? exception = null)  => Log(LogLevel.Warn,  message, exception);
    public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);
    public void Fatal(string message, Exception? exception = null) => Log(LogLevel.Fatal, message, exception);
}
```

```csharp
// KhaozEngine.Diagnostics/LogManager.cs
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

    /// <summary>The default category used by <see cref="Log"/>'s convenience methods.</summary>
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

    /// <summary>Submits an entry to the write path. (Task 5 adds the async branch.)</summary>
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LogManagerSyncTests"`
Expected: PASS (11 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Diagnostics/ILogger.cs KhaozEngine.Diagnostics/LogFormatter.cs KhaozEngine.Diagnostics/LoggerOptions.cs KhaozEngine.Diagnostics/CategoryLogger.cs KhaozEngine.Diagnostics/LogManager.cs KhaozEngine.Tests/Logging/LogManagerSyncTests.cs
git commit -m "feat(diagnostics): add synchronous LogManager core, ILogger, formatter"
```

---

## Task 5: Async writer thread (queue, `Flush` barrier, `Shutdown`, `DroppedCount`)

**Files:**
- Modify: `KhaozEngine.Diagnostics/LogManager.cs`
- Test: `KhaozEngine.Tests/Logging/LogManagerAsyncTests.cs`

Approach: introduce a private `WorkItem` (either a `LogEntry` or a flush marker holding a `ManualResetEventSlim`), a bounded `BlockingCollection<WorkItem>`, and a single background writer thread. `Submit` enqueues non-blocking (`TryAdd`; on failure increment `DroppedCount`). `Flush` enqueues a flush marker and waits for the writer to reach it (so all entries queued before the call are written and sinks flushed). On flush, if entries were dropped since the last report, write one synthetic `Warn` entry. `Shutdown` completes the queue, joins the thread, then flushes/disposes sinks. Synchronous mode keeps the inline path from Task 4.

- [ ] **Step 1: Write the failing test**

```csharp
// KhaozEngine.Tests/Logging/LogManagerAsyncTests.cs
using System;
using System.Threading;
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
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LogManagerAsyncTests"`
Expected: FAIL (`DroppedCount` does not exist; async tests time out or see 0 entries because Task 4's `Submit` writes inline only and there is no queue/Flush barrier).

- [ ] **Step 3: Modify `LogManager` to add the async path**

Replace the entire contents of `KhaozEngine.Diagnostics/LogManager.cs` with:

```csharp
// KhaozEngine.Diagnostics/LogManager.cs
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

    /// <summary>The default category used by <see cref="Log"/>'s convenience methods.</summary>
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
        if (!queue!.TryAdd(new WorkItem(entry)))
        {
            Interlocked.Increment(ref dropped);
        }
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

        if (queue!.IsAddingCompleted) { ReportDropsIfAny(); FlushSinks(); return; }

        // Push a flush marker and wait for the writer to reach it. The writer reports any drops and
        // flushes sinks while handling the marker, so all entries queued before this call are written
        // first. The drop warning is written by the writer thread, never re-enqueued (so it can't itself
        // be dropped when the queue is full).
        using var done = new ManualResetEventSlim(false);
        try
        {
            queue.Add(new WorkItem(done));   // flush markers must not be dropped; brief block is acceptable off the hot path
            done.Wait();
        }
        catch (InvalidOperationException)
        {
            ReportDropsIfAny();
            FlushSinks();   // queue completed concurrently
        }
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
```

- [ ] **Step 4: Run the async and the Task 4 sync tests to verify both pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LogManagerAsyncTests|FullyQualifiedName~LogManagerSyncTests"`
Expected: PASS (all of Task 4's sync tests still green + 5 async tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Diagnostics/LogManager.cs KhaozEngine.Tests/Logging/LogManagerAsyncTests.cs
git commit -m "feat(diagnostics): add non-blocking background writer with flush barrier"
```

---

## Task 6: Console + Debug sinks (`ConsoleSink`, `DebugSink`)

**Files:**
- Create: `KhaozEngine.Diagnostics/Sinks/ConsoleSink.cs`
- Create: `KhaozEngine.Diagnostics/Sinks/DebugSink.cs`
- Test: `KhaozEngine.Tests/Logging/ConsoleSinkTests.cs`
- Test: `KhaozEngine.Tests/Logging/DebugSinkTests.cs`

Note: `DebugSink` writes via `System.Diagnostics.Trace` (not `Debug`) so it survives Release builds and is testable with a `TraceListener`.

- [ ] **Step 1: Write the failing tests**

```csharp
// KhaozEngine.Tests/Logging/ConsoleSinkTests.cs
using System;
using System.IO;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

public class ConsoleSinkTests
{
    private static LogEntry Entry(string msg, LogLevel level) =>
        new(new DateTimeOffset(2026, 6, 10, 1, 2, 3, TimeSpan.Zero), level, "Cat", msg);

    [Fact]
    public void EmitWritesFormattedLineToStdout()
    {
        var originalOut = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            using var sink = new ConsoleSink();
            sink.Emit(Entry("hello", LogLevel.Info));
        }
        finally { Console.SetOut(originalOut); }

        Assert.Contains("[INFO] [Cat] hello", buffer.ToString());
    }

    [Fact]
    public void ErrorsGoToStdErrWhenEnabled()
    {
        var originalErr = Console.Error;
        var errBuffer = new StringWriter();
        Console.SetError(errBuffer);
        try
        {
            using var sink = new ConsoleSink(useStdErrForErrors: true);
            sink.Emit(Entry("boom", LogLevel.Error));
        }
        finally { Console.SetError(originalErr); }

        Assert.Contains("[ERROR] [Cat] boom", errBuffer.ToString());
    }

    [Fact]
    public void BelowSinkMinimumLevelIsSkipped()
    {
        var originalOut = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            using var sink = new ConsoleSink(minimumLevel: LogLevel.Warn);
            sink.Emit(Entry("verbose", LogLevel.Info));
        }
        finally { Console.SetOut(originalOut); }

        Assert.Equal(string.Empty, buffer.ToString().Trim());
    }
}
```

```csharp
// KhaozEngine.Tests/Logging/DebugSinkTests.cs
using System;
using System.Diagnostics;
using System.Text;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

public class DebugSinkTests
{
    private sealed class CapturingListener : TraceListener
    {
        public readonly StringBuilder Output = new();
        public override void Write(string? message) => Output.Append(message);
        public override void WriteLine(string? message) => Output.Append(message).Append('\n');
    }

    [Fact]
    public void EmitWritesToTraceListeners()
    {
        var listener = new CapturingListener();
        Trace.Listeners.Add(listener);
        try
        {
            using var sink = new DebugSink();
            sink.Emit(new LogEntry(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero), LogLevel.Info, "Cat", "trace me"));
        }
        finally { Trace.Listeners.Remove(listener); }

        Assert.Contains("[INFO] [Cat] trace me", listener.Output.ToString());
    }

    [Fact]
    public void BelowSinkMinimumLevelIsSkipped()
    {
        var listener = new CapturingListener();
        Trace.Listeners.Add(listener);
        try
        {
            using var sink = new DebugSink(minimumLevel: LogLevel.Error);
            sink.Emit(new LogEntry(DateTimeOffset.UnixEpoch, LogLevel.Info, "Cat", "skip"));
        }
        finally { Trace.Listeners.Remove(listener); }

        Assert.Equal(string.Empty, listener.Output.ToString());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ConsoleSinkTests|FullyQualifiedName~DebugSinkTests"`
Expected: FAIL to compile (`ConsoleSink`/`DebugSink` do not exist).

- [ ] **Step 3: Write the implementations**

```csharp
// KhaozEngine.Diagnostics/Sinks/ConsoleSink.cs
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
```

```csharp
// KhaozEngine.Diagnostics/Sinks/DebugSink.cs
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ConsoleSinkTests|FullyQualifiedName~DebugSinkTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Diagnostics/Sinks/ConsoleSink.cs KhaozEngine.Diagnostics/Sinks/DebugSink.cs KhaozEngine.Tests/Logging/ConsoleSinkTests.cs KhaozEngine.Tests/Logging/DebugSinkTests.cs
git commit -m "feat(diagnostics): add ConsoleSink and DebugSink"
```

---

## Task 7: Rotating file sink (`FileSink`, `FileSinkOptions`)

**Files:**
- Create: `KhaozEngine.Diagnostics/Sinks/FileSinkOptions.cs`
- Create: `KhaozEngine.Diagnostics/Sinks/FileSink.cs`
- Test: `KhaozEngine.Tests/Logging/FileSinkTests.cs`

Rotation rules: rotate-on-launch copies an existing active log to `PreviousPath` (when set). Size-based rotation, when `MaxBytes` is set, renames the active file to `Path.1` (shifting `Path.1`→`Path.2`, ... up to `MaxFiles` archives, pruning the oldest) and reopens a fresh active file. The writer uses `AutoFlush = true` so entries survive a hard crash; writes happen on the manager's writer thread, so this never blocks the game loop.

- [ ] **Step 1: Write the failing test**

```csharp
// KhaozEngine.Tests/Logging/FileSinkTests.cs
using System;
using System.IO;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

public class FileSinkTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "khaoz-filesink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static LogEntry Entry(string msg, LogLevel level = LogLevel.Info) =>
        new(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero), level, "Cat", msg);

    [Fact]
    public void EmitWritesFormattedLineToFile()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "game.log");
        try
        {
            using (var sink = new FileSink(new FileSinkOptions { Path = path }))
            {
                sink.Emit(Entry("hello"));
            }
            Assert.Contains("[INFO] [Cat] hello", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RotatesExistingLogToPreviousPathOnOpen()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "game.log");
        string prev = Path.Combine(dir, "game.prev.log");
        try
        {
            using (var first = new FileSink(new FileSinkOptions { Path = path, PreviousPath = prev }))
                first.Emit(Entry("session one"));
            using (var second = new FileSink(new FileSinkOptions { Path = path, PreviousPath = prev }))
                second.Emit(Entry("session two"));

            Assert.Contains("session one", File.ReadAllText(prev));
            string current = File.ReadAllText(path);
            Assert.Contains("session two", current);
            Assert.DoesNotContain("session one", current);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WithoutPreviousPathDoesNotRotate()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "game.log");
        string prev = Path.Combine(dir, "game.prev.log");
        try
        {
            using (var first = new FileSink(new FileSinkOptions { Path = path }))
                first.Emit(Entry("one"));
            using (var second = new FileSink(new FileSinkOptions { Path = path }))
                second.Emit(Entry("two"));
            Assert.False(File.Exists(prev));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SizeBasedRotationCreatesArchivesAndPrunesToMaxFiles()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "game.log");
        try
        {
            // Small MaxBytes so each ~30-byte line forces a roll; keep at most 2 archives.
            var options = new FileSinkOptions { Path = path, MaxBytes = 20, MaxFiles = 2 };
            using (var sink = new FileSink(options))
            {
                for (int i = 0; i < 5; i++) sink.Emit(Entry("line" + i));
            }

            Assert.True(File.Exists(path));                       // active
            Assert.True(File.Exists(path + ".1"));                // newest archive
            Assert.True(File.Exists(path + ".2"));                // older archive
            Assert.False(File.Exists(path + ".3"));               // pruned beyond MaxFiles
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BelowSinkMinimumLevelIsSkipped()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "game.log");
        try
        {
            using (var sink = new FileSink(new FileSinkOptions { Path = path, MinimumLevel = LogLevel.Error }))
            {
                sink.Emit(Entry("verbose", LogLevel.Info));
                sink.Emit(Entry("boom", LogLevel.Error));
            }
            string text = File.ReadAllText(path);
            Assert.DoesNotContain("verbose", text);
            Assert.Contains("boom", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EmitOnUnwritablePathNeverThrows()
    {
        // A directory that cannot be created (path under an existing file) must not throw.
        string dir = TempDir();
        string fileAsDir = Path.Combine(dir, "afile");
        File.WriteAllText(fileAsDir, "x");
        string badPath = Path.Combine(fileAsDir, "nested", "game.log");
        try
        {
            using var sink = new FileSink(new FileSinkOptions { Path = badPath });
            var ex = Record.Exception(() => sink.Emit(Entry("hello")));
            Assert.Null(ex);
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~FileSinkTests"`
Expected: FAIL to compile (`FileSink`/`FileSinkOptions` do not exist).

- [ ] **Step 3: Write the implementations**

```csharp
// KhaozEngine.Diagnostics/Sinks/FileSinkOptions.cs
using System;

namespace KhaozEngine.Diagnostics;

/// <summary>Configuration for a <see cref="FileSink"/>.</summary>
public sealed class FileSinkOptions
{
    /// <summary>Active log file path (required).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>When set, an existing active log is copied here on open (rotate-on-launch).</summary>
    public string? PreviousPath { get; set; }

    /// <summary>When set, the active file rotates to a numbered archive once it reaches this many bytes.</summary>
    public long? MaxBytes { get; set; }

    /// <summary>Maximum number of numbered archives to retain (oldest pruned). Defaults to 1 when size rotation is on.</summary>
    public int? MaxFiles { get; set; }

    /// <summary>Optional per-sink threshold; entries below it are skipped.</summary>
    public LogLevel? MinimumLevel { get; set; }

    /// <summary>Optional custom line formatter. Defaults to <see cref="LogFormatter.Format"/>.</summary>
    public Func<LogEntry, string>? Formatter { get; set; }
}
```

```csharp
// KhaozEngine.Diagnostics/Sinks/FileSink.cs
using System;
using System.IO;
using System.Text;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// Writes formatted entries to a file with rotate-on-launch plus optional size-based rotation and
/// retention. Uses an <c>AutoFlush</c> writer so entries survive a hard crash. Thread-safe; never throws.
/// </summary>
public sealed class FileSink : ILogSink
{
    private readonly object gate = new();
    private readonly FileSinkOptions options;
    private readonly Func<LogEntry, string> formatter;
    private StreamWriter? writer;
    private long bytesWritten;

    /// <summary>Opens the sink, performing rotate-on-launch if configured.</summary>
    public FileSink(FileSinkOptions options, IClock? clock = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.Path)) throw new ArgumentException("FileSinkOptions.Path is required.", nameof(options));
        formatter = options.Formatter ?? (e => LogFormatter.Format(e));
        // clock is currently unused (archive naming is index-based, not time-based); accepted for API symmetry.
        Open();
    }

    private void Open()
    {
        try
        {
            string? dir = Path.GetDirectoryName(options.Path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            if (!string.IsNullOrWhiteSpace(options.PreviousPath) && File.Exists(options.Path))
            {
                try { File.Copy(options.Path, options.PreviousPath!, overwrite: true); }
                catch { /* best-effort rotation */ }
            }

            writer = new StreamWriter(options.Path, append: false, Encoding.UTF8) { AutoFlush = true };
            bytesWritten = 0;
        }
        catch
        {
            writer = null;   // fall back silently; Emit becomes a no-op
        }
    }

    /// <inheritdoc />
    public void Emit(in LogEntry entry)
    {
        if (options.MinimumLevel.HasValue && entry.Level < options.MinimumLevel.Value) return;
        lock (gate)
        {
            if (writer is null) return;
            try
            {
                string line = formatter(entry);
                writer.WriteLine(line);
                bytesWritten += line.Length + Environment.NewLine.Length;
                if (options.MaxBytes is long max && bytesWritten >= max)
                {
                    RollBySize();
                }
            }
            catch { /* never throw */ }
        }
    }

    private void RollBySize()
    {
        try
        {
            writer!.Flush();
            writer.Dispose();
            writer = null;

            int keep = options.MaxFiles is int n && n > 0 ? n : 1;

            string oldest = options.Path + "." + keep;
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = keep - 1; i >= 1; i--)
            {
                string src = options.Path + "." + i;
                string dst = options.Path + "." + (i + 1);
                if (File.Exists(src)) File.Move(src, dst, overwrite: true);
            }
            if (File.Exists(options.Path)) File.Move(options.Path, options.Path + ".1", overwrite: true);

            writer = new StreamWriter(options.Path, append: false, Encoding.UTF8) { AutoFlush = true };
            bytesWritten = 0;
        }
        catch
        {
            writer = null;
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        lock (gate)
        {
            try { writer?.Flush(); }
            catch { /* best-effort */ }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
        {
            try { writer?.Flush(); writer?.Dispose(); }
            catch { /* best-effort */ }
            writer = null;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~FileSinkTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Diagnostics/Sinks/FileSinkOptions.cs KhaozEngine.Diagnostics/Sinks/FileSink.cs KhaozEngine.Tests/Logging/FileSinkTests.cs
git commit -m "feat(diagnostics): add rotating FileSink with size rotation + retention"
```

---

## Task 8: Static ambient facade (`Log`, `NullLogger`)

**Files:**
- Create: `KhaozEngine.Diagnostics/NullLogger.cs`
- Create: `KhaozEngine.Diagnostics/Log.cs`
- Test: `KhaozEngine.Tests/Logging/LogFacadeTests.cs`

Note: `Log` holds process-wide static state, so tests must reset it. Each test calls `Log.Shutdown()` in a `finally` to detach the manager.

- [ ] **Step 1: Write the failing test**

```csharp
// KhaozEngine.Tests/Logging/LogFacadeTests.cs
using System;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

[Collection("LogFacade")]   // static state: do not parallelize with other facade tests
public class LogFacadeTests
{
    private static (LoggerOptions options, InMemorySink sink) SyncOptions()
    {
        var sink = new InMemorySink();
        var options = new LoggerOptions { Synchronous = true, MinimumLevel = LogLevel.Trace, DefaultCategory = "App" };
        options.Sinks.Add(sink);
        return (options, sink);
    }

    [Fact]
    public void CallsBeforeConfigureAreNoOps()
    {
        Log.Shutdown();   // ensure unconfigured
        Assert.False(Log.IsConfigured);
        var ex = Record.Exception(() =>
        {
            Log.Info("nobody home");
            Log.For<LogFacadeTests>().Error("still nobody");
        });
        Assert.Null(ex);
        Assert.NotNull(Log.For<LogFacadeTests>());   // returns a no-op logger, never null
    }

    [Fact]
    public void ConfigureRoutesToManager()
    {
        var (options, sink) = SyncOptions();
        try
        {
            Log.Configure(options);
            Assert.True(Log.IsConfigured);
            Log.For<LogFacadeTests>().Info("routed");
            var e = Assert.Single(sink.Entries);
            Assert.Equal(nameof(LogFacadeTests), e.Category);
            Assert.Equal("routed", e.Message);
        }
        finally { Log.Shutdown(); }
    }

    [Fact]
    public void ConvenienceMethodsUseDefaultCategory()
    {
        var (options, sink) = SyncOptions();
        try
        {
            Log.Configure(options);
            Log.Warn("careful");
            var e = Assert.Single(sink.Entries);
            Assert.Equal("App", e.Category);
            Assert.Equal(LogLevel.Warn, e.Level);
        }
        finally { Log.Shutdown(); }
    }

    [Fact]
    public void MinimumLevelDelegatesToManager()
    {
        var (options, _) = SyncOptions();
        try
        {
            Log.Configure(options);
            Log.MinimumLevel = LogLevel.Error;
            Assert.Equal(LogLevel.Error, Log.MinimumLevel);
        }
        finally { Log.Shutdown(); }
    }

    [Fact]
    public void ReconfiguringShutsDownThePreviousManager()
    {
        var firstSink = new InMemorySink();
        var firstOptions = new LoggerOptions { Synchronous = true };
        firstOptions.Sinks.Add(firstSink);
        var (secondOptions, secondSink) = SyncOptions();
        try
        {
            Log.Configure(firstOptions);
            Log.Configure(secondOptions);   // replaces + shuts down the first
            Log.Get("X").Info("to second");

            Assert.Empty(firstSink.Entries);
            Assert.Single(secondSink.Entries);
        }
        finally { Log.Shutdown(); }
    }

    [Fact]
    public void ShutdownDetachesManager()
    {
        var (options, _) = SyncOptions();
        Log.Configure(options);
        Log.Shutdown();
        Assert.False(Log.IsConfigured);
        Assert.Null(Log.Manager);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LogFacadeTests"`
Expected: FAIL to compile (`Log` does not exist).

- [ ] **Step 3: Write the implementations**

```csharp
// KhaozEngine.Diagnostics/NullLogger.cs
using System;

namespace KhaozEngine.Diagnostics;

/// <summary>A logger that discards everything. Returned by <see cref="Log"/> before configuration.</summary>
internal sealed class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();

    private NullLogger() { }

    public string Category => string.Empty;
    public bool IsEnabled(LogLevel level) => false;
    public void Log(LogLevel level, string message, Exception? exception = null) { }
    public void Trace(string message, Exception? exception = null) { }
    public void Debug(string message, Exception? exception = null) { }
    public void Info(string message, Exception? exception = null) { }
    public void Warn(string message, Exception? exception = null) { }
    public void Error(string message, Exception? exception = null) { }
    public void Fatal(string message, Exception? exception = null) { }
}
```

```csharp
// KhaozEngine.Diagnostics/Log.cs
using System;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// Process-wide ambient logging facade over a single configured <see cref="LogManager"/>. Games call
/// <see cref="Configure(LoggerOptions)"/> once at startup, then log via <see cref="For{T}"/> /
/// <see cref="Get(string)"/> or the convenience methods. Calls before configuration are safe no-ops.
/// </summary>
public static class Log
{
    private static readonly object gate = new();
    private static LogManager? manager;

    /// <summary>True once a manager has been configured.</summary>
    public static bool IsConfigured { get { lock (gate) { return manager is not null; } } }

    /// <summary>The configured manager, or <c>null</c>.</summary>
    public static LogManager? Manager { get { lock (gate) { return manager; } } }

    /// <summary>Builds and adopts a manager from <paramref name="options"/>.</summary>
    public static void Configure(LoggerOptions options) => Configure(new LogManager(options));

    /// <summary>Adopts an existing manager (for example one built via DI). Shuts down any previous manager.</summary>
    public static void Configure(LogManager newManager)
    {
        LogManager? previous;
        lock (gate)
        {
            previous = manager;
            manager = newManager;
        }
        previous?.Shutdown();
    }

    /// <summary>Minimum level of the configured manager (no-op getter returns <see cref="LogLevel.Info"/> when unconfigured).</summary>
    public static LogLevel MinimumLevel
    {
        get { return Manager?.MinimumLevel ?? LogLevel.Info; }
        set { var m = Manager; if (m is not null) m.MinimumLevel = value; }
    }

    /// <summary>Returns a logger for category <c>typeof(T).Name</c>, or a no-op logger when unconfigured.</summary>
    public static ILogger For<T>() => Manager?.GetLogger<T>() ?? NullLogger.Instance;

    /// <summary>Returns a logger for <paramref name="category"/>, or a no-op logger when unconfigured.</summary>
    public static ILogger Get(string category) => Manager?.GetLogger(category) ?? NullLogger.Instance;

    private static ILogger Default()
    {
        var m = Manager;
        return m is null ? NullLogger.Instance : m.GetLogger(m.DefaultCategory);
    }

    /// <summary>Logs at <see cref="LogLevel.Trace"/> under the default category.</summary>
    public static void Trace(string message, Exception? exception = null) => Default().Trace(message, exception);
    /// <summary>Logs at <see cref="LogLevel.Debug"/> under the default category.</summary>
    public static void Debug(string message, Exception? exception = null) => Default().Debug(message, exception);
    /// <summary>Logs at <see cref="LogLevel.Info"/> under the default category.</summary>
    public static void Info(string message, Exception? exception = null) => Default().Info(message, exception);
    /// <summary>Logs at <see cref="LogLevel.Warn"/> under the default category.</summary>
    public static void Warn(string message, Exception? exception = null) => Default().Warn(message, exception);
    /// <summary>Logs at <see cref="LogLevel.Error"/> under the default category.</summary>
    public static void Error(string message, Exception? exception = null) => Default().Error(message, exception);
    /// <summary>Logs at <see cref="LogLevel.Fatal"/> under the default category.</summary>
    public static void Fatal(string message, Exception? exception = null) => Default().Fatal(message, exception);

    /// <summary>Flushes the configured manager (no-op when unconfigured).</summary>
    public static void Flush() => Manager?.Flush();

    /// <summary>Shuts down and detaches the configured manager.</summary>
    public static void Shutdown()
    {
        LogManager? previous;
        lock (gate)
        {
            previous = manager;
            manager = null;
        }
        previous?.Shutdown();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LogFacadeTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Diagnostics/NullLogger.cs KhaozEngine.Diagnostics/Log.cs KhaozEngine.Tests/Logging/LogFacadeTests.cs
git commit -m "feat(diagnostics): add static Log facade over ambient LogManager"
```

---

## Task 9: Crash-hook helper (`CrashHandler`)

**Files:**
- Modify: `KhaozEngine.Diagnostics/KhaozEngine.Diagnostics.csproj` (add `InternalsVisibleTo`)
- Create: `KhaozEngine.Diagnostics/CrashHandler.cs`
- Test: `KhaozEngine.Tests/Logging/CrashHandlerTests.cs`

The fatal-report logic is exposed as an `internal` method so tests can invoke it without raising a real process-level unhandled exception.

- [ ] **Step 1: Add `InternalsVisibleTo` to the Diagnostics project**

Edit `KhaozEngine.Diagnostics/KhaozEngine.Diagnostics.csproj` to add the item group (matches the pattern already in `KhaozEngine.UI.csproj`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Diagnostics</PackageId>
    <Description>Game-agnostic diagnostics: thread-safe timestamped file logger with previous-session rotation. Pure System.IO, no MonoGame dependency.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```

(The `<Description>` is updated to describe the full service in Task 11.)

- [ ] **Step 2: Write the failing test**

```csharp
// KhaozEngine.Tests/Logging/CrashHandlerTests.cs
using System;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

[Collection("LogFacade")]   // CrashHandler holds static state like Log
public class CrashHandlerTests
{
    private static (LogManager mgr, InMemorySink sink) SyncManager()
    {
        var sink = new InMemorySink();
        var options = new LoggerOptions { Synchronous = true, MinimumLevel = LogLevel.Trace };
        options.Sinks.Add(sink);
        return (new LogManager(options), sink);
    }

    [Fact]
    public void ReportLogsFatalCrashEntryWithException()
    {
        var (mgr, sink) = SyncManager();
        try
        {
            CrashHandler.Install(mgr);
            CrashHandler.Report("Unhandled exception", new InvalidOperationException("kaboom"), null);

            var e = Assert.Single(sink.Entries);
            Assert.Equal(LogLevel.Fatal, e.Level);
            Assert.Equal("Crash", e.Category);
            Assert.Contains("Unhandled exception", e.Message);
            Assert.NotNull(e.Exception);
            Assert.Equal("kaboom", e.Exception!.Message);
        }
        finally { CrashHandler.Uninstall(); }
    }

    [Fact]
    public void ReportWithoutExceptionStillLogsRawObject()
    {
        var (mgr, sink) = SyncManager();
        try
        {
            CrashHandler.Install(mgr);
            CrashHandler.Report("Unhandled exception object", null, "weird-non-exception");
            var e = Assert.Single(sink.Entries);
            Assert.Equal(LogLevel.Fatal, e.Level);
            Assert.Contains("weird-non-exception", e.Message);
        }
        finally { CrashHandler.Uninstall(); }
    }

    [Fact]
    public void ReportWithoutInstallIsNoOp()
    {
        CrashHandler.Uninstall();   // ensure detached
        var ex = Record.Exception(() => CrashHandler.Report("nobody", new Exception("x"), null));
        Assert.Null(ex);
    }

    [Fact]
    public void InstallTwiceThenUninstallLeavesNoHandlers()
    {
        var (mgr, _) = SyncManager();
        CrashHandler.Install(mgr);
        CrashHandler.Install(mgr);   // must not double-register
        CrashHandler.Uninstall();
        // After a single Uninstall the AppDomain handler is gone; Report is now a no-op.
        var ex = Record.Exception(() => CrashHandler.Report("after", new Exception("x"), null));
        Assert.Null(ex);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CrashHandlerTests"`
Expected: FAIL to compile (`CrashHandler` does not exist).

- [ ] **Step 4: Write the implementation**

```csharp
// KhaozEngine.Diagnostics/CrashHandler.cs
using System;
using System.Threading.Tasks;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// Wires process-level crash signals (<see cref="AppDomain.UnhandledException"/> and
/// <see cref="TaskScheduler.UnobservedTaskException"/>) to a <see cref="LogManager"/>: logs a
/// <see cref="LogLevel.Fatal"/> entry under category <c>Crash</c> and flushes. On a terminating
/// unhandled exception it also shuts the manager down so the log file is closed cleanly.
/// </summary>
public static class CrashHandler
{
    private static readonly object gate = new();
    private static LogManager? target;
    private static UnhandledExceptionEventHandler? domainHandler;
    private static EventHandler<UnobservedTaskExceptionEventArgs>? taskHandler;

    /// <summary>Installs handlers that route crashes to <paramref name="manager"/>. Idempotent.</summary>
    public static void Install(LogManager manager)
    {
        if (manager is null) return;
        lock (gate)
        {
            UninstallCore();
            target = manager;
            domainHandler = (_, e) => OnUnhandled(e.ExceptionObject, e.IsTerminating);
            taskHandler = (_, e) => { Report("Unobserved task exception", e.Exception, e.Exception); e.SetObserved(); };
            AppDomain.CurrentDomain.UnhandledException += domainHandler;
            TaskScheduler.UnobservedTaskException += taskHandler;
        }
    }

    /// <summary>Installs handlers routed to the ambient <see cref="Log.Manager"/> (no-op if none).</summary>
    public static void Install()
    {
        var m = Log.Manager;
        if (m is not null) Install(m);
    }

    /// <summary>Removes any installed handlers.</summary>
    public static void Uninstall()
    {
        lock (gate) { UninstallCore(); }
    }

    private static void UninstallCore()
    {
        if (domainHandler is not null) AppDomain.CurrentDomain.UnhandledException -= domainHandler;
        if (taskHandler is not null) TaskScheduler.UnobservedTaskException -= taskHandler;
        domainHandler = null;
        taskHandler = null;
        target = null;
    }

    private static void OnUnhandled(object exceptionObject, bool isTerminating)
    {
        string context = isTerminating ? "Unhandled exception (terminating)" : "Unhandled exception";
        Report(context, exceptionObject as Exception, exceptionObject);
        if (isTerminating)
        {
            LogManager? m;
            lock (gate) { m = target; }
            m?.Shutdown();
        }
    }

    /// <summary>Logs a fatal crash entry and flushes. Exposed for testing; safe when uninstalled.</summary>
    internal static void Report(string context, Exception? exception, object? raw)
    {
        LogManager? m;
        lock (gate) { m = target; }
        if (m is null) return;

        var log = m.GetLogger("Crash");
        if (exception is not null) log.Fatal(context, exception);
        else log.Fatal($"{context}: {raw}");
        m.Flush();
    }
}
```

- [ ] **Step 5: Run test to verify it passes, then commit**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CrashHandlerTests"`
Expected: PASS (4 tests).

```bash
git add KhaozEngine.Diagnostics/KhaozEngine.Diagnostics.csproj KhaozEngine.Diagnostics/CrashHandler.cs KhaozEngine.Tests/Logging/CrashHandlerTests.cs
git commit -m "feat(diagnostics): add CrashHandler for unhandled/unobserved exceptions"
```

---

## Task 10: Promote `AppDataPaths`

**Files:**
- Create: `KhaozEngine.Diagnostics/AppDataPaths.cs`
- Test: `KhaozEngine.Tests/Logging/AppDataPathsTests.cs`

Port SpaceGame's OS-correct resolver, parameterized by app folder name and cached per name.

- [ ] **Step 1: Write the failing test**

```csharp
// KhaozEngine.Tests/Logging/AppDataPathsTests.cs
using System;
using System.IO;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

public class AppDataPathsTests
{
    [Fact]
    public void ResolveReturnsPathEndingInAppFolderName()
    {
        string app = "KhaozEngineTest_" + Guid.NewGuid().ToString("N");
        try
        {
            string dir = AppDataPaths.Resolve(app);
            Assert.EndsWith(app, dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        finally { TryDelete(app); }
    }

    [Fact]
    public void ResolveCreatesTheDirectory()
    {
        string app = "KhaozEngineTest_" + Guid.NewGuid().ToString("N");
        try
        {
            string dir = AppDataPaths.Resolve(app);
            Assert.True(Directory.Exists(dir));
        }
        finally { TryDelete(app); }
    }

    [Fact]
    public void ResolveIsCachedPerName()
    {
        string app = "KhaozEngineTest_" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Equal(AppDataPaths.Resolve(app), AppDataPaths.Resolve(app));
        }
        finally { TryDelete(app); }
    }

    [Fact]
    public void CombineJoinsUnderTheBaseDirectory()
    {
        string app = "KhaozEngineTest_" + Guid.NewGuid().ToString("N");
        try
        {
            string baseDir = AppDataPaths.Resolve(app);
            string logPath = AppDataPaths.Combine(app, "game.log");
            Assert.Equal(Path.Combine(baseDir, "game.log"), logPath);
        }
        finally { TryDelete(app); }
    }

    private static void TryDelete(string app)
    {
        try
        {
            string dir = AppDataPaths.Resolve(app);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { /* best-effort cleanup */ }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AppDataPathsTests"`
Expected: FAIL to compile (`AppDataPaths` does not exist).

- [ ] **Step 3: Write the implementation**

```csharp
// KhaozEngine.Diagnostics/AppDataPaths.cs
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// Resolves the OS-correct per-application data directory (for logs, saves, settings):
/// Windows <c>%APPDATA%\&lt;app&gt;</c>, macOS <c>~/Library/Application Support/&lt;app&gt;</c>,
/// Linux <c>$XDG_DATA_HOME/&lt;app&gt;</c> or <c>~/.local/share/&lt;app&gt;</c>, with fallbacks.
/// The directory is created on first access and the result cached per app name.
/// </summary>
public static class AppDataPaths
{
    private static readonly ConcurrentDictionary<string, string> cache = new();

    /// <summary>Returns (creating if needed) the base data directory for <paramref name="appFolderName"/>.</summary>
    public static string Resolve(string appFolderName)
    {
        if (string.IsNullOrWhiteSpace(appFolderName)) throw new ArgumentException("App folder name is required.", nameof(appFolderName));
        return cache.GetOrAdd(appFolderName, name =>
        {
            string dir = ResolveBase(name);
            try { Directory.CreateDirectory(dir); } catch { /* best-effort */ }
            return dir;
        });
    }

    /// <summary>Returns a path under the app's base directory.</summary>
    public static string Combine(string appFolderName, params string[] parts)
    {
        string baseDir = Resolve(appFolderName);
        if (parts is null || parts.Length == 0) return baseDir;
        string[] all = new string[parts.Length + 1];
        all[0] = baseDir;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }

    private static string ResolveBase(string app)
    {
        if (OperatingSystem.IsWindows())
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData)) return Path.Combine(appData, app);
        }
        else if (OperatingSystem.IsMacOS())
        {
            string appSupport = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appSupport)) return Path.Combine(appSupport, app);
        }
        else if (OperatingSystem.IsLinux())
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdg)) return Path.Combine(xdg, app);
            string? home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home)) return Path.Combine(home, ".local", "share", app);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData)) return Path.Combine(localAppData, app);

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "." + app.ToLowerInvariant());
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AppDataPathsTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Diagnostics/AppDataPaths.cs KhaozEngine.Tests/Logging/AppDataPathsTests.cs
git commit -m "feat(diagnostics): promote AppDataPaths OS-correct resolver into the engine"
```

---

## Task 11: Remove `FileLogger`, update package metadata + README

**Files:**
- Delete: `KhaozEngine.Diagnostics/FileLogger.cs`
- Delete: `KhaozEngine.Tests/FileLoggerTests.cs`
- Modify: `KhaozEngine.Diagnostics/KhaozEngine.Diagnostics.csproj` (`<Description>`)
- Modify: `KhaozEngine.Diagnostics/README.md`

- [ ] **Step 1: Delete the old logger and its tests**

```bash
git rm KhaozEngine.Diagnostics/FileLogger.cs KhaozEngine.Tests/FileLoggerTests.cs
```

- [ ] **Step 2: Update the package `<Description>`**

In `KhaozEngine.Diagnostics/KhaozEngine.Diagnostics.csproj`, change the `<Description>` line to:

```xml
    <Description>Game-agnostic logging service: levels, pluggable sinks (rotating file / console / debug / in-memory), category loggers, a static Log facade over an injectable LogManager, non-blocking background writes, crash hooks, and OS-correct AppDataPaths. Pure .NET, no MonoGame dependency.</Description>
```

- [ ] **Step 3: Replace `KhaozEngine.Diagnostics/README.md`**

Overwrite with:

```markdown
# KhaozEngine.Diagnostics

Game-agnostic logging service. Pure .NET, no MonoGame dependency.

## Quick start

```csharp
using KhaozEngine.Diagnostics;

var options = new LoggerOptions { MinimumLevel = LogLevel.Info, DefaultCategory = "Boot" };
options.Sinks.Add(new FileSink(new FileSinkOptions
{
    Path = AppDataPaths.Combine("MyGame", "game.log"),
    PreviousPath = AppDataPaths.Combine("MyGame", "game.prev.log"),
    MaxBytes = 5 * 1024 * 1024,
    MaxFiles = 3
}));
options.Sinks.Add(new ConsoleSink());

Log.Configure(options);
CrashHandler.Install();          // route unhandled/unobserved exceptions to the log

Log.For<Game>().Info("started");
// ... on exit:
Log.Shutdown();
```

## Pieces

- `Log` — static ambient facade (`Log.For<T>()`, `Log.Info(...)`, `Log.Configure`, `Log.Flush`, `Log.Shutdown`). No-op before `Configure`.
- `LogManager` + `LoggerOptions` — injectable instance core (DI/tests). Runtime-settable `MinimumLevel`. Async by default; set `Synchronous = true` for deterministic tests.
- `ILogger` — category logger (`Trace`/`Debug`/`Info`/`Warn`/`Error`/`Fatal`, each with an optional exception).
- `ILogSink` + `FileSink` (rotate-on-launch + size rotation + retention), `ConsoleSink`, `DebugSink`, `InMemorySink`. Implement `ILogSink` for custom targets (in-game console, crash uploader).
- `CrashHandler` — wires `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`.
- `AppDataPaths` — OS-correct per-app data directory.
- `IClock`/`SystemClock` — injectable timestamps.

Logging never throws and never blocks the caller (writes happen on a background thread; `Flush`/`Shutdown` drain them).
```

- [ ] **Step 4: Verify the whole test suite is green without `FileLogger`**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (all logging tests; no `FileLoggerTests`). Confirm there are no remaining references to `FileLogger` anywhere:
Run: `grep -rn "FileLogger" KhaozEngine.Diagnostics KhaozEngine.Tests` → expected: no matches.

- [ ] **Step 5: Commit**

```bash
git add -A KhaozEngine.Diagnostics KhaozEngine.Tests
git commit -m "feat(diagnostics)!: remove FileLogger; update package metadata + README

BREAKING CHANGE: FileLogger is replaced by the LogManager/Log service."
```

---

## Task 12: Engine docs (consumer contract + consumers matrix notes)

**Files:**
- Modify: `docs/USING-KHAOZENGINE.md` (add a Diagnostics/logging contract section)
- Modify: `docs/CONSUMERS.md` (notes only; version line is bumped in Task 13)

Do **not** touch the version line, the matrix version numbers, `CHANGELOG.md`, or `Directory.Build.props` here. Those are Task 13 (the coordinated release).

- [ ] **Step 1: Add a logging section to `docs/USING-KHAOZENGINE.md`**

Find the section list (it ends around "## Versioning & change process") and insert a new section before "## Versioning & change process":

```markdown
## Diagnostics / logging (`KhaozEngine.Diagnostics`)

One logging service for every game. Configure it once at startup and log through the static `Log`:

```csharp
var options = new LoggerOptions { MinimumLevel = LogLevel.Info, DefaultCategory = "Boot" };
options.Sinks.Add(new FileSink(new FileSinkOptions
{
    Path = AppDataPaths.Combine("MyGame", "game.log"),
    PreviousPath = AppDataPaths.Combine("MyGame", "game.prev.log"),
}));
options.Sinks.Add(new ConsoleSink());
Log.Configure(options);
CrashHandler.Install();
```

Rules for consumers:

- Configure `Log` once per process (desktop `Program`, Android `MainActivity`, iOS `Program`). Call `Log.Shutdown()` on exit.
- Log via `Log.For<T>()` (category = type name) or `Log.Info/Warn/Error/...`. Pass an exception as the optional second argument.
- The game owns its paths: resolve them with `AppDataPaths.Resolve("<AppName>")` / `Combine(...)` and pass them into `FileSinkOptions`. The engine logging core is path-agnostic.
- Add a game-specific target (in-game console overlay, crash uploader) by implementing `ILogSink` and `Log.Manager.AddSink(...)`. Do not fork the engine logger.
- Logging never throws and never blocks the game loop (async writer thread; `Flush`/`Shutdown` drain it; `CrashHandler` flushes on a crash).
- `MinimumLevel` is runtime-settable for an in-game verbosity toggle.
```

- [ ] **Step 2: Update the Diagnostics notes in `docs/CONSUMERS.md`**

In the "## Notes" section, replace the `FileLogger`-specific wording so it describes the service (leave the version numbers for Task 13). Change the SpaceGame and Nullwake note bullets' "thin facade over `FileLogger`" phrasing to:

> its logging goes through the engine `Log` service (`KhaozEngine.Diagnostics`); the game configures sinks + `AppDataPaths` at startup and logs via `Log`.

And update the Hardpoint note to drop "a candidate to migrate" once migrated (the migration itself is a follow-on plan; for now keep Hardpoint's note accurate to main).

- [ ] **Step 3: Commit**

```bash
git add docs/USING-KHAOZENGINE.md docs/CONSUMERS.md
git commit -m "docs: add logging contract to USING-KHAOZENGINE; update CONSUMERS notes"
```

---

## Task 13: Release engine 4.0.0 (COORDINATED — do last)

**Files:**
- Modify: `Directory.Build.props` (`<Version>`)
- Modify: `CHANGELOG.md`
- Modify: `docs/CONSUMERS.md` (engine-version line + matrix)

> **Coordination gate (hard constraint):** these files are also touched by the `worktree-batch1-promote` release. Before doing this task, run `git -C /Users/antonio/KhaozEngine fetch && git -C /Users/antonio/KhaozEngine log --oneline -5 origin/main` and confirm no other chat is mid-release. If batch1's 3.1.0 has already landed on `main`, merge it in first (this becomes 4.0.0 cumulatively). If batch1 is about to release, wait for it. Never pack/overwrite the shared `local-feed` concurrently.

- [ ] **Step 1: Merge latest `main`**

```bash
cd /Users/antonio/KhaozEngine/.claude/worktrees/logging-service
git fetch origin
git merge origin/main          # pulls in batch1's work if it has merged; resolve any conflicts
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj   # expected: PASS after merge
```

- [ ] **Step 2: Bump the version**

In `Directory.Build.props`, change `<Version>3.0.0</Version>` to `<Version>4.0.0</Version>`.
(If batch1's 3.1.0 already merged, this still goes to 4.0.0 — the breaking `FileLogger` removal drives the major.)

- [ ] **Step 3: Add the newest-first CHANGELOG entry**

At the top of `CHANGELOG.md` (after the intro line, before the most recent existing entry), add:

```markdown
## KhaozEngine 4.0.0

- **KhaozEngine.Diagnostics**: replaced the minimal `FileLogger` with a full logging service.
  `LogManager` (instance core, injectable) + a static `Log` facade own a runtime-settable
  `MinimumLevel`, an injectable `IClock`, and a list of `ILogSink`s. Category loggers via
  `Log.For<T>()` / `GetLogger(string)` stamp a component tag on each `LogEntry`
  (`Trace`/`Debug`/`Info`/`Warn`/`Error`/`Fatal`, each with an optional exception). Writes are
  non-blocking by default (a single background thread drains a bounded queue; overflow is counted in
  `DroppedCount` and never blocks the caller) with a synchronous mode for deterministic tests;
  `Flush`/`Shutdown` drain the queue and flush sinks, and logging never throws.
- Sinks: `FileSink` (rotate-on-launch + optional size-based rotation + retention via
  `FileSinkOptions.MaxBytes`/`MaxFiles`, `AutoFlush` for crash survivability), `ConsoleSink`
  (stderr for errors), `DebugSink` (`System.Diagnostics.Trace`), and `InMemorySink` (tests). Games
  add their own target by implementing `ILogSink`.
- `CrashHandler.Install` wires `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`
  to log a `Fatal` `Crash` entry and flush, so games stop hand-rolling crash hooks.
- Promoted `AppDataPaths`: OS-correct per-app data directory resolver (Windows `%APPDATA%`, macOS
  `~/Library/Application Support`, Linux XDG), created on first access and cached per app name. Engine
  logging stays path-agnostic; games pass resolved paths into `FileSinkOptions`.
- **BREAKING**: `FileLogger` is removed. Consumers move to `Log`/`LogManager`. The default log line
  format gains a `[Category]` field: `[ts] [LEVEL] [Category] message`. Major bump; all packages to 4.0.0.
```

- [ ] **Step 4: Update `docs/CONSUMERS.md` version line + matrix**

Change `**Engine current version:** `3.0.0`` to `4.0.0`, bump the Diagnostics column / engine-version references as appropriate, and update `_Last verified_` to today. (Consumer pins stay where they are until each migration plan runs.)

- [ ] **Step 5: Pack to the shared local-feed and verify**

```bash
cd /Users/antonio/KhaozEngine        # main checkout (feed lives here)
mkdir -p local-feed
cd /Users/antonio/KhaozEngine/.claude/worktrees/logging-service
dotnet pack -c Release -o /Users/antonio/KhaozEngine/local-feed
ls /Users/antonio/KhaozEngine/local-feed/KhaozEngine.Diagnostics.4.0.0.nupkg   # expected: present
```

- [ ] **Step 6: Commit, merge to main, tag, push**

```bash
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md
git commit -m "Release KhaozEngine 4.0.0 (logging service replaces FileLogger)"
```

Then integrate to `main` and tag (from the main checkout). Confirm again no concurrent release is in flight:

```bash
cd /Users/antonio/KhaozEngine
git merge --no-ff worktree-logging-service
git tag v4.0.0
git push origin main
git push origin v4.0.0
```

- [ ] **Step 7: Final verification**

Run: `cd /Users/antonio/KhaozEngine && dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS. Confirm `v4.0.0` is pushed and CI publishes to GitHub Packages on the tag.

---

## Self-review

**Spec coverage:**
- Levels + runtime min-level filter → Tasks 1, 4 (filter, runtime set), Task 8 (`Log.MinimumLevel`). ✓
- Pluggable sinks (file, console/debug, in-memory, extensible interface) → Tasks 3, 6, 7; `AddSink` Task 4. ✓
- Message + exception + category (`GetLogger<T>`/`GetLogger(string)`) → Tasks 1, 4. ✓ No structured scopes (YAGNI) ✓.
- File rotation: rotate-on-launch + size rotation + retention → Task 7. ✓
- Non-blocking + guaranteed flush on shutdown/crash + never throws → Tasks 5, 9; never-throw tested in Tasks 4, 7. ✓
- Crash-hook helper → Task 9. ✓
- Instance core + static facade → Tasks 4, 8. ✓
- Pure .NET, headless-testable (fake clock, in-memory sink, sync mode) → throughout. ✓
- Promote AppDataPaths → Task 10. ✓
- Replace FileLogger (breaking, 4.0.0) → Task 11, 13. ✓
- Docs (USING + CONSUMERS) → Tasks 12, 13. ✓
- Release coordination vs batch1 → Task 13 gate. ✓

**Placeholder scan:** No TBD/TODO; every code/test step has full code; commands have expected output. ✓

**Type consistency:** `LogManager.Submit`/`IsEnabled`/`Now` are `internal` and used by `CategoryLogger`; `CrashHandler.Report` is `internal` (InternalsVisibleTo added Task 9); `Log.For<T>()`/`Get`/`Configure`/`Flush`/`Shutdown`/`MinimumLevel`/`Manager`/`IsConfigured` consistent across Tasks 8 and the CrashHandler `Install()` overload; `FileSinkOptions` property names (`Path`/`PreviousPath`/`MaxBytes`/`MaxFiles`/`MinimumLevel`/`Formatter`) consistent between Tasks 7 and 11/12 usage. ✓

---

## Follow-on plans (separate, after 4.0.0 is packed)

Each consumer migration is its own plan + its own repo worktree, written once `KhaozEngine.Diagnostics 4.0.0` is in `local-feed`:

1. **SpaceGame migration** — delete `GameLogger.cs` + local `AppDataPaths.cs`; configure `Log` (FileSink via `AppDataPaths.Resolve("SpaceGame")` + ConsoleSink + `CrashHandler.Install`); rewrite `GameLogger.*` call sites to `Log`. Bump the Diagnostics pin to 4.0.0.
2. **Nullwake migration** — delete standalone `GameLogger.cs`; configure `Log` at the three entry points (`Nullwake.DesktopGL/Program.cs`, `Nullwake.Android/MainActivity.cs`, `Nullwake.iOS/Program.cs`) writing `AppDataPaths.Resolve("Nullwake")/game.log` + `game.prev.log`; replace the duplicated crash hooks (`NullwakeGame.InstallUnhandledExceptionLogging` + the inline hooks in `DesktopGL/Program.cs`) with `CrashHandler`; rewrite call sites; update Nullwake's AGENTS.md "sole diagnostic path" rule to point at engine `Log`. Bump pins to 4.0.0.
3. **Hardpoint adoption** — bump engine pins from 2.4.0 to 4.0.0; add a direct `KhaozEngine.Diagnostics` reference; configure `Log` (FileSink via `AppDataPaths.Resolve("Hardpoint")` + ConsoleSink + `CrashHandler.Install`) at startup; replace any `Debug.Write` calls with `Log`.
