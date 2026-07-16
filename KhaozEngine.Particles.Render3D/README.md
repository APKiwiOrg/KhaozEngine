# KhaozEngine.Particles.Render3D

The 3D arm of the KhaozEngine particle system: turn-key glue between the render-free
`KhaozEngine.Particles` simulation and Render3D's modern particle pass. `Scene3D` extension methods
map a `ParticleSystem` (or a whole `ParticleEffectPlayer`) through a per-emitter `ParticleLook` to
`DrawParticle`, `DrawTrail`, and budgeted `AddLight`. Kept separate from `KhaozEngine.Particles` so a
headless server or a 2D game never drags in Render3D. Presentation only, holds no sim state.

The sim stays render-free. Shape, blend, stretch, trails and light links are renderer vocabulary, so
they live in `ParticleLook` here, not baked into the particle. A pool with mixed looks is handled by
giving each phase of an effect its own look.

## Extensions on `Scene3D`

Both live in the `KhaozEngine.Particles` namespace on purpose, so the `using` you already have for
`ParticleSystem` and `EmitterConfig` brings the extensions and the presets into scope too.

- `scene.DrawParticles(system, in look, lightBudget = 4)` queues every live particle in one
  `ParticleSystem` as a modern sprite. Forwards trails and links lights when the look asks for it.
- `scene.DrawEffect(player, looks, lightBudget = 4)` draws every phase of a playing
  `ParticleEffectPlayer`, one `ParticleLook` per phase. `looks.Length` must equal `player.PhaseCount`.
  The one `lightBudget` is shared across the whole effect and spent phase by phase.

Immediate-mode: call once per frame inside the 3D pass.

## `ParticleLook`

The per-emitter presentation recipe:

- `Shape` and `ShapeParam`: the procedural sprite shape and its [0,1] tuning knob.
- `Blend`: alpha or additive compositing.
- `Stretch`: velocity-stretch factor. 0 keeps a round camera-facing quad, larger elongates along motion.
- `Orientation`: `CameraFacing` (default) billboards toward the camera, or `FlatGround` lies flat in the
  ground (XZ) plane for shockwave rings and ground glows.
- `SoftFadeScale`: per-sprite multiplier on `Scene3D.ParticleSoftFade` (0 means 1, the default). A
  `FlatGround` look wants a small value (around 0.1) so the floor immediately behind the coplanar quad does
  not fade it out, and dense smoke can raise it for a longer, softer approach.
- `Trails` and `TrailStyle`: when `Trails` is true and the pool has trail capacity, each particle's
  motion history is forwarded as a tapered ribbon to `Scene3D.DrawTrail`.
- `TrailWidthScale`: trail half-width as a multiple of the particle's size (scaled down the tail).
  `<= 0` is treated as 0.5.
- `LightRadius` and `LightIntensity`: `LightRadius > 0` with a positive `LightIntensity` links the
  brightest live particles as budgeted point lights. Per-particle intensity is scaled by the particle's
  alpha, so a fading particle dims its light. 0 disables the light link.
- `Flipbook`: an optional `ParticleFlipbook` (atlas `TextureHandle`, `Columns` x `Rows` grid, optional
  motion-vector sheet + `MotionStrength`, `Loop`). Default (an invalid texture) keeps the look on the
  procedural `Shape` path. When set, the adapter resolves each sprite's `FlipbookFrame` and the atlas frame
  replaces the procedural shape. Load the sheets with `Scene3D.LoadTexture`.
- `FlipbookMode`: `LifeOneShot` (default) sweeps the sheet once across a particle's life and clamps on the
  last cell (one-shot explosion/impact sheets), or `TimeLoop` advances at `FlipbookFps` and wraps (looping
  fire/smoke). Only read when `Flipbook` is active.
- `FlipbookFps`: playback rate for `TimeLoop`, in frames per second. 0 is treated as 12. Ignored by
  `LifeOneShot`.
- `FlipbookRandomStart`: for `TimeLoop`, staggers each particle's start frame by its seed so a burst of
  identical looping sprites does not play in lockstep. Defaults to true (set false for a synchronized loop).
