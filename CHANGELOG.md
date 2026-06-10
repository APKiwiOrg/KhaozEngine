# Changelog

All notable changes to KhaozEngine. Versions are shared across all packages.

## KhaozEngine 3.1.0

- **KhaozEngine.Diagnostics**: replaced the minimal `FileLogger` with a full logging service.
  `LogManager` (instance core, injectable) + a static `Log` facade own a runtime-settable
  `MinimumLevel`, an injectable `IClock`, and a list of `ILogSink`s. Category loggers via
  `Log.For<T>()` / `GetLogger(string)` stamp a component tag on each `LogEntry`
  (`Trace`/`Debug`/`Info`/`Warn`/`Error`/`Fatal`, each with an optional exception). Writes are
  non-blocking by default (a single background thread drains a bounded queue; overflow is counted in
  `DroppedCount`, reported on the next flush, and never blocks the caller) with a synchronous mode for
  deterministic tests; `Flush`/`Shutdown` drain the queue and flush sinks, and logging never throws,
  including after shutdown.
- Sinks: `FileSink` (rotate-on-launch + optional size-based rotation + retention via
  `FileSinkOptions.MaxBytes`/`MaxFiles`, `AutoFlush` for crash survivability), `ConsoleSink`
  (stderr for errors), `DebugSink` (`System.Diagnostics.Trace`), and `InMemorySink` (tests). Games
  add their own target by implementing `ILogSink`.
- `CrashHandler.Install` wires `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`
  to log a `Fatal` `Crash` entry and flush, so games stop hand-rolling crash hooks.
- Promoted `AppDataPaths`: OS-correct per-app data directory resolver (Windows `%APPDATA%`, macOS
  `~/Library/Application Support`, Linux XDG), created on first access and cached per app name. Engine
  logging stays path-agnostic; games pass resolved paths into `FileSinkOptions`.
- **BREAKING (shipped as a minor):** `FileLogger` is removed; consumers move to `Log`/`LogManager`. The
  default log line format gains a `[Category]` field: `[ts] [LEVEL] [Category] message`. Numbered 3.1.0
  (not 4.0.0) by owner decision: every consumer is first-party and migrated in lockstep, so the 3.x
  line is kept. This deliberately deviates from the usual SemVer "breaking = major" rule. All packages
  to 3.1.0.

## KhaozEngine 3.0.0

