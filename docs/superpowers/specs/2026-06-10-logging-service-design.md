# KhaozEngine logging service: design

Status: approved design, pre-implementation.
Date: 2026-06-10.
Scope: evolve `KhaozEngine.Diagnostics` into a full logging service, replace the minimal `FileLogger`,
and migrate all three consumers (Nullwake, SpaceGame, Hardpoint) onto it.

## 1. Problem

Each game rolls its own logging or none:

- Engine ships `KhaozEngine.Diagnostics.FileLogger` (3.0.0): instance-based, thread-safe, single file,
  `AutoFlush` `StreamWriter`, rotate-previous-aside, `Info/Warn/Error/Error(msg, ex)`,
  `Shutdown`/`Dispose`. No level filter, no categories, no sinks, synchronous writes.
- SpaceGame: migrated. `GameLogger` static facade over `FileLogger`, paths from a full OS-correct
  `AppDataPaths` (Windows `%APPDATA%`, macOS `~/Library/Application Support`, Linux XDG).
- Nullwake: on `main` still rolls its **own** standalone static `GameLogger`
  (`LocalApplicationData/Nullwake/` only). Crash hooks live in `NullwakeGame.cs`
  (`InstallUnhandledExceptionLogging`) and are duplicated in `Nullwake.DesktopGL/Program.cs`. Its
  Diagnostics adoption exists only on an unmerged feature branch (the CHANGELOG/CONSUMERS claims of
  "adopted by Nullwake" describe that branch, not `main`).
- Hardpoint: no logger at all. Pins engine 2.4.0 (behind 3.0.0). `vendor/` nuget convention.
- Server-side `ILogger<T>` lives in separate server projects, not the game client.

Goal: one well-designed logging service in the engine that every game uses, replacing the scattered
state.

## 2. Decisions (approved)

1. **Replace `FileLogger` (breaking).** Delete it and its tests; the new service is the only logging
   API. SpaceGame migrates in the same release. Forces a **major bump to 4.0.0**.
2. **Games use the engine `Log` static directly.** Delete Nullwake's and SpaceGame's `GameLogger`
   facades; rewrite call sites to the engine `Log`. The engine still exposes an instance core
   (`LogManager`) for tests/DI; `Log` is the ambient facade games call.
3. **Message + exception + category only.** No structured fields / scopes in v1 (YAGNI).
4. **Promote `AppDataPaths`** into the engine: OS-correct base-dir resolver shared by all games.
   Save/settings path specifics stay game-side.
5. **Non-blocking model A:** background writer thread + bounded queue, with a synchronous mode for
   deterministic tests. (See section 6.)
6. **No `Microsoft.Extensions.Logging` bridge** in v1. `KhaozEngine.Diagnostics` stays dependency-free;
   the server keeps its own `ILogger<T>`.

## 3. Goals / non-goals

Goals: levels with runtime-settable minimum filter; pluggable sinks (rotating file, console/debug,
in-memory for tests, game-extensible); category/component tags via `GetLogger<T>()` /
`GetLogger(string)`; file rotation done right (rotate-on-launch + optional size rotation + retention);
non-blocking by default with guaranteed flush on shutdown and crash; logging never throws; a crash-hook
helper; both an instance core and a static ambient facade; pure .NET, headless-testable with a fake
clock and in-memory sink.

Non-goals (v1): structured fields / scopes; per-category level filtering; `Microsoft.Extensions.Logging`
bridge; network/remote sinks shipped by the engine (games can add their own via `ILogSink`); log
querying / parsing.

## 4. Public API

All in `KhaozEngine.Diagnostics`, pure System.IO / System.Threading, no MonoGame dependency.

### 4.1 Core model

```csharp
public enum LogLevel { Trace, Debug, Info, Warn, Error, Fatal }

public readonly struct LogEntry
{
    public DateTimeOffset Timestamp { get; }
    public LogLevel      Level     { get; }
    public string        Category  { get; }
    public string        Message   { get; }
    public Exception?    Exception { get; }
    public LogEntry(DateTimeOffset timestamp, LogLevel level, string category,
                    string message, Exception? exception = null);
}

public interface IClock { DateTimeOffset Now { get; } }
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance;
    public DateTimeOffset Now { get; }   // => DateTimeOffset.Now
}
```

### 4.2 Category logger

