# KhaozEngine roadmap / backlog

Larger feature areas identified but not yet scheduled. Current released version: **3.3.0** (Batch 2).

The next concrete pass is **3.4.0** (small, tracked separately): a `SettingsManager<T>.sanitizeOnLoad`
hook + `AudioSystem` explicit/repeat playback, plus a few review nits. It exists to unblock the
SpaceGame / Nullwake adoptions. The items below are the *bigger* areas to come back to afterward
(roughly "Batch 3").

## Camera — first-class follow / scroller camera (`KhaozEngine.Graphics`)

`Camera2D` (3.3.0) is the generic matrix base only (position/zoom/rotation -> view matrix, world<->screen,
bounds clamp). Build a follow-camera layer on top that drives `Camera2D.Position` each frame; the base
needs no changes:

- Follow a target with smoothing / damping
- Deadzone / camera window (don't move until the target leaves a box) — biggest platformer feel upgrade
- Look-ahead (lead in the direction of motion)
- Parallax background layers off the same camera
- Smooth zoom

Motivated by a planned platformer / side-scroller. Nullwake's existing OreField follow-cam should
eventually compose this instead of its own math. (Base design: `docs/superpowers/specs/2026-06-11-graphics-camera2d-design.md`.)

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
