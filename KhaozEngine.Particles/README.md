# KhaozEngine.Particles

Pure, deterministic, headless-testable particle simulation for the MonoGame-free 5.x stack
(`System.Numerics` + BCL only, no Veldrid/MonoGame, no project refs).

- `ParticleSystem` — capacity-bounded pool. `Emit` a burst, `Update(dt)` to age/integrate/interpolate/recycle.
  Dead particles are swap-removed so `Active` is a contiguous `ReadOnlySpan<Particle>` of the live prefix.
- `Particle` — live interpolated state (`Position`/`Velocity`/`Age`/`Life`/`Size`/`Color`) a renderer reads.
- `EmitterConfig` — spawn params (lifetime/speed ranges, cone `Direction` + `SpreadDegrees`, `Gravity`,
  `Drag`, start/end size + color). `Spark` and `Puff` static defaults.
- `RateAccumulator` — turns a per-second rate into an int emit-count per frame, carrying the fractional remainder.

Determinism: an internal xorshift32 RNG seeded by the ctor `seed`. Two systems with the same seed and the
same `Emit`/`Update` calls produce identical particles. No `System.Random`, no wall-clock.

Render-agnostic by design: this package never references a renderer. The game iterates `system.Active` and
draws each particle (e.g. a Render3D camera-facing billboard). Part of the post-MonoGame 5.x line; see
`docs/ROADMAP.md` ("The post-MonoGame pivot").
