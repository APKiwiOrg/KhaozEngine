# Per-body water optics: approved design of unbuilt milestones

Program tracker: [water-optics.md](water-optics.md). Each part moves into the living docs when its milestone
lands, and is deleted from here.

## M2 underwater sunlight for submerged receivers

Owner-approved. Issue: https://github.com/APKiwiOrg/KhaozEngine/issues/1588.

### Intent

Opaque surfaces under water are lit today exactly as if the water were not there. The key (sun) shadow lookup
follows the unrefracted air ray at full strength with the dry kernel. A floating object's shadow therefore lands
`h * tan(theta_air)` away from it on a bed `h` metres down, crisp and undimmed, and the object reads as hovering.
Grimhollow proved this with a discriminating capture: disabling only the floater's own shadow made it sit on the
water, while 2x and 4x absorption left the offset shadow unchanged. Grimhollow currently ships floaters without a
sun shadow as an interim and restores it in https://github.com/APKiwiOrg/Grimhollow/issues/644 once this ships.

The goal is physically grounded underwater key lighting that every lit receiver gets from one shared path, with no
change at all to frames that do not opt in, and a foundation that caustics (M3) and later underwater effects read
from rather than rebuild.

### Goals

1. **Refracted shadow.** For a receiver inside an opted-in water volume, the key light reaches it along the
   refracted ray. Refract the sun direction at the still-water surface with Snell's law, n = 1.333 for water
   against air, flat surface normal. The ray enters the water at the entry point `E` above the receiver `P`.
   Casters above the surface are tested at `E` rather than at `P`. That alone moves a floater's bed shadow under
   it (the offset shrinks from `h tan(theta_air)` to `h tan(theta_water)`, 40 to 50 percent closer at typical
   sun elevations).
2. **Submerged and surface-straddling casters keep a shadow.** Casters between the surface and the receiver (a
   floater's submerged hull, a submerged rock, a swimming character, an underwater bank, the lake bed above a dry
   cave) still occlude, through a short march along the refracted segment that counts only casters below the
   surface.
3. **Attenuation.** Key light, diffuse and specular, is multiplied per channel by
   `exp(-AbsorptionPerMetre * L)`, where `L = h / cos(theta_water)` is the in-water path length, using the body's
   resolved absorption. Only the key light changes. Weaker key against unchanged ambient is what fades the bed
   shadow's contrast with depth, the physical cue #1588 asks for. The water pass's existing depth tint already
   darkens the view of deep beds, so dimming ambient as well would double-darken them.
4. **Depth-softened penumbra.** The key-shadow kernel radius grows with the in-water path,
   `radius = baseRadius + L * UnderwaterPenumbraPerMetre`, at a fixed tap count, capped.
5. **Any water plane, opted in per body.** Procedural or FFT, bound or unbound optics, any producer. Off by
   default everywhere.
6. **Bounded water volumes.** Rectangle edges are exact and stacked bodies resolve to the nearest surface above.
   Each body's volume is a slab from `FloorY` to its surface. Nothing below the floor is affected. A dry space
   inside the slab but under the local bed (a cave beneath the shallow end of a sloped riverbed) is classified as
   submerged, but only its key path changes, and the bed above it already shadows that path, so it stays dark
   as it is today. Exact local containment would need the bed field in every lit receiver and is out of scope.

### Non-goals

- Caustics. M3, built on the records this milestone introduces, with wave normals.
- Refraction of the view ray (the bed seen through water stays where it is drawn), camera-underwater view fog,
  light shafts.
- Point lights, sky ambient, directional irradiance and fill under water. They keep their current values. A
  later underwater-volume consumer can attenuate them together with a physical view-path model in the water pass.
- Multi-body light paths. The light path is modelled through the receiver's own body only, with its entry point on
  that body's infinite surface plane. An entry point that lands on dry land beside a narrow pool sits under the
  bank's surface, so the bank's stored face shadows it, which is physically right.
