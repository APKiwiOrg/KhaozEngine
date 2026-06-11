# KhaozEngine roadmap / backlog

Larger feature areas identified but not yet scheduled. Current released version: **3.3.0** (Batch 2).

The next concrete pass is **3.4.0** (small, tracked separately): a `SettingsManager<T>.sanitizeOnLoad`
hook + `AudioSystem` explicit/repeat playback, plus a few review nits. It exists to unblock the
SpaceGame / Nullwake adoptions. The items below are the *bigger* areas to come back to afterward
(roughly "Batch 3").

## Camera — first-class follow / scroller camera (`KhaozEngine.Graphics`)

`Camera2D` (3.3.0) is the generic matrix BASE only: position/zoom/rotation -> view matrix,
world<->screen, and a `ClampPosition` bounds helper. A game can hand-roll a scroller camera on it
today, but the engine does NOT yet provide the follow / "feel" layer, so every consumer would
reimplement it. Build a `FollowCamera` that drives `Camera2D.Position`/`Zoom` each frame (base
unchanged). Full 2D scroller / platformer camera needs:

Follow & framing
- Target follow with damping/smoothing, settable PER AXIS (platformers decouple X/Y, e.g. only
  re-centre Y when grounded/landing)
- Deadzone / camera window (soft box the target moves in before the camera reacts) - biggest feel upgrade
- Target offset / composition bias (frame the player low so you see more ahead/below)
- Look-ahead (lead in movement/facing direction, with its own smoothing)
- Multi-target framing (auto position + zoom to fit N targets) - co-op / shared screen

Constraints
- Bounds confiner: auto-apply `ClampPosition` during follow, with per-region bounds
- Room / region cameras: different bounds (and optionally settings) per area, Metroidvania-style

Motion polish
- Smooth / eased zoom transitions
- Camera blends: lerp position/zoom/rotation between setups over a duration (room hand-offs)
- Instant snap: teleport on respawn / scene load / room change
- Pixel-perfect snapping: round camera position to the pixel grid for pixel-art (kills sub-pixel shimmer)

Effects & parallax
- Screen shake (lives in `KhaozEngine.Effects`, perturbs the camera; trauma-based decay) - see below
- Parallax background layers scrolling at fractional rates off the same camera

Motivated by a planned platformer / side-scroller. (Base design:
`docs/superpowers/specs/2026-06-11-graphics-camera2d-design.md`.)

Note: `Camera2D` is a uniform full-viewport projection. Nullwake's camera does NOT use it (its
`OreField.RefToScreen` is a non-uniform scale into a screen sub-rect - a different model with nothing
to delete, see CONSUMERS.md). Converging Nullwake later would require adding sub-viewport +
non-uniform-scale support to the camera, or Nullwake's projection stays game-specific.

## Screen shake (`KhaozEngine.Effects`)

A screen-shake effect that perturbs the camera (Effects -> Graphics interplay). Pairs with the
follow-camera layer above.

## Particle unification (`KhaozEngine.Effects`)

`Effects.ParticleSystem` (3.3.0) is rect-based and pooled. SpaceGame's `ParticleManager` has richer
features that were kept game-side during Batch 2 because they were architecturally different:
textured sprites, particle tails / trails, and on-death recursion (a dying particle spawns children).
Fold these into the engine so SpaceGame can adopt and the two converge.

## PrimitiveRenderer.DrawCircle / DrawRing (`KhaozEngine.UI`)

`KhaozEngine.UI.PrimitiveRenderer` has filled rects + lines but no circle/ring. SpaceGame has a
thickness-aware ring renderer (`CircleRenderer`); contribute a `DrawCircle` / thickness-aware
`DrawRing` overload back to `PrimitiveRenderer` so consumers stop reimplementing it.

## SFX audio (`KhaozEngine.Audio`)

`KhaozEngine.Audio` is music-only (one track at a time + master x music volume). Games that mix sound
effects keep their own SFX volume/mixing (e.g. SpaceGame's `AudioVolumeMixer`). A future SFX layer
(one-shot playback, channels, separate SFX vs music volume) would let those move into the engine.

---
_Source: coordinated promote-into-KE effort, 2026-06-11. Update as items are scheduled or shipped._
