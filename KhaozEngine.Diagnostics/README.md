# KhaozEngine.Diagnostics

Game-agnostic logging **and runtime telemetry**. Pure .NET.

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

**`Configure` changes the process default, including for code that resolved its logger first.** A
logger from `Log.For<T>()` / `Log.Get(...)` is bound to its CATEGORY and to the facade, not to whichever manager
was configured when you asked for it, so it reads the current one on every call. That means the usual shape is
safe: cache the logger in a `static readonly ILogger` field, touch the type whenever you like, and configure
logging whenever the process gets round to it. It also means a `Log.Configure` late in startup, or a second one
that swaps the sink set, does not strand anything that already holds a logger. `Log.Configure` shuts down the
manager it replaces (flushing, then disposing its sinks). An active `Log.Use` override stays selected until its
scope ends, then the logger reads the latest process fallback.

`Log.Use(LogManager manager)` borrows a manager for the current async flow and returns an `IDisposable`.
Null throws `ArgumentNullException`. Inside `using (Log.Use(manager))`, cached `Log.For<T>()` and `Log.Get(...)`
loggers follow that manager, including its minimum level, default category, timestamps and sinks.
`Log.Manager`, `Log.IsConfigured`, the `Log.MinimumLevel` getter and `Log.Flush()` select it too. A scope
never shuts down or disposes its manager. `Log.Configure`, `Log.Shutdown` and the `Log.MinimumLevel` setter
always mutate the process manager, even inside a scope. Scope disposal restores the current process fallback,
including a reconfigured or shut-down default.

Scopes nest and survive awaits. Child tasks inherit their starting scope, while their overrides and disposal
do not change parents or siblings. Dispose in reverse creation order in each flow. Disposing an active outer
scope throws `InvalidOperationException` without changing selection. Disposing a token already absent from
the current flow is a no-op, including repeat disposal. A token has no shared disposed flag, so inherited
flows can each restore independently. Await children before disposing their borrowed managers. Suppressed
execution-context flow does not inherit scopes. Before the first `Use` for a facade, reads bypass async-local
lookup. Subsequent reads, including after disposal, allocate nothing for manager selection. This is an
allocation contract, not a timing guarantee.

A logger from `LogManager.GetLogger(...)` is the deliberate opposite: it belongs to THAT manager for its whole
life, which is what makes the injected path (DI, and a test asserting against a manager it owns) mean what it
says.

`CrashHandler.Install()` arms process handlers and holds no manager, resolving the process default when a
crash is reported. It ignores `Log.Use` scopes. Install it before or after `Log.Configure` and
reconfigure as often as you like: the fatal crash line follows the live manager and needs no re-install.
`CrashHandler.Install(myManager)` is the injected shape and pins to the manager you hand it. Because arming no
longer depends on a manager being configured, an unobserved task exception is marked observed while the handler
is installed even if nothing is configured to log it, which only shows at all if you have switched
`ThrowUnobservedTaskExceptions` on.

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
record and is orthogonal to the last-chance `CrashReport` file `GameApp` arms automatically for every head (see
below): that file catches a crash before any logging is configured and writes to an OS crash location, so the two
write to different destinations and never double-handle a crash into the same file. The older single-file rotating shape
(`game.log` -> `game.prev.log`) is still just `new FileSink(new FileSinkOptions { Path, PreviousPath })` built
directly - `SessionLog` deliberately standardises on the per-session shape that keeps a tester's crash history.

## Last-chance crash file (`CrashReport`, since 17.36.0)

The session log is the record when a game configured one. `CrashReport` is the floor for when it did not, or for
a crash that happens before it did: it writes the exception to its own file with no configuration at all, in the
place a tester already looks for crashes.

```csharp
CrashReport.Install(new CrashReportOptions { ProcessLabel = "MyGame" });
CrashReport.Note("backend", device.Backend.ToString());   // any fact worth having at the moment of a crash
```

`GameApp` does both for every head already (opt out with `GameAppOptions.SuppressCrashReportFile`, and it notes
the graphics backend as soon as its window exists), so this is only wired by hand in a head that is not a
`GameApp`, e.g. a dedicated server. Calling it yourself first is also fine: `GameApp` arms only when
`CrashReport.IsInstalled` is false, so your own directory, label and retention win.