- New shadow-map data. The atlas stays one stored caster depth per texel.
- Wave-displaced refraction. The entry point uses the still-water level. Swell displacement is far smaller than
  today's unrefracted error.
- Changing the water pass. Its vertical-depth tint and alpha grade stay as they are.

### Anti-goals (a failure even if it technically works)

- Any pixel change in a frame with no opted-in water.
- A visible seam at the waterline. At depth 0 the result must equal the dry result exactly.
- A dry space below a water body lit through that body.
- Per-pixel cost that grows without bound with the number of water planes.
- Backend divergence beyond the repository's existing golden tolerances.

### Approaches considered (owner choice: the binned water list, "most correct long term")

| Option | What it is | Verdict |
| --- | --- | --- |
| Binned water volumes | CPU bins opted-in water volume records into the existing view-space cluster grid, uploaded as a storage buffer. Lit fragments test the few records in their cluster. | Chosen. Exact edges and stacked bodies, no range limit, bounded cost, reuses the point-light cluster seam, and its records are what M3 reads. |
| Top-down water level map | Rasterize opted-in planes from above into a camera-centred level texture. One fetch per lit pixel. | Rejected. One surface per texel collapses stacked bodies, edges quantize to texels, finite radius. |
| Correct in the water pass | Relight the bed inside the water shader. | Rejected. The forward renderer keeps no separate sun term, dividing by the dry shadow fails exactly in dry shadow, and receivers seen from below or the side are missed. |

Scores (quality, performance, simplicity, reuse): binned list 32, level map 28, water pass 18.

### Design

#### Public API (Render3D)

- `WaterSettings.UnderwaterSunlight` (bool, default false). Scene-wide default for every water plane.
- `WaterSettings.UnderwaterPenumbraPerMetre` (float, metres of extra penumbra radius per metre of in-water path,
  default 0.03, finite and non-negative). The default reflects sun angular size plus typical surface-slope
  scatter. It is an art control.
- `WaterLook.UnderwaterSunlight` (bool?) and `WaterLook.UnderwaterPenumbraPerMetre` (float?) per-plane
  overrides, resolved exactly like the existing nullable look overrides and covered by the existing reflection
  pins (`WaterLookTests` override coverage, `WaterSettings.CopyFrom` completeness).
- `WaterPlane.FloorY` (float?, default null meaning unbounded below). The lowest point of this body's water
  volume under the plane's rectangle. A receiver below it is not in this body. A trailing optional constructor
  argument by the existing `Optics` precedent, carried by `WithOptics`, and by render-origin reconstruction
  (`Scene3D.RenderOrigin.cs` `RelativeWaterPlanes`, which carries every field by hand, so it needs a test).
- The refractive index is a constant, not a knob.
- Absorption is the body's existing resolved `AbsorptionPerMetre`. Zero absorption means no attenuation.

TileWorld sets `FloorY` on every plane it emits from the body's lowest bed corner height under that rectangle,
with a small downward margin, so its stacked bodies are exact without consumer work. Native MapDoc water (R8b)
must do the same, recorded in the R8b handoff.

#### Water volume records and clustering

- Each frame, after queueing and before the opaque pass (where point-light clusters are built today), the scene
  collects every queued plane whose resolved look has `UnderwaterSunlight` true and builds one record per plane in
  render-relative coordinates: rectangle min and max XZ, still surface Y, floor Y (or a large negative), resolved
  absorption RGB, penumbra-per-metre. Ten floats, packed as three uvec4 (48 bytes) for alignment, with the reader
  layout documented on `WaterVolumeClusterBuilder`. Submission order is preserved.
- Records are binned into the existing view-space cluster grid (16 x 9 x 24 froxels, the same jittered raster
  view-projection, render-relative eye and log depth slices as point lights), so a fragment computes ONE cluster
  index for both point lights and water. A water-only frame prepares this grid even with no point lights.
  A failed light assignment marks all light headers for full-list fallback without invalidating a mixed frame's
  shared water grid. Point-light-only frames retain their legacy uniform bytes. Point-light shading still exits
  on its own count. A record enters a cluster when its box (rectangle by floor to surface)
  intersects the froxel, using a conservative range then an exact plane test, mirroring
  `PointLightClusterRanges` and `PointLightClusterBuilder`. Share the froxel geometry, not the per-primitive test
  (spheres versus boxes).
