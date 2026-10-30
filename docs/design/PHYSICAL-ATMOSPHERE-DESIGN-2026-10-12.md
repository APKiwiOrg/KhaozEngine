# The physical atmosphere program

Design rationale for an opt-in physical sky, distance fog, volumetric clouds, cloud shadows and a night sky.
Program issues: phase 1 [#1366](https://github.com/APKiwiOrg/KhaozEngine/issues/1366), phase 2
[#1367](https://github.com/APKiwiOrg/KhaozEngine/issues/1367), phase 3
[#1368](https://github.com/APKiwiOrg/KhaozEngine/issues/1368), phase 4
[#1369](https://github.com/APKiwiOrg/KhaozEngine/issues/1369). Deferred siblings: the aerial perspective volume
[#1364](https://github.com/APKiwiOrg/KhaozEngine/issues/1364) and eye adaptation
[#1365](https://github.com/APKiwiOrg/KhaozEngine/issues/1365). Consumer origin is
[Grimhollow#490](https://github.com/APKiwiOrg/Grimhollow/issues/490), with server weather in
[Grimhollow#491](https://github.com/APKiwiOrg/Grimhollow/issues/491).

This document is the full design for phase 1. Its milestones live in the `atmosphere` program in the
owner's program store. Phases 2 to 4 are outlined here and get their own design section, reviewed by the
owner, before each is planned.

## The problem

The engine sky (`SkySettings`, `SkyFrag`, `SkyMath`) is a two-colour ramp between a horizon and a zenith colour
with a sun disc, a halo and extra discs. Games feed it hand-calibrated day, dusk and night palettes through
`SunCycle`, and the same palettes set the key light, fill and ambient. There is no scattering model, no cloud of
any kind and no fog term anywhere in the engine ([#45](https://github.com/APKiwiOrg/KhaozEngine/issues/45)).

Grimhollow's owner asked for a higher fidelity sky with a smooth atmospheric gradient and moving realistic
clouds. Grimhollow uses the screen-space ramp, so its horizon colour does not stay on the horizon when the camera
pitches, and nothing hides the edge of the world.

## Owner decisions, 2026-10-12

| # | Decision | Reason recorded |
| --- | --- | --- |
| D1 | The sky, clouds and fog are engine work, not Grimhollow code. | Rendering defaults to the engine, the sky is an engine-internal far-plane pass the game cannot reach, and Ruinborne benefits. |
| D2 | Volumetric clouds, chosen over a painted cloud layer and low-poly puffs from live WebGL mockups at noon, dusk and night. | The request was for realistic clouds. |
| D3 | A physical scattering model drives the sky, not artist palettes. | The owner wants to keep path tracing and other physically based lighting open. Those consume sky radiance in physical units, and a palette sky would have to be rebuilt and retuned at that point. |
| D4 | The program covers distance fog, cloud shadows, server weather and a night sky as well. | Owner scope choice. |
| D5 | Each engine phase is released and adopted by Grimhollow before the next, so each phase is seen in game. | Owner choice over one batched release. |
| D6 | Phase 1 uses LUTs with analytic fog (approach A below). The aerial perspective volume is filed as #1364 so it is not lost. | Lowest engine cost for nearly all of the quality, and the upgrade to the volume is additive. |
| D7 | The physical model leads the look. Art overrides only keep a game's mood. | Owner look target for Hollowmere. |
| D8 | Eye adaptation is out of scope and filed as #1365. | Fixed exposure is kept, with an art-scaled moon. |

## The program

| Phase | Repository | Content | Depends on | Issue |
| --- | --- | --- | --- | --- |
| 1 | KhaozEngine | Physical sky, lighting derived from it, water reflection, analytic distance fog | none | #1366 |
| 2 | KhaozEngine | Volumetric clouds lit by the atmosphere | 1 | #1367 |
| 3 | KhaozEngine | Cloud shadows on world geometry | 2 | #1368 |
| 4 | KhaozEngine | Stars composited under the atmosphere | 1 | #1369 |
| 5 | Grimhollow | Adoption after each engine release | each release | Grimhollow#490 |
| 6 | Grimhollow | Server-held weather driving cloud coverage and wind | 2 in Grimhollow | Grimhollow#491 |

Each engine phase rides the staged engine version at merge time, or cuts one, and is released through a
KhaozEngine-rooted session when the owner starts the release.

## Phase 1, alternatives weighed

**A. LUT sky with analytic fog (chosen).** Transmittance, multi-scattering and sky-view lookup textures after
Hillaire 2020. Fog is an analytic exponential height integral per fragment, coloured from the sky-view LUT. A CPU
mirror of the model drives the lighting.

**B. Full Hillaire with an aerial perspective volume.** The same sky, with fog read from a camera-aligned froxel
volume of about 32 cubed. More accurate over long distances, at low sun and at altitude. `GpuTextureDescription`
offers `Texture2D` and `Texture2DArray` only, so it first needs 3D textures on Metal, D3D11 and Vulkan.

**C. Analytic sky (Hosek-Wilkie or Preetham).** Cheapest. Daytime only, it breaks at twilight and at night, and it
gives fog and clouds nothing consistent to light from.

| Criterion | A | B | C |
| --- | --- | --- | --- |
| Sky and twilight quality | 8 | 9 | 5 |
| Fog consistency with the sky | 8 | 9 | 4 |
| Readiness for path tracing and probes | 8 | 9 | 3 |
| Engine cost, new GPU features | 8 | 4 | 9 |
| Runtime cost | 9 | 7 | 10 |
| Testability through a CPU mirror | 9 | 7 | 8 |
| **Total** | **50** | **45** | **39** |

A to B is additive. The fog chunk keeps its interface and swaps its colour and transmittance source for the volume
once 3D textures exist, which phase 2 may want anyway.

## Phase 1, public shape

All new types live in `KhaozEngine.Render3D`.

**`AtmosphereSettings`**, reached as `PixelPostProcessSettings.Atmosphere` next to `Sky` and `Water`. `Enabled`
defaults to false, so every existing scene, Ruinborne included, renders as before.

- Physical parameters with an Earth preset from Hillaire 2020: planet radius, atmosphere height, Rayleigh
  scattering and scale height, Mie scattering, extinction, anisotropy and scale height, ozone absorption, ground
  albedo.
- Light sources: sun illuminance, and moon illuminance as an art-scaled fraction of the sun, not the physical
  ratio of about one in 400,000.
- World mapping: world units per kilometre, default 1000 (one unit is one metre), and an aerial distance scale.
  A playable map is a few kilometres across, where real air is close to invisible, so the scale compresses
  distance and scale heights together for the fog.
- Art overrides: day, dusk and night tints evaluated on the elevation weights `SunCycle` already uses to blend
  its palettes, ambient scale, fill scale, a night ambient floor, a night sky floor (airglow) and a haze density
  with its own height falloff for extra mist. Tints multiply everything the atmosphere outputs (sky, fog colour,
  key light, ambient and fill), so sky and lighting never disagree. Ambient and fill scale touch lighting only.

**`AtmosphereModel`**, a public CPU implementation of the same model the shaders evaluate: transmittance, single
scattering, the multi-scattering approximation, sky radiance along a direction and the derived lighting. It is the
test oracle, and it drives the lighting so nothing is read back from the GPU. Shader functions name the C# method
they mirror, the convention `SkyMath` and `SkyFrag` already follow.

**`AtmosphereRenderer`**, internal. It owns the LUT render targets and passes, the atmosphere UBO and the binding
shared with every participating pipeline.

**`SunCycle` integration.** `SunCycle` keeps owning the clock, latitude, solar declination, heading, the sun and
moon directions, `NightKeyMode` and the key source handover. When `Atmosphere.Enabled` is true,
`SunCycle.Apply` takes the key light colour, fill and ambient from `AtmosphereModel` instead of the palettes, and
the palettes are ignored. The XML docs say so. Lighting depends on camera altitude, so an additive overload
`SunCycle.Apply(in SunCycleState, PixelPostProcessSettings, Vector3 cameraWorldPosition) -> SunCycleState` returns
the state with the atmosphere's horizon colour in the existing `HorizonColor` property, and a game keeps feeding
`Water.HorizonColor` from it. The existing void `Apply` stays and evaluates at world height zero.

### Units

The model computes radiance relative to the sun's illuminance at the top of the atmosphere. The engine shades a
Lambert surface as `albedo * LightColor * NdotL` with no `1/pi`, so one conversion keeps everything on one scale.
With `S` the art-scaled sun illuminance:

```
LightColor = S * T_sun                    (transmittance along the sun direction at the camera)
Ambient    = S * E_sky(up)                (sky irradiance on an upward-facing surface)
SkyValue   = S * pi * L_sky(direction)    (sky radiance in the engine's Lambert convention)
```

A white surface lit by the noon sun and the noon sky behind it then sit on one physical scale. HDR exposure and
ACES tonemapping handle brightness as they do today. That consistency is the property a later path tracer or
probe system needs (D3).

## Phase 1, rendering

### Lookup textures

Half-float render targets drawn by fullscreen fragment passes, the pattern the engine already uses.

| LUT | Size | Rebuilt when |
| --- | --- | --- |
| Transmittance | 256 by 64 | atmosphere parameters change |
| Multi-scattering | 32 by 32 | atmosphere parameters change |
| Sky-view | 192 by 108, latitude mapped non-linearly for horizon detail | every frame, before the scene |

The sky-view LUT accumulates single scattering from both the sun and the moon plus multi-scattering, so twilight
with the sun just set and the moon up needs no special case. It depends on the camera altitude through the world
mapping.

### Sky pass

The sky pass stays in `DrawBackground` as the far-plane, Equal-depth-test pass and branches to the atmosphere when
it is enabled.

- Sky radiance comes from the sky-view LUT. Below the horizon it shows the planet's ground lit through the
  atmosphere, which replaces the `SkyGround` band in this mode.
- The sun disc gets limb darkening and is coloured by transmittance along its direction. It is HDR bright, so the
  existing bloom catches it. The moon keeps the existing extra-disc path, coloured by its own transmittance.
- The atmosphere is world-anchored by nature, so it is evaluated along the view ray whatever `SkyHorizon` says.

### Water

`skyAlongDirection` in `WaterFrag` gets an atmosphere branch that samples the sky-view LUT, so reflections match
the sky. `SkyMath`'s water mirror gains the same branch through `AtmosphereModel`.

### Distance fog

One shared GLSL chunk, `AtmosphereFogGlsl`, with its bindings chunk, spliced by string concatenation the way
`LightingCommonGlsl` is.

- **Transmittance.** Optical depth from the camera to the fragment through exponential height densities: the
  atmosphere's ground-level Rayleigh and Mie extinction and the haze term, each with its scale height, all scaled
  by the aerial distance scale. Each exponential term has a closed-form integral along the segment, with the
  equal-height limit handled. `T = exp(-tau)` per channel.
- **Near onset (20.40.0, #1406).** `AerialOnsetDistance` `s`, in world units, replaces the segment distance `d`
  with `d * d / (d + s)` before the kilometre scaling, in every closed-form term. It has zero value and slope at
  the camera, never exceeds `d` and tends to `d - s`. Heights are not shaped, so the height falloff keeps its
  meaning, and a horizontal ray still reaches `T = 0` at infinity. Plain Beer-Lambert grows linearly from the
  camera, so a haze tuned to dissolve the world edge also put 11 to 25 percent fog on anything 25 to 50 m away.
  An 80 m onset leaves 3 percent at 25 m while the edge keeps 83 to 93 percent. The default 0 keeps the linear
  distance exactly.
- **Colour.** The sky-view LUT sampled along the view direction, with elevation clamped at the horizon for
  downward views, then tinted. The result is `colour * T + haze * (1 - T)`.
- **Downward phase ratio (20.40.0, #1406).** The clamped sample is the horizon texel at the view's azimuth, so
  toward a low sun it carries the Mie aureole at the horizon's scattering angle. A ray 30 to 60 degrees down has
  a true angle 30 to 60 degrees wider, and the glow was 6 to 10 times too bright. Below the horizon the sample is
  scaled per channel by `R = S(true) / S(sample)`, where
  `S(mu) = Tsun * (sigmaR * Prayleigh(mu) + sigmaM * Pmie(mu)) + f * Tmoon * (...) + Psi * (sigmaR + sigmaM)`.
  That is the in-scatter of a homogeneous slab of the Earth air at the camera's scaled altitude, with the moon key
  included because the LUT also holds it. `Tsun` and `Tmoon` are the lighting's source transmittances already in
  the UBO, `f` is the moon fraction and `Psi` the sun's plus `f` times the moon's multiple-scattering source at
  the physical camera altitude. The slab's extinction cancels in the ratio, so the LUT keeps its exact colour at
  the horizon. Against that slab at the true angle the fog colour is within 6 percent of luma for views 10 to 60
  degrees down at dawn, morning and noon, the same agreement the LUT has with the slab at the horizon. `R` is
  exactly one at and above the geometric horizon. The true angle eases in over one final sky texel span below the
  horizon, mirroring the sky side's last-cell easing, so the downward colour leaves the horizon with zero slope
  and the 20.39.0 eye-level join is unchanged. In vacuum `R` is one.
- **Why that colour.** A horizontal ray at infinite distance has `T = 0`, so distant terrain converges exactly to
  the sky behind it and the horizon has no seam. The forward-scattering glow toward a low sun comes from the LUT
  on horizontal rays and from the phase ratio below them. Two known errors remain with the volume in #1364: haze
  in deep shadowed valleys under a low sun is too bright, because the in-scatter is not shadowed by geometry, and
  there is no separate ground-mist layer with its own base height. Haze extinguishes only and takes the Earth
  air's colour.
- **Where it applies.** `ModelFrag`, `ModelDissolveFrag`, `SkinnedModelFrag`, `SkinnedModelDissolveFrag`,
  `SplatFrag`, `TileGroundFrag`, the foliage path, `WaterFrag`, `DecalFrag`, `BillboardFrag`,
  `TexturedBillboardFrag`, `ParticleFrag`, `TrailFrag` and `BeamFrag`. Debug lines and `DistortionFrag` are left
  alone.
- **Blend handling.** Opaque and alpha-blended draws use the full blend. Decals fog their own colour so their blend
  over an already fogged surface stays correct. Additive effects are attenuated by `T` only, so sparks do not turn
  into glowing fog.
- **Bindings.** A small atmosphere UBO plus the sky-view LUT at one binding shared by every participating
  pipeline. Since 20.40.0 the UBO ends with `FogScatter` (Rayleigh and Mie scattering at the camera's scaled
  altitude), `FogSource` (the multiple-scattering term and the onset) and `FogHorizon` (the sine and cosine of the
  fog's horizon angle, a per-frame constant of the phase ratio), 368 bytes in all. The exact slots are fixed in the plan against each pipeline's layout. When the atmosphere is off, a
  uniform flag makes the chunk a no-op, so there are no pipeline permutations.

## Phase 1, lighting

- **Key light.** By day, sun illuminance times transmittance along the sun direction times the current tint. It
  goes from warm white at noon to deep orange at sunset with no palette, and it reaches zero as the sun sets behind
  the planet. By night under `NightKeyMode.Moon`, the moon takes the key through the existing handover, as moon
  illuminance times its transmittance times the night tint. Light direction, shadows and handover timing stay
  exactly as `SunCycle` does them.
- **Ambient.** Sky irradiance on an upward-facing surface, integrated over the hemisphere from the sun-lit and the
  moon-lit sky, times ambient scale, clamped up to the night ambient floor. Shadows turn blue at noon and violet at
  dusk without tuning.
- **Fill.** Ground bounce: ground irradiance from the sun and sky times ground albedo times fill scale. The fill
  direction stays as `SunCycle` sets it today.
- **Night.** A moonlit sky comes out physically as a dim blue day sky. The night tint adds the cool, desaturated
  look the eye expects, and the night sky floor keeps a moonless sky from going pure black. Stars are phase 4.
- **Caching.** The multi-scattering table and a one-dimensional table of ambient, fill and horizon colour against
  sun elevation are built on the CPU when the settings change, which costs a few milliseconds once. Per frame the
  CPU does two transmittance integrals (sun and moon) and table lookups. `SunCycleState.HorizonColor` is the mean
  of a ring of horizon directions.
- **Exposure.** Real day and night differ by about six orders of magnitude and the engine has fixed exposure. The
  art-scaled moon illuminance and the night floors let one exposure work around the clock (D8). Defaults for both
  are chosen in the plan so the Earth preset reads at the default exposure day and night.

## What phase 1 does not change

Shadows, the lighting model, TAA, the pass order and the starfield. Issue
[#1153](https://github.com/APKiwiOrg/KhaozEngine/issues/1153) (the sky painting over billboards, beams and trails)
sits in the same pass position and is neither fixed nor worsened.

## Phase 1, budget

The transmittance and multi-scattering LUTs rebuild only on settings changes. Per frame the cost is the sky-view
pass of about 20,000 texels and, per fogged fragment, a few `exp` calls and one texture sample. The expectation is
well under 0.3 ms on Apple silicon. The plan measures it once on the development Mac through `build-slot`.

## Phase 1, testing

All in `KhaozEngine.Render.Tests`.

- `AtmosphereModel` CPU tests: Earth-preset transmittance against values recorded from Hillaire's reference
  implementation, the key light reddening monotonically as the sun descends and reaching zero behind the planet,
  ambient never below the floor, the horizon colour continuous across the sun to moon handover, fog transmittance
  of one at zero distance and zero for a horizontal ray at infinity, and the fog colour converging to the sky at
  the horizon.
- LUT parameterisation round trips for every UV to height and angle mapping the shaders mirror.
- Shader source validation for every shader the fog chunk is spliced into, on all three backends.
- UBO layout tripwires for the atmosphere UBO and for the changed sky and water blocks.
- Goldens: noon, sunset, moonlit night and fog over distant terrain, plus one golden proving a scene with the
  atmosphere off renders as before.

If code is ported from the reference implementation, its attribution goes in `NOTICE.md`.

## Phase 1, docs and release

This design doc indexed in `docs/INDEX.md`. A `CHANGELOG.md` entry riding the staged version at merge time. The
public API in `docs/USING-KHAOZENGINE.md`. Closing #45 and updating
[#50](https://github.com/APKiwiOrg/KhaozEngine/issues/50). The release goes through a KhaozEngine-rooted session
when the owner starts it.

## Grimhollow adoption of phase 1

Recorded here for the program and executed in Grimhollow under Grimhollow#490: the engine bump checklist in
Grimhollow's `AGENTS.md`, `HollowmereViewer` opting in, `HollowmereLighting` replacing its palettes with
`AtmosphereSettings` and Hollowmere's mood tints (a slightly cool noon and warm dusks) while keeping latitude,
declination and heading, `Water.HorizonColor` from the atmosphere, fog and the aerial distance scale tuned so the
world edge dissolves, showcase and crowd shots opting in while icon and portrait captures stay without it, and
`SHADOWS-DAY-NIGHT-DESIGN` updated. The owner reviews the look from side by side previews and then one live proof.

## Later phases, outline

**Phase 2, volumetric clouds (#1367).** A ray-marched cloud layer with altitude, thickness, coverage, cloud type,
wind and seed. Rendered at reduced resolution with its own temporal accumulation, independent of TAA jitter, and
composited in the background pass. Lit by the atmosphere: sun transmittance colour on the tops, sky ambient by
height, a two-lobe phase function and a powder term, then aerial perspective toward the horizon. Time is supplied
by the caller, so a networked game can drive it from a server clock and every client draws the same sky. Step
counts and resolution scale through `RenderQuality`. Open for its design section: the cloud shape noise source
and whether it is baked once on the CPU or generated on the GPU, how clouds meet terrain that rises into the layer,
and whether the volume in #1364 lands first.

**Phase 3, cloud shadows (#1368).** Cloud density projected along the sun direction into a world-aligned shadow
texture around the camera, sampled by the shared lighting chunk to attenuate the key light.

**Phase 4, night sky (#1369).** Stars composited under the atmosphere, fading with sky brightness and
transmittance through twilight. `DrawBackground` draws either the sky or the starfield today, so this is engine
work.

**Phase 6, server weather (Grimhollow#491).** Game and server work. The engine only exposes the coverage and wind
inputs from phase 2.