- `Distortion`: an optional `DistortionLook` (`Shape`, `ShapeParam`, `Strength`, `SoftFadeScale`). When
  `Strength` is non-zero the phase emits one `DistortionSprite` per live particle INSTEAD of a visible sprite, so
  it WARPS the scene behind it (heat haze, refractive shockwave ring, splash lens) rather than drawing over it.
  Each sprite's strength is scaled by the particle's alpha so the field fades with life. The inactive default
  (`Strength` 0) keeps the phase on the normal `Shape` / `Flipbook` particle path. See the Render3D distortion
  pass in `docs/USING-KHAOZENGINE.md`.

```csharp
var look = new ParticleLook
{
    Orientation = ParticleOrientation.FlatGround,
    Distortion = new DistortionLook
    {
        Shape = DistortionShape.Ripple, ShapeParam = 0.15f, Strength = 1.5f, SoftFadeScale = 0.12f,
    },
};
```

Trails are sampled oldest-to-newest, so the tail is faintest and thinnest and the head is full. Light
selection is a small-K partial selection by alpha, no allocation, and never exceeds `Scene3D.MaxPointLights`.

A flipbook look, motion-vector sheet optional (a motion sheet reads fluid at low frame counts, a plain
sheet cross-fades between frames):

```csharp
Scene3D.TextureHandle atlas = scene.LoadTexture(sheetRgba, 512, 512);   // an 8x8 grid = 64 frames

var look = new ParticleLook
{
    Blend = BillboardBlend.Additive,
    Flipbook = new ParticleFlipbook(atlas, Columns: 8, Rows: 8, Loop: true),
    FlipbookMode = ParticleFlipbookMode.TimeLoop,
    FlipbookFps = 24f,
};
scene.DrawParticles(system, in look);
```

The `Shockwave` preset is the worked orientation example: its ring look is `FlatGround` with a small
`SoftFadeScale` (0.12), and the ring phase lifts off the floor with `ParticleEffectPhase.OriginOffset`
(y 0.09) so a quad exactly coplanar with the ground is not erased by the soft depth fade.

## `VfxPresets`

Modern ready-to-use presets, each a `VfxPreset` (a `ParticleEffect` paired with one `ParticleLook` per
phase). Each property returns a fresh instance per call, so a caller can mutate what it gets. Authored to
read at roughly 8 to 12 world units from the camera, origin on the ground, +Y up.

- `FireBurst`: a punchy impact. White flash, radial sparks, light-linked embers, short smoke puff.
- `FrostShatter`: an icy shatter. Star and spark shards, an expanding ice ring, a faint mist.
- `HealMotes`: gentle rising green-gold star sparkles that softly light the target.
- `EmberDrift`: ambient warm embers drifting up on turbulence (braziers, campfires).
- `SparkShower`: a single fountain of stretched sparks arcing up and falling.
- `Shockwave`: a fast expanding ground ring plus a low outward puff of dust, with a refraction-ring phase that
  warps the scene behind the nova (an active `Ripple` distortion look).
- `SmokePlume`: a steady rising column of soft, turbulent smoke.
- `ArcaneSparkle`: swirling violet-to-cyan magic sparkles that pulse and faintly light the caster.
- `HeatHaze`: a shimmering heat haze, a slow rising column that warps the scene (a `Heat` distortion) under a
  faint warm additive shimmer. For braziers, lava, desert air, exhaust.

## Usage

```csharp
using KhaozEngine.Particles; // sim + adapter + presets, one namespace

// Author once from a preset (or build your own ParticleEffect + looks):
VfxPreset preset = VfxPresets.FireBurst;
var player = new ParticleEffectPlayer(preset.Effect, maxInstances: 8, seed: 1);
ParticleLook[] looks = preset.Looks.ToArray();

// On a hit:
player.Play(impactPoint, Vector3.UnitY);

// Each frame:
player.Update(dt);
scene.DrawEffect(player, looks);
```

Depends on `KhaozEngine.Particles` + `KhaozEngine.Render3D`. In the `Game3D` umbrella.
