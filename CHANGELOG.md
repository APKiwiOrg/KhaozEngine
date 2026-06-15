# Changelog

All notable changes to KhaozEngine. Versions are shared across all packages.

## Tools

Repo utilities under `tools/`. Not packages: never versioned, packed, or tagged.

### PixelLabSheetAssembler

- New offline tool (`tools/PixelLabSheetAssembler`, `IsPackable=false`). Assembles a PixelLab
  character export (zip or dir) plus an animation name into one `Direction8` grid sheet PNG for
  `PixelLabSpriteLoader.FromGridSheet`: 8 rows in `Direction8` order, N frame columns, uniform cell
  size, feet-on-baseline anchoring (opaque-bbox bottom), and hold-previous (or hold-next for a
  leading gap) missing-frame tolerance with warnings. Prints the `frameCount` and suggested `fps`.
  Uses SixLabors.ImageSharp 2.1.13 (Apache-2.0); no MonoGame/GraphicsDevice. See its README.

## KhaozEngine 4.8.0

Breaking change shipped as a minor bump: the `5.x` line is reserved for the experimental branch, so
this breaking namespace move ships as `4.8.0` rather than `5.0.0`. Pin deliberately if you implement
the moved contract.

- **`IChannelSplittable<TSelf>` and the `NetChannelReliability` enum moved from
  `KhaozEngine.Netcode.LiteNetLib` to `KhaozEngine.Netcode`** (namespace
  `KhaozEngine.Netcode.LiteNetLib` -> `KhaozEngine.Netcode`). Both are pure: the interface is just
  the `Has*/Extract*` members, the enum is two values, and neither names a LiteNetLib type. Moving
  them lets a batch DTO that lives in a transport-agnostic project (e.g. one shared with a web
  server) implement the split contract without pulling a UDP transport into that project.
- **`ChannelSplitter` stays in `KhaozEngine.Netcode.LiteNetLib`** (its `Send<T>` orchestration and
  `ToDeliveryMethod` genuinely use `LiteNetLib.DeliveryMethod`). `KhaozEngine.Netcode.LiteNetLib`
  now has a package dependency on `KhaozEngine.Netcode` for the moved types.
- **`KhaozEngine.Netcode` still has no LiteNetLib dependency** (only MonoGame). A dedicated test
  project (`KhaozEngine.Netcode.DecouplingTests`) references only the core package and implements
  `IChannelSplittable<T>` on a dummy struct; it compiling is the standing guard that the contract
  stays transport-free.

No type-forwards: `[TypeForwardedTo]` redirects the *assembly* for an unchanged full type name, so
it cannot bridge a *namespace* change. No shipped consumer references these types yet (all consumers
on 4.0.0; netcode unadopted), so nothing breaks in practice. Migration for any code that used them
is a one-line `using` swap:

```csharp
// before
using KhaozEngine.Netcode.LiteNetLib;   // IChannelSplittable<T>, NetChannelReliability, ChannelSplitter
// after
using KhaozEngine.Netcode;              // IChannelSplittable<T>, NetChannelReliability
using KhaozEngine.Netcode.LiteNetLib;   // ChannelSplitter (keep only if you call Send/ToDeliveryMethod)
```

## KhaozEngine 4.7.0

Additive. Two new packages extracting SpaceGame's reusable netcode. No change to existing packages.

### KhaozEngine.Netcode (new)

- New package: game-agnostic, transport-free netcode primitives (refs MonoGame for `Vector2`/`MathHelper`).
- `UnitAxisQuantizer`: 8-bit quantization of a unit-range `[-1,1]` axis to a signed byte and back
  (`Quantize` clamps then rounds `*127` away-from-zero; `Dequantize` is `v/127f`). The game keeps its
  own command record + packed field layout. Determinism: this rounding is sim-hash-relevant for any game
  that dequantizes commands before its host-authoritative deterministic sim, so the scheme is fixed.