```csharp
public interface ILogger
{
    string Category { get; }
    bool IsEnabled(LogLevel level);
    void Log(LogLevel level, string message, Exception? exception = null);
    void Trace(string message, Exception? exception = null);
    void Debug(string message, Exception? exception = null);
    void Info (string message, Exception? exception = null);
    void Warn (string message, Exception? exception = null);
    void Error(string message, Exception? exception = null);
    void Fatal(string message, Exception? exception = null);
}
```

Concrete `CategoryLogger` is internal; obtained from `LogManager` / `Log`.

### 4.3 Instance core

```csharp
public sealed class LoggerOptions
{
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;
    public bool     Synchronous  { get; set; } = false;   // true = inline writes (tests)
    public int      QueueCapacity{ get; set; } = 10_000;  // bounded; drop-newest on overflow
    public IClock   Clock        { get; set; } = SystemClock.Instance;
    public string   DefaultCategory { get; set; } = "App";
    public IList<ILogSink> Sinks { get; }   // populated before constructing LogManager
}

public sealed class LogManager : IDisposable
{
    public LogManager(LoggerOptions options);
    public LogLevel MinimumLevel { get; set; }   // runtime-settable
    public long DroppedCount { get; }            // entries dropped due to a full queue
    public ILogger GetLogger(string category);
    public ILogger GetLogger<T>();               // category = typeof(T).Name
    public void AddSink(ILogSink sink);          // thread-safe; e.g. add in-game console at runtime
    public void Flush();                         // drain queue + flush all sinks (blocking)
    public void Shutdown();                      // Flush, stop writer thread, dispose sinks
    public void Dispose();                       // => Shutdown
}
```

### 4.4 Static ambient facade (what games call)

```csharp
public static class Log
{
    public static void Configure(LoggerOptions options); // builds + owns the ambient LogManager
    public static void Configure(LogManager manager);     // adopt an existing manager (DI / tests)
    public static bool IsConfigured { get; }
    public static LogManager? Manager { get; }
    public static LogLevel MinimumLevel { get; set; }

    public static ILogger For<T>();
    public static ILogger Get(string category);

    // convenience over DefaultCategory:
    public static void Trace(string message, Exception? ex = null);
    public static void Debug(string message, Exception? ex = null);
    public static void Info (string message, Exception? ex = null);
    public static void Warn (string message, Exception? ex = null);
    public static void Error(string message, Exception? ex = null);
    public static void Fatal(string message, Exception? ex = null);

    public static void Flush();
    public static void Shutdown();
}
```

Before `Configure`, every `Log` call is a safe no-op (never throws); `IsConfigured` is false.
`Configure` is idempotent-safe: a second call shuts the previous manager down and adopts the new one.

### 4.5 Sinks

```csharp
public interface ILogSink : IDisposable
{
    void Emit(in LogEntry entry);
    void Flush();
}

public sealed class FileSinkOptions
{
    public string  Path         { get; set; }   // active log path (required)
    public string? PreviousPath { get; set; }   // rotate-on-launch target (optional)
    public long?   MaxBytes     { get; set; }   // size-based rotation threshold (optional)
    public int?    MaxFiles     { get; set; }   // retention: keep N archives (optional)
    public LogLevel? MinimumLevel { get; set; } // per-sink threshold (optional)
    public Func<LogEntry, string>? Formatter { get; set; } // optional line formatter
}

public sealed class FileSink    : ILogSink { public FileSink(FileSinkOptions options, IClock? clock = null); }
public sealed class ConsoleSink : ILogSink { public ConsoleSink(LogLevel? minimumLevel = null, bool useStdErrForErrors = true); }
public sealed class DebugSink   : ILogSink { public DebugSink(LogLevel? minimumLevel = null); }   // System.Diagnostics.Debug.WriteLine
public sealed class InMemorySink: ILogSink
{
    public IReadOnlyList<LogEntry> Entries { get; }  // thread-safe snapshot
    public void Clear();
}
```

Default line format: `[yyyy-MM-dd HH:mm:ss.fff] [LEVEL] [Category] message`, with any exception
appended on following lines. This adds `[Category]` versus the old `FileLogger` format; acceptable as
the format is not a contract and this is a major bump.

### 4.6 Crash handler

