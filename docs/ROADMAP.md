# KhaozEngine roadmap / backlog

Larger feature areas identified but not yet scheduled. Current released version: **7.53.2** (the shared
`<KhaozEngine5xVersion>` line, which is the engine: the custom MonoGame-free stack plus the graduated foundation packages). The legacy 4.x
line and its six genuinely-MonoGame packages (`Graphics`/`Input`/`Screens`/`Sprites`/`Time`/`UI`) were
DELETED from the engine source, so the engine itself is now entirely MonoGame-free. All three consumers are
off MonoGame too and pin the 7.x line on their own schedule: Hardpoint (`Game3D` + `Foundation`), Nullwake
(`Game2D`), and SpaceGame (`Game2D` + the split-out `SpaceGame.Sim`) - exact pins in CONSUMERS.md. The
breaking 6.0.0 `Primitives.Color` migration has been adopted
everywhere, so the post-MonoGame pivot is fully complete on both the engine and consumer sides. As of 6.0.0
there is also a new zero-dependency `KhaozEngine.Primitives` leaf at the bottom of the graph (`Color`,
`DeterministicRng`, `XorRng`, `MathUtil`, `ViewportMath`, `Easing`).

Several items from the original (3.3.0-era) backlog have since shipped: the camera follow/framing
layer, the pan/zoom gesture controller, and `PrimitiveRenderer` circle/ring drawing. Those are listed
under each area as **Shipped** so the remaining work is clear. (The primitive/camera code that the 4.0.0
release once put in `KhaozEngine.Graphics` now lives in `KhaozEngine.Render2D` / `KhaozEngine.Effects` on the
MonoGame-free stack; the 4.x `Graphics`/`UI` packages were deleted. Roadmap references below use the current
package names.)

## MMO / authoritative-multiplayer netcode stack (program)

**Decision (2026-06-25):** build the server + netcode track toward an authoritative, persistent-world,
MMO-scale multiplayer game. Authoritative client-server (server simulates, clients predict + interpolate),
not lockstep. Full architecture + the engine-vs-infra boundary + the phased decomposition are in
`docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md`. The render-scale track (frustum culling,
world streaming, LOD, tilemap) is a separate plan, not part of this program.

Phases (each sub-project gets its own spec + plan): **0** transport seam + fixed-tick host · **1** entity
replication + session lifecycle · **2** interest management + world-store seam · **3** zoning/sharding +
dedicated-server template.

- **Phase 0 shipped (`7.35.0`).** `KhaozEngine.Netcode` gained the `INetTransport` byte-transport seam
  (`NetConnectionId`/`NetEvent`) + a deterministic in-memory `LoopbackTransport`; `KhaozEngine.Netcode.LiteNetLib`
  gained `LiteNetLibServerTransport`/`LiteNetLibClientTransport` (UDP); and the new `KhaozEngine.Simulation`
  package added `FixedTickHost` (the headless fixed-timestep accumulator). Plan:
  `docs/superpowers/plans/2026-06-25-mmo-phase0-transport-and-tick-host.md`.
- **Phases 1 + 2 shipped (`7.36.0`).** 1D session lifecycle (`NetServer`/`NetClient`, handshake,
  `IConnectionAuthenticator`, `SlotAllocator`, session events) in `KhaozEngine.Netcode`. 1C entity replication
  (new `KhaozEngine.Replication`): `NetId`, `ReplicationRegistry`, `SnapshotWriter`, `ClientReplicationView`
  (full-state + baseline/delta + interpolation), `ServerReplicator`. 2E interest management (`InterestGrid` +
  `SnapshotWriter.WriteFiltered`). 2F world store (new `KhaozEngine.WorldStore`): `IWorldStore` async seam +
  `InMemoryWorldStore`. Plans: `docs/superpowers/plans/2026-06-25-mmo-phase1d-session-lifecycle.md`,
  `...-phase1c-entity-replication.md`.
- **Phase 3 shipped (`7.38.0`).** Seamless sharded world grid (`KhaozEngine.Sharding`): cell grid, cross-cell
  ghosting, exactly-once authority handoff, per-client home-cell serving, `MmoServerSample` reference server.
- **ECS job scheduling shipped** as its own program (`docs/superpowers/specs/2026-06-26-ecs-parallel-job-system-design.md`):
  worker-pool seam + parallel cell ticks (`7.41.0`), parallel `ForEach` + read/write access declarations (`7.42.0`);
  the conditional system scheduler was benchmark-gated and de-scoped.