- `ClientPrediction<TState,TCommand>`: client-side prediction + authoritative reconciliation. Seq-keyed
  pending-command buffer with oldest-drop bound, ack-prune, rebase to an authoritative basis + replay of
  unacknowledged commands, and decaying render-offset error smoothing with hard-snap and dead-zone. Game
  supplies `IPredictedState<TSelf>` (Position + WithPosition) and `ITickSimulator<TState,TCommand>`
  (one deterministic step); tunables via `PredictionSettings` (`PredictionSettings.Default` = 60 Hz,
  256-command buffer, 100u snap, rate 8, 1.5u dead-zone). Returns `ReconciliationResult`. State type is
  `struct`-constrained.
- `RemoteCommandQueue<TCommand>`: host-side per-slot, seq-ordered command queue. Dedups duplicate
  `(slot,seq)` and negative seqs, returns a caller-supplied neutral command for an empty slot, tracks
  the last-acknowledged seq per slot. Determinism-neutral (orders/dedups only).

### KhaozEngine.Netcode.LiteNetLib (new)

- New package: LiteNetLib channel-split kernel (refs `LiteNetLib 2.1.2`).
- `IChannelSplittable<TSelf>` + `ChannelSplitter.Send`: split a batch into its unreliable
  (position/transient, latest-wins) and reliable (spawns/destroys/events) parts and send each non-empty
  part on its own channel (Sequenced vs ReliableOrdered) so reliable events never head-of-line-block
  position updates. `NetChannelReliability` + `ChannelSplitter.ToDeliveryMethod` expose the mapping. The
  game keeps its own batch DTO and field layout.

## KhaozEngine 4.6.0

Additive. New package `KhaozEngine.Updates`. No change to existing packages.

### KhaozEngine.Updates (new)

- New package centralizing a game-agnostic **delta auto-update pipeline** (promoted from SpaceGame so
  Hardpoint/Nullwake can reuse it). Determinism-neutral (never touches sim/RNG). Pure .NET
  (+ `KhaozEngine.Diagnostics`), no MonoGame dependency.
- `UpdateManifest` - SHA256 file manifest (`path`/`sha256`/`size`, ordinal-sorted, stable camelCase
  JSON wire format). `GenerateFromDirectory(dir, version, platform)` builds one from an install dir
  (also usable by an offline publish-side manifest generator); `ComputeDiff(local, remote)` returns
  `FilesToDownload` + `FilesToDelete` + `TotalDownloadBytes`.
- `IUpdateSource` - host-agnostic transport. `HttpUpdateSource` is the default (HTTP against a
  configurable `ServerBaseUrl` + `LatestVersionPath` template; files resolved as siblings of the
  manifest - SpaceGame's Azure Blob layout, but a game points it elsewhere via config or implements
  the interface for any backend). `LatestVersionInfo` carries version/build/manifest-url/required.
- `UpdateService` - the check -> download -> apply state machine (`UpdateState`), with resumable
  staging (already-staged files with a matching SHA256 are skipped; corrupt downloads retry up to
  `MaxDownloadRetries`), boot hygiene (stale-staging cleanup, interrupted-apply detection), and
  offline-safe checks (failures fall back to `Idle`). Shim launch and process exit are injectable via
  `UpdateServiceOptions`, so the whole lifecycle is headless-testable. `Platform`/`InstallDir` default
  to the current OS runtime id / `AppContext.BaseDirectory`.
- `UpdateApplier` + `IUpdaterEnvironment` - the cross-platform **staged-apply core** for an external
  updater shim: wait for the game to exit, back up each install file before overwriting, copy with
  retries for locked files, roll every overwrite back on any failure (install never left half-new),
  abort before touching the install if a staged source is missing, delete removed files, install the
  new manifest, clear the macOS quarantine attribute, relaunch. All side effects go through
  `IUpdaterEnvironment` (`SystemUpdaterEnvironment` is the real impl); a game's shim is just
  `UpdateApplier.Run(args, new SystemUpdaterEnvironment(log))`.
- `ApplyUpdateConfig` is the `apply-update.json` handoff contract; it (de)serializes through a
  source-generated `UpdatesJsonContext`, so the shim needs no reflection and stays trim/AOT safe.
