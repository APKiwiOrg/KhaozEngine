# Particle attractor, design (2026-07-20)

Status: shipped 14.7.0 (issue #255). Second half of the Ruinborne death pass: the skinned corpse
dissolve shipped in 14.4.0/14.5.0, this capability lets the dissolving corpse's essence visibly
drain into the killer. Consumer: https://github.com/APKiwiOrg/Ruinborne/issues/120.

## Problem

`KhaozEngine.Particles` had no way for live particles to move toward anything other than their own
spawn velocity plus gravity/drag/turbulence. The consumer wanted motes that accelerate toward a
*moving* world-space target (the killer, tracked per frame) and are consumed on arrival, with the
spawn rate itself tied to an external ramp (the corpse's dissolve threshold) rather than running
flat for the whole effect.

## Decisions

1. **System-level state, not per-instance.** `ParticleAttractor` is one nullable field on
   `ParticleSystem` (`Attractor`), applied to every live particle in that pool, not a per-particle
   tag chosen at spawn time. `ParticleSystem` is a flat structure-of-arrays pool with no per-particle
   metadata beyond the fields already needed for rendering (position/velocity/age/size/color/spin).
   Adding a per-particle attractor reference (or even an index into a small attractor table) would
   grow every particle slot for a feature only one phase of one preset uses, and would need its own
   swap-remove bookkeeping alongside `_ignoreAttract` (added at the same granularity: one bool per
   slot, the cheapest possible per-particle knob). No consumer asked for two different pulls acting
   on the same pool at once. `EmitterConfig.IgnoreAttractor` is the one per-particle-class knob that
   *is* needed: it lets one phase of a multi-phase effect (the haze) opt all of its particles out of
   the system's single attractor, without touching the pool's per-particle layout at all (it is
   captured once per particle from its config at emit time, same as every other config-derived
   field). If a future consumer needs two independent pulls on one pool, that is an additive change
   (a second `ParticleAttractor?` field, or a small array). Nothing here forecloses it.

2. **One `ParticleEffectPlayer`-level attractor per drain instance, forwarded to every phase pool.**
   `ParticleEffectPlayer.Attractor` is a single nullable field, assigned straight through to every
   phase's `ParticleSystem.Attractor` on write. A player already owns one `ParticleSystem` per phase
   (for mixed per-phase looks), so "forward to all phases" is the natural shape, and
   `IgnoreAttractor` is still what lets a phase decline it. This matches `EssenceMotes` exactly: one
   player, one drain target, two phases (motes pulled, haze free). A consumer driving several
   independent drains (say, several corpses draining into several killers) uses several player
   instances, which the API already supports (`maxInstances` on one player is for *concurrent plays
   of the same effect*, not concurrent independent targets).

3. **Per-instance tagging (a particle remembering which target pulled it) was considered and
   rejected.** The only scenario where it would matter is one pool serving two simultaneously-active
   attractors with different targets, which no consumer wants and which the engine's "one player per
   drain instance" shape already covers more cheaply. Implementing it would mean either a per-particle
   attractor index (a new per-slot array, swap-removed like every other per-particle field) or boxing
   attractor state per particle, for a capability nothing downstream asks for. Deferred: not designed
   away, just not built ahead of a consumer.

4. **Bool opt-out (`IgnoreAttractor`), not a float influence weight.** The considered alternative was
   a per-config `float AttractorInfluence` (0 = ignore, 1 = full pull, in between a partial pull), so
   a phase could be, say, half-pulled for a softer look. No consumer wanted partial influence: the
   Ruinborne drain wants motes fully pulled and haze fully free, a binary split. A float default of 1
   (full pull) reproduces the bool's "not opted out" case, so adding graduated influence later is a
   backward-compatible field addition (default 1f keeps every existing config identical), not a
   breaking change to what shipped. Building the graduated version now would be a knob with no
   consumer and no way to verify it feels right.

5. **The force is appended after turbulence, inside the same per-particle loop, before position
   integration.** `ParticleSystem.Update` already has a single ordered per-particle pass: age forward,
   apply gravity/drag (baked into velocity at emit time via the existing config), add the turbulence
   curl term, *then* integrate position. The attractor pull is one more velocity-accumulating term
   appended at the same point turbulence is, gated behind `hasAttractor && !_ignoreAttract[i]`, so a
   particle with no attractor (the overwhelming common case, `Attractor` defaults to null) pays one
   branch and nothing else: the loop is bit-identical to the pre-attractor build whenever `Attractor`
   is unset. This is the sim's existing gated-append discipline (every additive `EmitterConfig` field
   before this one, from turbulence to spin to trails, was folded in the same way: append a term
   behind a cheap guard, never restructure the loop or reorder existing terms). `MaxSpeed` clamps
   immediately after the pull is added, in the same block, so the cap only ever measures the velocity
   the pull actually produced this frame, not a stale value from before turbulence.

6. **Absorb is checked post-integration, not pre-integration.** The kill-radius test
   (`DistanceSquared(p.Position, attractor.Target) <= killSq`) runs after `p.Position += p.Velocity *
   dt`, using the position the particle actually arrives at this frame, not the position it started
   the frame at. Checking pre-integration would let a fast-moving particle overshoot through the kill
   radius (enter and exit within one frame's integration step) without ever being seen inside it,
   silently missing an absorb the player's eye would expect to have happened. Post-integration is a
   standard "did we cross the line this step" ordering and costs nothing extra: the distance check was
   going to read `p.Position` either way, and the post-integration value is the one already about to
   be handed to `_trailPos`/the renderer this frame if the particle survives. An absorbed particle
   is swap-removed via the pool's existing `RecycleAt`, so the loop's index bookkeeping (`i`
   held, `_count` decremented, `continue` to re-examine the slot now holding the swapped-in tail
   particle) is the same pattern the pool already uses for natural age-out, not a new removal path.

7. **Emission-rate modulation is two knobs, not one, because it has two different drivers.**
   `ParticleEffectPhase.RateCurve` (optional `ParticleCurve`, authored on the phase, evaluated against
   `local / Duration`) is a shape baked into the effect at author time: "this phase's own rate ramps up
   over its first third regardless of anything the game does." `ParticleEffectPlayer.RateScale` (a
   plain `float`, default 1, read fresh every `Update`) is a value the *game* computes every frame and
   has no way to know ahead of authoring time: a dissolve threshold, a channel-up windup, a distance
   falloff. Collapsing them into one knob would force a choice between exposing player-level control
   over an authored-per-phase curve (breaking the "one look, several uses" value of a preset, since
   every consumer of `EssenceMotes` would need to fight or replace the authored curve to drive its own
   ramp) or losing the ability to author a rate shape at all (every preset would need the game to
   supply the full curve externally, even for effects with no runtime driver). They multiply
   (`rate = phase.RatePerSecond * RateScale`, then `* RateCurve.Evaluate(norm)` if set) so an author
   can still shape a phase's own envelope while a game independently scales the whole player, and
   `EssenceMotes` ships with no `RateCurve` on either phase precisely because its ramp is meant to come
   from the consumer's `RateScale`, not from the preset itself.

## Testing

- `ParticleAttractorTests.cs` (488 lines): pull-toward-target math (with and without `StrengthCurve`
  shaping, `MaxSpeed` clamping), absorb-on-arrival (counters, `OnAbsorbed`, swap-remove correctness
  against the rest of the pool), `IgnoreAttractor` opt-out, `Attractor = null` mid-flight (drift/fade,
  no snap), zero/negative `Strength`/`KillRadius`/`MaxSpeed` disabling their half of the behaviour.
- `ParticleEffectAttractorTests.cs` (273 lines): `ParticleEffectPlayer.Attractor`/`OnAbsorbed`
  forwarding to every phase pool, `AbsorbedLastUpdate`/`AbsorbedTotal` summing across phases,
  `RateScale` interacting with `RateCurve` (multiplicative, each independently optional).
  `ParticleCurveTests.cs` gained `One` coverage (constant 1 across the domain).
- `VfxPresetsTests.cs`: `EssenceMotes` phase count, look count, and the haze phase's
  `IgnoreAttractor = true`.
- GPU: `Golden3D_ParticlesAttractor` (`scene3d_particles_attractor.metal.txt`), `EssenceMotes` played
  and stepped 108 frames toward a fixed target with the attractor re-assigned every step (exercising
  the same per-frame call path a moving target would), locking the attracted motion, kill-radius
  absorb, and max-speed clamp end to end through the modern particle pass. Two pixel-presence
  GpuFacts in `ParticleAttractorGpuTests.cs`: the luminance-weighted pixel centroid measurably closes
  on the target's screen projection between an early and a late frame, and the live bright-pixel count
  collapses once the drain has had time to fully absorb.
- The shipped skinned dissolve pass (`Scene3D.DrawSkinned`'s dissolve overload, CharDissolve) is
  untouched: this capability is a pure addition to `KhaozEngine.Particles`/`.Render3D`, no shared code
  path with the dissolve shader or `ModelDissolveFrag`.

## Rejected alternatives

- **A generic force-field abstraction** (attractors, repulsors, vortices, wind zones as one
  polymorphic `IParticleForce` list per system): no consumer asked for anything beyond a single point
  pull, and a virtual-dispatch force list would cost an indirection per particle per force in the hot
  `Update` loop for capability nothing downstream uses yet. `ParticleAttractor` is a plain value
  struct evaluated inline, matching every other additive `EmitterConfig`/`ParticleSystem` field's
  cost profile (zero when unset, one branch plus a few FLOPs when set). Revisit if a second force type
  (a repulsor, a wind zone) gets an actual consumer.
- **Absorb as a separate post-process pass over `Active` instead of folded into `Update`'s existing
  loop**: would need its own iteration over the live prefix (and its own swap-remove interaction with
  whatever `Update` already removed for natural age-out), doubling the per-frame walk for no benefit,
  since the position each particle needs to test against `KillRadius` is only known once `Update`
  has already integrated it this frame.
- **A float influence weight on `IgnoreAttractor` instead of a bool** (decision 4 above): deferred,
  no consumer, and the bool's default (false = pulled) is exactly influence 1.0, so upgrading later is
  additive, not breaking.