- **KhaozEngine.UI**: new `PrimitiveRenderer.DrawRing` (static + instance overloads) draws a circle
  outline with sub-pixel **float** thickness by stitching rotated 1x1-pixel quads along the radius
  path, so fractional thicknesses render faithfully (unlike `DrawCircle`'s integer line width). No-op
  when radius or thickness is non-positive. `RingSegments(radius, segmentsOverride)` exposes the
  segment count: an explicit override (floored at 3) or a radius-adaptive count clamped to `[18, 64]`.
- New package **KhaozEngine.Diagnostics** with `FileLogger`: a thread-safe, timestamped file logger
  for diagnosing silent crashes and startup failures. `Initialize(logFilePath, previousLogFilePath?)`
  opens an `AutoFlush` `StreamWriter` and rotates an existing log aside (when a previous path is given)
  so the most recent run is always in the primary file; `Info`/`Warn`/`Error`/`Error(msg, ex)` write
  `[ts] [LEVEL] message` lines; `Shutdown` (also `Dispose`) flushes and closes. Every method swallows
  IO failures so logging can never crash the game. Pure `System.IO`, no MonoGame dependency. The log
  path is the caller's concern (each game resolves its own app-data path and passes it in). Extracted
  from SpaceGame's in-house `GameLogger` (Nullwake had a near-identical copy; Hardpoint had none);
  instance-based and headless-testable. Adopted by SpaceGame and Nullwake.
- **KhaozEngine.Content**: fix `JsonSchemaValidator` crash ("Overwriting registered schemas is not
  permitted") when multiple data files reference the same schema file (share a `$id`). The validator
  now passes an isolated `SchemaRegistry` via `BuildOptions` to each `JsonSchema.FromText()` call
  instead of using the global static registry, so repeated builds and multi-file directories with
  shared schemas no longer abort with exit code 134. No API surface change; all existing callers
  are unaffected.
- Major bump consolidates the Content validator fix, the new Diagnostics package, and the
  `DrawRing` primitive into one clean release after untangling concurrent development. All changes are
  additive; no behaviour change for existing consumers. All packages bump to 3.0.0.

## KhaozEngine 2.4.0

- **KhaozEngine.UI**: new `PannableCanvas`, a generic pannable viewport. Owns a camera offset;
  pans on drag (`InputManager.GetDragDelta`) and vertical wheel (`InputManager.GetScrollIn`) within
  a caller-set `Viewport`; clamps the camera to `ContentBounds` inflated by `Padding` (centering an
  axis when content is smaller than the viewport). Exposes `WorldToScreen`/`ScreenToWorld`,
  `PointerWorld`, and `TryGetTap(out pressWorld, out releaseWorld)` (gated on the press-origin tap
  invariant so it stays click-through-safe). `CenterOn`/`Focus`/`CenterContent` recenter the camera.
  `Draw(sb, gd, renderScale, scaleMatrix, drawWorld)` scissor-clips to the viewport and invokes a
  world-space draw callback (pass `vr.Scale`/`vr.ScaleMatrix`). Zoom is not implemented; a single
  fixed scale, with the transform seam kept for later.
- Generalizes the inline camera/pan code in Nullwake's `SkillTreeScreen` so a node-graph / map screen
  needs no per-game reinvention. Additive and opt-in; no behaviour change for existing consumers.
  All packages bump to 2.4.0.

## KhaozEngine 2.3.0

- **KhaozEngine.Time**: new `TimeSkip` (+ `TimeSkipResult`) for advancing a simulation by a span of
  sim-time in one analytical call. `Advance(simSeconds, step)` clamps to an optional `MaxSimSeconds`,
  scales by `Multiplier`, skips requests below `MinSimSeconds` (and any `<= 0`), invokes the consumer's
  analytical catch-up callback once, raises `Completed`, and returns a `TimeSkipResult`
  (requested/applied seconds, `WasCapped`, `Ran`). Static `TimeSkip.ElapsedSimSeconds(lastSave, now,
  timeScale)` computes offline wall time (clamped >= 0, optionally scaled by sim speed).
- For on-demand "fast-forward for credits" and offline catch-up. The engine simulates nothing itself
  (the game supplies the analytical step); there is no per-frame budget because analytical catch-up is
  instant. Additive and opt-in; no behaviour change for existing consumers. All packages bump to 2.3.0.

## KhaozEngine 2.2.0

- New package **KhaozEngine.Time** with `GameClock`: separates real delta time (UI, transitions,
  notifications) from a scaled simulation delta. `TimeScale` gives slow-mo (`<1`), normal (`1`), and
  fast-forward (`>1`); `Pause()`/`Resume()` freeze the sim orthogonally to `TimeScale` (resume keeps the
  intended speed); `Paused`/`Resumed` events fire on transitions; `IsPaused` is true when paused or
  `TimeScale == 0`.
- **KhaozEngine.Screens**: `ScreenManager` now owns a `GameClock` (new `ScreenManager(InputManager, GameClock)`
  overload to share one), exposes `Clock`/`IsPaused`/`TimeScale`/`RealDeltaSeconds`/`ScaledDeltaSeconds`,
  drives transitions on real dt (so they stay live while paused), dispatches new
  `GameScreen.OnPause()`/`OnResume()` virtuals to stacked screens on pause transitions, and is now
  `IDisposable` (unsubscribes from a shared clock).
- Additive and opt-in. Default `TimeScale == 1` makes scaled dt identical to today, so the existing
  consumers are unchanged. Gameplay reads `ScaledDeltaSeconds` (e.g. `world.Update(ScaledDeltaSeconds)`);
  UI/transitions/notifications keep using real time. SpaceGame's fixed-timestep lockstep never reads the
  scaled delta, so determinism is preserved. All packages bump to 2.2.0.

## KhaozEngine 2.1.0

- New package **KhaozEngine.Content** (pure .NET, depends on JsonSchema.Net): `ConfigLoader.Load<T>`
  (embedded/disk JSON) and `JsonSchemaValidator` (instance + directory validation), plus a bundled
  validator tool and a `buildTransitive` target that validates a consumer's `Data/` against its schemas
  when `KhaozContentDataDir` is set. Generalizes Nullwake's config pattern; opt-in. All packages bump to
  2.1.0 (unified versioning); no changes to the existing four.

## KhaozEngine 2.0.0 (unified versioning)

- All four packages (Input, Screens, UI, Ecs) now share one version line and the `v*` tag scheme; the
  separate `ecs-v*` line is retired and `Ecs` no longer overrides its version. **No functional change:**
  Input/Screens/UI `2.0.0` are identical to `0.2.1`, and Ecs `2.0.0` is identical to `1.6.0`. Future
  releases bump all four together. Games can adopt `2.0.0` whenever convenient; existing vendored
  `0.2.1`/`1.6.0` references keep working.

## KhaozEngine.Ecs 1.6.0

- Deterministic outcome model: `EntityCommandBuffer.Defer(Action<World>)` (ordered deferred actions);
  a pull-model typed event channel (`World.Emit<T>` / `Events<T>`, cleared by `AdvanceTick`); and
  `DeterministicRng` (xorshift128+, seedable, save/resume `State`). Drawing RNG inside deferred actions
  gives a reproducible draw sequence (record order = the deterministic iteration order from 1.5.0).
  Additive and opt-in. Completes the determinism work (Cycles A + B).

## KhaozEngine.Ecs 1.5.0

- Deterministic iteration order: queries, `ForEach`, and serialization now walk archetypes in a
  guaranteed creation order (an explicit ordered list) rather than relying on `Dictionary` enumeration.
  Iteration is reproducible for an identical operation sequence, run-to-run and across processes
  (foundation for lockstep determinism). Swap-remove within an archetype is unchanged. Additive.

## KhaozEngine.Ecs 1.4.0

- Add named system groups: `AddSystem(system, group)`, `SetGroupOrder(...)`, `UpdateGroup(name, dt)`,
  and `SystemGroups`. `Update(dt)` runs all groups in order; `UpdateGroup` runs one (e.g. a
  fixed-timestep simulation group). Systems without a group use `"default"`, so existing usage is
  unchanged. Additive.

## KhaozEngine.Ecs 1.3.0

- Add a parent-child hierarchy: built-in `Parent` component, `World.SetParent` / `Detach` /
  `GetParent` / `Children`, and `DespawnTree` (cascade) vs plain `Despawn` (detaches children to
  root). Cycle-guarded. Hierarchies serialize (the children index rebuilds on load; `Parent` is
  auto-included by `WorldSerializer`). Transform propagation stays game-side. Additive.

## KhaozEngine.Ecs 1.2.0

- Add per-tick change detection: `World.AdvanceTick()` (call once per frame), `Added<T>()` /
  `Removed<T>()` (automatic from structural changes), `Changed<T>()` with explicit `MarkChanged<T>(e)`
  (since `ref` writes are invisible to the ECS). `Removed<T>` may include despawned entities. The load
  path does not generate events. Additive; no breaking change.

## KhaozEngine.Ecs 1.1.0

- Add `WorldSerializer`: JSON save/load of a `World` (entities + components + id-allocator state).
  Entities restore at their exact id/version so `Entity`-typed fields survive; tags and free-slot
  versions are preserved. Construct with your component types or `FromAssemblyOf<T>()`. Resources and
  systems are not serialized. Additive; no breaking change.

## KhaozEngine.Ecs 1.0.0

- Rewrite as a struct-based archetype ECS: versioned `Entity`, archetype/column storage, `ref`
  `Get<T>`, `With`/`Without` queries, `ForEach` arities 1-8, `EntityCommandBuffer`, typed `Resources`.
- Breaking vs 0.1.x: components are now `struct : IComponent`; `Get<T>` returns `ref T`; the
  `List<Entity> Query<T>()` overloads are replaced by `ForEach`. Versioned independently of the
  other KhaozEngine packages (which stay on 0.2.x).

## 0.2.1

- Fix: `PrimitiveRenderer.DrawProgressBar` rendered short bars as a solid line in the border
  color. A bar only a few pixels tall (e.g. a zoomed-out HP bar at 2px) left zero inner height
  after subtracting a 1px border on each side, so the fill never drew and the border covered the
  whole bar. The border thickness is now capped to keep at least a 1px fill area, dropping to 0 on
  bars too small to fit one. Adds headless geometry regression tests.

## 0.2.0

- `InputManager`: middle/right mouse-button edges (`IsMiddle/RightDown/JustPressed/JustReleased`).
- `InputManager.Touches` — active touches in virtual coordinates with stable ids (`TouchPoint.Id`).
- `InputManager.TryGetPinch(out Pinch)` — virtual midpoint, distance, per-frame delta, scale ratio.
- Optional gamepad/keyboard controller cursor via `cursorSpeed` ctor arg + `Update(raw, isActive, dt)`.
- All additive; 0.1.x consumers are unaffected until they bump.

## 0.1.3

- Fix: desktop clicks were suppressed whenever the game window was not at the screen
  origin. `InputManager`'s in-window check compared window-relative mouse coords against
  `WindowBounds` carrying the window's screen offset, so `Contains` rejected every click.
  The check now ignores `WindowBounds.Location` (uses Width/Height only), and
  `MonoGameRawInput` reports the client area at the origin. Adds headless regression tests.

## 0.1.2

- Add per-package README files (shown on the NuGet package pages).
- Add this changelog.

## 0.1.1

- XML documentation comments across the public API of `KhaozEngine.Input`, `.Screens`, and `.Ecs`.
- Enable `GenerateDocumentationFile` so docs ship in the packages for IntelliSense.
- No functional change from 0.1.0.

## 0.1.0

Initial release. Four packages extracted from Hardpoint/Nullwake/SpaceGame:

- **KhaozEngine.Input** — unified pointer (mouse+touch), `IsTapIn` press-origin invariant
  (click-through fix), region blocking, drag/scroll/pinch, keyboard + gamepad + menu-navigation,
  coordinate-transform seam (`Identity` / `Matrix` / `VirtualResolution`), all behind the testable
  `IRawInput` seam.
- **KhaozEngine.Screens** — screen stack with top-to-bottom routing, `ConsumeWhenVisible` /
  `ConsumeWhenHandled` policies, and transitions.
- **KhaozEngine.UI** — widget library, `PrimitiveRenderer`, `TextInputHandler`.
- **KhaozEngine.Ecs** — minimal `World` / `Entity` / `ISystem`.

30 headless tests. Hardpoint migrated onto it.
