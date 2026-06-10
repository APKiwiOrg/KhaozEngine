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