- One storage buffer, appended to set 0 of every lit receiver layout (rigid, skinned, splat, tile ground, and
  through rigid, foliage and CPU-skinned), holds per-cluster headers and index lists, then records. Appending
  avoids renumbering existing bindings. Allocation, upload, bindings and the first shading reader are implemented.
  Set 0 appends binding 15 for rigid, foliage and CPU-skinned receivers, 7 for GPU-skinned, and 6 for splat and
  tile ground. Declarations landed with their reader because shader optimization removes an unread buffer and
  would leave Direct3D11 material registers inconsistent with the appended C# layouts. Shaderc preserve-bindings
  remains unchanged.
- Caps: 4096 records, 32 indices per cluster. A cluster that overflows is flagged and its fragments scan the full
  record list, which stays correct and only costs more.
- Off switch: the per-frame water-volume count in `ClusterCamera.w`. Zero skips the whole path as a uniform branch
  without reading the buffer, so stale records from an earlier frame are unreachable. Empty frames skip building
  and uploading. Point lights work the same way: their skipped uploads leave old buffer contents, and shading
  exits on the separate light count (`ModelRenderer.WaterVolumes.cs`, `ShaderSources.Lighting.cs`).
  `UboLayoutTests.cs` is at its size cap, so the new field's layout pins go in a new test type.
- Fragment query: for the fragment's cluster, test each listed record: XZ inside the rectangle (exact, inclusive
  on min, exclusive on max to match rectangle tiling), `floorY <= y < surfaceY`. Pick the record with the lowest
  surface above the fragment. Ties resolve to the first in submission order.

#### Shading

All of this lives in the shared lighting block, so the six call sites (`ModelFrag`, `ModelDissolveFrag`,
`SkinnedModelFrag`, `SkinnedModelDissolveFrag`, `SplatFrag`, `TileGroundFrag`) and their motion and visibility
variants inherit it. The `keyShadow = sampleKeyShadow(...)` call statements stay textually unchanged because the
key-visibility export splices after that exact statement.

The volume query, refracted entry lookup, submerged-caster march, depth-widened kernel and key diffuse/specular
attenuation are implemented.

1. Query the water volume for the fragment. Not submerged means today's path, bit for bit.
2. Submerged at depth `h`: compute the refracted in-water direction, `L = h / cos(theta_water)`, and `E` on the
   surface.
3. Above-surface occlusion: the key-shadow lookup at `E` with `E`'s own cascade and the depth-widened kernel,
   using the receiver's own normal offset and slope bias (from `P`'s geometric normal and `ndl`) exactly as the dry
   lookup does. That keeps step 7's continuity exact.