- Default directory: `~/Library/Logs/KhaozEngine` on macOS, which is the same tree the system's own `.ips` crash
  report lands in under `DiagnosticReports`. `%LOCALAPPDATA%\KhaozEngine\crash` on Windows,
  `$XDG_STATE_HOME/KhaozEngine/crash` (else `~/.local/state/...`) elsewhere. Override with
  `CrashReportOptions.Directory`.
- One file per crash, `{process}-crash-{yyyyMMdd-HHmmss-fff}-{pid}-{counter}.log`, pruned to
  `MaxRetainedReports` (default 20) per process label. The process id and the counter are what make the name
  unique: unobserved task exceptions arrive from the finalizer thread in a burst, and a stamp that stops at the
  millisecond loses every collision silently (a measured burst of twelve wrote seven files).
- Unobserved task exceptions are recorded by default (`IncludeUnobservedTaskExceptions`) and get their OWN stem,
  `{process}-taskfault-...`, and their own retention pool. They come from the finalizer thread at a garbage
  collection, so they arrive long after the failure and usually while the game is running fine. Keeping them out
  of the crash stem is what stops a tester collecting a task fault when they were asked for the crash file.
- Retention matches the whole generated shape, not the stem, so a label whose stem starts with another's
  (`show` against `show-crash`) no longer prunes the other's reports out of a shared crash directory. Two labels
  that SANITISE to the same stem (`My Game` and `My-Game`) do share one pool, by construction.
- Contents: timestamp, process, engine version, runtime, OS, the crash context, every `Note`, then the exception
  type, message and full stack.
- A hostile exception costs its stack and not its report. `Message` and `ToString` are both virtual, so both are
  read inside their own `try`: the header (type included, read through the non-virtual `GetType`) is written
  first, the stack is appended after, and a throwing `ToString` leaves a line saying so. Retention runs only
  once a file exists, so a crash that could not be written cannot delete one that was.
- Never throws and needs nothing configured, because it runs on the runtime's crash path.

## Recent log ring (`RecentLogSink`)

`RecentLogSink` keeps the last N formatted lines in memory so a bug report can attach what the game logged just
before the player pressed the key. Add it next to the session log's file sink and read it when a report is taken.

```csharp
var recent = new RecentLogSink();                  // 500 lines by default
options.Sinks.Add(recent);
IReadOnlyList<string> lines = recent.Snapshot();   // oldest first, a copy
```

- A fixed ring. Once full, each new line drops the oldest, so memory stays at `Capacity` lines.
- Each line is `LogFormatter.Format(entry)`, exception text included. A line longer than `MaxEntryChars` (2000)
  is cut there and ends with ` [truncated]`. The cut never splits a surrogate pair.
- Thread-safe, never throws from `Emit`, and `Flush` and `Dispose` do nothing.
- `InMemorySink` stays the test capture. It keeps every entry unbounded and is not meant for a shipped head.

## Report redaction (`ReportRedactor`)

`ReportRedactor` is the backstop every bug report string passes through before it leaves the machine. Sections
should still avoid writing secrets. This catches what slips through.

```csharp
var redactor = new ReportRedactor();               // home read from the user profile folder
string safe = redactor.Redact(line);               // same instance back when nothing matched
IReadOnlyList<TelemetryHeaderValue> env = redactor.SelectEnvironment(pairs);
ReportRedactor reporter = redactor.WithReporterIdentityTerms(() => new[] { displayName ?? "", subject ?? "" });
string anonymous = reporter.Redact(line);
```

- Rules run in a fixed order, each writing `[redacted]` (`ReportRedactor.Mask`): PEM private key blocks, JWTs,
  provider tokens (GitHub, Slack, AWS access key ids, `sk-` keys), Bearer and Basic credentials (the scheme is
  kept), secret URL query values (the parameter name is kept), and secret key and value pairs such as
  `password=`, `"apiKey": "..."` or `AccountKey=` (the key and separator are kept).
- `WithReporterIdentityTerms(source)` calls the game source once now and returns an independent snapshot. It masks
  complete escaped literal terms, including occurrences inside longer text, after secrets and before home. Matching
  is invariant case-insensitive, longest first, with the non-backtracking engine and no wall-clock deadline. Null, blank
  and terms shorter than three characters are ignored. A source or enumeration that throws, a null result, an
  unsupported pattern, or a combined escaped pattern above 4096 characters fails closed by masking every string.
  The source must finish promptly. `BugReportServiceOptions.ReporterIdentityTerms` takes this snapshot at each
  report's finish, so the game can return the signed-in display name and subject without caching them at startup.
