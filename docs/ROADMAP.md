# KhaozEngine roadmap / backlog

Larger feature areas identified but not yet scheduled. Current released version: **5.51.0** (the 5.x line,
which is the engine: the custom MonoGame-free stack plus the graduated foundation packages). The legacy 4.x
line is frozen-ish at `4.12.0` and now carries only the genuinely-MonoGame packages
(`Effects`/`Graphics`/`Input`/`Screens`/`Sprites`/`Time`/`UI`), consumed by the still-4.x SpaceGame.

Several items from the original (3.3.0-era) backlog have since shipped: the camera follow/framing
layer, the pan/zoom gesture controller, and `PrimitiveRenderer` circle/ring drawing. Those are listed
under each area as **Shipped** so the remaining work is clear. The 4.0.0 release also moved
`PrimitiveRenderer`/`ColorHelper` into `KhaozEngine.Graphics` (see CHANGELOG); roadmap references below
use the current namespaces.

## The post-MonoGame pivot (5.x line) — strategic direction

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

### Current status (as of `5.18.0-experimental`)

**Every subsystem needed to drop MonoGame is now proven on the custom stack — no feasibility unknowns
remain; the rest is productization + porting.** Shipped 5.x packages (shared version, currently
`5.47.0`):

| Subsystem | Status |
|---|---|
| 3D rendering | shipped — `KhaozEngine.Render3D` (IsoCamera3D, runtime glTF lit/cel, PixelPostProcess, starfield) |
| 2D rendering | shipped — `KhaozEngine.Render2D` (SpriteBatch, Camera2D, Texture2D + PNG) |
| Text | shipped — `SpriteFont` (runtime TTF via stb_truetype) in Render2D |
| Audio | shipped — `KhaozEngine.Audio` graduated to OpenAL streaming (WAV/OGG/MP3) |
| Windowing + input | shipped — `KhaozEngine.Windowing` (`AppWindow` + engine-native keyboard/mouse `InputState` + bounds-aware `Pointer` with the click-through invariant; Render2D draws via `Render2DSurface`, the standalone `Render2DHost` was folded away). Gamepad/touch/pinch + virtual-resolution transforms are follow-ups |
| Screens / UI | shipped (widget set) — `KhaozEngine.Gui`: `ScreenStack` routing + transitions, `Screen` base, and the widgets `Button`/`Label`/`Panel`/`Slider`/`Toggle` + `Dropdown`/`TextInput`/`Tooltip`/`PopupPanel`/`ScrollablePanel` over `Pointer`; `TextLayout` wrap/align + `SpriteBatch` scissor clip in Render2D; `TextEntry` key→char. Pause/timescale, touch/gamepad, and virtual-resolution transforms are the remaining follow-ups |
| Math | done — `System.Numerics` |

Caveats to clear during productization: Metal-only so far (per-backend clip-Y + MRT-clear handling pending);
SDL2 bundled per-sample; production audio should bundle **openal-soft** (macOS's system OpenAL is deprecated,
same as its OpenGL).

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
   `cross-platform-gpu.yml` CI matrix runs the golden-snapshot tests per backend: **Metal + D3D11 (WARP) are
   green and blocking**, with committed per-backend goldens. **Still open:** the Linux Vulkan leg is
   non-blocking (Mesa lavapipe crashes at `vkEnumeratePhysicalDevices` on the hosted runner) and the `gl`
   override parses but is unverified — both wait for real GPU CI / hardware. See `docs/CROSS-PLATFORM.md`.

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
   toggle. Proves the renderer + the look. Metal-only for now.
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
5. **Migrate the games.** Hardpoint / Nullwake / SpaceGame onto the 5.x stack; retire the 4.x MonoGame line.
6. **Mobile.** iOS/Android platform layers (lifecycle, touch, packaging, stores).

### Version policy (two lines)

