# KhaozEngine roadmap / backlog

Larger feature areas identified but not yet scheduled. Current released version: **4.9.0**.

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

### Current status (as of `5.5.0-experimental`)

**Every subsystem needed to drop MonoGame is now proven on the custom stack — no feasibility unknowns
remain; the rest is productization + porting.** Shipped 5.x packages (shared version, currently
`5.5.0-experimental`):

| Subsystem | Status |
|---|---|
| 3D rendering | shipped — `KhaozEngine.Render3D` (IsoCamera3D, runtime glTF lit/cel, PixelPostProcess, starfield) |
| 2D rendering | shipped — `KhaozEngine.Render2D` (SpriteBatch, Camera2D, Texture2D + PNG) |
| Text | shipped — `SpriteFont` (runtime TTF via stb_truetype) in Render2D |
| Audio | shipped — `KhaozEngine.Audio` graduated to OpenAL streaming (WAV/OGG/MP3) |
| Windowing + input | shipped — `KhaozEngine.Windowing` (`AppWindow` + engine-native keyboard/mouse `InputState` + bounds-aware `Pointer` with the click-through invariant; Render2D draws via `Render2DSurface`, the standalone `Render2DHost` was folded away). Gamepad/touch/pinch + virtual-resolution transforms are follow-ups |
| Math | done — `System.Numerics` |

Caveats to clear during productization: Metal-only so far (per-backend clip-Y + MRT-clear handling pending);
SDL2 bundled per-sample; production audio should bundle **openal-soft** (macOS's system OpenAL is deprecated,
same as its OpenGL).

**Next milestone:** **port `Screens` + `UI` onto the custom stack** — they sit directly on the `Pointer`
(now ported) + Render2D, and are the last engine layers between the foundation and a running game. Also finish
input breadth (gamepad/touch/pinch, virtual-resolution transforms) as games need it. Then migrate a first game.

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

### Version policy (two lines during the transition)

- **4.x line** — the existing MonoGame packages; one shared version in `Directory.Build.props`; keeps
  shipping 4.8.0, 4.9.0, ... normally and in parallel.
- **5.x experimental line** — the custom-stack packages (`Render3D`, `Render2D`, ...) share a second version,
  `Directory.Build.props` `<KhaozEngine5xVersion>` (currently `5.5.0-experimental`), and release together
  under one `vX.Y.Z-experimental` tag. (The first two Render3D releases, 5.0.0/5.1.0, predate this and were
  per-package.) The doc-version guard checks the shared 4.x version only; the 5.x line is exempt (like
  consumer pins). Packages graduate
  4.x -> 5.x as they are ported.

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