- The home directory becomes `~`, matched case-insensitively with either separator and only where the path ends,
  so `/Users/anna` survives a home of `/Users/ann`. Trailing separators on the home are ignored, and a home
  shorter than three characters turns the rule off.
- Every rule runs on the non-backtracking engine, so it is linear in the input by construction. A JWT
  may start only at the start of the text or after a character that cannot be in a token, and that character is
  kept. The home rule matches an escaped literal path and its following boundary, preserving that boundary for
  adjacent paths. An unsupported home pattern or one above 4096 escaped characters masks every string.
- Secret key/value pairs match the key, quotes and separator with a non-backtracking prefix pattern, then scan
  the value up to the original whitespace, quote, comma, semicolon or ampersand boundary. This avoids growing a
  combined key/value automaton across reports while retaining the same masked values.
- The linear guarantee has a one-time cost. The engine turns each pattern into an automaton, so building the
  shared default rules allocates once per process, when the first redactor is constructed.
  `BugReportService` builds its redactor at construction, so that lands at game startup. The automaton grows
  lazily as it meets new input, so the first report also pays matcher setup. Redaction runs over
  log text a player can influence, and linear time keeps that bounded whatever the text holds.
- Matchers explicitly disable wall-clock timeouts, so machine load cannot erase ordinary report text. Input and
  intermediate strings above 4,194,304 UTF-16 code units fail closed as `[redacted]`. This bounds matching work
  without depending on elapsed time. Replacement growth is checked before the next pass and before returning.
- Thread-safe. Clean text is returned as the same instance without allocating.
- `SelectEnvironment` keeps only `KE_` variables, chosen and sorted exactly as
  `TelemetrySessionHeader.SelectEngineVariables` does. A name containing `TOKEN`, `SECRET`, `PASSWORD`,
  `PASSWD` or `KEY` in any case has its whole value masked. Every other value passes through `Redact`.

## Categories

Every entry carries a **category**, rendered by the formatter as `[Category] message`. Choose it once and never repeat it in the message text:

- One class's logs → `Log.For<T>()` (category = the type name).
- A subsystem spanning classes, or a game-side module with no single owning type (e.g. `CloudSave`, `Update`) → `Log.Get("ModuleName")` with a stable PascalCase name. The category is a plain string, so non-engine modules categorize the same way.
- `Log.Info/Warn/Error(...)` with no category → the configured `DefaultCategory`; for one-off lines, not per-subsystem logging.

Do **not** prefix the message with the category: `Log.Info("[CloudSave] saved")` double-tags as `[App] [CloudSave] saved`. Write `Log.Get("CloudSave").Info("saved")` → `[CloudSave] saved`.

## Pieces

- `Log` - static ambient facade (`Log.For<T>()`, `Log.Info(...)`, `Log.Use`, `Log.Configure`, `Log.Flush`, `Log.Shutdown`). Without a scope or process manager logging is a no-op. Cached loggers follow the current scope and process fallback.
- `LogManager` + `LoggerOptions` - injectable instance core (DI/tests). Runtime-settable `MinimumLevel`. Async by default, and set `Synchronous = true` for deterministic tests. A logger from `LogManager.GetLogger` stays bound to that manager.
- `ILogger` - category logger (`Trace`/`Debug`/`Info`/`Warn`/`Error`/`Fatal`, each with an optional exception).
- `ILogSink` + `FileSink` (rotate-on-launch + size rotation + retention), `ConsoleSink`, `DebugSink`, `InMemorySink`. Implement `ILogSink` for custom targets (in-game console, crash uploader).
- `CrashHandler` - wires `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` into the log.
  `Install()` follows the ambient `Log` (resolved per crash, so install order does not matter), `Install(manager)`
  pins to the manager you pass.
- `CrashReport` + `CrashReportOptions` + `CrashReportKind` - the last-chance crash FILE, written with no logging
  configured. The kind picks the stem: an unhandled exception is a crash, an unobserved task exception is a task
  fault, and they retain separately.
- `IClock`/`SystemClock` - injectable timestamps.

