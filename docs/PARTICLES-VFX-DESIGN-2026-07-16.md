# Particles/VFX Modernization Design

Date: 2026-07-16. Branch: `feature/particles-vfx`. Status: approved for implementation
(autonomous session, owner delegated design authority in the task brief).

## Problem

Every 3D effect the engine can produce today is a uniform camera-facing soft-blob billboard.
`Scene3D.DrawBillboard(worldPos, size, color, blend)` draws an untextured smoothstep disc with no
depth test, after the post chain, and `KhaozEngine.Particles` interpolates size and color linearly
from start to end. Stacked additively these read as 2005-era smoke puffs. Ruinborne's ability VFX
exposed the ceiling. The goal is modern ARPG/MMO-quality spell and combat effects.

Consumer-documented gaps (SpaceGame port notes, Ruinborne registry): no per-particle color
variation within a burst, no velocity tails, no emission shapes, no rate-to-system wiring, no
depth interaction (effects float on top of the world), one blend per draw site.

## Verified current state (exploration summary)

- `KhaozEngine.Particles`: `Particle`, `EmitterConfig` (12 fields, `Spark`/`Puff` presets),
  `ParticleSystem` (fixed arrays, swap-remove, deterministic `XorRng`, `Active` span),
  `RateAccumulator`, `ScreenShake`. Depends only on Primitives. Untouched feature-wise since 5.21.
- Untextured billboards draw post-post at viewport resolution with depth disabled. Textured
  billboards, beams, and trails draw pre-post into the model MRT with depth test on, no write,
  `PreserveDestination` on the normal/depth attachments.
- `RenderResources.DepthColorTex` (R32Float, NDC z after perspective divide) is sampled by
  `GroundDecalRenderer` via `texelFetch` at `gl_FragCoord` with a raw (un-clip-corrected)
  `InvViewProj` world reconstruction. The decal pass draws after `ResolveDepth`, uses one UBO, and
  gates a Full/Reduced quality knob through a uniform float. This is the proven soft-depth recipe.
- `DrawTrail(ReadOnlySpan<TrailSample>, TrailStyle)` and `DrawBeam` already exist with golden
  coverage. `TrailSample(Position, HalfWidth, Alpha)`.
- Hard rules: exactly ONE uniform buffer per pipeline at set 0 binding 0, declared identically in
  both stages (Metal via SPIRV-Cross mis-binds a second UBO). Vertex inputs and fragment
  interpolants must be contiguous and all consumed (D3D11/FXC hole miscompiles). Sample textures
  in binding order. `GpuClip.Correct` for the VP used to position vertices, raw inverse for
  reconstruction. New visual features need a pixel-readback test with `Golden` in the name, plus
  per-backend goldens (metal baked locally, d3d11/vulkan via `cross-platform-gpu.yml`
  workflow_dispatch bake=true). A new packable project must be added to `KhaozEngine.slnx`.
- Backward-compat surface actually exercised by the four games: `ParticleSystem(capacity[,seed])`,
  `Emit(in EmitterConfig, Vector3, int)`, `Update(float)`, `Active`, `ActiveCount`, `Clear()`,
  every `EmitterConfig` field, `Spark`/`Puff`, `Particle.Position/.Size/.Color`,
  `DrawBillboard(Vector3, float, Color, BillboardBlend)` with both blends, `AddLight`. Config
  mutation idioms in the wild: preset-then-field-assign, full object initializer, `with`
  expressions. All must keep compiling and behaving.

## Architecture decisions

### D1: Where the new code lives

Three-package split, following the codified adapter-package rule ("a new simulation-facing edge
belongs behind a seam interface or in an adapter package, not directly on Render3D",
ArchitectureTests) and the existing `Telegraphs.Render3D` / `Terrain.Render3D` precedent:

1. **`KhaozEngine.Particles`** (existing): all sim-side modernization. Stays a Primitives-only,
   render-free, deterministic, headless-testable leaf. No presentation vocabulary enters it.
