# Indirect diffuse

Proposed design, 2026-10-24. The owner approved the approach and its physical calibration. Work is tracked in
[#1553](https://github.com/APKiwiOrg/KhaozEngine/issues/1553). The later stages are
[#1554](https://github.com/APKiwiOrg/KhaozEngine/issues/1554).

Every lit surface receives one orientation-independent ambient colour plus a directional fill light. With the
physical atmosphere on, the ambient is the sky irradiance on an upward face and the fill carries the ground
bounce `(direct × GroundAlbedo + bounce)` along a consumer-chosen direction. A face pointing down therefore
receives only the flat sky ambient. Grimhollow draws roofs indoors, and their undersides render at RGB (8,7,12)
at noon. Turning the fill around lights one roof pitch and leaves the other dark, so a single directional fill
cannot stand in for light that arrives from a whole hemisphere.

Version 1 makes indirect diffuse light depend on the surface direction, keeps the physics the atmosphere already
computes, and gives the shaders one slot that a probe volume or ray-traced global illumination can fill later.

| Decision | Contract and reason |
|---|---|
| D1 | Opt in through `PixelPostProcessSettings.IndirectDiffuse`, an `IndirectDiffuseMode` of `Uniform` (default) or `Directional`. `Uniform` leaves the frame data and every pixel byte-identical, which the existing goldens prove. Existing consumers change nothing until they choose `Directional`. |
| D2 | Represent the term as `IrradianceSH`, a public readonly struct of nine RGB coefficients of real second-order (L2) spherical harmonics projecting incident radiance. `Evaluate(normal)` applies the clamped-cosine convolution of Ramamoorthi and Hanrahan (2001) and returns irradiance in the units of `AmbientColor`. L2 reproduces irradiance from any distant lighting within a few percent with nine coefficients, and it is the usual per-probe format, so the later stages reuse it. |
| D3 | `PixelPostProcessSettings.AmbientIrradiance` holds the frame's `IrradianceSH`. With the atmosphere on, the scene writes it every frame (D5). Without the atmosphere the consumer sets it. `IrradianceSH.SkyAndGround(sky, ground)` builds two uniform hemispheres for palette consumers and tests, and `IrradianceSH.Uniform(colour)` builds the isotropic case. |
| D4 | The shared lighting block gains `vec3 indirectDiffuse(vec3 N, vec3 worldPos)`. The six lit fragments (`ModelFrag`, `ModelDissolveFrag`, `SkinnedModelFrag`, `SkinnedModelDissolveFrag`, `SplatFrag`, `TileGroundFrag`) use it where they read `Ambient.rgb` today. In `Uniform` mode it returns `Ambient.rgb` with no arithmetic. In `Directional` mode it evaluates the SH and clamps below at zero. `worldPos` is unused in version 1 and reserved for the probe volume. Water, particles, decals, billboards, outlines and silhouettes are unchanged. |
| D5 | In `Directional` mode the atmosphere projects the sky radiance over the dome above the geometric horizon, and adds a uniform ground radiance below the horizon whose irradiance on a downward face equals today's fill value. `AmbientScale` scales the sky part, `FillScale` scales the ground part and the frame tint applies to both. The moon contributes through the same path scaled by `MoonFraction`. `AtmosphereLighting.Ambient` keeps the upward-face irradiance for consumers that read it. |
| D6 | In `Directional` mode the atmosphere writes zero to `FillLightColor`, because the ground bounce now arrives through the SH and would otherwise count twice. `Uniform` mode keeps today's fill. The fill direction setting remains for consumers without the atmosphere. |
| D7 | The night floor `NightAmbientFloor × NightTint` stays a floor. When the darker of the upward and downward irradiance falls below it, the shortfall is added isotropically, so neither pole is darker than today's floor at night. |
| D8 | `AtmosphereLightingCache` gains a sky SH table per altitude slice and source elevation, projected in a frame with the source at zero azimuth and built once with the existing table. Coefficients that are odd across the source's vertical plane vanish and are not stored. Each frame interpolates the table, rotates the result about the vertical axis by the source azimuth in closed form, and adds the ground and moon terms. The frame path allocates nothing and stays well under 0.1 ms of CPU time. |
| D9 | The frame uniform buffer gains an indirect block holding the mode and the nine coefficients as `vec4` entries. Every shader that declares the frame block mirrors it. The implementation plan fixes the placement after reading the existing layout tests, and a std140 layout test pins the offsets. |
| D10 | Calibration anchors. With the atmosphere on in `Directional` mode, `Evaluate(up)` matches `AtmosphereLighting.Ambient` and `Evaluate(down)` matches the fill value `Uniform` mode would carry, each within 2 percent across source elevations and altitude slices. A consumer that switches modes therefore sees no brightness jump on upward faces, and a downward face receives exactly the bounce it lacked. |

## Units

The coefficients project radiance `L(ω)` onto the real L2 basis with its standard normalisation. `Evaluate(n)`
returns `∫ L(ω) max(dot(n, ω), 0) dω`, scaled by the convolution weights π, 2π/3 and π/4 for bands 0, 1 and 2. That is
the quantity `AtmosphereLightingCache` already integrates for `Ambient`, so both modes share one unit.

The ground is a Lambertian plane with exitance `F`, the fill value. Its radiance below the horizon is `F / π`, so a
face pointing straight down receives `F`. A vertical face receives about half of the ground term and half of the
sky term, as physical light would.

## What changes on screen

`Uniform` mode changes nothing. In `Directional` mode, upward faces keep their brightness by D10. Downward faces
gain the warm ground bounce, which lights roof undersides, eaves and tree canopy undersides evenly. Vertical faces
trade the directional fill for half sky and half ground, so walls facing the old fill direction lose a little and
walls facing away gain a little. Surfaces that received the fill from above, such as roof tops and terrain under a
downward fill, lose that fill. A consumer recovers overall brightness with `AmbientScale`, `FillScale` and the key
light rather than with a non-physical fill.

## Tests

- CPU tests in `KhaozEngine.Render.Tests`:
  - Projection of a uniform sphere evaluates to the same constant in every direction.
  - Two uniform hemispheres evaluate to their analytic values at up, down and horizontal normals.
  - Rotation about the vertical axis matches a direct projection with the rotated source.
  - D10 anchors across source elevations and altitude slices, the night floor of D7 and the zero fill of D6.
  - `Uniform` mode writes frame bytes identical to today.
  - No allocation per frame in `Directional` mode.
- Shader validation compiles every changed vertex and fragment shader through SPIR-V, MSL and HLSL.
- One new golden scene, `scene3d_indirect_directional`, places an open-sided canopy over lit ground at noon so a
  downward face is in view. It is baked on the Metal, Vulkan and D3D11 native backends per `CROSS-PLATFORM.md`.
  Every existing golden stays unchanged.

## Consumer adoption

Grimhollow sets `Directional`, removes its fill direction override, and calibrates `AmbientScale` and `FillScale`
from paired indoor and outdoor stills that its owner approves. Its branch that draws roofs indoors merges after
that adoption. Other consumers are unaffected until they opt in.

## Future stages

[#1554](https://github.com/APKiwiOrg/KhaozEngine/issues/1554) records two later stages, each needing its own spec.

- **Probe volume.** A world-space grid of probes in the same L2 format, sampled by `worldPos` inside
  `indirectDiffuse`. This is what lets interiors receive less sky than open ground. Probes may be baked against
  world geometry or refreshed by ray queries.
- **Ray-traced or path-traced global illumination.** Irradiance computed by tracing fills the same slot. This needs
  acceleration structure and ray query support in the GPU seam, which no backend has today and the D3D11 backend
  never will.

Version 1 guarantees both stages the slot signature, the SH format and the units. The physical derivation in D5
is what a tracer converges to under open sky over flat ground, so the look does not reset when tracing arrives.
This revisits the scope note in [#54](https://github.com/APKiwiOrg/KhaozEngine/issues/54), which kept global
illumination out without a concrete game pull. Grimhollow's indoor lighting is that pull. Specular indirect light
and reflection probes remain out of scope there.

## Out of scope

Specular indirect light, ambient occlusion, interior occlusion before the probe volume, automatic derivation for
palette consumers and water lighting.

## Release

An additive minor version with its `CHANGELOG.md` entry, the consumer contract in `docs/USING-KHAOZENGINE.md`,
the Render3D README and this document indexed in `docs/INDEX.md`. The owner approves the exact tag.