(OS-correct app-data paths live in `KhaozEngine.App` as `AppDataPaths`; resolve `FileSinkOptions.Path` through it.)

Logging never throws and never blocks the caller (writes happen on a background thread; `Flush`/`Shutdown` drain them).

## Telemetry (since 8.2.0)

Headless, renderer-free building blocks for an in-game diagnostics/telemetry HUD. The
[`KhaozEngine.Gui`](../KhaozEngine.Gui) `DiagnosticsOverlay` renders them; the recorder writes them to disk.

- `FrameStats` - per-frame meter. `Sample(dt)` once per frame, then read `Fps`, `FrameMsAvg`/`FrameMsMin`/`FrameMsMax` over a rolling window (default ~1s; `new FrameStats(windowSeconds)`) and `ManagedBytes` (`GC.GetTotalMemory(false)`). Non-positive / NaN / infinite `dt` are ignored. Unit-testable from a synthetic dt stream.
- `PassTimings` - the same shape as `FrameStats` but keyed by pass name: `Sample(pass, ms)` once per pass per frame, then read `AvgMs(pass)`/`MinMs(pass)`/`MaxMs(pass)` over a rolling window (default ~1s) and `PassNames` (first-sampled order). `Reset()` forgets every pass. The window is REAL time, not summed pass time: each sample is timestamped off an `IClock` and trimmed by wall clock, since a pass costs a fraction of a frame and summing its milliseconds would stretch the window to tens of seconds, by a different factor per pass. `new PassTimings(windowSeconds, clock)` takes a fake clock so a test drives the window without sleeping. Feed it from a renderer's own per-pass CPU-encode readout (e.g. `KhaozEngine.Render3D.Scene3D.PassTimingsMs`, once `Scene3D.EnableTiming` is set) - this package stays GPU-free, so it does not own the pass boundaries itself. Measures CPU encode time, not true GPU execution time. See the `KhaozEngine.Render3D` README and `docs/USING-KHAOZENGINE.md` for that distinction. The optional `KhaozEngine.Gpu` timestamp-pool seam separately measures GPU execution on supported devices.
- `TelemetryRecorder` + `TelemetryChannel(string Name, double Value)` - streams a session to a JSON Lines file, one object per `Sample(elapsedSeconds, channels)` (`{"t":12.34,"fps":59.7,...}`), flushed per line so a crash leaves a valid partial file. `Start(path)` / `Start(path, TelemetrySessionInfo)` / `Stop()` / `IsRecording` / `CurrentPath`, plus `IDisposable`. Records raw numbers, and non-finite values serialize as JSON `null`.
  - **The sample row is the session's channel for a number that MOVES**, and the header is the channel for what was already fixed at start. Since 17.32.0 the GPU stack projects its own soak counters into these rows through `KhaozEngine.Gpu`'s `GpuTelemetryChannels.AppendTo(row, window.Counters)` (`gpuDrainMs`, `gpuBackpressureStalls`, and the rest). That mapping lives there and not here for the same reason `GpuTelemetry` does: that package references this one, not the reverse.