2. **`KhaozEngine.Render3D`** (existing): a new first-class particle render pass with its own
   drawing vocabulary (`ParticleSprite`, `ParticleShape`, `Scene3D.DrawParticles`). Knows nothing
   about `ParticleSystem`.
3. **`KhaozEngine.Particles.Render3D`** (NEW adapter package): the turn-key glue. Maps
   `ParticleSystem`/`ParticleEffectPlayer` state through a per-emitter `ParticleLook` to
   `DrawParticles`, `DrawTrail`, and budgeted `AddLight`. Ships the modern VFX preset library.
   Depends on Particles + Render3D. Added to the `Game3D` umbrella, `KhaozEngine.slnx`, the README
   catalog, and the ArchitectureTests allow-lists.

Rationale for the adapter over per-game glue: three of four games hand-rolled the same
Active-to-DrawBillboard loop (rule of three met), and the modern loop is bigger (looks, trails,
light budget). Rationale for keeping presentation out of the sim: shape/blend/stretch are
renderer vocabulary. A pool with mixed looks is handled by per-phase pools in the effect player,
not by baking render tags into sim particles.

### D2: Sim API evolution (additive, not parallel)

| Criterion (weight) | Additive evolution of EmitterConfig | Parallel new config + system |
|---|---|---|
| Backward compat risk (x3) | 9 (zero-default = legacy semantics) | 7 (old intact but two systems) |
| Ecosystem coherence (x2) | 9 (one sim, presets keep working) | 3 (fork, old one rots) |
| Design freedom (x1) | 6 (struct grows, defaults constrained) | 9 |
| Maintenance (x2) | 8 | 4 |
| **Total (weighted)** | **67** | **48** |

Additive wins. Every new `EmitterConfig` field defaults to zero and zero means "legacy behavior".
The render side is where the parallel-new-path lives instead (D3), because the legacy overlay
billboard genuinely cannot express soft particles.

