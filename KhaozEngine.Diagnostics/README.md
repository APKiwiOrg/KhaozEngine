# KhaozEngine.Diagnostics

Game-agnostic logging **and runtime telemetry**. Pure .NET, no MonoGame dependency.

**Frameworks: `net8.0` and `net10.0`.** This leaf multi-targets `net8.0` alongside the engine-wide
`net10.0` because `KhaozEngine.ServerStatus` references it and must be consumable from an Azure Functions
app on the Linux Consumption plan, which has no .NET 10. A `net10.0` consumer resolves the `net10.0` asset
automatically, so this is transparent to every other package.

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

## One-call session bootstrap (`SessionLog`)

The block above (file sink + console sink + `Log.Configure` + `CrashHandler.Install`) is the same at every game's
process entry point. `SessionLog.Configure` collapses it into one call and standardises the richer **per-launch
session log** shape: it prunes old session files, opens one fresh timestamped `session-{yyyyMMdd-HHmmss}.log`,
adds a console sink, adopts the pair as the ambient `Log`, installs `CrashHandler`, and writes one self-identifying
startup line (optional game build version + the engine version read off the engine assembly). It returns the path
of the file it opened. `FileSink` opens the session log with an explicit `FileShare.Read`, so a crash reporter or
tail tool can read the file live for the whole process lifetime while it is held open for writing.

```csharp
using KhaozEngine.App;          // AppDataPaths
using KhaozEngine.Diagnostics;

var paths = new AppDataPaths("APKiwi", "MyGame");
SessionLog.Configure(paths.GetFilePath("logs"), "MyGame", buildVersion: BuildConfig.DisplayVersion);
// or the full form:
SessionLog.Configure(new SessionLogOptions
{
    Directory = paths.GetFilePath("logs"),
    ProcessLabel = "MyGame.Server",
    MaxRetainedSessions = 10,     // session-*.log files beyond this are pruned on startup
    Console = true,
    BuildVersion = BuildConfig.DisplayVersion,
});
```

