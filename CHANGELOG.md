# Changelog

All notable changes to KhaozEngine. Versions are shared across all packages.

## KhaozEngine 3.9.0

Camera framing + follow, both in `KhaozEngine.Graphics`. Additive, no breaking changes.

### Camera2D framing helpers: CenterOn + Focus (fit-to-rect zoom)

`Camera2D` gains the framing math that consumers were hand-rolling (Hardpoint's `BoardFraming`,
SpaceForge's grid framing, `PannableCanvas`'s long-dormant `Focus(rect)` zoom seam):

- `CenterOn(Vector2 world)` — sets `Position` so the world point is at the viewport center (an explicit
  alias for API parity).
- `Focus(Rectangle worldRect, Viewport viewport, float paddingFraction = 0f, float minZoom, float maxZoom)`
  — fit-to-rect: sets `Zoom` so the rect (optionally inflated by `paddingFraction` on each side) is fully
  visible (contain fit, `min(viewport.Width / rectW, viewport.Height / rectH)`), clamped to
  `minZoom`/`maxZoom`, then centers `Position` on the rect. Pure and headless. Does not clamp to world
  bounds — call `ClampPosition` after if the rect is a sub-region. A no-arg-viewport overload uses the
  stored `Viewport` property.

Because these live on `Camera2D`, both `CameraController` and (once consolidated) `PannableCanvas`
inherit them.

### CameraFollow (target-follow with smoothing + deadzone)

New `CameraFollow` drives a `Camera2D` to follow a moving target. The game decides what to follow; this
owns only the smoothing/deadzone/clamp. Kept separate from the gesture `CameraController` — a screen
typically uses one or the other.

- `Update(Vector2 target, float dt, Viewport viewport, Rectangle worldBounds)` — eases toward the target,
  then clamps via `Camera2D.ClampPosition`. Headless (explicit `Viewport`).
- **Frame-rate-independent smoothing**: per-frame catch-up is `1 - exp(-Stiffness * dt)`, so the result
  is independent of step size / frame rate. `Stiffness <= 0` snaps instantly.
- **Optional deadzone**: a screen-space (virtual) `Rectangle` the target may move within before the camera
  chases; once the target crosses an edge the camera moves just enough to put it back on that edge.
  `Rectangle.Empty` (default) disables it (camera centers on the target).

Wiring:

    var camera = new Camera2D { Viewport = GraphicsDevice.Viewport };
    var follow = new CameraFollow(camera) { Stiffness = 8f, Deadzone = new Rectangle(360, 240, 200, 120) };
    // per frame:
    follow.Update(playerWorldPos, dt, GraphicsDevice.Viewport, levelBounds);
    // or frame a region instead of following:
    camera.Focus(levelBounds, GraphicsDevice.Viewport, paddingFraction: 0.05f, minZoom: 0.5f, maxZoom: 3f);

## KhaozEngine 3.8.0

New package `KhaozEngine.Sprites`: 2D sprite + directional-animation playback. Additive, no breaking
changes. Replaces flat-primitive entity rendering with directional, animated sprites for all games.

### KhaozEngine.Sprites (new)

- **`Direction8`** — the 8 facings `S, SE, E, NE, N, NW, W, SW`, ordered so the enum value is the
  direction's row index in a PixelLab grid sheet. `Direction8Extensions.FromVector(facing, fallback)`
  maps a movement/aim vector to the nearest of 8 in y-down screen space (+X east, +Y south); magnitude
  is irrelevant, a 22.5-degree seam rounds to the higher (clockwise) direction, and a zero vector
  returns `fallback`. `ToVector()` returns the unit facing.
- **`SpriteSheetLayout`** — pure grid math (no `Texture2D`, headless): `FromFrameSize` / `FromGrid`,
  then `GetFrame(row, column)` -> source `Rectangle`. **`SpriteSheet`** pairs it with a texture.
- **`SpriteFrame`** — a `(Texture2D, Rectangle)` drawable frame; frames carry their own texture so an
  animation can span one packed sheet or a set of loose per-frame textures.
