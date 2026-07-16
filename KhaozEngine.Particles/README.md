# KhaozEngine.Particles

Pure, deterministic, headless-testable particle simulation for the MonoGame-free 5.x stack
(`System.Numerics` + BCL only, no Veldrid/MonoGame, no project refs).

- `ParticleSystem` - capacity-bounded pool. `Emit` a burst, `Update(dt)` to age/integrate/interpolate/recycle.
  Dead particles are swap-removed so `Active` is a contiguous `ReadOnlySpan<Particle>` of the live prefix.
  Opt into per-particle motion-history trails with the ctor `trailSamples` (see Trails below).
- `Particle` - live interpolated state (`Position`/`Velocity`/`Age`/`Life`/`Size`/`Color`/`Rotation`/`Seed`) a renderer reads.
- `EmitterConfig` - spawn params (lifetime/speed ranges, cone `Direction` + `SpreadDegrees`, `Gravity`,
  `Drag`, start/end size + color). `Spark` and `Puff` static defaults. Modern fields (emission shapes, life
  curves, colour/size variance, spin, turbulence) layer on top, all zero-default to this legacy behaviour (see
  the modern-VFX sections below).
- `RateAccumulator` - turns a per-second rate into an int emit-count per frame, carrying the fractional remainder.

Determinism: an internal xorshift32 RNG seeded by the ctor `seed`. Two systems with the same seed and the
same `Emit`/`Update` calls produce identical particles. No `System.Random`, no wall-clock.

## Modern emission and shading

All of these are additive `EmitterConfig` fields and every one zero-defaults to the exact legacy behaviour, so
an unmodernised config emits and shades bit-for-bit as before.

- **Curve-driven life** (`SizeCurve`, `AlphaCurve`, both `ParticleCurve`). A `ParticleCurve` is a readonly
  `{ ParticleCurveKind Kind, float Param }` that remaps the normalised age fed to the start/end lerp, so a
  particle value stays `lerp(start, end, curve.Evaluate(n))`. Kinds: `Linear` (identity, the default, bit-identical
  to the legacy straight lerp), `EaseIn`, `EaseOut`, `EaseInOut`, `Flash` (snap to Start at birth, peak at `Param`,
  decay back to End), `FadeInOut` (trapezoid, `Param` = edge fraction, reads as fade-in/hold/fade-out), `Pulse`
  (`Param` = cycle count). Static factories: `ParticleCurve.EaseOut`, `ParticleCurve.Flash(0.15f)`, etc.
- **Emission shapes** (`Shape`, `ShapeRadius`, `ShapeShell`). `Point` (default, spawn at the origin), `Sphere`,
  `Hemisphere` (a dome folded to `Direction`, +Y when Direction is ~zero), `Disc` (perpendicular to `Direction`,
  +Y when ~zero). `ShapeShell` 0 fills the volume, 1 spawns only on the surface/edge, blending in between. A ring
  is a `Disc` with `ShapeShell` 1, and a cone emitter is a `Disc` position plus the spread cone, so neither needs
  its own enum value.
- **Radial velocity** (`VelocityMode`). `Cone` (default) is the legacy spread cone around `Direction`. `Radial`
  launches each particle outward from the origin through its spawn point, for explosions and shockwaves.
- **Per-particle variance**. `SizeVariance` (0..1) bakes a `1 + SizeVariance*(2u-1)` multiplier into both start and
  end size. `VaryColor` with `StartColorB`/`EndColorB` blends each particle a random `t` between the A and B colour
  pairs (random-between-two-gradients). `UseMidColor` + `MidColor` make a 3-stop gradient with the mid stop at
  normalised age 0.5.
- **Spin and rotation**. `SpinMin`/`SpinMax` (rad/s, negatives allowed) draw a per-particle spin integrated into
  `Particle.Rotation`. `RandomStartRotation` gives each particle a random initial roll in [0, 2pi).
