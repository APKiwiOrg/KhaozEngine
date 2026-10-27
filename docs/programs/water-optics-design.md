# Water optics design

Approved design of water optics milestones not built yet. Each section is rewritten into the living docs
and deleted from here when its milestone lands. Program state is in [the tracker](water-optics.md).

## M5 Water column light and shadow, with a bent view of the bed

Issue: https://github.com/APKiwiOrg/KhaozEngine/issues/1608.

### Intent

The owner wants a realistic river above and below the water. M2 (underwater sunlight, v21.15.0) refracts,
attenuates and softens the sun on submerged receivers, but in Grimhollow the duck still reads as detached
(https://github.com/APKiwiOrg/Grimhollow/issues/644#issuecomment-6430043721). Its refracted bed shadow is
correctly offset below the body, and nothing else in the frame ties that shadow to the bird. The river also
turned green, because the bed's key light is attenuated by `AbsorptionPerMetre` and the water pass tints the
same bed again by its own depth grade, then hides most of it behind an alpha of 0.78 to 0.92.

Real water ties a floater to its shadow in two ways this engine lacks. The water column itself scatters
sunlight toward the eye, so the column is visibly darker where a floater blocks the sun. And the bed is seen
through a physical view path, so its shadow fades with depth exactly as the bed does. M5 adds both, plus the
bed seen bent through the ripples.

### Goals

- Objects over water cast shadows that read as attached to them, fading with depth (owner).
- The depth at which bed shadows and the bed itself fade is set by the water's clarity, not a separate
  cutoff (owner).
- One absorption and scattering model drives both the view path and M2's sunlight, so a bed is never
  tinted twice and shallows keep their own colour (owner, WO-D13).
- Sun glint and the sun-lit part of foam disappear inside a floater's shadow.
- The bed seen through the surface shifts with the ripples (second landing).
- Works for procedural and FFT bodies alike. Unlike caustics, nothing here reads wave data from the lit
  shaders, so the wave source does not matter.

### Non-goals

- Reflections of the scene (M6, https://github.com/APKiwiOrg/KhaozEngine/issues/1609). The surface keeps
  its analytic sky reflection.
- Caustics (M3), flow (M7), interaction ripples (M8), wet banks (M9), the underwater camera (M10).
- Ray or path traced lighting (https://github.com/APKiwiOrg/KhaozEngine/issues/1558). The owner ruled M5
  delivers attached shadows without it.
- Simulated multiple scattering. An analytic estimate of it is in scope (owner), a volumetric simulation is
  not.

### Anti-goals

- A frame with no opted-in body changes in any way. No copy, no extra binding work that reaches the GPU
  beyond what layout stability needs, no shading difference, no golden rebake.
- An opted-in shallow bed that looks muddier or greener than the same bed dry. At 5 cm the bed must read
  as itself.
- A shadow that floats free of the floater, which is the defect this exists to fix.
- Visible noise from the march on a still frame, with temporal anti-aliasing on or off.

### Constraints

- Opt in per body through `WaterLook`, off by default, matching M2's pattern and the byte-stable rule.
- Public fixtures are synthetic (WO-D7).
- Device requirements do not rise. The water pass gains sampled textures and a uniform block, not storage
  buffers (`LitReceiverRequirements` stays at 3 fragment storage buffers).
- The per-plane water UBO slot stays 1280 bytes. Shadow cascade data is shared per frame, not packed per
  plane (packing it would grow every slot to 1536, see `UboLayoutTests`).
- KESIZE: new behaviour goes in new types. The water fragment source and `WaterRenderer` are already large.
- No fixed GPU budget. The cost is measured on the renderer performance workloads before it ships and the
  owner rules on it with the numbers (same rule as M3).

### How the water pass works today

`ShaderSources.Water.cs` `FragSource` fetches scene depth at the pixel (481), rebuilds the ground point,
takes the vertical depth below the surface (486) and runs `opticalBedDepth` (`ShaderSources.WaterOptics.cs`),
which returns the vertical depth to the authored bed when M1 optics are bound and the per-pixel vertical
depth otherwise. Body colour is `mix(Deep, Shallow, exp(-AbsorptionPerMetre * bedDepth))` with an alpha
from the colours' alphas (568 to 571). Fresnel mixes in the sky reflection (591 to 596), then glint, foam
and the shore fade, and the result alpha-blends over the scene (684 to 691). The pass binds no shadow map
and no scene colour. Per-plane values are CPU-packed into one UBO slot per plane (`WaterRenderer.cs`
544 to 564). The cascade atlas (`ShadowMapRenderer`) is complete before the water draw, which runs in the
same command list after decals (`Scene3D.cs` 1539).

### Approaches considered

Composite method:
- **Scene colour copy plus a physical composite (chosen).** Copy the scene colour before the water pass,
  then compute `bed * T + inscatter` per channel and write the result. This is the only option that supports
  per-channel absorption, a shadowed in-scatter and the bent view, and the copy is reused by M6.
- Keep alpha blending with a scalar transmittance (dropped). It cannot absorb per channel or bend the view,
  so the river would stay double-tinted on the light path or wrong on the view path.

Water column shadow (owner picked the march):
- **Short march (chosen).** Four jittered samples along the refracted view ray inside the water, each one a
  point comparison against the key cascade atlas, weighted by the light that depth scatters back to the eye.
  It mirrors M2's four-slice march and stays correct in murky and clear water.
- One sample at the scattering-weighted mean depth (rejected). A quarter of the cost, but in clear deeper
  water the whole shadow sits at one depth and can look misplaced.
- A light-space water shadow texture (dropped). It costs a new pass and memory for little gain at river
  depths.

Depth limit (owner picked clarity):
- **Clarity (chosen).** One clarity setting per look sets extinction, so bed visibility, tint and shadow
  fade stay consistent by construction.
- An explicit shadow cutoff, or clarity plus a cutoff override (rejected). A cutoff can disagree with how
  visible the bed is.

### Design

#### Opt-in and authoring

New nullable `WaterLook` fields, inheriting from new `WaterSettings` fields as M2's do:

- `WaterColumnLight` (bool, default false). The opt-in.
- `WaterColor` (`Color`, RGB). The colour deep water shows from its own scattered light, seen straight
  down. Silty green-brown for a river, blue for clear sea. It is calibrated under a reference white sun at
  zenith and sky, so under the scene's real light it shifts as real water does at dusk or in shade (owner).
- `Clarity` (metres). The vertical depth at which a bed seen straight down falls to about 5% visibility.

When `WaterColumnLight` resolves true for a plane, underwater sunlight is on for that plane as well, and the
absorption M2 packs into that plane's water volume record is the derived extinction below, not
`AbsorptionPerMetre`. This is what WO-D13 requires. `AbsorptionPerMetre`, `ShallowColor`, `DeepColor`,
`ShallowDepth` and `Opacity` keep their meaning for bodies that do not opt in, and are not read by an
opted-in body's water column (the shore fade and foam still use their own settings). `DrawWater` rejects a
non-finite or non-positive `Clarity`, a `Clarity` whose derived extinction is not finite and positive in
float (checked in double before conversion), and a `WaterColor` channel outside [0, 1] on the resolved
settings, as M2 does for its fields.

Derivation, documented in the XML docs and mirrored by a CPU helper the tests use:

- Light units are the engine's. A white Lambertian receiver under `LightColor` (1, 1, 1) at normal
  incidence shades to 1, so irradiance is pi times `LightColor`, and ambient acts as uniform sky radiance
  `AmbientColor`. Every scattering term carries that factor of pi, or the water would render about three
  times too dark beside its own banks. The fill light does not reach the water column.
- Mean extinction `sigma_t = 3 / Clarity`, so `exp(-sigma_t * Clarity)` is about 0.05.
- Scattering stays energy conserving, with single-scattering albedo `a` at most one per channel (owner).
  Single scattering alone leaves murky water far darker than real silty water, whose brightness comes from
  light bouncing many times, so the radiance model adds an analytic multiple-scattering estimate on top of
  the four-sample single-scattering march. The estimate's form is the builder's, from an established
  closed-form approximation.
- `a` is the inverse of the deep-water function (single plus multiple scattering, vertical view, reference
  light of unit sun at zenith plus unit ambient), so `WaterColor` reproduces itself in deep water. In engine
  light units an albedo of one reaches about 2.25 under the reference light, so every channel in [0, 1] is
  reachable and no cap is built. CPU tests pin the round trip at black, white and the river values.
- Per-channel extinction `sigma_t_rgb` is spread around the mean so channels the water colour lacks are
  absorbed faster. `sigma_s = a * sigma_t_rgb`, `sigma_a = sigma_t_rgb - sigma_s`.
- M2's record absorption for the plane is `sigma_t_rgb`. The direct sun beam is attenuated by extinction.

`TileWaterLooks.River` gains tuned `WaterColor` and `Clarity` values with `WaterColumnLight` left null, so
consumers opt in explicitly. The first values come from owner-reviewed renders of the synthetic river
fixture. Grimhollow picks its final values at adoption.

#### Scene colour copy

When at least one opted-in plane is queued this frame, the scene colour is made readable after ground
decals and before the first water draw:

- MSAA frames resolve `MsColor` into the single-sample `ColorTex` before water. Water writes `MsColor`, so
  reading `ColorTex` is hazard-free, and the existing final `ResolveColor` overwrites it afterwards.
- Single-sample frames copy `ColorTex` mip 0 into a new same-format target with `CopyTextureSubresource`,
  the pattern `TemporalPostTargets.CopyOpaque` already uses.
- Frames with no opted-in plane do neither. The water set binds a 1x1 placeholder so the layout is stable.

The copy holds everything drawn before water (the opaque scene, textured billboards, beams, trails,
translucent overlays and silhouettes, sky and ground decals, `Scene3D.cs` 1441 to 1531) and none of the
water. A water surface seen through another opted-in water surface therefore shows the scene behind it
without the farther water. That is rare in a
river view and is documented as a limitation.

#### Shadow access in the water pass

The water set appends the key cascade atlas, a point clamp sampler and the per-frame cascade block
(matrices, splits, texel sizes, biases, normal offsets) the lit pass already writes. The builder either binds
the lit pass's existing frame uniforms or adds an equivalent shared block written once per frame, without
growing the per-plane slot. The water set's staleness check (`TargetsMatch`) gains atlas identity so a shadow
layout replacement rebinds it. `pcfCascade` and the cascade filter in `ShaderSources.ShadowVisibility.cs`
already take every resource as a parameter and are reused. `projectCascade` and `sampleKeyShadow` read
lit-pass names and are not, so the water pass gets its own small cascade-select helper sharing the same
selection and blend rules. When shadows are disabled scene-wide, visibility is 1 and the composite still
applies.

#### The composite, first landing

Per opted-in water pixel, with `S` the surface point, `v` the view direction refracted at the surface with
`n = 1.333` (the same constant as M2), `B` the bed point and `d` the in-water view path length:

- `d` runs along the refracted view ray. It is the vertical bed depth `h` the water pass already computes
  at `S` (scene depth, or the authored bed through `opticalBedDepth` when M1 optics are bound) divided by
  `mu = -v.y`. Refraction keeps `mu` at about 0.66 or more, so the division stays bounded. The march runs
  along `S + v * s` for `s` in `[0, d]`. In landing 1 the bed colour is the copy at the original pixel,
  which is the approximation landing 2's bent view removes. Sample light depths are measured from the
  still-water surface, as M2's are, and clamp to zero above it on a displaced crest.
- View transmittance `T = exp(-sigma_t_rgb * d)`.
- Single-scattered sun: four samples `X_i` along `v` from `S` to `B`, jittered within equal-weight strata of
  the analytic in-scatter integral. Each contributes sun colour times `exp(-sigma_t_rgb * depth(X_i) / cos
  theta_sun_water)` for the refracted sun path down to it, times `exp(-sigma_t_rgb * |X_i - S|)` back to the
  eye, times `sigma_s` and an isotropic phase, times the sun visibility at `X_i`.
- Each sample's sun visibility follows M2's entry rule rather than the atlas's unrefracted ray through `X_i`.
  The sample's refracted surface entry is `E_i = X_i - t * (depth(X_i) / -t.y)` with M2's refracted sun
  travel `t`, and visibility is one point comparison of the cascade atlas at `E_i`, which selects its own
  cascade as M2's slices do. This keeps column shadows aligned with M2's bed shadows. As M2 counts its
  slices, that is four comparisons before cascade blending, and a sample inside a blend band also compares
  in the neighbouring cascade, so the worst case is eight plus the surface lookup at `S`.
  Casters wholly below the surface do not shadow the column. A submerged rock still darkens the bed through
  M2 but not the water above it, which is documented.
- Ambient in-scatter uses the scene's ambient term, unshadowed, integrated analytically over `d`.
- The multiple-scattering estimate is driven by the sun and sky light entering the surface, and dims with
  the march's mean sun visibility, so an attached shadow still reads in murky water where most of the
  brightness is multiply scattered.
- Transmitted light is `bed * T + inscatter_sun + inscatter_multiple + inscatter_ambient`. The bed colour in the copy is already
  lit by M2's attenuated sun, so the light path and view path are each counted once.
- Fresnel, the sky reflection and the shore fade keep their existing math. The output is
  `mix(transmitted, reflection, fresnel)`, mixed toward the copy by the shore fade, written with alpha 1 so
  the existing alpha-blend state replaces the pixel. No new pipeline variant is needed. The per-plane flag in
  the UBO slot selects the path.
- The glint and the sun-lit part of foam multiply by the surface point's sun visibility (one `pcfCascade`
  lookup at `S`).
- Atmosphere fog. The copy is already fogged for its own distance, so fogging the finished composite
  would fog the bed twice. The composite works in fogged space instead. The copy term enters as is, scaled
  by `(1 - fresnel) * T`, and fog applies only to the water's own terms (in-scatter, reflection, glint,
  foam), with the fog's in-scatter weighted by the share of the pixel the copy term does not already carry.
  This is exact at the waterline, and its error is bounded by the fog change across the in-water distance
  `d`, a metre or two in a river.

Noise: the march uses interleaved gradient noise. With temporal anti-aliasing on, the pattern advances per
frame so history converges. Water pixels read as reactive in the temporal resolve wherever their weighted
luminance differs from the opaque copy (`ShaderSources.TemporalPrepare.cs` 73), which weakens that history
there, so the noise level is an acceptance test, not an assumption. Without temporal anti-aliasing the pattern is screen-fixed, as M2's kernel is.

As `d` tends to zero, `T` tends to one and the in-scatter to zero, so the waterline blends into the dry bed
with no seam, matching M2's waterline property.

#### The bent view, second landing

The copy lookup and the scene depth lookup move by a screen offset from the surface normal's horizontal
slope, scaled by `d` and the refraction at `n = 1.333`. An offset whose scene depth lies in front of the
water surface is rejected and the unoffset sample is used, so nothing above the water appears in the bent
image. `d`, `T` and the march use the offset sample's depth. The normal is whatever the water pass already
computes for that pixel, procedural or FFT.

#### Diagnostics

Scene diagnostics report the number of opted-in planes, whether the copy ran and which copy path it used,
matching the counters M2 added for water volume records.

### Acceptance

GPU facts on the synthetic river (a straddling box floater over a flat sand bed, sun at 35 degrees,
procedural ripples), absolute probes rather than self-baked goldens:

- The water column directly beside the floater, inside its sun shadow volume as seen from the camera, is
  darker than the unshadowed water at the same depth by a stated margin, and the dark region touches the
  floater's waterline.
- Bed shadow contrast falls monotonically with depth and is under 5% at the `Clarity` depth.
- At 5 cm the bed seen through the water matches the same bed dry, lit by M2 sunlight, within a stated
  tolerance.
- A bed seen through 30 cm matches the CPU mirror's `bed * T + inscatter` within tolerance, proving it is
  tinted once.
- The glint is absent inside the floater's shadow and present outside it.
- With no opted-in plane, the frame is byte-identical to the same scene with every new look field left
  null, every existing water golden passes without a rebake, and the copy does not run (diagnostics).
- MSAA and single-sample, temporal anti-aliasing on and off, D3D11, Vulkan and Metal via the cross-platform
  GPU workflow.
- Still-frame noise in the shadowed column stays under a stated temporal variance with temporal
  anti-aliasing on and off.
- Second landing: a straight bed stripe appears displaced by the ripple slope, and a red pole standing above
  the water next to the stripe never appears inside the bent image.

A GPU fact checks that deep water under reference light renders its reachable `WaterColor` within
tolerance, and that the attached shadow still reads at the murkiest `Clarity` the river preset uses.

CPU tests pin the `WaterColor` and `Clarity` derivation, the round trip, look inheritance, validation,
the byte-identical UBO packing of a look with the new fields null, and that an opted-in plane's water
volume record carries the derived extinction.

Cost: the renderer performance workloads from https://github.com/APKiwiOrg/KhaozEngine/issues/1602 gain an
M5 ON variant of the village and outdoors water, and the owner rules on the measured cost before M5 ships.
That run needs its own owner approval naming its legs and cap.

### Landings

1. Opt-in and authoring, the copy, shadow access, the composite, glint and foam shadowing, the shared
   extinction for M2's records, diagnostics and the river preset values.
2. The bent view.

Each landing updates `docs/RENDER-PIPELINE.md`, the Render3D README, `docs/USING-KHAOZENGINE.md`, the
`WaterSettings` and `WaterLook` XML docs and the tracker in the same commit. Grimhollow restores the duck's
shadow and picks its river values when it adopts the release carrying landing 1.

### Left to the builder

- The multiple-scattering approximation, the albedo inversion and the per-channel spread of extinction,
  within the pinned properties.
- Whether shadow access binds the lit pass's frame uniforms or a new shared block.
- The new types' names and file split under KESIZE.
- Probe positions, margins and tolerances in the GPU facts, stated in the tests.
- Whether the composite shares code with the M2 helpers or mirrors them, as long as the refraction constant
  and cascade rules stay single-sourced.