- **5.x line — the engine.** The custom-stack packages (`Gpu`, `Windowing`, `Render2D`, `Render3D`, `Gui`,
  `Audio`, `Particles`, `Game`) **and**, as of **`5.46.0`**, the graduated MonoGame-free foundation packages
  (`Ecs`/`Serialization`/`Content`/`Diagnostics`/`App`/`Localization`/`Persistence`/`Pooling`/`Platform`/
  `Updates`/`Collision`/`Netcode`/`Netcode.Abstractions`/`Netcode.LiteNetLib` — 14 packages) share the version
  `Directory.Build.props` `<KhaozEngine5xVersion>` and release together under one `vX.Y.Z` tag. It dropped the
  `-experimental` suffix at `5.31.0` (after the audit-driven P0 + P1: correctness net, instancing, the
  graphics-backend seam behind `KhaozEngine.Gpu`, the `GameApp` loop facade). **The foundation graduated onto
  this line at `5.46.0`** (audit P1#9) so a 5.x game pins **only** 5.x packages; it was a non-breaking
  re-version (same assemblies/API, just a version-string swap from `4.12.0`). The **doc-version guard now
  checks this line** (`<KhaozEngine5xVersion>`). (The first two Render3D releases, 5.0.0/5.1.0, predate the
  shared line and were per-package.)
- **4.x line — legacy, frozen-ish.** After the `5.46.0` graduation it carries **only** the genuinely-MonoGame
  packages (`Effects`/`Graphics`/`Input`/`Screens`/`Sprites`/`Time`/`UI`), consumed by the still-4.x SpaceGame
  until it migrates; then they're deleted and MonoGame is fully gone. It bumps only when one of those packages
  needs a release. Its `<Version>` (`4.12.0`) is no longer the "current engine version" and is not guard-checked
  (it lags like a consumer pin).

Design spec: `docs/superpowers/specs/2026-06-15-render3d-custom-engine-design.md`.

## Camera: first-class follow / scroller camera (`KhaozEngine.Graphics`)

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

**Still open** (the deeper scroller/platformer feel layer):
- Per-axis follow tuning (platformers decouple X/Y, e.g. only re-centre Y when grounded/landing).
  `CameraFollow` today smooths both axes with one `Stiffness`.
- Look-ahead (lead in movement/facing direction, with its own smoothing).
- Multi-target framing (auto position + zoom to fit N targets), for co-op / shared screen.
- Room / region cameras: different bounds (and optionally settings) per area, Metroidvania-style.
- Smooth / eased zoom transitions and camera blends (lerp position/zoom/rotation between setups over a
  duration, for room hand-offs); instant snap on respawn / scene load.
- Pixel-perfect snapping: round camera position to the pixel grid for pixel-art (kills sub-pixel shimmer).
- Parallax background layers scrolling at fractional rates off the same camera.
- Screen shake that perturbs the camera (lives in `KhaozEngine.Effects`, see below).

Motivated by a planned platformer / side-scroller. (Base design:
`docs/superpowers/specs/2026-06-11-graphics-camera2d-design.md`.)

Note: `Camera2D` is a uniform full-viewport projection. Nullwake's camera does NOT use it (its
`OreField.RefToScreen` is a non-uniform scale into a screen sub-rect, a different model with nothing
to delete, see CONSUMERS.md). Converging Nullwake later would require adding sub-viewport +
non-uniform-scale support to the camera, or Nullwake's projection stays game-specific.

## Screen shake (`KhaozEngine.Effects`)

A screen-shake effect that perturbs the camera (Effects to Graphics interplay; the
`Effects -> Graphics` package dependency exists as of 4.0.0). Trauma-based decay. Pairs with the
follow-camera layer above. Not yet built.

## Particle unification (`KhaozEngine.Effects`)

`Effects.ParticleSystem` is rect-based and pooled. SpaceGame's `ParticleManager` has richer features kept
game-side: textured sprites, particle tails / trails, and on-death recursion (a dying particle spawns
children). Fold these into the engine so SpaceGame can adopt and converge. (SpaceGame is on 4.0.0 but
still does NOT reference `KhaozEngine.Effects`, which is the blocker.)

## SFX audio (`KhaozEngine.Audio`)

`KhaozEngine.Audio` is music-only (one track at a time + master x music volume). Games that mix sound
effects keep their own SFX volume/mixing (e.g. SpaceGame's `AudioVolumeMixer`). A future SFX layer
(one-shot playback, channels, separate SFX vs music volume) would let those move into the engine.

## Shipped (closed roadmap items)

- **`PrimitiveRenderer` circle/ring:** `DrawCircle`, `DrawFilledCircle`, and a thickness-aware,
  radius-adaptive `DrawRing` (with `RingSegments`) shipped and now live in `KhaozEngine.Graphics`
  (moved from `UI` in 4.0.0). SpaceGame's ring rendering and Hardpoint's tower range rings use them.

---
_Source: coordinated promote-into-KE effort, 2026-06-11; shipped-items reconciled 2026-06-13 at 4.0.0;
version line tracks the current release (4.1.0 was logging-only, no roadmap area moved). Update as items
are scheduled or shipped._