- 46 headless tests (manifest diffing, resume skip/retry, download verification, apply / rollback /
  abort).

## KhaozEngine 4.5.0

Additive. Two new packages of game-agnostic 2D primitives, ported verbatim from SpaceGame.

### KhaozEngine.Collision (new package)

- New package: deterministic 2D collision + broadphase primitives. Refs `MonoGame.Framework.DesktopGL`
  for `Vector2`. Float math and iteration order are bit-identical to the SpaceGame originals
  (`CircleCollision`, `EnemySpatialIndex`) so it can be adopted in a lockstep sim without moving the hash.
- `CircleCollision` (static): `Intersects(Vector2, float, Vector2, float)` and `Intersects(ICircleCollider,
  ICircleCollider)` broad overlap (`DistanceSquared <= combined^2`, touching counts), plus three
  `DoCollidersCollide` overloads (collider/collider, bare-circle/collider, collider/bare-circle) that apply
  per-pixel precise refinement when a side implements `IPreciseCircleCollisionTarget`.
- `ICircleCollider` (`Position`, `Radius`) and `IPreciseCircleCollisionTarget` (`IntersectsCircle`).
- `SpatialHashGrid`: uniform spatial hash for broadphase. Generic rebuild via `BeginRebuild(capacity)` +
  `Add(index, position, radius)` per item (replaces the snapshot-coupled `Rebuild`), then
  `QueryCandidates(center, radius)` / `GetQueryIndex(i)` / `SortQueryIndicesAscending(count)`. Cell coord =
  `(int)MathF.Floor(world / cellSize)`, queries walk Y-outer/X-inner, cell chains are LIFO (head insertion).
  Renamed off "Enemy"; stores caller-supplied indices into whatever collection the caller owns.

### KhaozEngine.Pooling (new package)

- New package: `ObjectPool<T>` where `T : class, IPoolable`, a fixed-capacity free-list pool genericized
  from SpaceGame's `XpFlyerPool` (XpFlyer specialization + `Update`/`Draw` dropped). Zero dependencies.
- O(1) `Rent()` (null when exhausted) / `Return(item)` (resets, ignores foreign items), `Clear()`,
  `ActiveCount`/`FreeCount`, and `GetActive(slot)` over a swap-removal-compacted active set. `IPoolable`
  exposes `PoolIndex` (pool-owned) + `Reset()`.

## KhaozEngine 4.4.0

Additive. New package `KhaozEngine.Platform` for native platform interop. No change to existing packages.

### KhaozEngine.Platform (new)

- New package: game-agnostic native platform interop, pure BCL P/Invoke, no MonoGame dependency.
- `Clipboard`: cross-platform system-clipboard facade. `TryGetClipboardText()` / `TrySetClipboardText(string)`
  dispatch SDL2 first, then a macOS `NSPasteboard` fallback, then an optional Android/iOS bridge.
  `TrySetClipboardImagePng(byte[])` covers macOS + mobile; `TrySetClipboardImageRgba32(w, h, rgba)` writes a
  bottom-up `CF_DIB` on Windows. Every call is best-effort and never throws (a missing/failing backend
  yields `""` / `false`).
- `Clipboard.MobileBridgeTypeName`: fully-qualified type name of the consumer's mobile clipboard bridge,
  resolved by reflection across loaded assemblies (static `TryGetClipboardText(out string)` /
  `TrySetClipboardText(string)` / `TrySetClipboardImagePng(byte[])`). Defaults to `null` (mobile fallback
  skipped); reassigning clears the resolution cache. This replaces the hard-coded bridge type name in the
  promoted-from source, so consumers register their own bridge.
- Ported verbatim from SpaceGame's `ClipboardInterop` (the SDL2 / Windows GDI / macOS Objective-C / mobile
  marshaling is unchanged); the dispatch/fallback ordering and the `CF_DIB` packing are extracted into pure
  helpers and covered by headless tests. The native bridges themselves can't run headless.

## KhaozEngine 4.3.1

Bugfix. No API change.

### KhaozEngine.Audio

