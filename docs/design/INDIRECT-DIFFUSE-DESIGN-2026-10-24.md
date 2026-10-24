# Indirect diffuse

Accepted implementation contract for 21.13.0, 2026-10-24.

The owner approved the approach and its physical calibration. Work is tracked in
[#1553](https://github.com/APKiwiOrg/KhaozEngine/issues/1553). The later stages are
[#1554](https://github.com/APKiwiOrg/KhaozEngine/issues/1554).

Before 21.13.0, every lit surface received one orientation-independent ambient colour plus a directional fill
light. With the physical atmosphere on, the ambient was the sky irradiance on an upward face and the fill carried
the ground bounce `(direct × GroundAlbedo + bounce)` along a consumer-chosen direction. A face pointing down
therefore received only the flat sky ambient. Grimhollow draws roofs indoors, and their undersides render at RGB (8,7,12)
at noon. Turning the fill around lights one roof pitch and leaves the other dark, so a single directional fill
cannot stand in for light that arrives from a whole hemisphere.

Version 1 makes indirect diffuse light depend on the surface direction, keeps the physics the atmosphere already
computes, and gives the shaders one slot that a probe volume or ray-traced global illumination can fill later.

| Decision | Contract and reason |
|---|---|
| D1 | Opt in through `PixelPostProcessSettings.IndirectDiffuse`, an `IndirectDiffuseMode` of `Uniform` (default) or `Directional`. `Uniform` leaves frame bytes `[0, 1376)` unchanged and the new indirect tail zero, with every existing pixel byte-identical as the existing goldens prove. Existing consumers change nothing until they choose `Directional`. |
| D2 | Represent the term as `IrradianceSH`, a public readonly struct of nine pre-convolved RGB coefficients in the Y-up real second-order (L2) spherical harmonic basis. The clamped-cosine band weights of Ramamoorthi and Hanrahan (2001) are already stored in the coefficients. `Evaluate(Vector3 unitNormal)` sums the basis and clamps each channel at zero, returning irradiance in `AmbientColor` units. The span constructor takes exactly nine coefficients, `Coefficient(index)` reads them, `Scale(Vector3 rgb)` scales channels and `+` adds coefficients. This per-probe format is reusable by later stages. |
| D3 | `IndirectDiffuse` and `AmbientIrradiance` are public fields on `PixelPostProcessSettings`, matching the class's other members. `AmbientIrradiance` holds the frame's `IrradianceSH` and defaults to `Zero` (`default`). With the atmosphere on in `Directional`, the scene and `SunCycle.Apply` write it (D5). Without the atmosphere the consumer sets it. `IrradianceSH.SkyAndGround(Vector3 skyIrradiance, Vector3 groundIrradiance)` builds two uniform hemispheres from their upward and downward irradiance, and `IrradianceSH.Uniform(Vector3 irradiance)` builds the isotropic case. |
| D4 | The shared lighting block gains `vec3 indirectDiffuse(vec3 N, vec3 worldPos)`. The six lit fragments (`ModelFrag`, `ModelDissolveFrag`, `SkinnedModelFrag`, `SkinnedModelDissolveFrag`, `SplatFrag`, `TileGroundFrag`) use it where they read `Ambient.rgb` today. In `Uniform` mode it returns `Ambient.rgb` with no arithmetic. In `Directional` mode it evaluates the SH and clamps below at zero. `worldPos` is unused in version 1 and reserved for the probe volume. Water, particles, decals, billboards, outlines and silhouettes are unchanged. |
| D5 | In `Directional` mode the atmosphere projects the sky radiance over the dome above the geometric horizon, and adds a uniform ground radiance below the horizon whose irradiance on a downward face equals today's fill value. `AmbientScale` scales the sky part, `FillScale` scales the ground part and the frame tint applies to both. The moon contributes through the same path scaled by `MoonFraction`. `AtmosphereLighting.Ambient` keeps the upward-face irradiance for consumers that read it. |
| D6 | In `Directional` mode the atmosphere writes zero RGB to `FillLightColor`, preserving its alpha, because the ground bounce now arrives through the SH and would otherwise count twice. `Uniform` mode keeps today's fill and clears `AmbientIrradiance`. `AmbientColor` and the returned `SunCycleState` keep their original values. The fill direction setting remains for consumers without the atmosphere. |
| D7 | The night floor `floor = NightAmbientFloor × NightTint` is applied per pole. After sky and ground are combined, add `IrradianceSH.SkyAndGround(max(floor - up, 0), max(floor - down, 0))`, using the unclamped upward and downward evaluations. These exact hemisphere terms bring each pole to its floor without moving the other. |
| D8 | A separate `AtmosphereIrradianceCache` owns a lazy sky SH table at the same 181 elevations and eight quadratic altitude slices as `AtmosphereLightingCache`. It samples with mirror symmetry about the source's vertical plane at zero azimuth, retaining only coefficients 0, 1, 3, 6, 7 and 8. Each cell is corrected to a zero downward sky pole and normalised so its upward value equals that cell's ambient. Matching elevation and physical-altitude interpolation preserves the anchors. Each frame rotates the interpolated result about Y by the source azimuth and adds the moon and ground terms. The first measured one-time table build was 250.752 ms, about 250 ms. Warm frame evaluation allocates nothing. |
| D9 | The indirect tail starts at byte 1376 after the interior-cut tail. It contains `vec4 IndirectParams` (`x = 1` in `Directional`), then `vec4 IrradianceSH[9]` (`xyz` used), for 160 bytes and `UboBytes = 1536`. The six lit fragment blocks declare this tail. Vertex and unrelated shader blocks need not declare trailing members they never read. Layout tests pin these offsets. |
| D10 | Calibration anchors are exact within floating-point precision at every source elevation and altitude slice. `Evaluate(up)` equals `AtmosphereLighting.Ambient`, including its floor. `Evaluate(down)` equals `max(Fill, floor)`, where `Fill` is the value `Uniform` would carry and `floor = NightAmbientFloor × NightTint`. The sky correction and independent pole floors preserve these anchors without transferring a shortfall to the other pole. |

Without the atmosphere, `Directional` with an unset `AmbientIrradiance` (`default` or `Zero`) uploads
`IrradianceSH.Uniform` of `AmbientColor`'s RGB. Switching only the mode therefore retains ambient lighting.
The fill stays independent in this path. Consumers supplying a ground hemisphere also zero `FillLightColor`
RGB if that hemisphere already accounts for the fill.

## Units

Radiance `L(ω)` projects onto the Y-up real L2 basis as `c_i = ∫ L(ω) Y_i(ω) dω`. Stored irradiance coefficients
are `e_i = A_l × c_i`, with convolution weights π, 2π/3 and π/4 for bands 0, 1 and 2.
`Evaluate(n) = max(Σ e_i Y_i(n), 0)` approximates `∫ L(ω) max(dot(n, ω), 0) dω` per channel without applying the
weights again. This is the quantity `AtmosphereLightingCache` integrates for `Ambient`, so both modes share one
unit. The atmosphere table's pole corrections and normalisation enforce D10 on the L2 approximation.

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

[GPU run 40049041585](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/40049041585) passed all 351
render tests on each of Metal, Vulkan and D3D11 with no failures or skips. The new golden and every existing
golden passed on all three backends without changing existing references. In the paired canopy fixture,
Directional underside display luminance was about 2.5 times Uniform on D3D11.

## Consumer adoption

Grimhollow adoption is separate consumer work. It sets `Directional`, removes its fill direction override, and
calibrates `AmbientScale` and `FillScale` from paired indoor and outdoor stills that its owner approves. Its
branch that draws roofs indoors merges after that adoption. Other consumers are unaffected until they opt in.

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