New `EmitterConfig` fields (all zero-default = exactly today's behavior):

- `EmissionShape Shape` (`Point=0, Sphere, Hemisphere, Disc`), `float ShapeRadius`,
  `float ShapeShell` (0 = volume, 1 = surface/edge). Disc orients perpendicular to `Direction`
  (or +Y when Direction is ~zero). A ring is Disc with ShapeShell 1, and a cone emitter is Disc
  position plus the existing velocity spread cone, so neither needs its own enum value.
- `ParticleVelocityMode VelocityMode` (`Cone=0` = today's spread cone, `Radial` = outward from
  origin through the spawn point: explosions and shockwaves).
- `float SizeVariance` (0..1, per-particle multiplier jitter on both start and end size).
- `bool VaryColor`, `Color StartColorB`, `Color EndColorB` (per-particle random t blends the A
  and B pairs, classic random-between-two-gradients).
- `bool UseMidColor`, `Color MidColor` (3-stop gradient, mid at norm 0.5).
- `ParticleCurve SizeCurve`, `ParticleCurve AlphaCurve`. `ParticleCurve` is a readonly struct
  `{ ParticleCurveKind Kind, float Param }` with kinds `Linear=0, EaseIn, EaseOut, EaseInOut,
  Flash (fast rise to the Start/End lerp peak then decay, Param = peak time), FadeInOut
  (trapezoid, Param = edge fraction), Pulse (Param = cycle count)`. Curves remap the norm fed to
  the existing Start/End lerp, so Linear (default 0) is bit-identical to today. Static factories:
  `ParticleCurve.EaseOut()`, `ParticleCurve.Flash(0.15f)`, etc.
- `float SpinMin, SpinMax` (rad/s, negatives allowed), `bool RandomStartRotation`.
- `float TurbulenceStrength`, `float TurbulenceFrequency`: a deterministic curl-flavored value
  noise force (pure function of position, system time, and system seed, CPU-side, hash-based, no
  wall clock).

`Particle` gains `float Rotation` and `float Seed` (0..1, derived by hashing a monotonic emit
counter so it consumes NO rng draw). RNG draw discipline: for a config with all new fields at
defaults, `Emit` consumes exactly the historical sequence (life, 2 direction draws, speed) so
same-build determinism and the existing test suite hold unchanged. Every new feature's draws are
gated behind its enabling field. A test pins the legacy draw sequence against a hand-replicated
`XorRng`.

`ParticleSystem`:
- Optional trail history: `ParticleSystem(int capacity, uint seed = 1, int trailSamples = 0)`.
  When enabled, ring buffers (capacity x trailSamples positions + ages) sampled every
  `TrailSampleInterval` (property, default 1/30 s) in `Update`. `RecycleAt` moves the ring block
  with the particle (same parallel-array pattern as the existing bakes). Read API:
  `int GetTrail(int particleIndex, Span<ParticleTrailPoint> dest)` returning oldest-to-newest,
  `ParticleTrailPoint { Vector3 Position, float Age }`. Memory is opt-in (default 0 keeps legacy
  footprint).

Scheduling layer (new, in Particles, headless):
- `ParticleEffectPhase { EmitterConfig Config, float Delay, float Duration, float RatePerSecond,
  int BurstCount, int PoolCapacity }`: Duration 0 with BurstCount > 0 is an instant burst,
  RatePerSecond > 0 streams while active.
- `ParticleEffect`: an immutable list of phases (an authored effect: impact = flash burst + spark
  burst + smoke stream + ring).
- `ParticleEffectPlayer`: owns one pool per phase (mixed looks stay renderable per-phase),
  bounded concurrent instances, `Play(Vector3 origin, Vector3 direction)`, `Update(float dt)`,
  per-phase `Active` access for the adapter. Deterministic from a ctor seed.

`RateAccumulator`, `ScreenShake`, presets `Spark`/`Puff`: untouched.

### D3: Render path (new pass, legacy untouched)

| Criterion (weight) | Extend legacy overlay renderer | New depth-interleaved instanced pass |
|---|---|---|
| Soft particles possible (x3) | 1 (no depth access post-post) | 9 (decal recipe, DepthColorTex) |
| Regression risk to shipped visuals (x3) | 4 (shared pipeline/shader) | 9 (legacy path untouched) |
| Post/bloom integration (x2) | 2 (draws after post) | 9 (pre-post, additive feeds bloom) |
| CPU cost at scale (x1) | 4 (6 verts CPU-expanded each) | 8 (1 instance record, GPU expand) |
| **Total (weighted)** | **23** | **89** |

New internal `ParticleRenderer` in Render3D. `DrawBillboard` and its renderer are not modified at
all (zero visual change, documented as the legacy overlay path with its own niche: crisp
unoccluded markers).

Public vocabulary:
- `enum ParticleShape : byte { SoftGlow = 0, Ember, Spark, Wisp, Ring, Star }`, procedural SDF
  shapes in the fragment shader (see D4).
- `struct ParticleSprite { Vector3 Position; Vector3 Velocity; float Size; float Rotation;
  Color Color; ParticleShape Shape; float ShapeParam; float LifeNorm; float Seed; float Stretch;
  BillboardBlend Blend; }`.
- `Scene3D.DrawParticle(in ParticleSprite)` and `Scene3D.DrawParticles(ReadOnlySpan<ParticleSprite>)`.
- Host-owned knobs (decal-knob precedent, not cleared by `Begin`):
  `Scene3D.ParticleQuality { Full = 0, Reduced = 1 }` and `Scene3D.ParticleSoftFade` (world
  units, default 0.35, 0 disables the depth fade and its texture work).

Mechanics:
- Instanced draw: per-vertex stream = quad corner (float2, 6 verts), per-instance stream =
  18 floats (center, velocity, size+rotation, rgba, shape/param/lifeNorm/seed, stretch+blend).
  All attributes consumed in the vertex shader (D3D11 contiguity), interpolants contiguous from
  location 0 and all read by the fragment.
- Draw position in the frame: after `ResolveDepth` and after water, before the depth-tested debug
  lines, into the same target the decal/water passes use, at internal resolution, so additive
  particles feed bloom and all post. Depth state: test LessEqual, no write. Blend: single
  premultiplied-alpha state `(One, InverseSourceAlpha)`. The fragment emits premultiplied color
  and alpha 0 for additive sprites, so alpha and additive particles interleave correctly in ONE
  back-to-front sorted stream (reuses `TransparencySort`). If the target is the 3-attachment MRT,
  attachments 1 and 2 get `PreserveDestination`.
- One UBO, set 0 binding 0, identical declaration in both stages: clip-corrected `ViewProj`, raw
  `InvViewProj`, camera right/up/pos, time, soft-fade distance, quality flag. Depth texture +
  sampler at bindings 1 and 2 (textures past the first UBO are safe per the seams doc).
- Soft fade: decal recipe verbatim (`texelFetch` at `gl_FragCoord`, NDC reconstruct through raw
  `InvViewProj`, view-space distance delta, `saturate(delta / SoftFade)`).
- Velocity stretch in the vertex shader: project velocity to the camera plane, orient the quad's
  local X along it, elongate by `1 + Stretch * |v_projected| / Size` (clamped). Stretch 0 keeps
  the camera-facing round quad.
- MSAA: same handling as decals (samples the resolved depth, renders into the current target).
- `GpuClip.Correct` on ViewProj, raw inverse for reconstruction, matching decals.

### D4: Procedural SDF shapes over texture atlases

| Criterion (weight) | In-shader SDF/noise | Texture atlas |
|---|---|---|
| Cross-backend golden stability (x2) | 8 (sin-free hash21 idiom already golden-proven in decals) | 9 |
| Asset pipeline required (x2) | 10 (none) | 3 (authoring + shipping + loading) |
| Art flexibility ceiling (x2) | 6 | 9 |
| Fit with engine's procedural-first identity (x1) | 10 | 4 |
| Runtime cost (x1) | 7 (ALU, gated by quality knob) | 8 (bandwidth) |
| **Total (weighted)** | **65** | **54** |

Procedural wins for v1, and the escape hatch already exists: the textured `DrawBillboard`
overloads remain the artist-texture path, documented as such. Shape library (fragment, branch on
shape id, noise = value noise over the decal `hash21` polynomial hash, never sin-based hashing):

- `SoftGlow`: tunable-falloff gaussian-like disc, Param = softness. Default reads like a premium
  version of the legacy blob.
- `Ember`: tight hot core plus warm halo, Param = core fraction, subtle seed+time flicker.
- `Spark`: rounded capsule streak along local X with a bright head and tapered tail, Param = tail
  sharpness. Pairs with velocity stretch.
- `Wisp`: radial falloff modulated by 2-octave value noise, eroded (threshold rises) with
  LifeNorm so smoke dissolves at the edges rather than uniformly fading. Param = erosion bias.
- `Ring`: soft annulus, Param = band thickness. Shockwaves and impact rings.
- `Star`: 4-point anisotropic glint, Param = ray sharpness. Magic sparkles.

Quality knob: `Reduced` drops the second noise octave and the flicker term (uniform-float branch,
one pipeline, decal precedent).

### D5: Adapter package content

- `ParticleLook { ParticleShape Shape; float ShapeParam; BillboardBlend Blend; float Stretch;
  bool Trails; TrailStyle TrailStyle; float TrailWidthScale; float LightRadius;
  float LightIntensity; }` (light fields 0 = no light link).
- `ParticleSceneExtensions`: `DrawParticles(this Scene3D, ParticleSystem, in ParticleLook)` (maps
  Active spans to `ParticleSprite` records, forwards trails via `GetTrail` to `DrawTrail`, and
  when the look has light fields set, adds the top-K brightest particles (intensity x alpha) as
  point lights, K = `ParticleLightBudget` parameter, default 4, always leaving headroom under
  `Scene3D.MaxPointLights`), plus `DrawEffect(this Scene3D, ParticleEffectPlayer,
  ReadOnlySpan<ParticleLook> perPhase)`.
- `VfxPresets`: paired `(ParticleEffect, ParticleLook[])` modern presets used by the showcase and
  as consumer on-ramps: `FireBurst`, `FrostShatter`, `HealMotes`, `EmberDrift`, `SparkShower`,
  `Shockwave`, `SmokePlume`, `ArcaneSparkle`.

### D6: Light-linked particles

Budgeted in the adapter (D5), not in Render3D: `AddLight` is already per-frame immediate-mode
with a 16-light engine cap, so the adapter picks the top-K link candidates and fades intensity
with particle alpha. No sim or renderer change needed beyond what exists.

## Testing

- Headless (Particles): curve evaluation per kind (Linear bit-equals legacy), 3-stop and varied
  gradients, per-shape emission distributions with fixed seeds, radial velocity mode, size
  variance, spin/rotation integration, turbulence determinism (same seed same field), trail ring
  cadence/wrap/swap-remove integrity, effect player scheduling (delay, burst, rate, duration,
  instance cap) and determinism, legacy RNG draw-sequence pin against a hand-replicated XorRng,
  all existing tests unchanged and green.
- Headless (Render3D): instance-record build and sort as pure functions (existing
  TexturedBillboardSort precedent), plus `ShaderValidation.ValidatePair` coverage for the new
  shader pair (CPU cross-compile to HLSL/MSL/SPIR-V, catches the D3D11/Metal landmines without a
  device).
- GpuFact property tests (pixel readback, no goldens): each shape renders non-background, soft
  fade dims a floor-intersecting particle vs a floating one, stretch elongates along motion,
  additive brightens over alpha, quality Reduced still renders.
- `ParticleShowcaseGpuTests` (NO "Golden" in the name): preset grid over a dark floor dumped via
  `PngWriter` to `KE_PNG_DUMP_DIR`, human-reviewed for taste iteration.
- `Golden3D_ParticlesModern` (name carries "Golden"): one deterministic composed frame (fixed
  seeds, pre-stepped sims, frozen `EffectTimeSeconds`), baked on Metal locally, d3d11 + vulkan
  baked via `cross-platform-gpu.yml` workflow_dispatch `bake=true` on this branch, committed, then
  verified green in assert mode via a PR before merge (branch pushes do not run the matrix). The
  existing `CrossBackendGoldenTests` bad-bake net covers the new scene automatically.

## Compatibility statement

- `EmitterConfig`/`ParticleSystem`/`Particle`/`RateAccumulator`/`ScreenShake`: all existing
  members unchanged, all new fields zero-default to legacy behavior, all three observed config
  idioms still compile. Legacy configs keep the exact historical RNG draw sequence in-build.
  Cross-version RNG stream identity is NOT promised (never was) and the CHANGELOG says so.
- `DrawBillboard` (all overloads), `BillboardBlend`, `AddLight`: untouched code paths, identical
  rendering.
- New APIs are purely additive: SemVer minor.

## Documentation sweep (release ritual inputs)

README catalog row + umbrella table (Game3D gains `Particles.Render3D`), new package README,
updated Particles and Render3D package READMEs, `USING-KHAOZENGINE.md` section (authoring an
effect, drawing it, quality knobs, legacy path status), `DEPENDENCY-SEAMS.md` (new adapter edge +
the particle pass's UBO/layout notes), `CHANGELOG.md` entry with the version bump,
`docs/ROADMAP.md` current-version line, `KhaozEngine.slnx` entry, ArchitectureTests allow-lists.

## Execution plan (tiered subagents, no Fable subagents)

1. **P1 sim** (opus): Particles package features + headless tests.
2. **P2 render** (inline, taste-critical per owner approval): shaders, ParticleRenderer, Scene3D
   integration, validation tests. Runs parallel to P1 (different packages).
3. **P3 adapter** (opus): Particles.Render3D package, presets, slnx/catalog mechanics.
4. **P4 visuals** (inline): showcase PNG iteration until it reads modern.
5. **P5 goldens** (sonnet + coordinator-owned CI monitoring): bake matrix, PR, assert green.
6. **P6 docs + release** (opus under supervision): semicolon/em-dash sweep first, then the full
   ritual, next FREE version, verify GitHub Packages.