- `MacOsMusicBackend.TryLoadTrack` now locates the built track file by probing the formats the
  content pipeline actually emits (`.ogg`, `.mp3`, `.m4a`, `.wav`, `.aiff`, `.caf`), preferring
  `.ogg`. It previously looked only for a raw `.mp3` on disk, but the DesktopGL pipeline transcodes
  music to `.ogg` (the `.xnb` is just a header that references it), so every track failed to load and
  no music played. AVAudioPlayer decodes the built `.ogg` directly.
- The native AVAudioPlayer bridge is now created lazily on first playback instead of in the
  constructor, so track loading is headless-testable on non-macOS CI.

## KhaozEngine 4.3.0

Additive. Completes the isometric toolkit's picking + extensibility seams from 4.2.0. No behaviour
change for existing 4.2.0 calls.

### KhaozEngine.Graphics

- `IsometricProjection.ScreenToWorld(screen, z)`: inverts the projection on the horizontal plane at
  height `z` (not just the ground). `ScreenToWorld(screen, 0)` equals `ScreenToGround`. This is the
  building block for picking over varying terrain - a consumer that owns the heightmap tests candidate
  heights front-to-back; the toolkit supplies the per-plane inverse.
- `IIsometricProjection` interface, implemented by `IsometricProjection`. Consumers can depend on the
  seam and substitute a fake/stub projection in headless tests (mirrors `Input.IDesignViewport`).
- `IsoDepth.DepthKey` gains an optional `zWeight` (default 1): scales how strongly height pushes a
  drawable toward the front, so a tall stack can be made to sort in front of a taller-but-nearer
  neighbour, or `zWeight: 0` drops height from ordering. Existing 4-argument calls are unchanged.

## KhaozEngine 4.2.0

Additive. A render-only isometric toolkit in `KhaozEngine.Graphics`, plus an opt-in footprint
anchor on the directional sprite draw path. No gameplay/grid/pathfinding concepts: consumers keep
their own world model and project at draw time. Orthographic consumers are unaffected (the only
signature change is a trailing optional parameter).

### KhaozEngine.Graphics

- `IsometricProjection`: configurable 2:1-style tile footprint (default 64x32) and `heightScale`
  (defaults to tile height). `WorldToScreen(wx, wy, z = 0)` maps world to screen
  (`sx = (wx - wy) * TileWidth/2`, `sy = (wx + wy) * TileHeight/2 - z * HeightScale`);
  `ScreenToGround(screen)` inverts on the ground plane (`z = 0`), returning a continuous world
  point for picking. `z` is a real input now (v1 callers pass 0) - the seam for terrain height.
- `IsoDepth.DepthKey(wx, wy, z = 0, layer = 0)` returns a comparable `IsoDepthKey` for Y-sorting a
  draw list: primary order `wx + wy + z`, integer `layer` as tiebreak. The consumer sorts its own list.
- `PrimitiveRenderer.DrawIsoDiamond` (filled 2:1 tile), `DrawIsoBlock` (top + two shaded side faces
  for a given height), `DrawIsoEllipse` (filled 2:1, for shadows) and `DrawIsoEllipseOutline`
  (stroked 2:1, for range rings). Match the existing pixel-quad rendering style.
- `ColorHelper.Scale(color, factor)`: per-channel RGB multiply (alpha kept), clamped - used for the
  default block face shading.

### KhaozEngine.Sprites

- `SpriteAnchor` enum and a new optional `anchor` parameter on `DirectionalAnimatedSprite.Draw`
  (default `Center`, unchanged). `FootprintBottomCenter` anchors the draw position at the frame's
  bottom-centre so a tall iso sprite stands on its (z-lifted) tile instead of being centred on it.
  An explicit `origin` still overrides the anchor. Facing/`Direction8` logic is unchanged.

## KhaozEngine 4.1.0

Additive. Logging normalization: packages that log now lean on the logger's category (already
rendered by `LogFormatter` as `[Category]`) instead of hand-rolled message prefixes, and fall back
to the ambient `Log` facade when no `ILogger` is injected. Two more packages gain logging where it
earns its keep. No public type removed; on-disk formats unchanged.

