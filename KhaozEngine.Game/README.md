# KhaozEngine.Game (5.x)

A 2D game-loop facade over the MonoGame-free 5.x stack. `GameApp` is an abstract base that owns the
per-frame composition + ordering so a game can't get it wrong:

```
OnLoad();
each frame:
  Clock.Update(dt)
  Viewport.Update(window size)   -> OnResize on change
  Pointer.Update(input, Viewport)
  OnResume(wallGap)              // only when the wall-clock gap exceeds the threshold (OS sleep/suspend/hang)
  OnUpdate(Dt)
  OnRenderWorld(frame)           // empty by default; a subclass renders a world here (e.g. a 3D scene)
  Surface2D.NewFrame(frame); Batch.Begin(Viewport); OnDraw2D(Batch); Batch.End()
```

Subclass it and override `OnLoad` / `OnUpdate(dt)` / `OnDraw2D(batch)` / `OnResize(w, h)`; call
`Quit()` to close. Construct with `GameAppOptions.For(title, w, h)` (set `DesignWidth/Height`,
`ScaleMode`, `ClearColor` as needed).

Set `GameAppOptions.PresentMode` (`Vsync` default / `Immediate`), the frame cap, and `GameAppOptions.WindowMode`
(`Windowed` default) for the initial presentation (present mode since 9.23.0, window mode since 9.24.0). A positive
`GameAppOptions.FrameCapHz` is an explicit cap that paces the loop to a target Hz with a monotonic-clock limiter
independent of the swapchain's vsync - pin it to an integer multiple of a fixed network tick (e.g. 60/120 for 30 Hz)
to keep presentation phase-aligned. Frame cap, `FrameCap`, `BackgroundThrottle`, and window mode are applied on both
the default and a custom `WindowFactory` window. `PresentMode` selects the swapchain vsync at creation on the default
window (a custom factory must forward it, though it can be flipped live).

**Backend-aware cap + background throttle** (since 10.96.0): sensible pacing by default, so a game no longer free-runs
a whole core plus the GPU out of the box. `GameAppOptions.FrameCap` defaults to `FrameCap.Auto` - a real cap on Metal
+ vsync (the display refresh, else 120, where the CPU otherwise free-runs), uncapped on D3D11/Vulkan where vsync
throttles. A consumer-set value wins: `FrameCap.Uncapped` is the pre-10.96 free-run, `FrameCap.Hz(n)` a fixed cap, and
a positive `FrameCapHz` overrides `FrameCap`. `GameAppOptions.BackgroundThrottle` (default ON when `null`) idles a
minimized game (skips render + present, but `OnUpdate` keeps running so netcode/timers advance) and drops an
unfocused-but-visible one to a low cap. `BackgroundThrottlePolicy.Disabled` renders full-rate in the background. Both
are live-changeable via `GameApp.FrameCap` / `GameApp.BackgroundThrottle`.

**Runtime display settings** (since 9.24.0): change present mode, frame cap, window mode, and resolution live
mid-session (no crash, no leaked swapchain) via `GameApp.Display` (the cohesive `IDisplaySettings` surface) or the
`GameApp.PresentMode` / `FrameCapHz` / `WindowMode` pass-throughs. Read a `DisplaySettings` snapshot from
`Display.CurrentDisplay`, tweak it (`with`), and `Display.ApplyDisplay(...)` it back from a settings screen.
Since 9.26.0 `Display` also carries window placement - position + monitor (`WindowX` / `WindowY` / `MoveTo`,
`Monitors` / `CurrentMonitorIndex` / `MoveToMonitor`, `EnsureVisible`) and `X`/`Y` on the `DisplaySettings` snapshot -
so a game can persist + restore its full window placement across launches (the restore self-clamps on-screen).
`GameApp.Backend` exposes the active graphics backend for per-platform tuning. You no longer need to branch on it to
cap Metal - `FrameCap.Auto` (the default) does that. The engine warns once only if you explicitly force an uncapped
free-run with vsync on Metal.