4. Submerged occlusion: march the refracted segment from `E` to `P` in four depth slices. At each slice end,
   one comparison reconstructs the stored caster's world height on the texel-snapped light ray. It counts only
   casters below `surfaceY` and inside the slice's height band. Non-horizontal rays use shared world-height
   boundaries, so different widened tap offsets cannot open gaps through a roof. The
   inverse height coefficients are each orthographic basis row's Y component divided by its squared length.
   Those denominators remain nonzero at every light elevation, including exactly horizontal. The height-band
   endpoint is inclusive so a horizontal ray can occlude, and a separate light-depth comparison excludes
   casters beyond the endpoint. The final endpoint is the receiver's geometric plane evaluated on each offset
   tap's ray, retaining the normal offset and existing light-depth bias. A signed plane comparison multiplies
   by the plane's light-depth slope, with no division by light elevation or plane slope. A parallel plane keeps
   the light-depth endpoint test. This excludes the receiver's own upslope samples as well as a flat bed.
   All ends use the receiver's geometric normal
   offset. Internal boundaries share a per-pixel interleaved-gradient-noise offset of up to half a slice, leaving
   no band gaps. The final end stays at the biased receiver. TAA can resolve the jitter. The four slice rays
   spread with the same widened radius, converted separately for each selected cascade. Their offsets vanish
   at zero widening. Surface and slice depths are evaluated on the actual offset, snapped texel's light ray.
   - Caster-face evidence: the opaque pipeline culls clockwise fronts. Outward CCW box triangles and ordinary
     glTF triangles with orientation-preserving transforms retain their sun-facing near side. The loader
     preserves triangle order. Metal atlas readback reconstructs the straddling box's stored top at `y = 0.15`,
     for both the procedural box and its opaque glTF round trip. The shadow renderer's far-side comment does not
     describe this case. All live backends report `ClipSpaceYInverted = false`, so `GpuClip.Correct` is identity.
     Vulkan's negative-height viewport and direct front-face mapping preserve the shared convention.
   - Why a march and not one lookup at `P`: the straddling box's above-water face already shadows `E`, but a
     wholly submerged rock or cave roof lies behind that lookup. The march finds those stored depths along the
     refracted segment. A single lookup at `P` would follow the dry direction for the whole depth and restore
     the dry offset. Opposite caster winding can retain a far side instead, which the same depth bands also test.
   - Coverage: each slice selects and blends its own cascades. The outer coverage fade applies once at the real
     receiver, rather than at each virtual slice point. Otherwise a roof near the atlas border can brighten a
     covered cave floor. The entry lookup's own coverage fade applies only to its above-surface term.
   - Error: within a slice, a caster's shadow follows the dry direction. Apart from atlas quantization and
     receiver bias, displacement error is bounded by that slice's vertical depth span times
     `tan(theta_air) - tan(theta_water)`. Jitter makes the widest end band at most `1.5 * h / 4` deep.
   - Limitation: the atlas keeps one caster per texel, the nearest retained face. A submerged caster in the same light
     texel column as a nearer above-water caster is hidden from the march. Solving that needs depth layers, which
     this milestone does not add.
5. `keyShadow` is the product of 3 and 4, then the existing strength, fade and cascade blending. It stays a
   scalar occlusion, so the R8 key-visibility export and its fully-lit certificate (non-positive strength means
   exactly one) stay valid.
6. Key diffuse and specular are multiplied by the path transmittance. Indirect, fill, point lights, emissive and
   fog are unchanged.
7. Continuity: as `h` goes to 0, `E` goes to `P` with the same offset and bias, the transmittance goes to 1, the
   march length goes to 0 so its term goes to 1, and the kernel goes to the dry kernel. The waterline has no seam.
   A test pins this.

Kernel: the bilinear 3x3 comparison kernel folds into a weighted 4x4 footprint, using 16 point depth comparisons.
The underwater kernel starts with those taps and tent weights, then spreads the same 16 comparisons using
`radiusTexels = min(8, 1 + L * UnderwaterPenumbraPerMetre / texelWorld)`, using each lookup cascade's matrix to
convert world metres to texels. Per-pixel rotation and positional jitter use interleaved gradient noise, with
amplitudes growing from zero over the first texel of widening. TAA jitters the receiver against this screen-fixed
pattern. At zero widening, taps and weights are exactly the dry bilinear kernel. Over the first texel of
widening, tap positions and weights morph into a centred, equal-weight pattern from a concentric square-to-disc
mapping of a 4x4 cell grid. This interpolates one set of taps, never two filtered results. The stratified pattern
uses all 16 comparisons, avoiding the phase-dependent variance of rotating the dry filter's nine effective taps
at wider radii. That variance can expose an otherwise shadowed hull edge. The fully widened pattern stays
inside the nominal radius rather than magnifying the folded dry fringe beyond it.
All taps clamp inside their cascade's atlas column and compare stored point depths before weighting. Added
displacement follows the analytic horizontal entry plane, evaluated at the actual snapped and clamped texel.
Only the displacement from the original dry tap adjusts comparison depth, preserving its original bias and
zero-widening result. Without that plane correction, outer rays can pass above a shallow caster and erase its
shadow. This single kernel family keeps the cost at 16 comparisons even in shallow water. Blending a separate
disc result with the dry filter would double that cost where most submerged pixels are. The march retains
one comparison per slice, with offsets that grow
from its centre ray using the same radius. Submerged fragments pay one kernel plus the four slice comparisons,
roughly 1.25 times today's comparison count before cascade blending. In a frame with opted-in water, other lit
fragments pay a cluster list walk of usually zero to two records. A frame without opted-in water pays one
uniform branch.