- **`SpriteAnimation`** — ordered frames + per-frame duration + loop flag (`FromFps` or seconds ctor).
  **`SpriteAnimationPlayer`** advances it by a `float` seconds delta or a `GameTime`, yields the current
  frame, loops, flags `IsFinished` for one-shots, and `Play(anim, preservePhase)` swaps animations. A
  small relative tolerance on the frame boundary keeps exact-multiple deltas from dropping a frame to
  float noise.
- **`DirectionalAnimatedSprite`** — one animation per `Direction8`, plays the one matching the current
  facing, draws via `SpriteBatch` with a centered origin by default; switching facing preserves the
  animation phase so a walk cycle stays smooth. `Update(facing, gameTime)` does both in one call.
- **`PixelLabSpriteLoader`** — builds a `DirectionalAnimatedSprite` from a PixelLab export, either an
  assembled grid sheet (`FromGridSheet`: 8 direction rows x N frame columns) or loose per-direction
  frame textures (`FromFrames`). PixelLab's row order is isolated here (in `RowFor`) so the core types
  stay PixelLab-agnostic. Note: PixelLab exports loose per-frame PNGs, not a canonical sheet, so the
  grid layout matches an assembly step's output; verify row order against a real export on first use.

The animation clock decouples from `KhaozEngine.Time` deliberately (advances on a `float` delta), so
callers feed either `GameTime.ElapsedGameTime` or a scaled `GameClock.ScaledDeltaSeconds`.

## KhaozEngine 3.7.0

Two additive camera/viewport features. No breaking changes.

### KhaozEngine.Graphics: CameraController (pan/zoom/pinch gesture controller)

New `CameraController` drives an existing `Camera2D` from an `InputManager`, so gameplay can pan
and zoom an arbitrary world render without re-implementing the gesture math. It owns no matrix math
of its own: it reuses `Camera2D.ScreenToWorld` and `Camera2D.ClampPosition`.

- **Pan**: single-pointer drag and two-finger drag (by pinch midpoint travel). Grab-and-drag, so
  world content tracks the finger; the screen delta is divided by `Zoom` to a world delta.
- **Zoom**: scroll wheel (desktop) and pinch (mobile), clamped to `MinZoom`/`MaxZoom`. Zoom is about
  the cursor / pinch midpoint — the focal world point stays under the pointer. `WheelZoomStep` is the
  multiplicative factor per 120-unit notch (fractional/multi-notch deltas scale smoothly via a power).
- **Bounds clamp**: after pan/zoom, clamps via `Camera2D.ClampPosition(Position, worldBounds, viewport)`
  so the view stays inside a caller-supplied world rectangle (auto-centers when the world is smaller).
- **Tap vs pan**: `TryGetTap(out pressWorld, out releaseWorld)` mirrors `PannableCanvas.TryGetTap` and
  honors the press-origin invariant — gameplay places a tower on a tap but treats a drag as a pan
  (a pan returns true too, but its press/release world points differ, so a same-target check rejects it).
- **Headless**: `Update(Viewport, Rectangle worldBounds)` takes an explicit `Viewport` like `Camera2D`,
  so the step is unit-testable with no `GraphicsDevice`. Toggles: `EnablePan`, `EnableZoom`, `BlockInput`.

`KhaozEngine.Graphics` now references `KhaozEngine.Input` (for `InputManager`). Wiring:

    var camera = new Camera2D { Viewport = GraphicsDevice.Viewport };
    var controller = new CameraController(input, camera) { MinZoom = 0.5f, MaxZoom = 4f };
    // per frame, after input.Update(...):
    controller.Update(GraphicsDevice.Viewport, worldBounds);
    if (controller.TryGetTap(out var pressWorld, out var releaseWorld)) { /* place on tap */ }
    spriteBatch.Begin(transformMatrix: camera.GetViewMatrix());