Override `OnResume(TimeSpan wallGap)` to react to an OS sleep/suspend/hibernate (or a long hang): it fires once,
before `OnUpdate`, on the first frame whose wall-clock gap (`Clock.RealWallGapSeconds`, which survives a suspend
where the frame `dt` does not) exceeds `GameAppOptions.ResumeGapThresholdSeconds` (default 30s; 0 or negative
disables). Use it for offline/AFK catch-up, timer re-sync, or an auto-pause. The 0.1s sim-delta clamp and `Dt`
are unaffected.

Set `GameAppOptions.WindowIconPath` (a PNG) or `WindowIcons` (explicit decoded `ImageRgba`, multi-res,
wins over the path) for the runtime window/taskbar icon; `GameApp` decodes via `Render2D.ImageRgba`
and applies it through `AppWindow.SetIcon` while the window is still hidden, then shows it, so the Windows
taskbar button is born with the icon. Windows/Linux get the live title-bar/taskbar icon. On macOS
`SetIcon` is a no-op (GLFW can't set the Dock icon), so when `WindowIconPath` is set `GameApp` also feeds that
PNG to `AppWindow.SetMacDockIcon` to set the Cocoa Dock / Cmd-Tab icon at runtime (fixes the generic document
icon on an unbundled `dotnet run`). The Windows `.exe` icon stays a per-game `<ApplicationIcon>`.

Set `GameAppOptions.AppUserModelId` (e.g. `"APKiwi.Nullwake"`, a dotted `CompanyName.ProductName`) so the
running app's **Windows taskbar button shows the app icon** instead of the generic `.exe` placeholder. `GameApp`
sets the process's explicit Windows AppUserModelID (via `AppWindow.TrySetProcessAppUserModelId`) before creating
the window, which also stabilises taskbar grouping/pinning. Null (the default) keeps the current behaviour;
no-op off Windows.

**Single-instance guard** (opt-in). Set `GameAppOptions.SingleInstance = true` to allow only one live
instance of the game to run at a time. `GameApp`'s constructor claims a named OS mutex (via
`KhaozEngine.App.SingleInstanceGuard`) at the very top, BEFORE any window or GPU device is created, keyed by
`GameAppOptions.SingleInstanceId` (falling back to `AppUserModelId` - setting `SingleInstance` with neither
set throws). If another instance already holds the key, this process asks it to come to the foreground, logs
one line, and exits cleanly (code 0) without ever constructing a window. The owning instance listens for that
foreground request on a background thread and drives the actual OS focus call (`AppWindow.RequestForeground`)
from inside its own `Run` frame loop, since GLFW is not thread-safe for that call off the main thread. See
`KhaozEngine.App`'s README ("SingleInstanceGuard") for how this composes with a forced `AppRelaunch.Restart`
and resolves the auto-updater's relaunch-stacking gap (`KhaozEngine.Updates`), so a post-update relaunch that
lands on a still-running sibling self-resolves instead of stacking a third window.

```csharp
var options = GameAppOptions.For("Nullwake", 440, 956);
options.AppUserModelId = "APKiwi.Nullwake";
options.SingleInstance = true;   // reuses AppUserModelId as the guard key
```