- **Turbulence** (`TurbulenceStrength`, `TurbulenceFrequency`). A deterministic curl-flavoured value-noise force
  (`ParticleNoise.Curl`): a pure function of position, system time, and the particle's seed (integer-hash noise, no
  trig-based hashing, no wall clock), so the swirl is reproducible across builds. `TurbulenceFrequency <= 0` is
  treated as 1.

`Particle` gains two read-only-for-renderers fields alongside the interpolated state: `Rotation` (the current
billboard roll in radians, integrated from the emitter spin) and `Seed` (a stable per-particle randomiser in
[0,1), hashed from a monotonic emit counter so it consumes NO RNG draw). A renderer or the turbulence field reads
`Seed` for per-particle variety.

## Trails (opt-in)

Pass `trailSamples > 0` to the `ParticleSystem` ctor to record a per-particle motion-history ring (default 0 keeps
the legacy footprint). Each live particle captures its position + age every `TrailSampleInterval` seconds
(property, default 1/30 s, carries the sub-interval remainder so the long-run cadence tracks the interval). Read a
particle's history with `int GetTrail(int particleIndex, Span<ParticleTrailPoint> dest)`, which copies
oldest-to-newest and returns the count (keeping the newest samples when the span is shorter than the history).
`ParticleTrailPoint` is `{ Vector3 Position, float Age }`, and `TrailCapacity` reports the depth. The ring block
moves with the particle on swap-remove, so trails survive recycling.

## Authored effects (scheduling)

A layered effect plays several emitters on one timeline, all headless and deterministic:

- `ParticleEffectPhase` - one `EmitterConfig` plus its schedule: `Delay`, `Duration`, `RatePerSecond`,
  `BurstCount`, `PoolCapacity`, `TrailSamples`, and `OriginOffset` (an effect-local emission offset authored with
  +Y as the effect axis and rotated onto the played direction, for lifting a ground ring off the floor or pushing
  a muzzle phase ahead of the hand). `Duration` 0 with a positive `BurstCount` is an instant burst, `RatePerSecond`
  > 0 streams while the phase is active.
- `ParticleEffect` - an immutable list of phases (impact = flash burst + spark burst + smoke stream + ring). The
  phase array is defensively copied.
- `ParticleEffectPlayer` - plays bounded concurrent instances with one `ParticleSystem` pool per phase, so mixed
  per-phase looks stay renderable. `Play(Vector3 origin, Vector3 direction)` starts an instance, rotating every
  phase's emitter `Direction` (and `OriginOffset`) from +Y onto the played direction. `Update(dt)` advances each
  instance's schedule then steps every pool once. `PhaseSystem(i)` exposes a pool for a renderer, with `AnyAlive`
  and `Clear()` rounding it out. Deterministic from the ctor seed.

## Backward compatibility

Every new `EmitterConfig` field zero-defaults to the exact legacy behaviour, and `Particle`, `ParticleSystem`,
`RateAccumulator`, and the `Spark`/`Puff` presets keep every existing member. Legacy configs also keep the
historical RNG draw sequence in-build: `Emit` draws the legacy prefix (life, cone direction, speed) first and
gates every new feature's draws behind its enabling field, so an unmodernised burst consumes exactly the
historical sequence and same-build determinism holds. Cross-build / cross-version RNG stream identity is not
promised (it never was).

## ScreenShake

A trauma-based, deterministic screen-shake offset generator (absorbed from the retired
`KhaozEngine.Effects` package in 9.0.0). Add trauma on impacts; the shake magnitude falls off as trauma
squared and decays over time. Seeded smooth noise keeps it reproducible and headless-testable (no
`System.Random` / wall-clock). It produces a positional `Offset` and a rotational `Angle` the game
composes onto its render camera:

```csharp
var shake = new ScreenShake();
shake.Add(0.6f);              // on an explosion / hit
// each frame:
shake.Update(dt);
renderCamera.Position = camera.Position + shake.Offset;
renderCamera.Rotation = camera.Rotation + shake.Angle;
```

Render-agnostic by design: this package never references a renderer. The game iterates `system.Active` and
draws each particle (e.g. a Render3D camera-facing billboard). Part of the MonoGame-free engine.