```csharp
public static class CrashHandler
{
    public static void Install(LogManager manager); // wires AppDomain.UnhandledException + TaskScheduler.UnobservedTaskException
    public static void Install();                    // uses the ambient Log.Manager
    public static void Uninstall();                  // unregisters (tests / teardown)
}
```

Handlers log a `Fatal` entry (category `Crash`) including the exception, then `Flush()`. On a
terminating unhandled exception they also `Shutdown()` so the file is closed cleanly. The fatal-report
logic is factored into an internal seam (`InternalsVisibleTo("KhaozEngine.Tests")`) so tests can invoke
it directly without raising a real unhandled exception.

### 4.7 App-data paths (promoted)

```csharp
public static class AppDataPaths
{
    public static string Resolve(string appFolderName);                    // OS-correct base dir, created on first call, cached per name
    public static string Combine(string appFolderName, params string[] parts); // path under the base dir
}
```

Resolution order matches SpaceGame's current resolver: Windows `%APPDATA%\<app>`, macOS
`~/Library/Application Support/<app>`, Linux `$XDG_DATA_HOME/<app>` or `~/.local/share/<app>`, then
`LocalApplicationData/<app>`, then `~/.<app>`. Lives in `KhaozEngine.Diagnostics` (pragmatic home: the
log path resolves here; games may reuse it for save/settings paths). Engine logging itself stays
path-agnostic: a game resolves its paths and passes them into `FileSinkOptions`.

## 5. Data flow

`Log.For<T>()` returns an `ILogger` for category `typeof(T).Name`. `logger.Info(msg)`:

1. Drop immediately if `level < MinimumLevel` (cheap, on the calling thread).
2. Build a `LogEntry` (timestamp from the injected `IClock`, the logger's category, message, exception).
3. Hand to the `LogManager` write pipeline (async: enqueue; sync: write inline).
4. Pipeline fans the entry out to every sink whose own optional threshold passes; each sink's `Emit`
   is wrapped in try/catch so one failing sink never breaks the others and logging never throws.

`Flush()` and `Shutdown()` drain the pipeline and flush every sink.

## 6. Non-blocking writes (model A)

- Async by default. `Emit` enqueues into a bounded queue (`QueueCapacity`, default 10k) and returns
  immediately. A single background writer thread drains the queue and writes to all sinks.
- Overflow: when the queue is full, drop the newest entry and increment `DroppedCount`. The game loop
  never blocks and memory never grows unbounded. The writer periodically emits a synthetic
  "N entries dropped" warning when `DroppedCount` advances.
- `Flush()` blocks until every entry enqueued before the call has been written and all sinks flushed
  (implemented with a flush marker the writer signals on reaching). Used by shutdown and crash so a
  clean exit or a caught crash loses nothing.
- `Shutdown()`/`Dispose()` completes the queue, joins the writer thread, flushes and disposes sinks.
- Synchronous mode (`LoggerOptions.Synchronous = true`): no background thread; `Emit` writes inline on
  the caller. Tests use this for deterministic assertions; `Flush` is then a direct sink flush.
- Logging never throws: enqueue never throws (bounded + drop), and every sink write is guarded.

Game log volume is low, so the queue is mostly insurance against a slow disk; the value is the
guaranteed-flush + never-throw + never-block contract, plus the deterministic sync path for tests.

## 7. Levels & filtering

Global `MinimumLevel` on `LogManager` (mirrored by `Log.MinimumLevel`), runtime-settable. Optional
per-sink `MinimumLevel` (e.g. console at `Info`, file at `Debug`). Per-category filtering is out of
scope for v1.

## 8. Testing (headless, deterministic)

- Sync-mode `LogManager` + `InMemorySink` + fake `IClock` → assert exact entries, levels, categories,
  timestamps with no timing flake.
- `MinimumLevel` filtering: below-threshold entries never reach sinks; runtime change takes effect.
- `FileSink`: rotate-on-launch into `PreviousPath`; no rotation when `PreviousPath` is null; size-based
  rotation crosses `MaxBytes`; retention prunes to `MaxFiles`. Temp-dir pattern from the existing
  `FileLoggerTests`.
- Async path: enqueue many entries, `Flush()`, assert all present and ordered; overflow increments
  `DroppedCount` and never blocks.
- Never-throws: a deliberately throwing sink does not surface to the caller and does not stop other
  sinks.
- `CrashHandler`: invoke the internal fatal-report seam with a manager backed by `InMemorySink`; assert
  a `Fatal` `Crash` entry and that `Flush` ran. `Install`/`Uninstall` register/unregister cleanly.
- `AppDataPaths`: per-OS path shape (guarded by `OperatingSystem.Is*`), directory created, result
  cached per app name.
- `Log` facade: no-op before `Configure`; routes to the configured manager after; `Configure` twice
  shuts the first down.

Every new behavior ships a test in `KhaozEngine.Tests`, per the engine rule.

## 9. Migration (same release)

### Engine
- Add the service (sections 4-7). Delete `FileLogger.cs` and `FileLoggerTests.cs`; replace with the new
  test suite. Promote `AppDataPaths`.
- Update `KhaozEngine.Diagnostics` README, `docs/USING-KHAOZENGINE.md` (new logging contract section),
  `docs/CONSUMERS.md` (version + adoption matrix).

### SpaceGame
- Delete `GameLogger.cs` facade and the local `AppDataPaths.cs` (use the promoted engine one; keep a
  thin game wrapper only if save/settings path constants are still wanted).
- At startup: `Log.Configure` with a `FileSink` (path via `AppDataPaths.Resolve("SpaceGame")`), a
  console sink, then `CrashHandler.Install`.
- Rewrite `GameLogger.*` call sites to `Log.*` / `Log.For<T>()`.

### Nullwake
- Delete the standalone `GameLogger.cs`.
- At the three entry points (`Nullwake.DesktopGL/Program.cs`, `Nullwake.Android/MainActivity.cs`,
  `Nullwake.iOS/Program.cs`): `Log.Configure` with a `FileSink` writing
  `AppDataPaths.Resolve("Nullwake")/game.log` + `game.prev.log` (preserve the rotate-on-launch
  behavior), then `CrashHandler.Install`.
- Replace the duplicated crash hooks (`NullwakeGame.InstallUnhandledExceptionLogging` and the inline
  hooks in `DesktopGL/Program.cs`) with `CrashHandler`.
- Rewrite `GameLogger.*` call sites to `Log.*`.
- Update Nullwake's AGENTS.md: the "sole diagnostic path" rule now points at the engine `Log`.

### Hardpoint
- Bump engine pin from 2.4.0 to the new version; vendor the new nupkgs per `vendor/` convention; add a
  direct `KhaozEngine.Diagnostics` `PackageReference`.
- At startup: `Log.Configure` with a `FileSink` (path via `AppDataPaths.Resolve("Hardpoint")`) +
  console sink + `CrashHandler.Install`.
- Replace any `Debug.Write`-style calls with `Log.*`.

## 10. Versioning & release coordination

- Target **4.0.0** (breaking: `FileLogger` removed). All packages share the one version.
- The `batch1-promote` worktree is mid-flight toward an additive **3.1.0** (adds `KhaozEngine.Localization`,
  edits the shared `KhaozEngine.Tests.csproj`, `KhaozEngine.slnx`). This logging work lives in its own
  `worktree-logging-service` tree and must not edit those shared release files until release time.
- Release ritual (engine CLAUDE.md), done as the final coordinated step only after confirming no other
  chat is mid-release: bump `Directory.Build.props` `<Version>` to `4.0.0` → add the newest-first
  `CHANGELOG.md` entry (same commit) → update `docs/CONSUMERS.md` engine-version line → merge latest
  `main` (which may by then include batch1's 3.1.0 + Localization) → `dotnet pack -c Release -o
  ./local-feed` (cumulative, do not `rm` old versions) → commit → `git tag v4.0.0` → push `main` + tag.
- Do not re-pack or overwrite the shared `local-feed` while batch1 is packing; last-writer-wins there
  caused a prior collision.

## 11. Risks / open items

- `AppDataPaths` in a "Diagnostics" package is a slight domain stretch (it serves saves/settings too).
  Accepted for now to avoid spinning up a new package; revisit if a `KhaozEngine.Platform`/`.Storage`
  package is ever wanted.
- Background writer thread in a game process: kept to a single thread, bounded queue, drop-on-overflow;
  tests run in sync mode so they stay deterministic.
- Migrating all three games plus the engine in one release is a large change set; each game is its own
  commit/PR off the engine release so they stay reviewable and can be adopted independently.
- Log line format changes (adds `[Category]`); acceptable under a major bump, called out in the
  CHANGELOG.