- **Open refinements:** delta+AoI unification, delta bit-packing/quantization, a SQLite `IWorldStore`. SpaceGame is the
  intended first adopter / testbed.

## Overworld / render-scale track (program)

The client-side world track the netcode program deferred: terrain, world streaming, LOD, prop scatter. Build
spine is **terrain -> character + camera -> streaming -> props**, with an asset-normalize pipeline underneath.
Reference pattern: `github.com/levy-street/world-of-claudecraft` (engine-agnostic pure-math world gen).

- **Terrain shipped (`7.43.0`).** The first sub-project: `KhaozEngine.Terrain` (render-free analytic
  `TerrainField` - deterministic stateless coordinate-hash height field with biome bands + Lake/Ridge/Flatten
  features + `TerrainCollision`) and `KhaozEngine.Terrain.Render3D` (chunked-LOD mesh builder with skirts,
  per-vertex splat weights, height/slope vertex-colour ramp, chunk AABB). Analytic field (no baked heightmaps),
  authoritative-server/visual-client (plain `float`). Spec:
  `docs/superpowers/specs/2026-06-27-terrain-system-design.md`.
- **Walkable slice shipped (`7.44.0`, camera tuned `7.45.0`).** The second sub-project: `FollowCamera3D` +
  `FollowCameraController` (`KhaozEngine.Render3D`, a third-person orbit camera) and `CharacterController3D`
  (`KhaozEngine.Game.Render3D`, terrain-agnostic WASD ground-clamped locomotion), plus `TerrainWalkSample`.
  Spec: `docs/superpowers/specs/2026-06-27-walkable-slice-design.md`.
- **Prop scatter + asset pipeline shipped (`7.46.0`).** The fourth sub-project (asset/render foundation folded
  in): `AssetManifest` + `PropLoader` (`KhaozEngine.Render3D`, kit manifest parse + scale-normalize to
  `heightMeters` + the 1.8 m validation guard), `PropScatter` (`KhaozEngine.Terrain`, deterministic
  coordinate-hash placement, streaming-ready), and `PropRenderer`/`Scene3D.DrawProps`
  (`KhaozEngine.Terrain.Render3D`, instanced + distance-culled). `TerrainWalkSample` forests the clearing with a
  committed CC0 Quaternius kit. Spec: `docs/superpowers/specs/2026-06-27-prop-scatter-design.md`.
- **Networked overworld shipped (`7.47.0`).** The fifth sub-project (the client and the authoritative server
  meet): `KhaozEngine.Locomotion` (render-free `CharacterMovement.Step` + `MoveTuning`, one movement core the
  local `CharacterController3D` now wraps) and `KhaozEngine.NetWorld` (`PlayerMoveSimulator`, `WorldServer` -
  single-`World` authoritative server over `NetServer`/`Replication`/`InterestGrid`, and `WorldClient` -
  prediction + reconciliation, `EntityRenderState[]`). Demos `NetworkedWalkServer` + `NetworkedWalkSample` let
  two clients walk the same terrain on localhost (props stay client-side deterministic scatter, not replicated).
  Single `World` at this stage (multi-cell sharding shipped later as 6b, `7.50.0`). Spec:
  `docs/superpowers/specs/2026-06-27-networked-overworld-design.md`.
- **Client world streaming shipped (`7.48.0`).** Sub-project 6a (walk an endless world): `TerrainStreamer` +
  `Scene3DChunkSink` (`KhaozEngine.Terrain.Render3D`) keep a ring of terrain chunks (+ their deterministic props)
  loaded around the player - load inside `LoadRadius`, unload past `UnloadRadius` (hysteresis), re-LOD on a
  `TerrainLod.PickLod` tier crossing, amortized to `MaxLoadsPerFrame` per update (no hitch). `ChunkCoord`/
  `ChunkGrid` map coord<->world; `IChunkSink` is the GPU-free-testable seam. `TerrainWalkSample` now walks
  forever; terrain/props are per-area deterministic so it composes with the networked client unchanged. Server
  stays single-`World`. Spec: `docs/superpowers/specs/2026-06-27-world-streaming-design.md`.
