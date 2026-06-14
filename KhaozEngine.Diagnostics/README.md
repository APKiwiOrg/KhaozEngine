# KhaozEngine.Diagnostics

Game-agnostic logging service. Pure .NET, no MonoGame dependency.

## Quick start

```csharp
using KhaozEngine.App;          // AppDataPaths lives in the KhaozEngine.App package
using KhaozEngine.Diagnostics;

var paths = new AppDataPaths("MyGame");
var options = new LoggerOptions { MinimumLevel = LogLevel.Info, DefaultCategory = "Boot" };
options.Sinks.Add(new FileSink(new FileSinkOptions
{
    Path = paths.LogFilePath,
    PreviousPath = paths.PreviousLogFilePath,
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

## Categories

Every entry carries a **category**, rendered by the formatter as `[Category] message`. Choose it once and never repeat it in the message text:

- One class's logs → `Log.For<T>()` (category = the type name).
- A subsystem spanning classes, or a game-side module with no single owning type (e.g. `CloudSave`, `Update`) → `Log.Get("ModuleName")` with a stable PascalCase name. The category is a plain string, so non-engine modules categorize the same way.
- `Log.Info/Warn/Error(...)` with no category → the configured `DefaultCategory`; for one-off lines, not per-subsystem logging.

Do **not** prefix the message with the category: `Log.Info("[CloudSave] saved")` double-tags as `[App] [CloudSave] saved`. Write `Log.Get("CloudSave").Info("saved")` → `[CloudSave] saved`.

## Pieces

- `Log` - static ambient facade (`Log.For<T>()`, `Log.Info(...)`, `Log.Configure`, `Log.Flush`, `Log.Shutdown`). No-op before `Configure`.
- `LogManager` + `LoggerOptions` - injectable instance core (DI/tests). Runtime-settable `MinimumLevel`. Async by default; set `Synchronous = true` for deterministic tests.
- `ILogger` - category logger (`Trace`/`Debug`/`Info`/`Warn`/`Error`/`Fatal`, each with an optional exception).
- `ILogSink` + `FileSink` (rotate-on-launch + size rotation + retention), `ConsoleSink`, `DebugSink`, `InMemorySink`. Implement `ILogSink` for custom targets (in-game console, crash uploader).
- `CrashHandler` - wires `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`.
- `IClock`/`SystemClock` - injectable timestamps.

(OS-correct app-data paths live in `KhaozEngine.App` as `AppDataPaths`; resolve `FileSinkOptions.Path` through it.)

Logging never throws and never blocks the caller (writes happen on a background thread; `Flush`/`Shutdown` drain them).