The game owns the directory (typically a `logs` subdir of `AppDataPaths`). This is the rich, category-tagged
record and is orthogonal to the last-chance `KhaozEngine.Game.StartupCrashLog` net `GameApp` installs
automatically on a no-console Windows GUI launch: that net only catches a startup crash before any logging is
configured and writes a bare file under `%LocalAppData%\KhaozEngine\crash`, so the two write to different
destinations and never double-handle a crash into the same file. The older single-file rotating shape
(`game.log` -> `game.prev.log`) is still just `new FileSink(new FileSinkOptions { Path, PreviousPath })` built
directly - `SessionLog` deliberately standardises on the per-session shape that keeps a tester's crash history.

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
- `TelemetryRecorder` + `TelemetryChannel(string Name, double Value)` - streams a session to a JSON Lines file, one object per `Sample(elapsedSeconds, channels)` (`{"t":12.34,"fps":59.7,...}`), flushed per line so a crash leaves a valid partial file. `Start(path)` / `Start(path, TelemetrySessionInfo)` / `Stop()` / `IsRecording` / `CurrentPath`, plus `IDisposable`. Records raw numbers, and non-finite values serialize as JSON `null`.
- `TelemetrySessionHeader` + `TelemetrySessionInfo` + `TelemetryHeaderValue(string Key, string Value)` (since 17.25.0) - the self-identifying FIRST line of every recording, so a capture says what produced it. A `session` envelope with a `v` schema integer (`SchemaVersion`) and deliberately NO `t` field, so one key tells it apart from a sample row in either direction.
  - **Engine-owned, resolved at `Start`:** `engine` (this assembly's informational version, SourceLink commit suffix included) and `env`, the set `KE_`-prefixed environment variables, name and value, sorted by name. Only that prefix is read, so a capture carries the levers that shaped the run and nothing else off the machine. `SelectEngineVariables` is the pure filter and `ReadProcessEnvironment` the one impure member, so the whole header is testable via `Build(session, environment)`.
  - **Handed in by the consumer:** `AppName` / `AppVersion` / `BuildName`, plus the GPU block (`GpuBackend`, `GpuBackendSource`, `GpuRequestedBackend`, `GpuRequestedOverride`, `AdapterDescription`, `SoftwareAdapter`, `DeviceLossReason`, `InjectedModules`, `DriverCommandLists` / `DriverConcurrentCreates`). Those are plain strings and nullable bools rather than the `KhaozEngine.Gpu` types because that package references THIS one, not the reverse. Fill them in one call with `KhaozEngine.Gpu`'s `GpuTelemetry.WithGpu`. Blank reads as JSON `null` on every field except `requestedOverride`, which is written exactly as it was read. `injectedModules` keeps null (never scanned) apart from `[]` (scanned, clean), and `threading` is null on every backend but Direct3D11.
  - **`softwareAdapter` and `deviceLossReason` (17.32.0)** are appended inside the envelope, so `session.v` does not move. `softwareAdapter` is three-valued: `true` on a software rasterizer (on Direct3D11, `DXGI_ADAPTER_FLAG_SOFTWARE`), `false` on hardware, `null` when nobody answered. Keep those apart, because performance numbers off a software rasterizer are not comparable with numbers off a GPU and a capture that cannot say which it was gets averaged in with the others. `deviceLossReason` is null on every ordinary session and otherwise carries `GetDeviceRemovedReason`'s answer plus the call site that noticed, for example `"DXGI_ERROR_DEVICE_HUNG at present"`. It is read at the FIRST site to notice, because `DXGI_ERROR_DEVICE_REMOVED` is sticky and by the time a crash handler asks, the reason has been overwritten by whatever ran next.
  - **A fallback records what was asked for.** `requestedBackend` is the backend that failed (set only on a `FallbackAfterFailure` source, and the only record of a player's own in-game backend choice on a `UserPreference` fallback). `requestedOverride` is the RAW `KE_GRAPHICS_BACKEND` value, written verbatim and never normalized, since a typo or stray quoting is exactly what it exists to show.
  - **The game's own durables:** `AddGameValue(key, value)` / `AddGameValues(pairs)` land under the header's `game` section, so nothing a game records can collide with an engine field. `AddGameValues` takes any `IEnumerable<KeyValuePair<string, string>>`, which is the one-call dump of an F1 overlay's rows. A repeated key replaces in place, and both return the instance so construction chains. The engine names no game type. `GameValues` hands back a genuinely read-only view, so casting it to `IList<T>` throws rather than opening a back door.
  - **A header the machine will not let us write degrades to a headerless recording.** The write is guarded against IO, permission, and environment failures, so none of those throws out of `Start` and the samples still record. It is not a blanket catch, so something like an `OutOfMemoryException` still propagates, as it should.
- `ClientNetStats` - the connection-health snapshot (`RttMs`, `PacketLoss`, `BytesInPerSec`/`BytesOutPerSec`, `SnapshotsPerSec`, `LastCorrectionMeters`/`AvgCorrectionMeters`, `Connected`) that `KhaozEngine.NetWorld`'s `WorldClient.NetStats` fills and the overlay renders. It lives here (not in NetWorld) so the Gui overlay can name it without depending on the server/netcode stack.

The overlay widget that draws these lives in [`KhaozEngine.Gui`](../KhaozEngine.Gui) (it depends on this leaf, not the reverse): `DiagnosticsOverlay` + `DiagnosticsOverlayTheme` (+ `OverlaySection`/`OverlayRow`) - an F1-toggled corner panel the game assembles each frame via `SetSections`, with `PerformanceSection(FrameStats)` / `PassTimingsSection(PassTimings)` / `NetworkSection(in ClientNetStats)` populators and a headless-testable `Update`. See the `KhaozEngine.Gui` README.