- **Persistent world store shipped (`7.49.0`).** The authoritative world now survives a server restart: two opt-in
  `IWorldStore` backends - `KhaozEngine.WorldStore.Sqlite` (`SqliteWorldStore`, dev/test + single-node, always-tested)
  and `KhaozEngine.WorldStore.SqlServer` (`SqlServerWorldStore`, prod = Azure SQL) - plus `KhaozEngine.NetWorld`'s
  `WorldPersistence` (load-on-join / save-on-leave / periodic dirty snapshot, keys `player:{accountId}`, backend-agnostic).
  Per-cell snapshot persistence pairs with 6b. Spec: `docs/superpowers/specs/2026-06-27-persistent-worldstore-design.md`.
- **Multi-cell server sharding shipped (`7.50.0`).** Sub-project 6b (finishes "6" with 6a streaming): the
  authoritative overworld now runs across a grid of cells. `KhaozEngine.NetWorld.ShardedWorldServer` (+
  `ShardedWorldServerConfig`) wires the existing movement stack onto a `KhaozEngine.Sharding` `ShardHost` -
  per-cell `PlayerMovementSystem` stepped via `ShardHost.Tick` (scheduler-fanned, deterministic), exactly-once
  authority handoff on boundary crossings (`ProcessHandoffs`, `NetId` stable), border ghosting (`SyncGhosts`),
  and a single home-cell area-of-interest snapshot per client framed identically - so the unchanged `WorldClient`
  walks across cell boundaries seamlessly and sees neighbour players. `WorldPersistence` is reused via the new
  `IWorldPersistenceHost`, player-keyed across cells. `NetWorld` now references `Sharding`. Spec:
  `docs/superpowers/specs/2026-06-27-multicell-sharding-design.md`.
- **Next sub-projects:** a procedural dungeon generator, animated characters/creatures (needs a glTF
  animation-clip-playback feature), and per-cell world-state snapshot persistence (pairs with sharding). PBR splat
  textures and a water shader are terrain-material upgrades.

## The post-MonoGame pivot (6.x line): strategic direction

**Decision (2026-06-15): KhaozEngine becomes a full, self-contained, cross-platform game framework with
no MonoGame dependency anywhere** — desktop (Win/Mac/Linux) first, mobile (iOS/Android) later, covering
2D / iso-lite games as well as 3D. The 4.x MonoGame-based packages keep shipping in parallel and are
ported over package-by-package; nothing breaks for current consumers mid-transition.

### Why

The trigger was a real-time 3D proof-of-concept. Two findings settled the direction (spikes on Apple
Silicon, net10.0):