#### Backends

- D3D11: receiver shading adds one structured SRV per lit pipeline (117 to 121 slots remain). Metal adds one
  buffer index, within the 22 positions of headroom. Vulkan lit fragments use three storage buffers.
  The spec guarantees four
  per stage. `VulkanDeviceFacts` records `maxPerStageDescriptorStorageBuffers`, passed through
  `GpuCapabilities.MaxFragmentStorageBuffers`. Scene and standalone lit renderer creation refuse reports below
  three before GPU allocation, naming the Vulkan limit. These are the point-light records, point-light
  clusters and water volumes.
- The cross-compiled shader corpus (`ShippedShaderPrograms`) and register-numbering pins update deliberately.

#### Placement (KESIZE)

`Scene3D.cs` is exactly at its baseline, so integration goes in a new `Scene3D` partial. New internal types own
record building and clustering, and the underwater GLSL block, rather than growing `PointLightClusterBuilder.cs`
(422) or `ShaderSources.Lighting.cs` (532). `UboLayoutTests.cs` (797) takes no new tests.

### Testing

Headless:
- Record building: opt-in resolution, render origin (including `FloorY`), FFT and bound planes, ordering, caps.
- Clustering against an independent brute-force oracle (seeded), boundaries, stacked bodies with floors, overflow
  fallback, empty-frame skip, zero warm allocations, used-prefix upload, following the point-light cluster tests.
- `WaterLook` and `WaterSettings` reflection pins, `WaterPlane` round trips, TileWorld `FloorY` from real bodies.

GPU (`[GpuFact]`, pixel invariants first, committed grids only where a grid adds value, baked through
`cross-platform-gpu.yml` with `bake=true` on all three backends):
- Floater straddling the surface over a flat bed at depth `h` under a low sun: the shadow centroid offset
  matches `h tan(theta_water)` within a tolerance and is clearly below `h tan(theta_air)`, and the shadow has no
  hole under the hull.
- Lit-bed brightness ratio under depth versus the same bed dry matches the path transmittance.
- Penumbra width increases with depth and stops widening at eight texels. Near-zero depths match the dry kernel.
- Sloped bank across the waterline: no step in brightness at the line.
- Cave under a lake stays dark. A fully submerged rock casts a bed shadow.
- A frame with no opted-in water is byte-identical to the current output on an existing golden scene.
- Each feature check is also confirmed by a PNG dump viewed during development, because a self-baked golden
  always passes itself.

Performance: measured against the renderer performance baseline on a scene with a large share of submerged
pixels and on a scene with none, reported per backend. The owner judges the cost.

### Docs (same commits as the behaviour)

Render3D README (cascaded shadows, water, absorption, per-plane looks), `docs/USING-KHAOZENGINE.md` (shadows and
water sections), `docs/RENDER-PIPELINE.md` water section, the R8b handoff (`FloorY` for native water), the
TileWorld.Render3D README (`FloorY` from bodies), CHANGELOG under the next version, and the program tracker. The
rationale and its gotchas sit next to the behaviour they explain.

### Left to the builder

Exact record and header packing, the cluster-range math reuse boundary, the analytic surface-depth derivation,
the march slice count and spacing (four uniform slices is the starting point, tuned against the floater and
submerged-rock fixtures), the fixed-budget kernel pattern and noise, the cascade choice details at `E`, TileWorld's floor margin,
test fixture geometry and tolerances, and how the work splits into tasks.