### KhaozEngine.Audio

- Log messages drop the redundant `Audio:` prefix across `AudioSystem` and the three backends. The
  category already identifies the source (`AudioSystem`, `MonoGameMusicBackend`, `MacOsMusicBackend`,
  `MacOsMusicPlayer`), so the prefix was doubling up. No behavior change beyond log text.

### KhaozEngine.Persistence

- `SaveEncoder`, `PersistenceQueue`, and `SettingsManager<T>` drop inline `[ClassName]` message
  prefixes and now resolve a logger via `?? Log.For<T>()` (the generic `SettingsManager<T>` uses the
  fixed category `SettingsManager` to avoid a `` `1 `` suffix). They log under their own category
  whether or not a logger is injected.
- `SaveEncoder`'s `logger` constructor argument is now **optional** (`ILogger? logger = null`); a null
  logger no longer throws, it falls back to the ambient facade. Callers passing a logger are
  unaffected.

### KhaozEngine.Content

- `ConfigLoader.Load<T>` now emits a Debug line naming the resolved source (disk path vs embedded
  resource) under category `ConfigLoader` - the usual "which config actually loaded" question. Adds a
  `KhaozEngine.Diagnostics` dependency. `JsonSchemaValidator` keeps its `TextWriter` reporter (it is a
  CLI tool surface, not runtime diagnostics).

### KhaozEngine.Localization

- `LocalizationManager.SetCulture` and `GetSupportedCultures` emit Debug lines (culture set, count of
  discovered cultures) under category `LocalizationManager`. Adds a `KhaozEngine.Diagnostics`
  dependency (still pure BCL, Diagnostics has no MonoGame dep).

Pure-compute packages (Ecs, Time, Sprites, UI, Graphics, Input, Serialization, Effects, App, Screens)
intentionally stay logless: no IO and no swallowed exceptions, so logging would be noise.

## KhaozEngine 4.0.0

Breaking. Inter-package tidy-up: a rendering primitive moves to the rendering package, and JSON
defaults are centralized in a new package. No runtime behavior change, but two namespaces moved and
`KhaozEngine.Effects` swaps a dependency, so consumers need `using` and possibly `<PackageReference>`
updates.

### KhaozEngine.Graphics

- `PrimitiveRenderer` and `ColorHelper` moved here from `KhaozEngine.UI` (namespace
  `KhaozEngine.UI` -> `KhaozEngine.Graphics`). They are low-level rendering helpers (1x1 pixel
  shapes, hex color parsing) with no UI concepts, so they belong in the rendering package that
  already sits below UI. **Migration:** add `using KhaozEngine.Graphics;` where you used
  `PrimitiveRenderer`/`ColorHelper`. `KhaozEngine.UI` consumers need no new package reference (UI
  already depends on Graphics); the types are just in a different namespace now.

### KhaozEngine.Effects

- Now depends on `KhaozEngine.Graphics` instead of `KhaozEngine.UI`. Its only use of UI was
  `PrimitiveRenderer`, which now lives in Graphics, so the package no longer drags in the whole UI
  widget set. **Migration:** if you reference `KhaozEngine.Effects` directly, no change; the
  transitive dependency just shifts from UI to Graphics.

### KhaozEngine.Serialization (new package)

- New leaf package holding `JsonDefaults`: shared `System.Text.Json` option baselines so config,
  persistence, and ECS serialize the same way. `TolerantRead` (case-insensitive, `//` comments,
  trailing commas), `IndentedWrite` (`WriteIndented`), and `IncludeFields` (round-trips public
  fields). Each is a single shared, effectively read-only instance. Pure BCL, no MonoGame.
- `KhaozEngine.Content` (`ConfigLoader`), `KhaozEngine.Persistence` (`AtomicJsonWriter`,
  `PersistenceQueue`, `FileSettingsStorage`), and `KhaozEngine.Ecs` (`WorldSerializer`) now consume
  `JsonDefaults` instead of each declaring their own options. Public APIs and on-disk format are
  unchanged; these packages gain a `KhaozEngine.Serialization` dependency.

