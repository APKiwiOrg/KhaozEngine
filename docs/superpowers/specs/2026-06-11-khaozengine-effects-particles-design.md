# KhaozEngine.Effects — pooled particle system (promote Nullwake `HitParticleSystem`)

Status: approved design
Date: 2026-06-11
Batch: 2, item 11 (parallel promotion effort; coordinator owns the 3.3.0 release)

## Goal

Promote Nullwake's `HitParticleSystem` into a new game-agnostic package,
`KhaozEngine.Effects`, generalizing its two baked-in behaviors (sparks, embers)
into **data-driven presets** the caller chooses. Nullwake's current look is
preserved exactly by two built-in presets, so adoption is a near drop-in swap.

## Re-verification (done before design)

- No `KhaozEngine.Effects` (or `.Particles`) package exists. Genuinely new.
- Source `Nullwake.Core/Rendering/HitParticleSystem.cs` still present and matches:
  fixed-size (80) struct pool, ring-buffer cursor, zero-alloc; two behaviors
  (outward sparks, upward-drifting embers with sway); draws filled rects via
  `KhaozEngine.UI.PrimitiveRenderer`; `Update(double realDeltaSeconds)`.
- Two callers: `Nullwake.Core/Screens/GameplayScreen.cs` (`SpawnHitParticles`,
  `SpawnDotParticles`).
- Overlap noted: SpaceGame has its own `ParticleManager` / `Particle` /
  `ParticleEffectType`. Architecturally different (List-based/allocating,
  texture+tail draw, `OnDeath` recursion, 4 named effects). NOT folded in here —
  surfaced to coordinator as a possible future unification item.

## Decisions (locked with coordinator via relay)

1. **API shape**: data-driven config record + built-in presets.
2. **Draw dependency**: depend on `KhaozEngine.UI`, draw via
   `PrimitiveRenderer.DrawFilledRect` (matches original byte-for-byte).
3. **Package name**: `KhaozEngine.Effects` (namespace `KhaozEngine.Effects`).

## Package layout

- New project `KhaozEngine.Effects` (`net10.0`), csproj shaped like
  `KhaozEngine.Time` plus a `ProjectReference` to `../KhaozEngine.UI`.
- Packed `README.md` (`PackageReadmeFile`), `PackageId` = `KhaozEngine.Effects`,
  description line.
- Inter-package dependency via `ProjectReference` (repo convention).
- Wiring (in scope, one-line each): add project to `KhaozEngine.slnx`; add a
  `ProjectReference` to `KhaozEngine.Tests.csproj`.
- **Release-owned by coordinator**: do NOT edit `Directory.Build.props`
  `<Version>`, do NOT add a CHANGELOG entry, do NOT `dotnet pack` into the shared
  `local-feed`. The version is inherited from `Directory.Build.props` (3.2.0 in
  the worktree); the coordinator bumps to 3.3.0 at batch release.

## Public API

```csharp
namespace KhaozEngine.Effects;

public enum ParticleEmission { Radial, Directional }

// All tunables as data. record `with`-expressions let callers derive custom
// presets without engine changes, e.g. `ParticlePresets.Spark with { MaxSpeed = 120 }`.
public sealed record ParticleEmitterConfig
{
    public float MinLife { get; init; }
    public float MaxLife { get; init; }
    public float MinSpeed { get; init; }
    public float MaxSpeed { get; init; }
    public float StartSize { get; init; } = 1f;
    public float EndSizeFactor { get; init; } = 1f;          // 1 = constant; <1 shrinks over life
    public ParticleEmission Emission { get; init; } = ParticleEmission.Radial;
    public Vector2 Direction { get; init; } = new(0f, -1f);  // used when Directional
    public float SpreadRadians { get; init; } = 0f;          // cone half-angle for Directional
    public float JitterX { get; init; }                      // ± spawn offset (half-extent)
    public float JitterY { get; init; }
    public float SwayFrequency { get; init; }                // horizontal sin sway (0 = none)
    public float SwayAmplitude { get; init; }
    public Vector2 Acceleration { get; init; } = Vector2.Zero; // gravity etc.
    public Color? OverrideColor { get; init; }               // if set, ignores Emit's baseColor
    public Color BlendTarget { get; init; } = Color.White;   // else lerp(baseColor, target, amount)
    public float BlendAmount { get; init; } = 0f;
}

public static class ParticlePresets
{
    public static readonly ParticleEmitterConfig Spark; // == Nullwake SpawnHitParticles
    public static readonly ParticleEmitterConfig Ember; // == Nullwake SpawnDotParticles
}

// Read-only snapshot: for headless tests AND custom (non-PrimitiveRenderer) rendering.
public readonly record struct ParticleView(
    Vector2 Position, Vector2 Velocity, Color Color, float Size, float Life, float MaxLife);

public sealed class ParticleSystem
{
    public ParticleSystem(Random rng, int poolSize = 80);
    public int ActiveCount { get; }
    public void Emit(ParticleEmitterConfig config, Vector2 position, Color baseColor, int count);
    public void Emit(ParticleEmitterConfig config, Vector2 position, int count); // baseColor = White
    public void Update(double realDeltaSeconds);
    public void Draw(SpriteBatch spriteBatch, PrimitiveRenderer renderer);
    public IEnumerable<ParticleView> ActiveParticles(); // tests / custom draw; NOT used by hot Draw path
}
```