**WinExe console attach.** Ship the Desktop head as `<OutputType>WinExe</OutputType>` (no stray console window on
Windows). Because a Windows-subsystem exe has no console, `GameApp` calls `AppWindow.TryAttachParentConsole()` as
its very first action, attaching the launching terminal's console so `Console.Write*` still shows under
`dotnet run` / cmd / PowerShell; it is a silent no-op off Windows, on an Explorer/Start launch, and when output is
redirected. Opt out with `GameAppOptions.SuppressParentConsoleAttach = true` (default off). When a Windows GUI
launch leaves the process with no console, `GameApp` also installs a last-chance crash-log net (fatal startup
exceptions are written to a file under `%LOCALAPPDATA%\KhaozEngine\crash\`) so a no-console startup crash is never
silent - wire `KhaozEngine.Diagnostics.CrashHandler` for the richer `game.log` path. See
[docs/USING-KHAOZENGINE.md](../docs/USING-KHAOZENGINE.md) "Game head build settings".

**Point-space UI pass** (since 10.12.0, for DPI-aware crisp UI): `GameApp` exposes a per-frame
point-space `Ui` (a `UiViewport`) and `UiPointer` (a `Pointer` mapped through it), plus a new virtual
`OnDrawUi(SpriteBatch batch)` that runs in a SECOND draw pass after `OnDraw2D` each frame, with the batch
already in `Begin(Ui)`. `OnDraw2D` stays the design-space (letterboxed) game field, `OnDrawUi` is the
DPI-aware UI layer: author text via `DpiFont.For(Ui.DpiScale)` and hit-test with `UiPointer`. `OnDrawUi`
is empty by default, so a game that only overrides `OnDraw2D` is completely unaffected.

`SceneManager` gains `UiViewport`, `UiPointer`, and `DrawUi(SpriteBatch)`, and `GameScene` gains a virtual
`OnDrawUi(SpriteBatch)`. A scene draws its DPI-aware UI in `OnDrawUi` and hit-tests via `Manager.UiPointer`.

**Turn-key diagnostics HUD** (default ON, F1): `GameApp` / `GameApp3D` wire a `Gui.DiagnosticsHud` automatically -
an F1-toggled panel showing fps / frame-ms / heap, the frame draw counters (`RenderFrameStats`: draw calls,
instances, triangles, upload bytes, 2D quads/flushes/tex-switches), and, for a 3D app, per-pass CPU-encode
timings (`Scene3D.EnableTiming` is coupled to visibility, so it costs nothing while hidden). Hidden by default,
so the only cost until F1 is the always-on counter increments. `GameAppOptions.DiagnosticsToggleKey` rebinds the
key (default `Key.F1`) and `GameAppOptions.DisableDiagnosticsOverlay` turns it off. A subclass reaches it through
the protected `Diagnostics` property (e.g. `Diagnostics?.SetNetStatsSource(...)` for a Network section) and can
override `CollectFrameStats` / `SupportsPassTimings` (`GameApp3D` already adds the scene's `LastFrameStats` and
the pass-timing section). See `docs/USING-KHAOZENGINE.md` "Seeing where the frame goes".

**Turn-key client-side job scheduler** (default ON): `GameApp.JobScheduler` lazily builds a shared
`KhaozEngine.Simulation.ThreadPoolJobScheduler` sized to `GameAppOptions.JobSchedulerDegreeOfParallelism` (or
`Math.Max(1, Environment.ProcessorCount - 1)`, leaving one core free for the render/main thread, when that
option is unset) - or, when `GameAppOptions.DisableJobScheduler` is set, the deterministic single-threaded
`SingleThreadedJobScheduler` instead, so the property is always non-null and safe to assign unconditionally.
Wire it into your own ECS world once, at load time, for turn-key multi-core `World.ParallelForEach`:

```csharp
protected override void OnLoad()
{
    _world.DefaultScheduler = JobScheduler;   // one line: every no-scheduler ParallelForEach now fans across cores
}
```

See `docs/USING-KHAOZENGINE.md` "Client-side parallel ECS" for the determinism note (per-row-pure actions
only) and more wiring examples.

**Boot / startup screen** (the `Boot/` folder): a turn-key instant-on startup experience. Push `BootScreen` as
the FIRST scene and it shows a progress bar in the first frames, then runs a staged `BootPipeline` while the bar
advances (update check + apply, server-status min-version gate, then the game's own asset-warm-up steps), and
replaces itself with the game's first scene on success. The heavy work is deferred into the pipeline, so no game
asset loading precedes the bar (process start + window creation still precede it - that is the honest floor). The
boot screen renders with only the engine-internal default font (as a DPI-aware `DpiFont`, so its text is
texel-crisp on HiDPI) + a 1x1 white texture, so it needs zero game assets.

- `IBootStep` (`Name`, `Weight`, `RunAsync(IBootProgress, CancellationToken)`) is the step seam. Steps run in
  order, each mapped onto its weighted slice of one overall bar, and may report a determinate fraction or mark
  their slice indeterminate. Build a game step from a delegate with `BootStep.Create`. Throw `BootStepException`
  (with a `LocalizedText`) to fail with a localized message.
- `BootPipeline` runs the async steps on a main-thread pump (`Start` + `Pump` per frame), so a step body and any
  game asset warm-up run on the render thread (GPU-safe) while awaited I/O runs off-thread and the bar keeps
  animating. `Snapshot()` gives an immutable `BootView` to render.
- Built-in steps (each optional): `UpdateBootStep` wraps `UpdateService.EnsureUpToDateAsync` (feed check +
  download + apply-and-restart). `ServerStatusBootStep` wraps `ServerStatusClient` + `ServerStatusEvaluator`
  (one blocking fetch, min-version gate). Both degrade gracefully when the feed / endpoint is unreachable.
- `BootScreen.Create(white, font, options, firstScene, onQuit)` assembles the pipeline from `BootOptions` and
  returns the scene. `font` is a DPI-aware `DpiFont` (build it with `Surface2D.LoadDefaultDpiFont(pointSize,
  cacheSlots: 4)` - still zero game assets): the screen bakes each label at its exact device-pixel size, so text
  is crisp on HiDPI. A fixed `SpriteFont` overload is kept for back-compat but is resampled by the theme scales.
  `BootScreenTheme` restyles it (colours, bar geometry, optional logo + custom-background hook) without forking.
  An indeterminate step shows the marquee over a bare track with no fraction fill under it, and `MarqueeColor`
  resolves to a lightened `BarFill` unless assigned, so restyling the fill carries the marquee with it.
  `BootStrings` holds the localized `boot.*` copy with an English fallback.

```csharp
protected override void OnLoad()
{
    var white = Surface2D.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
    var font = Surface2D.LoadDefaultDpiFont(28f, cacheSlots: 4);   // DPI-aware engine font, no game asset

    var options = new BootOptions
    {
        UpdateService = _updates,                                // optional: null skips the update step
        ServerStatusClient = _status, LocalClientVersion = "1.4.0", // optional: null skips server status
        GameSteps = new[]
        {
            BootStep.Create(MyStrings.LoadTextures, weight: 3f, async (progress, ct) =>
            {
                for (int i = 0; i <= total; i++) { LoadTexture(i); progress.Report(i / (float)total); await Task.Yield(); }
            }),
        },
    };

    _scenes.Push(BootScreen.Create(white, font, options, firstScene: () => new MainMenuScene(...), onQuit: Quit));
}
```

It sits above `KhaozEngine.Windowing` + `Render2D` + `Gui`, and (for the boot steps) `KhaozEngine.Updates` +
`KhaozEngine.ServerStatus` - **no 3D renderer dependency**, so a 2D game pulls no Render3D. For a 3D world pass,
use `KhaozEngine.Game.Render3D` (`GameApp3D`, plus the `IGameScene3D` scene hook and the `SceneManager.Draw3D`
extension). It is the optional convenience layer: a game with special needs can still drive `AppWindow.Run`
directly.

```csharp
sealed class Demo : GameApp
{
    public Demo() : base(GameAppOptions.For("Demo", 960, 540)) { }
    protected override void OnUpdate(float dt) { if (Input.WasPressed(Key.Escape)) Quit(); }
    protected override void OnDraw2D(SpriteBatch batch) { /* draw */ }
}

using var app = new Demo();
app.Run();
```