## KhaozEngine 3.12.0

Additive. New keyed registry for directional sprites in `KhaozEngine.Sprites`.

### KhaozEngine.Sprites

- New `SpriteRegistry` - a keyed store of `DirectionalAnimatedSprite` with one bulk
  `Update(float deltaSeconds)` that advances every registered sprite's animation clock once per
  frame. `Add(key, sprite)` (non-empty key, no duplicates, non-null sprite), `Get(key)` returning
  the sprite or null, `Contains(key)`, and `Count`. Takes already-built sprites - loading by
  embedded-resource manifest name stays game-side, since resource names are game-specific.
  Centralizes the `Dictionary<string, DirectionalAnimatedSprite>` + per-frame bulk-advance that
  Hardpoint hand-rolls in `SpriteLibrary`.

## KhaozEngine 3.11.0

Additive seam so consumers stop wrapping `VirtualResolution` just to make screens headless-testable.

### KhaozEngine.Input

- New `IDesignViewport` interface: `int Width`, `int Height`, `float Scale`, `Matrix ScaleMatrix`.
  `VirtualResolution` now implements it (its existing properties already satisfy the contract - no
  behavior change). Screens that need only design-space size/scale/matrix can take an `IDesignViewport`
  and tests can hand them a fixed-size fake instead of standing up a `VirtualResolution`. Hardpoint's
  game-side `IViewport` + `VirtualResolutionViewport` adapter exist purely for this; they can drop the
  adapter and reference the engine interface directly.

## KhaozEngine 3.10.0

Shared camera-gesture core: `PannableCanvas` and `CameraController` now drive a `Camera2D` and share
one implementation of pan / zoom / pinch / clamp / tap. Additive API plus one scoped behavior change.

### KhaozEngine.Graphics

- `Camera2D.GetViewMatrix` now honors the viewport's X/Y offset (centers `Position` on
  `(viewport.X + W/2, viewport.Y + H/2)`). **Behavior change**, but only for a viewport with a non-zero
  X/Y origin (an inset sub-rectangle) - the previously unsupported/incorrect case. Whole-screen
  viewports (X = Y = 0, every prior call site) are unchanged. Makes inset viewports map correctly.
- New `Camera2D.PanByScreenDelta(screenDelta)` - grab-and-drag pan (`Position -= screenDelta / Zoom`).
- New `Camera2D.ZoomAboutScreenPoint(target, focusScreen, viewport, min, max)` - clamped zoom that keeps
  the world point under the focus fixed.
- New `PinchGestureTracker` - the shared two-finger pinch state machine (midpoint pan + zoom-about-focus).
- New `CameraGestures.TryGetTap(input, camera, viewport, out press, out release)` - the shared
  press-origin tap-vs-pan helper.
- `CameraController` now drives `Camera2D` through these shared pieces. No public API or behavior change.

### KhaozEngine.UI

- `PannableCanvas` delegates its transform / clamp / pan / tap math to a backing `Camera2D` (shared with
  `CameraController`). `CameraOffset` is preserved as the legacy additive view (`-Position * Zoom`).
  Drag pan, wheel-as-vertical-pan, scissor `Draw`, `BlockInput`, `Padding`, `ScrollPanSpeed`, and the
  press-origin tap invariant are byte-identical.
- New: real two-finger **pinch zoom** (the old `_zoom = 1f` seam is now live). New `MinZoom` / `MaxZoom`
  (defaults 0.1 / 10), `EnablePan` / `EnableZoom` (default true), and a `Camera` accessor. Wheel stays a
  vertical pan. Mouse-only behavior is unchanged. Disable pinch with `EnableZoom = false`; `EnablePan =
  false` disables all panning (drag, two-finger, and wheel).
- `Focus(rect)` now **fits zoom to the rect** (delegates to `Camera2D.Focus`, clamped to `MinZoom`/
  `MaxZoom`), fulfilling its long-standing "becomes fit-to-rect once zoom exists" intent - it previously
  only centered. Optional `paddingFraction` parameter. Use `CenterOn`/`CenterContent` for a center-only move.