- `TelemetrySessionHeader` + `TelemetrySessionInfo` + `TelemetryHeaderValue(string Key, string Value)` (since 17.25.0) - the self-identifying FIRST line of every recording, so a capture says what produced it. A `session` envelope with a `v` schema integer (`SchemaVersion`) and deliberately NO `t` field, so one key tells it apart from a sample row in either direction.
  - **Engine-owned, resolved at `Start`:** `engine` (this assembly's informational version, SourceLink commit suffix included) and `env`, the set `KE_`-prefixed environment variables, name and value, sorted by name. Only that prefix is read, so a capture carries the levers that shaped the run and nothing else off the machine. `SelectEngineVariables` is the pure filter and `ReadProcessEnvironment` the one impure member, so the whole header is testable via `Build(session, environment)`.
  - **Handed in by the consumer:** `AppName` / `AppVersion` / `BuildName`, plus the GPU block (`GpuBackend`, `GpuBackendSource`, `GpuRequestedBackend`, `GpuRequestedOverride`, `AdapterDescription`, `SoftwareAdapter`, `DeviceLossReason`, `InjectedModules`, `DriverCommandLists` / `DriverConcurrentCreates`). Those are plain strings and nullable bools rather than the `KhaozEngine.Gpu` types because that package references THIS one, not the reverse. Fill them in one call with `KhaozEngine.Gpu`'s `GpuTelemetry.WithGpu`. Blank reads as JSON `null` on every field except `requestedOverride`, which is written exactly as it was read. `injectedModules` keeps null (never scanned) apart from `[]` (scanned, clean), and `threading` is null on every backend but Direct3D11.
  - **`softwareAdapter` and `deviceLossReason` (17.32.0)** are appended inside the envelope, so `session.v` does not move. `softwareAdapter` is three-valued: `true` on a software rasterizer (on Direct3D11, `DXGI_ADAPTER_FLAG_SOFTWARE`, and on the native Vulkan backend a physical device whose type is CPU or whose driver id is Mesa's llvmpipe), `false` on hardware, `null` when nobody answered. Keep those apart, because performance numbers off a software rasterizer are not comparable with numbers off a GPU and a capture that cannot say which it was gets averaged in with the others. `deviceLossReason` is null on every ordinary session and otherwise carries `GetDeviceRemovedReason`'s answer plus the call site that noticed, for example `"DXGI_ERROR_DEVICE_HUNG at present"`. It is read at the FIRST site to notice, because `DXGI_ERROR_DEVICE_REMOVED` is sticky and by the time a crash handler asks, the reason has been overwritten by whatever ran next.
  - **A fallback records what was asked for.** `requestedBackend` is the backend that did not run, set on a `FallbackAfterFailure` source. It also appears on a `DefaultProviderMissing` source in 17.40.0 captures and in nothing newer, since that source was retired at 18.0.0 with the incumbent fallback it reported and has no producer. It is the only record of a player's own in-game backend choice on a `UserPreference` fallback. `requestedOverride` is the RAW `KE_GRAPHICS_BACKEND` value, written verbatim and never normalized, since a typo or stray quoting is exactly what it exists to show.
  - **The game's own durables:** `AddGameValue(key, value)` / `AddGameValues(pairs)` land under the header's `game` section, so nothing a game records can collide with an engine field. `AddGameValues` takes any `IEnumerable<KeyValuePair<string, string>>`, which is the one-call dump of an F1 overlay's rows. A repeated key replaces in place, and both return the instance so construction chains. The engine names no game type. `GameValues` hands back a genuinely read-only view, so casting it to `IList<T>` throws rather than opening a back door.
  - **A header the machine will not let us write degrades to a headerless recording.** The write is guarded against IO, permission, and environment failures, so none of those throws out of `Start` and the samples still record. It is not a blanket catch, so something like an `OutOfMemoryException` still propagates, as it should.
- `ClientNetStats` - the connection-health snapshot (`RttMs`, `PacketLoss`, `BytesInPerSec`/`BytesOutPerSec`, `SnapshotsPerSec`, `LastCorrectionMeters`/`AvgCorrectionMeters`, `Connected`) that `KhaozEngine.NetWorld`'s `WorldClient.NetStats` fills and the overlay renders. It lives here (not in NetWorld) so the Gui overlay can name it without depending on the server/netcode stack.

The overlay widget that draws these lives in [`KhaozEngine.Gui`](../KhaozEngine.Gui) (it depends on this leaf, not the reverse): `DiagnosticsOverlay` + `DiagnosticsOverlayTheme` (+ `OverlaySection`/`OverlayRow`) - an F1-toggled corner panel the game assembles each frame via `SetSections`, with `PerformanceSection(FrameStats)` / `PassTimingsSection(PassTimings)` / `NetworkSection(in ClientNetStats)` populators and a headless-testable `Update`. See the `KhaozEngine.Gui` README.

## Evaluated capacity demand

`KhaozEngine.Diagnostics.CapacityDemandDiagnostics.Observe` subscribes to count-only `CapacityDemand`
records for evaluated gates on success and refusal. Admission identities distinguish independent counters
and shared allowances. Operation identities distinguish nested acquisitions and windows within one build.
Checks and operation scopes allocate nothing without a subscriber, including first use. Observer failures
cannot alter admission. A gate whose allowance is a remainder or minimum names its derived limits object as
`allowanceSource`, after `Derive` recorded that object's `CapacityDemandOrigin` fields, and then publishes one
event per originating field. See [capacity demand](../docs/CAPACITY-DEMAND.md) for collection, lifetimes,
derived allowances, profile sizing and the per-limit enforcement-site map.