- **MonoGame's shader pipeline is a dead end on Apple.** A MonoGame DesktopGL context here reports
  `GL_VERSION "2.1 Metal - 90.5"` / `GLSL 1.20` (Apple's deprecated GL-on-Metal layer); `#version 130+`
  shaders fail. Custom shaders via MGFX also require the Windows HLSL compiler under Wine, which is
  deprecated/fragile on Apple Silicon. Modern + custom-shaders + iOS is not reachable through MonoGame.
- **A custom renderer works cleanly.** A headless spike created a `Veldrid` **Metal** device, cross-compiled
  a GLSL `#version 450` shader through SPIR-V to MSL, and rendered — no Wine, no offline shader step,
  compiled natively on the Mac.

### Foundation

.NET (not C++ — the renderer's heavy lifting is on the GPU, modern .NET handles the CPU side, iOS ships
via AOT, and ~21 existing C# packages + 3 games would be thrown away by a rewrite). Layers:

- **GPU:** `Veldrid` behind a KhaozEngine-owned seam (native **Metal** on Mac/iOS, **Vulkan** on Android,
  **D3D11/Vulkan** on Windows). Shaders authored once in GLSL -> SPIR-V -> MSL/HLSL/GLSL at load
  (`Veldrid.SPIRV`). Veldrid is low-maintenance upstream; the seam lets a `Silk.NET`-Vulkan backend
  replace it later without touching game/scene code.
- **Math:** `System.Numerics` (`Vector3`/`Matrix4x4`/`Quaternion`).
- **Windowing/input:** SDL2 / `Silk.NET` feeding the existing `IRawInput` seam.
- **Audio:** `OpenAL` via `Silk.NET.OpenAL` (or a small custom backend) replaces MonoGame audio.
- **Texture/content:** `StbImageSharp` etc.

### Current status (snapshot captured at 6.3.0)

_(Historical snapshot below; current state is 7.34.0 - see CONSUMERS.md / CHANGELOG.md. The table captured
the moment every subsystem was first proven on the custom stack; the version markers in it are left as a
record of when each landed.)_

**Every subsystem needed to drop MonoGame is now proven on the custom stack — no feasibility unknowns
remain; the rest is productization + porting.** Shipped engine packages (shared version, now at 7.34.0):

| Subsystem | Status |
|---|---|
| 3D rendering | shipped — `KhaozEngine.Render3D` (IsoCamera3D, runtime glTF lit/cel, PixelPostProcess, starfield) |
| 2D rendering | shipped — `KhaozEngine.Render2D` (SpriteBatch, Camera2D, Texture2D + PNG) |
| Text | shipped — `SpriteFont` (runtime TTF via stb_truetype) in Render2D |
| Audio | shipped — `KhaozEngine.Audio` graduated to OpenAL streaming (WAV/OGG/MP3) |
| Windowing + input | shipped — `KhaozEngine.Windowing` (`AppWindow` + engine-native keyboard/mouse `InputState` + bounds-aware `Pointer` with the click-through invariant; Render2D draws via `Render2DSurface`, the standalone `Render2DHost` was folded away). Gamepad/touch/pinch + virtual-resolution transforms are follow-ups |
| Screens / UI | shipped (widget set) — `KhaozEngine.Gui`: `ScreenStack` routing + transitions, `Screen` base, and the widgets `Button`/`Label`/`Panel`/`Slider`/`Toggle` + `Dropdown`/`TextInput`/`Tooltip`/`PopupPanel`/`ScrollablePanel` over `Pointer`; `TextLayout` wrap/align + `SpriteBatch` scissor clip in Render2D; `TextEntry` key→char. Pause/timescale, touch/gamepad, and virtual-resolution transforms are the remaining follow-ups |
| Math | done — `System.Numerics` |

Caveats from that snapshot, now resolved: per-backend clip-Y is handled (6.2.0 `GpuClip` derives the clip-Y
sign from `GpuCapabilities`; identity on Metal / D3D11, flipped on Vulkan) and validated by the
cross-platform-gpu CI matrix (Metal + D3D11 + Vulkan all blocking, lavapipe software Vulkan on Linux); the SDL2
per-sample bundling is gone (5.33.0 moved windowing/input to Silk.NET / GLFW, which bundles natives per-RID);
and production audio bundles **openal-soft** (5.12.0) instead of the deprecated macOS system OpenAL.

**End-to-end proven:** the `MiniGame` sample ("Catcher" — title menu, falling-block gameplay with score/lives
HUD, modal game-over, looping generated music) runs the whole 5.x stack (Windowing + Render2D + Gui + Audio)
as a real game loop, no MonoGame. The foundation is complete for a 2D game.

**Next milestones — engine maturity before any game migration.** The widget set is ported (core in
`5.7.0-experimental`, heavy + `SpriteBatch` scissor clip + `TextEntry` in `5.8.0-experimental`; cross-texture
painter's order fixed in `5.8.1-experimental`). Rather than migrate a game onto an immature stack, build the
engine to a real, resolution-independent, layout-capable state first. Agreed order, one at a time:

1. **Resolution independence + layout (shipped, `5.9.0-experimental`).** `DesignViewport`/`IDesignViewport`
   (Fit/Fill/Stretch, letterbox + centering, screen<->design mapping, `GetClipProjection`); design-space
   `Pointer`/`ScreenStack` hit-testing; `SpriteBatch.Begin(IDesignViewport)`; `Layout.Resolve` anchoring
   (`TopLeft`..`BottomRight`/`Center`/`Stretch`); `Screen.BackgroundColor`. `GuiSample` scales/centers/
   letterboxes on resize with aligned hit-testing. All headless-tested.
2. **Input breadth (shipped).** Part 1 (`5.10.0-experimental`): `GameClock` (pause/time-scale over `float`
   dt), `GestureRecognizer` (tap/long-press/drag), `PinchRecognizer` (two-point scale+pan). Part 2
   (`5.11.0-experimental`): `GamepadState`/`GamepadButton` + radial `Deadzone`, `TouchPoint`/`TouchPhase`, and
   non-breaking `InputState.Gamepads`/`Touches` (+ `Gamepad(i)`/`PrimaryGamepad`), with best-effort SDL2
   gamepad polling in `AppWindow`. All in `KhaozEngine.Windowing`, headless-tested, demoed in
   `WindowingSample`. Open: a *live* gamepad smoke needs a physical controller (polling is best-effort +
   compile-verified) and *touch* is mobile (the model + mapping are tested, live deferred).
3. **Native packaging / distribution (current).** Part 1 shipped (`5.12.0-experimental`): openal-soft bundled
   via `Silk.NET.OpenAL.Soft.Native` + `GetApi(true)`, so audio uses the shipped `libopenal` instead of the
   deprecated macOS system OpenAL.framework (verified on osx-arm64; native covers all 8 RIDs). Part 2 shipped
   (`5.33.0`): the SDL2 dependency is gone. `AppWindow`'s window/input platform was swapped from `Veldrid.Sdl2`
   (unmaintained, ships only osx-x64 natives) to **Silk.NET.Windowing + Silk.NET.Input** (GLFW), which bundles its
   natives per-RID across desktop, so a clean checkout / shipped game no longer needs `brew install sdl2`. The GPU
   stays Veldrid behind the `KhaozEngine.Gpu` seam; the swapchain is built from the native window handle via the
   new `GpuDeviceContext.CreateForWindow`. The per-sample `CopySdl2` targets were removed. Silk.NET is also the
   windowing/input foundation a future mobile (Android/iOS) project will build on.
4. **Cross-platform backends (mostly landed; Vulkan/GL remain).** Backend selection (Vulkan / D3D11 / GL /
   Metal) is built: `GpuBackendSelector` does an OS probe + `KE_GRAPHICS_BACKEND` override, `GpuDeviceContext`
   creates the device, and `GpuCapabilities` derives clip-Y / depth-range from the live device at runtime
   (wired into `Camera2D` + `ModelRenderer`, replacing the hard-wired Metal assumptions). The
   `cross-platform-gpu.yml` CI matrix runs the golden-snapshot tests per backend: **Metal + D3D11 (WARP) +
   Vulkan (Linux/lavapipe) are all green and blocking**, with committed per-backend goldens. **Still open:** the
   `gl` override parses but is unverified — it waits for real GPU CI / hardware. See `docs/CROSS-PLATFORM.md`.

Then migrate a real game (Hardpoint/Nullwake/SpaceGame) onto a stack that's actually ready. Richer text entry
(IME/locale/dead-keys) stays a later nicety — the current `TextEntry` is US-layout key-mapping.

**Game migration (started): Hardpoint -> 3D isometric on the 5.x stack.** Hardpoint is being rebuilt as a
full-3D iso game (full in-place port off MonoGame), starting with a thin vertical slice (3D board -> tile
pick -> place a tower -> an enemy walks the flow field -> fire). That slice needs generic 3D-engine support
first; **Phase A shipped in `5.13.0-experimental`**: multi-instance `Scene3D` (`LoadMesh`/`Begin`/`Draw`),
`IsoCamera3D` `ScreenToGround`/`ScreenToRay` picking, and `Render3DSurface` composing a 3D scene into an
`AppWindow` under a Render2D HUD. Phase B (the Hardpoint slice) consumes it, in the Hardpoint repo.

### Phased plan

1. **Render3D POC (shipped, `5.1.0-experimental`).** `KhaozEngine.Render3D`: `IsoCamera3D`, runtime glTF
   lit/cel draw, the `PixelPostProcess` chain (palette/dither/edge/upscale + procedural starfield),
   `Scene3D`/`Render3DHost` consumer API, standalone sample. Default look is smooth stylized space; retro is a
   toggle. Proves the renderer + the look. (Was Metal-only at the time; D3D11 + Vulkan backends since landed,
   with per-backend clip-Y derived from `GpuCapabilities` at 6.2.0.)
2. **Rendering core.** Harden into a reusable core + the multi-backend `IGraphicsBackend` seam; per-backend
   clip-space-Y and MRT-clear handling; window/input platform package (lift windowing out of `Render3DHost`).
3. **2D-on-Veldrid (started, `KhaozEngine.Render2D`).** Sprite batcher + textured quads + `Camera2D` +
   runtime TTF text (stb_truetype glyph atlas) — shipped and proven (the text-rendering risk is cleared).
   Still to do: the iso toolkit rebuilt on the custom renderer, sprite sorting/layers, text layout (wrapping).
   Confirms 2D / iso-lite games are covered (2D is strictly simpler than the 3D already proven).
4. **Port the 2D stack (started).** `KhaozEngine.Audio` graduated to the 5.x line with a cross-platform
   OpenAL streaming backend (WAV/OGG/MP3) - the first existing package ported off MonoGame, and the last
   *unproven* subsystem now proven. Still to do: move Input/Screens/UI/Sprites/Effects + content load off
   MonoGame onto the custom foundation.
5. **Migrate the games (done).** All three are off MonoGame and pin the 7.x line (Hardpoint, Nullwake,
   SpaceGame; exact pins in CONSUMERS.md), including the breaking 6.0.0 `Primitives.Color` adoption. The 4.x
   MonoGame line was retired (packages
   deleted, feed pruned to the 6.x floor).
6. **Mobile.** iOS/Android platform layers (lifecycle, touch, packaging, stores).

### Version policy (one shared line)

- **One shared version line (the whole MonoGame-free engine).** The `Primitives` leaf, the custom-stack
  packages (`Gpu`, `Windowing`, `Render2D`, `Render3D`, `Gui`, `Audio`, `Particles`, `Effects`, `Game`,
  `Game.Render3D`), the graduated MonoGame-free foundation packages
  (`Ecs`/`Serialization`/`Content`/`Diagnostics`/`App`/`Localization`/`Persistence`/`Pooling`/`Platform`/
  `Updates`/`Collision`/`Netcode`/`Netcode.Abstractions`/`Netcode.LiteNetLib`), and the four umbrella
  metapackages all share the `Directory.Build.props` `<KhaozEngine5xVersion>` prop (still so named, but it now
  governs the 6.x engine) and release together under one `vX.Y.Z` tag. The `-experimental` suffix was dropped
  at `5.31.0`; the foundation graduated onto the shared line at `5.46.0`; the line crossed to 6.0.0 with the
  `Primitives` leaf + the `Color` migration. The **doc-version guard checks this line**. (The first two
  Render3D releases, 5.0.0/5.1.0, predate the shared line and were per-package.)
- **The old 4.x MonoGame line was DELETED from the repo.** Its six genuinely-MonoGame packages
  (`Graphics`/`Input`/`Screens`/`Sprites`/`Time`/`UI`) are gone from `Directory.Build.props` and the source
  tree. SpaceGame has since ported, so the legacy `4.9.0` nupkgs were pruned from `local-feed` (now floored at
  the 6.x line); they remain recoverable from GitHub Packages. MonoGame is fully gone from the engine and all
  three consumers.

Design spec: `docs/superpowers/specs/2026-06-15-render3d-custom-engine-design.md`.

## Camera: first-class follow / scroller camera (`KhaozEngine.Render2D`)

`Camera2D` is the generic matrix base: position/zoom/rotation to view matrix, world<->screen, and a
`ClampPosition` bounds helper. A "feel" layer has since been built on top of it without changing the base.

**Shipped:**
- `CameraController` (3.7.0): pan/zoom/pinch gesture controller driving a `Camera2D` from an
  `InputManager` (drag + two-finger pan, wheel + pinch zoom about cursor/focus, world-bounds clamp,
  tap-vs-pan disambiguation). Shared gesture core (`PinchGestureTracker` / `CameraGestures`) added 3.10.0
  and also drives `UI.PannableCanvas` (which gained real pinch zoom).
- `CameraFollow` (3.9.0): eases `Position` toward a target with frame-rate-independent smoothing
  (`1 - exp(-Stiffness*dt)`), an optional screen-space `Deadzone` (camera window), and bounds clamp.
- `Camera2D.CenterOn(world)` + `Camera2D.Focus(rect, viewport, padding, minZoom, maxZoom)` (3.9.0):
  point framing and fit-to-rect contain-zoom (the framing math Hardpoint/SpaceForge hand-rolled).
- 5.52.0: `CameraFollow` ported to the 5.x `KhaozEngine.Render2D` line with **per-axis follow tuning**
  (`Stiffness` is a `Vector2`), **look-ahead** (`LookAheadSettings`: eased, clamped, per-axis lead along a
  caller-supplied velocity), and **pixel-perfect snapping** (`PixelSnap` on the rendered position, sub-pixel
  smoothing preserved). The old 4.x `KhaozEngine.Graphics` `CameraFollow` was retired with the rest of the 4.x
  line once SpaceGame ported.
- 5.53.0: `GroupCamera` + `CameraFraming` (`KhaozEngine.Render2D`) - **multi-target framing** for co-op /
  shared screen: eases position + zoom to fit N targets (padded bounding box, contain-fit zoom, per-axis
  `MinViewSize` floor, world-bounds clamp, instant `Warp`).
- 5.54.0: `CameraBlend` + `CameraState` + `Easing` (`KhaozEngine.Render2D`) - **eased camera blends**: a
  one-shot, time-based transition that lerps position/zoom/rotation between setups over a duration with a
  preset or custom easing curve (instant snap on duration 0). The primitive room/region cameras hand off with.
- 5.55.0: `RoomCamera` + `CameraRoom` (`KhaozEngine.Render2D`) - **room / region cameras**: per-area bounds
  (+ optional zoom), in-room follow confined to the region, and an eased hand-off (CameraBlend) when the
  target crosses into a new region. Composes the follow + blend layers.
- 5.56.0: `ScreenShake` (`KhaozEngine.Effects`, graduated to 5.x) - **screen shake**: trauma-based,
  deterministic offset generator (`trauma^2` falloff, seeded smooth noise, positional + rotational), composed
  onto the render camera. Pairs with the follow / room cameras.
- 5.57.0: `ParallaxLayer` + `Parallax.Wrap` (`KhaozEngine.Render2D`) - **parallax background layers**:
  per-axis scroll factor (0 = static .. 1 = world-locked) off the same camera, plus a positive-modulo tiling
  helper for infinitely repeating backgrounds. Completes the camera feel-layer backlog.

**Still open** (the deeper scroller/platformer feel layer):
- (none - the camera feel-layer backlog is complete as of 5.57.0)

Motivated by a planned platformer / side-scroller. (Base design:
`docs/superpowers/specs/2026-06-11-graphics-camera2d-design.md`.)

Note: `Camera2D` is a uniform full-viewport projection. Nullwake's camera does NOT use it (its
`OreField.RefToScreen` is a non-uniform scale into a screen sub-rect, a different model with nothing
to delete, see CONSUMERS.md). Converging Nullwake later would require adding sub-viewport +
non-uniform-scale support to the camera, or Nullwake's projection stays game-specific.

## Screen shake (`KhaozEngine.Effects`)

**Shipped (5.56.0)** as the trauma-based `ScreenShake` offset generator (see the camera section).

A screen-shake effect that perturbs the camera, composed onto the render camera in `KhaozEngine.Render2D`.
Trauma-based decay. Pairs with the follow-camera layer above.

## Particle unification

**Superseded.** The old `Effects.ParticleSystem` (rect-based, pooled) was removed when `KhaozEngine.Effects`
narrowed to just `ScreenShake`. Particle simulation now lives in two deliberate places: `KhaozEngine.Particles`
(3D, render-agnostic pool consumed via billboards) and `KhaozEngine.Render2D.Vfx.Particle2DSystem` (2D
screen-space pool with its own draw). They share the same emit / integrate / lerp-over-life model but differ in
dimensionality and recycle policy. SpaceGame's richer game-side features (textured sprites, tails / trails,
on-death recursion) stay game-side; a future pass could factor out the shared sim core, but there is no single
`Effects` particle target any more.

## SFX audio (`KhaozEngine.Audio`)

**Shipped (5.34.0).** `KhaozEngine.Audio` gained SFX one-shots + 3D positional audio over a 16-voice pool
(`PlaySfx`/`PlaySfx3D`/`SetListener`, per-channel volume) on top of the streaming music backend, so it is no
longer music-only. SpaceGame (now ported, on `7.3.0`) may still keep its game-side SFX mixing
(`AudioVolumeMixer`) layered over the engine audio.

## Shipped (closed roadmap items)

- **`PrimitiveRenderer` circle/ring:** `DrawCircle`, `DrawFilledCircle`, and a thickness-aware,
  radius-adaptive `DrawRing` (with `RingSegments`) shipped. SpaceGame's ring rendering and Hardpoint's tower
  range rings use them. (Originally in the 4.x `UI`/`Graphics` packages; the primitive drawing now lives on
  the MonoGame-free stack in `KhaozEngine.Render2D` / `Render3D`.)

---
_Source: coordinated promote-into-KE effort, 2026-06-11; shipped-items reconciled 2026-06-13 at 4.0.0.
The engine is now one MonoGame-free shared version line, currently 7.34.0 (see CONSUMERS.md / CHANGELOG.md).
Update as items are scheduled or shipped._