- `KhaozEngine.UI` now references `KhaozEngine.Graphics` (transitive package dependency added).

## KhaozEngine 3.9.0

Camera framing + follow, both in `KhaozEngine.Graphics`. Additive, no breaking changes.

### Camera2D framing helpers: CenterOn + Focus (fit-to-rect zoom)

`Camera2D` gains the framing math that consumers were hand-rolling (Hardpoint's `BoardFraming`,
SpaceForge's grid framing, `PannableCanvas`'s long-dormant `Focus(rect)` zoom seam):

- `CenterOn(Vector2 world)` - sets `Position` so the world point is at the viewport center (an explicit
  alias for API parity).
- `Focus(Rectangle worldRect, Viewport viewport, float paddingFraction = 0f, float minZoom, float maxZoom)`
  - fit-to-rect: sets `Zoom` so the rect (optionally inflated by `paddingFraction` on each side) is fully
  visible (contain fit, `min(viewport.Width / rectW, viewport.Height / rectH)`), clamped to
  `minZoom`/`maxZoom`, then centers `Position` on the rect. Pure and headless. Does not clamp to world
  bounds - call `ClampPosition` after if the rect is a sub-region. A no-arg-viewport overload uses the
  stored `Viewport` property.

Because these live on `Camera2D`, both `CameraController` and (once consolidated) `PannableCanvas`
inherit them.

### CameraFollow (target-follow with smoothing + deadzone)

New `CameraFollow` drives a `Camera2D` to follow a moving target. The game decides what to follow; this
owns only the smoothing/deadzone/clamp. Kept separate from the gesture `CameraController` - a screen
typically uses one or the other.

- `Update(Vector2 target, float dt, Viewport viewport, Rectangle worldBounds)` - eases toward the target,
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

- **`Direction8`** - the 8 facings `S, SE, E, NE, N, NW, W, SW`, ordered so the enum value is the
  direction's row index in a PixelLab grid sheet. `Direction8Extensions.FromVector(facing, fallback)`
  maps a movement/aim vector to the nearest of 8 in y-down screen space (+X east, +Y south); magnitude
  is irrelevant, a 22.5-degree seam rounds to the higher (clockwise) direction, and a zero vector
  returns `fallback`. `ToVector()` returns the unit facing.
- **`SpriteSheetLayout`** - pure grid math (no `Texture2D`, headless): `FromFrameSize` / `FromGrid`,
  then `GetFrame(row, column)` -> source `Rectangle`. **`SpriteSheet`** pairs it with a texture.
- **`SpriteFrame`** - a `(Texture2D, Rectangle)` drawable frame; frames carry their own texture so an
  animation can span one packed sheet or a set of loose per-frame textures.
- **`SpriteAnimation`** - ordered frames + per-frame duration + loop flag (`FromFps` or seconds ctor).
  **`SpriteAnimationPlayer`** advances it by a `float` seconds delta or a `GameTime`, yields the current
  frame, loops, flags `IsFinished` for one-shots, and `Play(anim, preservePhase)` swaps animations. A
  small relative tolerance on the frame boundary keeps exact-multiple deltas from dropping a frame to
  float noise.
- **`DirectionalAnimatedSprite`** - one animation per `Direction8`, plays the one matching the current
  facing, draws via `SpriteBatch` with a centered origin by default; switching facing preserves the
  animation phase so a walk cycle stays smooth. `Update(facing, gameTime)` does both in one call.
- **`PixelLabSpriteLoader`** - builds a `DirectionalAnimatedSprite` from a PixelLab export, either an
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
  the cursor / pinch midpoint - the focal world point stays under the pointer. `WheelZoomStep` is the
  multiplicative factor per 120-unit notch (fractional/multi-notch deltas scale smoothly via a power).
- **Bounds clamp**: after pan/zoom, clamps via `Camera2D.ClampPosition(Position, worldBounds, viewport)`
  so the view stays inside a caller-supplied world rectangle (auto-centers when the world is smaller).
