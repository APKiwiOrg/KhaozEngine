# KhaozEngine.Diagnostics

Game-agnostic logging **and runtime telemetry**. Pure .NET, no MonoGame dependency.

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

## Telemetry (since 8.2.0)

Headless, renderer-free building blocks for an in-game diagnostics/telemetry HUD. The
[`KhaozEngine.Gui`](../KhaozEngine.Gui) `DiagnosticsOverlay` renders them; the recorder writes them to disk.

- `FrameStats` - per-frame meter. `Sample(dt)` once per frame, then read `Fps`, `FrameMsAvg`/`FrameMsMin`/`FrameMsMax` over a rolling window (default ~1s; `new FrameStats(windowSeconds)`) and `ManagedBytes` (`GC.GetTotalMemory(false)`). Non-positive / NaN / infinite `dt` are ignored. Unit-testable from a synthetic dt stream.
- `PassTimings` - the same shape as `FrameStats` but keyed by pass name: `Sample(pass, ms)` once per pass per frame, then read `AvgMs(pass)`/`MinMs(pass)`/`MaxMs(pass)` over a rolling window (default ~1s) and `PassNames` (first-sampled order). `Reset()` forgets every pass. Feed it from a renderer's own per-pass CPU-encode readout (e.g. `KhaozEngine.Render3D.Scene3D.PassTimingsMs`, once `Scene3D.EnableTiming` is set) - this package stays GPU-free, so it does not own the pass boundaries itself. Measures CPU encode time, not true GPU execution time; see the `KhaozEngine.Render3D` README and `docs/USING-KHAOZENGINE.md` for what that distinction means and why (Veldrid 4.9.0 exposes no timestamp-query API).
- `TelemetryRecorder` + `TelemetryChannel(string Name, double Value)` - streams a session to a JSON Lines file, one object per `Sample(elapsedSeconds, channels)` (`{"t":12.34,"fps":59.7,...}`), flushed per line so a crash leaves a valid partial file. `Start(path)` / `Stop()` / `IsRecording` / `CurrentPath`; `IDisposable`. Records raw numbers; non-finite values serialize as JSON `null`.
- `ClientNetStats` - the connection-health snapshot (`RttMs`, `PacketLoss`, `BytesInPerSec`/`BytesOutPerSec`, `SnapshotsPerSec`, `LastCorrectionMeters`/`AvgCorrectionMeters`, `Connected`) that `KhaozEngine.NetWorld`'s `WorldClient.NetStats` fills and the overlay renders. It lives here (not in NetWorld) so the Gui overlay can name it without depending on the server/netcode stack.

The overlay widget that draws these lives in [`KhaozEngine.Gui`](../KhaozEngine.Gui) (it depends on this leaf, not the reverse): `DiagnosticsOverlay` + `DiagnosticsOverlayTheme` (+ `OverlaySection`/`OverlayRow`) - an F1-toggled corner panel the game assembles each frame via `SetSections`, with `PerformanceSection(FrameStats)` / `PassTimingsSection(PassTimings)` / `NetworkSection(in ClientNetStats)` populators and a headless-testable `Update`. See the `KhaozEngine.Gui` README.