Relationship to `PannableCanvas` (KhaozEngine.UI): both now carry pan/zoom gesture logic, but on
different coordinate conventions (`PannableCanvas` uses an additive offset and an inset sub-rectangle
viewport with scissor clipping; `CameraController` uses `Camera2D`'s position/zoom matrix). This
release ships `CameraController` standalone and leaves `PannableCanvas` as-is to avoid regressing the
games already on it (Hardpoint's map). Consolidating `PannableCanvas` onto `CameraController` is a
tracked follow-up; the two are not meant to diverge long-term.

### KhaozEngine.Input: opt-in desktop design-scale for VirtualResolution

`VirtualResolution` now offers a design-scaled mode on desktop, mirroring mobile: a fixed
`BaseWidth` × `ReferenceHeight` design space scaled to fill the window, so desktop UI presents the
same fixed design space (and scales up on a large/Retina window) instead of sizing in raw
back-buffer pixels.

- **Opt-in, non-breaking**: the desktop default (`isMobile:false` → scale 1, identity matrix, virtual
  size = back-buffer) is unchanged. Opt in with the new `VirtualResolution.DesignScaled(gdm, baseWidth,
  referenceHeight)` factory (still pass `isMobile:false` to the `InputManager`; only the scaling differs).
- **Fill policy**: fill-the-width, adaptive-height (the same as mobile) — no letterbox bars and no
  offset, so `ScreenToVirtual` stays a plain divide-by-`Scale` and `InputManager` hit-testing lines up.
- The `GraphicsDeviceManager` ctor argument is now nullable, and a new `Configure(int screenWidth,
  int screenHeight)` computes the scaling from an explicit size (`Initialize` delegates to it). This
  makes the scaling headless-testable and lets a consumer drive it from a known/fixed size.

Wiring a desktop game into design-scale:

    var vr = VirtualResolution.DesignScaled(graphicsDeviceManager, baseWidth: 932, referenceHeight: 430);
    vr.Initialize();                                  // and again on Window.ClientSizeChanged
    var input = new InputManager(isMobile: false, transform: vr);

## KhaozEngine 3.6.0

### KhaozEngine.Ecs: CachedQuery (per-tick allocation-free query reuse)

New `CachedQuery` lets sim hot paths reuse a single `Query` instead of allocating a fresh one
every tick. `World.Query()` returns `new Query(this)` per call, so calling it inside a per-tick
loop violates the consumers' "no per-frame allocation in sim hot paths" rule.

- `CachedQuery(Func<World, Query> build)` captures the filter builder once.
- `Query For(World world)` returns the reused `Query`, rebuilding it only when the `World`
  instance changes (`ReferenceEquals` check) — for consumers that recreate the `World` on
  run-reset. The underlying `Query` still self-refreshes its matched-archetype list on
  `ArchetypeGen` changes, so newly spawned archetypes are picked up through the cache.

Additive, no breaking changes. Usage:

    private readonly CachedQuery _projectiles = new(w => w.Query().With<ProjectileTag>());
    // per tick:
    _projectiles.For(world).ForEach((Entity e, ref Position p) => ...);

## KhaozEngine 3.5.0

### KhaozEngine.Graphics: DisplayManager (display/window configuration)

New `DisplayManager` centralizes MonoGame `GraphicsDeviceManager` + `GameWindow` setup so games
stop configuring the device bespoke.

- `DisplaySettings` (immutable record): `Width`/`Height`, `Mode` (`WindowMode.Windowed` /
  `BorderlessFullscreen` / `ExclusiveFullscreen`), `AllowUserResizing`, `MinWidth`/`MinHeight`
  floor, `SupportedOrientations`, `Title`. Factories `DisplaySettings.Landscape(w, h)` and
  `Portrait(w, h)`. Pure and headless-testable; build variants with `with`.
- `DevicePresets` catalog of common iOS logical-point sizes (iPhone SE to 15 Pro Max, iPad to
  Pro 12.9") via `DevicePreset.Portrait()` / `.Landscape()`.
- `DisplayManager(graphics, window, settings)` applies settings to the live device and exposes
  runtime mutators `Apply`, `SetResolution`, `SetMode`, `ToggleFullscreen`, `SetResizable`, plus
  `Width`/`Height`/`Size`/`IsFullscreen`. Enforces the min-size floor by clamping on
  `ClientSizeChanged`. Composes with `VirtualResolution`, which still reads the device for scaling.

One-liner for an iPhone 15 Pro Max landscape window (932x430):

    display = new DisplayManager(graphicsDeviceManager, Window, DisplaySettings.Landscape(932, 430));

## KhaozEngine 3.4.1

Bug fix for the 3.4.0 now-playing feature. No API or behaviour change for callers whose tracks all load.

- **KhaozEngine.Audio** - `AudioSystem.LoadContent` now drops any track that fails to load from its
  internal name list, keeping it aligned with the backend's compact track list. Previously a partial
  load failure left the names and the backend's indices misaligned, so `CurrentTrack` / `TrackChanged`
  reported the wrong song and `PlayTrack(name)` could resolve to the wrong index. The load log still
  reports `loaded/requested` against the originally requested count.

## KhaozEngine 3.4.0

Additive feature pass unblocking SpaceGame/Nullwake adoption, plus review-nit fixes. No breaking changes.

- **KhaozEngine.Persistence** - `SettingsManager<T>` gains an optional `sanitizeOnLoad` constructor hook
  (`Func<T,T>`). It runs on every load, including the initial load inside the constructor (which the
  `SettingsLoaded` event can't reach), so callers can clamp fields / migrate a schema version on the
  first load. Null = passthrough; a throwing hook is swallowed/logged and the unsanitized value is used.
  The README documents the `[JsonExtensionData]` + version-field downgrade-safe migration pattern.
- **KhaozEngine.Audio** - `AudioSystem` now supports explicit and repeating playback alongside random
  rotation: `PlayTrack(int)` / `PlayTrack(string)` (an unknown name or out-of-range index is a logged
  no-op, not a throw), a settable `PlayMode { RandomRotation, RepeatOne }` (default `RandomRotation`),
  and now-playing state via `CurrentTrack` plus the `TrackChanged` event.
- **KhaozEngine.Audio** - a transient exception while reading `IMusicBackend.IsPlaying` in `Update()`
  now skips the frame (logged) and recovers, instead of permanently disabling audio. The availability
  latch is reserved for real play/load failures.
- **KhaozEngine.Ecs** - `DeterministicRng.Next(maxExclusive)` and `Next(min, max)` now throw
  `ArgumentOutOfRangeException` on non-positive / empty ranges (previously a DivideByZero or
  negative-modulo trap).
- Docs/tests: `docs/USING-KHAOZENGINE.md` gains a `KhaozEngine.Graphics` / `Camera2D` section; the
  Effects pool-recycle test now asserts the oldest particles are actually overwritten.

## KhaozEngine 3.3.0

Batch 2 of the "promote duplicated game code into KhaozEngine" effort: three new packages plus
additions to two existing ones. All additive; no consumer adopts these yet.

- **KhaozEngine.Audio** (new; MonoGame + Diagnostics): `AudioSystem` (track-registry music player,
  seed-via-ctor + additive idempotent `RegisterTrack`/`RegisterTracks` that work pre- and post-load)
  over a public `IMusicBackend`. Public `MonoGameMusicBackend` and `MacOsMusicBackend` (the macOS
  backend works around MonoGame's broken `Song` playback via an AVAudioPlayer P/Invoke shim). Logs
  through an injected `ILogger` (defaults to the engine `Log`).
- **KhaozEngine.Effects** (new; MonoGame + UI): pooled, data-driven particle system. A
  `ParticleEmitterConfig` record holds all tunables; `ParticlePresets.Spark`/`.Ember` reproduce the
  promoted Nullwake hit effects; `ParticleSystem.Emit(config, position, baseColor, count)` with a
  ring-buffer pool. First resident of a generic visual-effects package (room for screen shake, flashes, etc.).
- **KhaozEngine.Graphics** (new; MonoGame): `Camera2D` — a generic 2D matrix camera
  (position/zoom/rotation → view matrix), headless `WorldToScreen`/`ScreenToWorld` (explicit `Viewport`,
  no `GraphicsDevice`), turn-key no-arg overloads via a settable `Viewport`, and a pure
  `ClampPosition` world-bounds helper. The base for a future follow/deadzone/parallax camera layer.
- **KhaozEngine.Persistence** additions: `AtomicJsonWriter` (crash-safe temp-then-move writes),
  `PersistenceQueue` (`IPersistenceQueue`; per-path coalescing async writer, never throws into the
  game, retry + `WriteFailed` event, blocking `Flush()` + flush-on-dispose), and
  `SettingsManager<T>` / `ISettingsStorage` / `FileSettingsStorage` (typed settings persisted via the
  queue, default paths through `KhaozEngine.App.AppDataPaths`). Persistence now also references `KhaozEngine.App`.
- **KhaozEngine.Ecs** addition: `DeterministicRng.CreateDerived(string systemName)` — named, stable,
  reproducible substreams (mixes the parent seed with a fixed string hash; not `string.GetHashCode`).
  Note: derived streams do not byte-match `System.Random`, so any consumer migrating to it must re-baseline golden values.

## KhaozEngine 3.2.0

Batch 1 of the "promote duplicated game code into KhaozEngine" effort. Three new pure-.NET packages
(plus a small consolidation of the `AppDataPaths` that 3.1.0 had shipped). No consumer adopts these yet.

- **KhaozEngine.App** (new, pure .NET): app/runtime helpers.
  - `BuildMetadata.Read(string key, string fallback, params Assembly?[] assemblies)` — reads
    `AssemblyMetadataAttribute` values at runtime, probing the supplied assemblies in order (null
    entries skipped), so a game can surface its own version/build identity without re-deriving it.
  - `AppDataPaths` — instance resolver for the OS-correct per-app data directory (Windows `%APPDATA%`,
    macOS `~/Library/Application Support`, Linux `$XDG_DATA_HOME`/`~/.local/share`, with fallbacks).
    `BaseDirectory` is resolved + created once and cached (thread-safe via `Lazy<T>`); convenience
    `SaveFilePath`/`SettingsFilePath`/`LogFilePath`/`PreviousLogFilePath`/`GetFilePath`. OS resolution
    sits behind an internal seam for headless testing.
  - `ServiceLocator : IServiceProvider` — generic register/resolve-by-type service registry backed by a
    `ConcurrentDictionary` (`Register`/`Replace`/`Get`/`TryGet`/`Has`/`GetService`). Fits
    `ScreenManager.Services`.
- **KhaozEngine.Localization** (new, pure .NET): `LocalizationManager(ResourceManager)` discovers the
  cultures backed by satellite resources (`GetSupportedCultures`) and sets the current thread culture
  (`static SetCulture`, fail-fast on null/empty); `DefaultCultureCode = "en-US"`.
- **KhaozEngine.Persistence** (new; refs `KhaozEngine.Diagnostics`): `SaveEncoder(byte[] hmacKey,
  string magicPrefix, ILogger logger)` wraps save JSON in a Base64 + HMAC-SHA256 envelope
  (`{prefix}:{hmac}:{base64}`) as a casual tamper-deterrent. Decoding is lenient (recovers the JSON
  even on an HMAC mismatch) and reports each outcome (Info / Warn / Error) through the injected
  engine `ILogger`.
- **AppDataPaths consolidation:** `KhaozEngine.App.AppDataPaths` is the canonical resolver; the
  duplicate static `KhaozEngine.Diagnostics.AppDataPaths` that 3.1.0 shipped is **removed** (engine
  logging is path-agnostic — pass resolved paths into `FileSinkOptions`). Removing a 3.1.0 public type
  is breaking in principle, but numbered 3.2.0 (not 4.0.0): no released consumer referenced it (3.1.0
  is not yet adopted by any game), consistent with 3.1.0's owner-choice handling of the `FileLogger`
  removal.

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