- **Tap vs pan**: `TryGetTap(out pressWorld, out releaseWorld)` mirrors `PannableCanvas.TryGetTap` and
  honors the press-origin invariant - gameplay places a tower on a tap but treats a drag as a pan
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
- **Fill policy**: fill-the-width, adaptive-height (the same as mobile) - no letterbox bars and no
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
  instance changes (`ReferenceEquals` check) - for consumers that recreate the `World` on
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
- **KhaozEngine.Graphics** (new; MonoGame): `Camera2D` - a generic 2D matrix camera
  (position/zoom/rotation → view matrix), headless `WorldToScreen`/`ScreenToWorld` (explicit `Viewport`,
  no `GraphicsDevice`), turn-key no-arg overloads via a settable `Viewport`, and a pure
  `ClampPosition` world-bounds helper. The base for a future follow/deadzone/parallax camera layer.
- **KhaozEngine.Persistence** additions: `AtomicJsonWriter` (crash-safe temp-then-move writes),
  `PersistenceQueue` (`IPersistenceQueue`; per-path coalescing async writer, never throws into the
  game, retry + `WriteFailed` event, blocking `Flush()` + flush-on-dispose), and
  `SettingsManager<T>` / `ISettingsStorage` / `FileSettingsStorage` (typed settings persisted via the
  queue, default paths through `KhaozEngine.App.AppDataPaths`). Persistence now also references `KhaozEngine.App`.
- **KhaozEngine.Ecs** addition: `DeterministicRng.CreateDerived(string systemName)` - named, stable,
  reproducible substreams (mixes the parent seed with a fixed string hash; not `string.GetHashCode`).
  Note: derived streams do not byte-match `System.Random`, so any consumer migrating to it must re-baseline golden values.

## KhaozEngine 3.2.0

Batch 1 of the "promote duplicated game code into KhaozEngine" effort. Three new pure-.NET packages
(plus a small consolidation of the `AppDataPaths` that 3.1.0 had shipped). No consumer adopts these yet.

- **KhaozEngine.App** (new, pure .NET): app/runtime helpers.
  - `BuildMetadata.Read(string key, string fallback, params Assembly?[] assemblies)` - reads
    `AssemblyMetadataAttribute` values at runtime, probing the supplied assemblies in order (null
    entries skipped), so a game can surface its own version/build identity without re-deriving it.
  - `AppDataPaths` - instance resolver for the OS-correct per-app data directory (Windows `%APPDATA%`,
    macOS `~/Library/Application Support`, Linux `$XDG_DATA_HOME`/`~/.local/share`, with fallbacks).
    `BaseDirectory` is resolved + created once and cached (thread-safe via `Lazy<T>`); convenience
    `SaveFilePath`/`SettingsFilePath`/`LogFilePath`/`PreviousLogFilePath`/`GetFilePath`. OS resolution
    sits behind an internal seam for headless testing.
  - `ServiceLocator : IServiceProvider` - generic register/resolve-by-type service registry backed by a
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
  logging is path-agnostic - pass resolved paths into `FileSinkOptions`). Removing a 3.1.0 public type
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
- `InputManager.Touches` - active touches in virtual coordinates with stable ids (`TouchPoint.Id`).
- `InputManager.TryGetPinch(out Pinch)` - virtual midpoint, distance, per-frame delta, scale ratio.
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

- **KhaozEngine.Input** - unified pointer (mouse+touch), `IsTapIn` press-origin invariant
  (click-through fix), region blocking, drag/scroll/pinch, keyboard + gamepad + menu-navigation,
  coordinate-transform seam (`Identity` / `Matrix` / `VirtualResolution`), all behind the testable
  `IRawInput` seam.
- **KhaozEngine.Screens** - screen stack with top-to-bottom routing, `ConsumeWhenVisible` /
  `ConsumeWhenHandled` policies, and transitions.
- **KhaozEngine.UI** - widget library, `PrimitiveRenderer`, `TextInputHandler`.
- **KhaozEngine.Ecs** - minimal `World` / `Entity` / `ISystem`.

30 headless tests. Hardpoint migrated onto it.