### Preset values (reproduce Nullwake exactly)

- `Spark`: MinSpeed 40, MaxSpeed 80, MinLife 0.22, MaxLife 0.35, StartSize 2,
  EndSizeFactor 1, Emission Radial, JitterX 3, JitterY 3, BlendTarget White,
  BlendAmount 0.5.
- `Ember`: MinSpeed 15, MaxSpeed 25, MinLife 0.45, MaxLife 0.7, StartSize 3,
  EndSizeFactor 0.3, Emission Directional, Direction (0,-1), SpreadRadians 0,
  JitterX 5, JitterY 3, SwayFrequency 6, SwayAmplitude 8,
  OverrideColor (255,160,40).

Old call → new call mapping (for the later Nullwake adoption item):
- `SpawnHitParticles(pos, oreColor)` → `Emit(ParticlePresets.Spark, pos, oreColor, 4)`
- `SpawnDotParticles(pos)` → `Emit(ParticlePresets.Ember, pos, 3)`

## Behavior

- **Pool**: fixed-size struct array, ring-buffer `_cursor`; emitting past capacity
  overwrites the oldest slots. Zero per-frame allocation. Default size 80 (Nullwake
  value); configurable via constructor. Each particle stores its own behavioral
  params (start size, end-size factor, sway freq/amp, phase, acceleration, color),
  so one system can mix particles spawned from different presets.
- **Emit(config, position, baseColor, count)**: per particle —
  - life = rand[MinLife, MaxLife], speed = rand[MinSpeed, MaxSpeed];
  - velocity: Radial → `angle = rand[0, 2π)`, `(cos,sin)·speed`; Directional →
    `Direction` rotated by `rand[-Spread, +Spread]`, scaled by speed;
  - spawn offset: `x ± JitterX`, `y ± JitterY` (uniform half-extent);
  - color: `OverrideColor ?? Lerp(baseColor, BlendTarget, BlendAmount)`;
  - sway phase: `rand[0, 2π)` (only matters when SwayAmplitude > 0).
- **Update(double realDeltaSeconds)**: for each live particle —
  `vel += Acceleration·dt; pos += vel·dt`; if `SwayAmplitude > 0`,
  `x += sin(elapsed·SwayFrequency + phase)·SwayAmplitude·dt` where
  `elapsed = MaxLife − Life`; `Life -= dt`, recycle at `Life ≤ 0`. Uses real delta
  (not sim delta) — same intent as the original (particles stay smooth regardless
  of game speed).
- **Draw(spriteBatch, renderer)**: thin shim. `alpha = Life/MaxLife`;
  `size = StartSize·(EndSizeFactor + (1−EndSizeFactor)·t)` with `t = Life/MaxLife`;
  `pixelSize = max(1, round(size))`; `renderer.DrawFilledRect(centeredRect, Color·alpha)`.
  Iterates the internal array directly (no enumerator alloc).
- **ActiveParticles()**: yields a `ParticleView` per live particle, exposing the
  current computed `Size` and stored base `Color`/`Velocity`/`Life`/`MaxLife`.
  For headless assertions and any caller that wants custom rendering. Not used by
  the built-in `Draw` hot path.

## Tests (`KhaozEngine.Tests`, headless, fixed dt)

New file `ParticleSystemTests.cs`. Seed a `new Random(<fixed>)` for determinism.

1. **Spawn**: `Emit(Spark, pos, color, 5)` → `ActiveCount == 5`.
2. **Age-out**: after enough `Update(dt)` to exceed MaxLife, `ActiveCount == 0`.
3. **Recycle / cap**: emit `poolSize + N` → `ActiveCount == poolSize` (cursor wraps,
   oldest overwritten).
4. **Radial spread**: spark particles get non-parallel velocities; positions leave
   the spawn point after `Update`.
5. **Directional ember**: with a fixed-up preset, mean Y decreases (rises) and X
   moves off-axis over several updates (sway).
6. **Acceleration (new behavior)**: a config with non-zero `Acceleration` changes a
   particle's velocity across updates (vs zero-accel control).
7. **Color**: spark `ParticleView.Color == Lerp(base, White, 0.5)`; ember
   `Color == (255,160,40)` regardless of `baseColor`.
8. **Size curve**: ember `ParticleView.Size` decreases toward `StartSize·0.3` as
   life drops; spark `Size` stays `StartSize`.

The SpriteBatch `Draw` path stays an untested thin shim (per item brief — keep the
SpriteBatch draw a shim; test the update/pool logic).

## Out of scope

- Nullwake call-site migration (separate adoption item).
- Unifying SpaceGame's `ParticleManager` (texture/tail/OnDeath) — surfaced to
  coordinator, not done here.
- Version bump / CHANGELOG / pack (coordinator owns 3.3.0 batch release).

## Open questions for coordinator

- Approve the public API surface above — `ParticleEmitterConfig` fields,
  `ParticleView`, `ActiveParticles()` — since Nullwake adoption and future callers
  bind to it.
- SpaceGame overlap: keep separate (recommended) or schedule a future unify item?
