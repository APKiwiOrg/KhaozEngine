# KhaozEngine roadmap / backlog

Larger feature areas identified but not yet scheduled. Current released version: **4.5.0**.

Several items from the original (3.3.0-era) backlog have since shipped: the camera follow/framing
layer, the pan/zoom gesture controller, and `PrimitiveRenderer` circle/ring drawing. Those are listed
under each area as **Shipped** so the remaining work is clear. The 4.0.0 release also moved
`PrimitiveRenderer`/`ColorHelper` into `KhaozEngine.Graphics` (see CHANGELOG); roadmap references below
use the current namespaces.

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
