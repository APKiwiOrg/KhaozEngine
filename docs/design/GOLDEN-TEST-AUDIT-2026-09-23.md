# Golden test audit (2026-09-23)

Issue: [#19](https://github.com/APKiwiOrg/KhaozEngine/issues/19), which also carries the finding of closed
[#16](https://github.com/APKiwiOrg/KhaozEngine/issues/16). Measured against `a7311f7d` on real Metal (Mac14,6,
Apple M2 Max) and on the Direct3D 11 (WARP) and Vulkan (lavapipe) legs through their CI artifacts. No golden
mechanism, tolerance, grid or test changed with this record.

## Verdict

The committed-grid goldens are a sound gross-regression net and a weak feature net. They reliably catch
anything that moves a region of the frame: a wrong sampler address mode, a swapped texture slot, a pass that
stops drawing, a broken water or particle shader. They cannot see sparse, thin or low-contrast detail, they
cannot see intermediate targets, and they cannot see a whole-frame change of up to about 10 percent. Six of
32 feature deletions measured here still pass, and three of those six are the feature their golden exists for.

The weak part is the tolerance, not the averaging. Same-backend captures are bit-identical from run to run on
all three legs, and real Metal matches the hosted Metal leg pixel for pixel. Every committed grid reproduces
within 0.0005. The 0.06 tolerance is about 120 times that, so today it absorbs nothing except real changes
that were never rebaked. At 0.01, five of the six false passes fail and every current grid still passes
with a margin of 20 times its worst delta.

The rebake ritual is not masking regressions as driver noise, because on the current legs there is no driver
noise to mask. The historical failures are references baked from broken renders (three cases) and real
changes that stayed under tolerance and shipped with no golden signal (two cases). The controlled same-GPU
comparison adopted for the 19.7.0 references is the right ritual and should become the only one.

## 1. What the mechanism compares

- **Downsample.** `GoldenGrid.Downsample` (`KhaozEngine.Imaging/GoldenGrid.cs:36`) averages RGB into a 32 x 18
  grid (`DefaultGridW` and `DefaultGridH`, lines 23 and 25). Cell edges are integer-divided, so a 480 x 320
  capture gives cells of 15 x 17 or 15 x 18 pixels, about 267 pixels each. Alpha is ignored.
- **Compare.** `GoldenGrid.Compare` (line 79) takes the absolute difference per channel per cell and fails on
  any single channel over `DefaultTolerance`, 0.06 (line 27, tested at line 97). The test is therefore a
  maximum over cells of a mean within each cell. It is neither a whole-grid mean nor a percentile.
- **Detection floor.** To move one cell channel by 0.06, a change must add up to about 16 full-scale pixel
  channels inside one 15 x 18 cell (0.06 x 267). #16 measured one star at about 0.012 of a cell. The floor
  scales with capture size: about 8 pixels at 320 x 240 (`tileworld_greybox`, `_textured`, `_river`), about 2
  at 128 x 128 (`tileworld_topdown`) and about 32 at 640 x 480 (`telegraph_ground_void`). One tolerance means
  four different sensitivities across the family.
- **Families.** `GoldenCompare.GoldenBackendToken` (`KhaozEngine.Render.Tests/Gpu/GoldenCompare.cs:100`) maps
  the running backend to `metal-native`, `direct3d11-native` or `vulkan-native`, and each leg compares only
  against `<name>.<family>.txt`. There are 44 scenes and 132 grids.
- **Bake.** `KE_UPDATE_GOLDENS=1` writes the grid instead of comparing (`GoldenCompare.cs:177`), refused for a
  family the backend does not own (`BakeRefusal`, line 152). CI bakes on a `workflow_dispatch` with `bake=true`
  and uploads `goldens-<family>` with a full-resolution `.bake.png` per scene
  (`.github/workflows/cross-platform-gpu.yml`, the `goldens-${{ matrix.backend }}` upload). A person commits
  the files. [#724](https://github.com/APKiwiOrg/KhaozEngine/issues/724) and `42566f7f` rebake `metal-native`
  on the owning M2 Max rather than the hosted leg, while the 19.7.0 set took its `metal-native` bytes from the
  hosted bake run. The 19.7.0 record (`docs/design/OLDEST-BACKLOG-RENDER-VALIDATION-2026-09-19.md`) added a
  same-GPU old against new comparison that decides which scenes a rebake may replace.
- **Evidence.** Every compare appends its worst delta to `golden-deltas.<family>.txt` (`GoldenDeltaLog`, called
  at `GoldenCompare.cs:210`), uploaded on every CI run. A failure also writes got, want and heat-map PNGs.
  Nothing reads the delta log automatically.
- **Where it runs.** The Direct3D 11 and Vulkan legs run only
  `FullyQualifiedName~Golden|FullyQualifiedName~FoliageGpuTests` on push and pull request, and the full suite
  weekly. The golden-named family is the only per-push GPU coverage on two of the three backends.
- **Guards.** Some goldens assert on the same grid before the compare: `scene3d_sky` (at least 15 foreground
  cells), `scene3d_water` (at least 40 water cells and a brightness spread), `scene3d_bloom` (at least 150
  near-black cells), `scene3d_sky_world_sun` (disc and control patches) and the cascade hand-off framing check.
  The bloom guard is one-sided by design. It bounds too much bloom, not too little.

## 2. Is the comparison right?

### Noise, measured

| comparison | captures | result |
|---|---|---|
| Same backend, bake run `35444309146` (`93eca9c4`) against bake run `35489002211` (`b515000b`) | 42 scenes x 3 backends | 126 of 126 bit-identical |
| Real Metal (this audit) against hosted `macos-26` Metal (bake run `35489002211`) | 18 scenes | 18 of 18 bit-identical |
| Committed grids against green run `35795469769` (`3bdc6d88`, no renderer change since) | 44 x 3 | worst 0.0005 (`scene3d_skinned_normalmap`, hosted Metal), every other 0.0001 or less |
| Committed grids against this machine, `KE_GPU_TESTS=1` | 44 | worst 0.0001 |

Driver noise, in the sense the tolerance was sized for, does not exist on the current legs. A delta is a code,
toolchain or runner-image change. The two recorded toolchain-sized events are the glslang fused multiply-add
drift of the shader toolchain swap (31 grids moved, worst 0.0431,
[#692](https://github.com/APKiwiOrg/KhaozEngine/issues/692) and #724) and a one 8-bit step difference between
the native and incumbent implementations (0.0046, [#1042](https://github.com/APKiwiOrg/KhaozEngine/issues/1042)).

### Across backends

Compared cell for cell, the three committed families agree within 0.0466 on every scene, median 0.0008. The
eight scenes above 0.03 are exactly the eight that keep the default starfield on (`scene3d`, `scene3d_fill`,
`scene3d_hdr_off`, `scene3d_splat`, `scene3d_splat_distance`, `scene3d_textured`, `telegraph_ground`,
`telegraph_modern`). In `scene3d`, 114 of the 119 cells over 0.01 are dark background cells, because the stars
land on different pixels per backend. The other 36 scenes agree within 0.0135. At the pixel level the
backends do differ (WARP against lavapipe reaches 0.79 in the starfield scenes and isolated edge pixels of
0.57 elsewhere). Per-backend families are needed for anything finer than the coarse grid. At 0.06 on the
coarse grid, one shared family would pass all 44 scenes on all three legs.

### Answers

- **Per-backend tolerance: no.** Measured noise is identical on all three legs, and it is zero. A per-backend
  value would have nothing to be calibrated against. Keep one tolerance and the three families.
- **The value: 0.06 is too loose by two orders of magnitude.** 0.01 is 20 times the worst current delta. It
  fails five of the six false passes in section 3 and still passes a one-step rounding change like #1042's.
- **Mean, max-cell or percentile.** The compare already fails on the worst cell. A percentile over cells would
  be strictly weaker and is not recommended.
- **Structural or perceptual terms: supplement, do not replace.** The mean grid is what caught the real port
  defects in section 4. The table scores the false passes and some deliberate subtle changes with candidate
  metrics, each taken between the unmodified and the modified capture on the same device. Coarse values can
  differ from section 3 in the fourth decimal, because section 3 compares against the committed grid.

| change | coarse 32 x 18 (current) | 64 x 36 | per-cell std dev, 32 x 18 | max pixel | pixels over 16/255 | luma SSIM, worst 8 x 8 block |
|---|---|---|---|---|---|---|
| starfield deleted | 0.0355 | 0.0896 | 0.1541 | 0.7882 | 782 | 0.0055 |
| debug ring deleted | 0.0461 | 0.0910 | 0.1675 | 0.6549 | 239 | 1.0000 |
| edge outline off (`scene3d`) | 0.0484 | 0.1036 | 0.1002 | 0.8588 | 1427 | -0.4105 |
| bloom off | 0.0524 | 0.1055 | 0.0414 | 0.5725 | 642 | 0.1278 |
| MSAA off | 0.0258 | 0.0807 | 0.0294 | 0.8471 | 164 | 0.5023 |
| cascade blend band off | 0.0079 | 0.0202 | 0.0192 | 0.2078 | 148 | 0.7861 |
| bloom intensity halved | 0.0219 | 0.0443 | 0.0181 | 0.2039 | 418 | 0.8611 |
| exposure 0.95 | 0.0265 | 0.0353 | 0.0102 | 0.0392 | 0 | 0.9784 |
| same backend, two runs | 0 | 0 | 0 | 0 | 0 | 1.0000 |
| WARP against lavapipe, non-starfield probe scenes, worst per column | 0.0135 | 0.0239 | 0.0270 | 0.5725 | 72 | 0.7283 |

A 64 x 36 grid at 0.06 catches five of the six. A per-channel standard deviation per cell sees the sparse
features strongly. Luma-only terms (SSIM, a luma gradient channel) are completely blind to the debug ring,
because a yellow line on a pale floor is nearly iso-luminant, so any structural term has to be per channel.
Nothing at grid resolution sees the cascade blend band with margin. That feature needs an in-session A/B pixel
assertion, the shape `MsaaResolveTargetGoldenTests` and `GroundDecalVoidGoldenTests` already use.

## 3. The deletion experiment

**Method.** A throwaway test class was compiled into `KhaozEngine.Render.Tests` from outside the tree through
MSBuild's `CustomAfterMicrosoftCommonTargets` hook, so no tracked file changed and no engine code was touched.
For each scene it copies the golden's setup, renders it unmodified first and requires that capture to
reproduce the committed `metal-native` grid (all 18 did, worst 0.0001), then renders each variant with one
feature switched off through settings or scene setup and scores it with `GoldenGrid.Compare` at the committed
tolerance, 0.06. Run with `KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native`, 18 passed, 0 skipped.

PASS means the golden still passes with the feature gone, so the feature is falsely covered. FAIL means the
golden sees the deletion. Margin is the worst cell minus 0.06.

| scene | area | feature removed | worst vs golden | cells over | result | margin |
|---|---|---|---|---|---|---|
| `scene2d` | 2D | the "KE" text string | 0.4128 | 8 | FAIL | 0.353 |
| `scene2d_primitives` | 2D | 3 px rectangle outline | 0.1498 | 26 | FAIL | 0.090 |
| `scene2d_primitives` | 2D | 2 px circle outline | 0.1275 | 15 | FAIL | 0.068 |
| `gui_button_glow` | GUI | both hover glows | 0.1340 | 61 | FAIL | 0.074 |
| `scene3d` | 3D | starfield (the #16 finding, by setting) | 0.0355 | 0 | **PASS** | -0.025 |
| `scene3d` | 3D | debug ground ring | 0.0461 | 0 | **PASS** | -0.014 |
| `scene3d` | post | edge outline pass | 0.0483 | 0 | **PASS** | -0.012 |
| `perspective_outline` | post | edge outline pass | 0.0610 | 1 | FAIL | 0.001 |
| `perspective_outline` | post | crease (normal) term of the outline | 0.0610 | 1 | FAIL | 0.001 |
| `telegraph_ground` | decals | all three decals | 0.3578 | 53 | FAIL | 0.298 |
| `telegraph_ground` | decals | decal outline edges | 0.2470 | 28 | FAIL | 0.187 |
| `telegraph_ground` | decals | circle sweep 0.7 set to 1.0 | 0.1549 | 12 | FAIL | 0.095 |
| `telegraph_ground_void` | decals | void fallback | 0.8075 | 84 | FAIL | 0.748 |
| `scene3d_shadow_map` | shadows | shadow map | 0.1912 | 5 | FAIL | 0.131 |
| `scene3d_cascade_handoff` | shadows | cascade blend band (0.15 set to 0) | 0.0080 | 0 | **PASS** | -0.052 |
| `scene3d_shadow_blob` | shadows | all blob shadows | 0.2255 | 17 | FAIL | 0.166 |
| `scene3d_shadow_blob` | shadows | height fade of the raised caster | 0.1350 | 7 | FAIL | 0.075 |
| `scene3d_water` | water | water plane | 0.4441 | 154 | FAIL | 0.384 |
| `scene3d_water` | water | sun glint | 0.4847 | 144 | FAIL | 0.425 |
| `scene3d_water` | water | shore fade | 0.2116 | 29 | FAIL | 0.152 |
| `scene3d_water` | water | foam | 0.0821 | 4 | FAIL | 0.022 |
| `scene3d_particles_modern` | particles | all particles | 0.5765 | 68 | FAIL | 0.517 |
| `scene3d_particles_modern` | particles | velocity stretch | 0.3383 | 16 | FAIL | 0.278 |
| `scene3d_particles_modern` | particles | soft depth fade | 0.1475 | 3 | FAIL | 0.088 |
| `scene3d_particles_flipbook` | particles | motion-vector warp | 0.5884 | 22 | FAIL | 0.528 |
| `scene3d_particles_flipbook` | particles | frame cross-fade (2.5 set to 2.0) | 0.0823 | 6 | FAIL | 0.022 |
| `scene3d_bloom` | post | bloom | 0.0524 | 0 | **PASS** | -0.008 |
| `scene3d_hdr_msaa` | post | MSAA 4x | 0.0258 | 0 | **PASS** | -0.034 |
| `scene3d_distortion` | post | all distortion sprites | 0.3961 | 32 | FAIL | 0.336 |
| `scene3d_distortion` | post | heat sprite | 0.2715 | 21 | FAIL | 0.212 |
| `scene3d_sky` | sky | sun disc and halo | 0.3351 | 36 | FAIL | 0.275 |
| `tileworld_textured` | tile world | ground textures (flat fallback) | 0.0970 | 295 | FAIL | 0.037 |

Eighteen scenes, 32 deletions, 6 false passes. Every deletion of a whole drawn area (decals, shadow map, blobs,
water, particles, distortion) fails with margin. The six passes are background layers (starfield, debug ring)
and screen-space or post effects (edge outline, bloom, MSAA, cascade blend band). Three of the six delete the
feature their golden was written to lock: `scene3d_bloom` (bloom), `scene3d_cascade_handoff` (the blend band)
and `scene3d_hdr_msaa` (MSAA). `perspective_outline` sees its outline in one cell by 0.001. Four of the fails
are narrow (under 0.025): both outline rows, foam and the flipbook cross-fade.

Subtle changes rather than deletions, same method:

| scene | change | worst vs golden | result |
|---|---|---|---|
| `scene3d` | HDR exposure 1.0 set to 0.95 | 0.0265 | PASS |
| `scene3d` | HDR exposure 1.0 set to 1.05 | 0.0235 | PASS |
| `scene3d` | HDR exposure 1.0 set to 0.90 | 0.0567 | PASS |
| `scene3d` | chroma preservation 0.75 set to 0.5 | 0.0581 | PASS |
| `scene3d` | tonemap operator ACES set to Reinhard | 0.3866 | FAIL |
| `scene3d_bloom` | bloom intensity 0.6 set to 0.3 | 0.0219 | PASS |
| `scene3d_shadow_map` | key light turned about 4 degrees | 0.0663 | FAIL, one cell |
| `scene3d_shadow_map` | shadow strength 0.85 set to 0.6 | 0.0881 | FAIL |

A 10 percent exposure drop and a tonemap default change of the kind 11.7.0 shipped both pass.

**Not measured:** the same deletions on Direct3D 11 and Vulkan. Outside the starfield scenes the families
agree within 0.0135, so the verdicts should transfer, but that is an inference.

## 4. What tier covers what

| feature | golden (section 3) | other GPU tier | net |
|---|---|---|---|
| Starfield | blind | `StarfieldGpuTests`: background-only placement, clear colour, alpha | raw-pixel tier only |
| Debug lines | blind | `DebugWireVolumeGpuTests`: depth routing, occlusion by pixel count | raw-pixel tier only |
| Post edge outline | one cell, 0.001 | orientation rows only. `TargetOutline*` test a different pass | thin |
| Bloom | blind to on, off and intensity | `HdrPipelineGpuTests.Hdr_bloom_extracts_over_range_only` (halo pixels), `BloomGpuTests` (allocation, alpha) | presence covered, strength not |
| MSAA | blind to on and off | `MsaaSceneGpuTests` (edge pixels), `MsaaResolveTargetGoldenTests` (resolve targets), `MsaaResolveWiringTests` (device-free) | covered elsewhere |
| Cascade blend band | blind | none. `ShadowSettingsTests` pins the default value, the framing guard is CPU-side | uncovered |
| Shadow map, strength, light direction | covered | several shadow rows | both |
| Blob shadows, height fade | covered | `BlobShadowReceiverGoldenTests`, `PropBlobShadowGpuTests` | both |
| Water glint, shore fade | covered | none asserts them | golden only |
| Water foam | covered, 0.022 | `WaterShoreGpuTests` (surf, whitecaps) | both |
| Particle stretch, soft depth fade | covered | `ParticleShowcaseGpuTests` dumps images only | golden only |
| Flipbook warp, cross-fade | covered | `FlipbookParticleGpuTests` (`Flipbook_motion_vectors_warp`, `Flipbook_blend_crossfades`) | both |
| Distortion | covered | `DistortionGpuTests` | both |
| Ground decals, edges, sweep, void | covered | `GroundDecalBatchGpuTests`, `TelegraphOutlineGpuTests`, `GroundDecalVoidGoldenTests` (A/B) | both |
| Sky sun disc | covered | `SkyWorldHorizonGoldenTests`, the `scene3d_sky_world_sun` patch guard | both |
| 2D text, thin 2D lines | covered | `DrawStringScaleGpuTests`, `HudTextBaselineGpuTests`, headless `PrimitiveRendererTests` | both |
| GUI hover glow | covered | `GuiDrawGlowTests` checks geometry headless | golden only for pixels |
| Tile ground textures | covered, 0.037 | `TileGroundMaterialGpuTests` (slot selection) | both |
| Whole-frame exposure or tonemap drift up to 10 percent | blind | `TonemapMathTests` (headless maths) | uncovered on the GPU |

**What only the goldens caught, on record.** The native Direct3D 11 backend's device samplers clamped where the
renderers assumed wrap, and `scene3d_texbillboard` (0.393) and `scene3d_particles_flipbook` (0.359) were the
only witnesses (CHANGELOG, native Direct3D 11 sampler entry). The FFT ocean's cross-compiled texture slots
swapped on Metal only, and "an image golden was what eventually caught it" (CHANGELOG, #323 entry). More
generally the golden subset is all the software legs run per push, so a port-level defect on WARP or lavapipe
between weekly full runs is found by a golden or by nothing.

**What the goldens are for:** a per-push, three-backend smoke net for region-scale changes, regression
detection for features that cover a meaningful fraction of a cell with contrast, and reviewable evidence
images. They are the only pixel coverage for water glint and shore fade, particle stretch and soft fade, and
the GUI glow.

**What they are not for:** sparse or thin detail (stars, debug lines, edge outlines), iso-luminant colour
changes, effect strength (bloom intensity, cascade blending), MSAA, anything held in an intermediate target
([#603](https://github.com/APKiwiOrg/KhaozEngine/issues/603), 91 goldens green with an MSAA resolve discarded),
whole-frame drift up to about 10 percent, and correctness at bake time. A golden baked from a broken render
passes forever.

## 5. Is the rebake ritual masking regressions?

| commit | what moved | cause | masked? |
|---|---|---|---|
| `eb145506` (06-16) | `scene3d.direct3d11` | The first WARP bake rendered the box and sphere much brighter than Metal and passed only because each family checks itself. Corrected by eye the same day | yes, a divergent reference |
| `d4a709bf` (07-07) | `scene3d_sky.metal` | The first bake was background-only: the sky pass painted over all geometry. Caught by eye the same day, and the foreground guard was added | yes, a bug baked in |
| `7ddde067` (07-20) | 8 scenes x 3 families, 636 to 1644 of 1728 channels, up to 0.058 | 11.7.0 chroma and 11.9.0 background pass changes shipped under tolerance with no rebake ([#18](https://github.com/APKiwiOrg/KhaozEngine/issues/18)) | no regression, but real changes shipped with no signal and used 97 percent of the tolerance |
| `6ecde5cf`, `ab81e024` (08-12) | `scene3d_sky_world_sun`, 0.769 on all three | The grids baked on 07-16 encoded a depth bug. They were green for about four weeks | yes, a bug baked in |
| `deff6b22` (09-13) | `silhouette_box` x 3 | welded target hull normals (`0b891b7b`) | no, intentional |
| `cfec0023` (09-19) | 21 scenes x 3 | 19.7.0 decal receiver rejection and chroma fit. Adopted only the scenes a same-GPU old against new capture showed moving | no, the ritual working |
| `42566f7f` (09-20) | 11 `metal-native`, 4 per software family, up to 0.0431 | family copies from the deleted incumbents plus toolchain drift, with byte-identical output shown across the default flip | no, attributed and measured |

Masking as driver noise is not the failure mode. Three references were wrong on the day they were baked, and
the mechanism has no way to notice that, because a fresh bake agrees with itself by construction. Two stretches
of real change rode under tolerance with no attribution until someone rebaked. Both failure modes are
tolerance and bake-time problems, and sections 6 and 7 address them.

## 6. Headroom

Current, worst delta against the tolerance of 0.06:

| leg | source | worst | scenes above 10 percent of tolerance |
|---|---|---|---|
| `metal-native`, owning M2 Max | this audit | 0.0001 | 0 of 44 |
| `metal-native`, hosted | run `35795469769` | 0.0005 | 0 of 44 |
| `direct3d11-native`, WARP | run `35795469769` | 0.0001 | 0 of 44 |
| `vulkan-native`, lavapipe | run `35795469769` | 0.0001 | 0 of 44 |

No scene is one small change from red on the noise side. The margin that matters now runs the other way: a
real change can move a scene by up to 0.06 and ship green.

History, worst delta per sampled green run (`golden-deltas-*` artifacts, and #18 for July):

| date | commit | Metal | Direct3D 11 (Veldrid, deleted in 18.0.0) | `direct3d11-native` | `vulkan-native` |
|---|---|---|---|---|---|
| 07-17 | #18, bake run `29567466645` | n/a | 0.046 to 0.058 on 8 scenes, Veldrid Vulkan the same | n/a | n/a |
| 08-10 | `06e2f6f9` | 0.0431 (`scene3d_sky_world_sun`) | 0.0584 (`scene3d_shadow_blob`) | 0.0110 | 0.0110 |
| 08-23 to 09-19 | `e6480b18` to `a8238035` | 0.0431, 12 scenes at or above 0.01 | deleted | 0.0110 | 0.0110 |
| 09-20 | `fc231005` | 0.0431 | deleted | 0.0046 | 0.0046 |
| 09-20 | `42566f7f`, after the rebake | 0.0005 | deleted | 0.0001 | 0.0001 |
| 09-22 | `3bdc6d88` | 0.0005 | deleted | 0.0001 | 0.0001 |

The Veldrid Direct3D 11 leg sat at 97 percent of tolerance on both sampled runs before its deletion, and the
Metal families held a 72 percent scene from 08-10 to 09-20. Both were green throughout.

## 7. Recommended follow-ups

Ranked. Each is sized for one backlog issue.

1. **Tighten the engine golden tolerance to 0.01** (priority/high). Every committed grid reproduces within
   0.0005 on all three legs, and captures are bit-identical across runs and between real and hosted Metal. At
   0.06 the starfield, debug lines, the edge outline, bloom and MSAA can each be deleted, and exposure can drop
   10 percent, with the golden still green. Give `GoldenCompare.Tolerance` its own value of 0.01 instead of
   inheriting `GoldenGrid.DefaultTolerance` (the public consumer default is a separate decision), and write the
   rule that a toolchain, driver or runner-image change which moves a grid is rebaked through the controlled
   same-GPU comparison. At 0.01 five of the six measured false passes fail. The cost is visible rebakes: the
   `metal-native` family as it stood from August to 19 September (12 grids at or above 0.01) would have been
   red until attributed, which is the intent.
2. **Give the cascade hand-off blend a test that can fail** (priority/high). `scene3d_cascade_handoff` passes
   with `ShadowCascadeBlend` at 0 (worst 0.0080) and no other row asserts the band, so the blend has no
   effective coverage. Add an in-session A/B pixel row (blend 0.15 against 0 on one device, pixels over 16/255
   in the band, measured 148 with a maximum of 0.21 on Metal) and correct the test comment that says the golden
   catches a dropped blend.
3. **Add a negative control to each committed-grid golden** (priority/medium). A shared helper renders the
   scene with the thing it exists to show removed and asserts the compare fails by at least twice the
   tolerance. It would have flagged the six false passes here at authoring time, and a control that removes the
   geometry would have failed on the background-only `scene3d_sky` bake. Cost is one more render per golden
   (the whole golden family takes 3 to 7 s on real Metal today), and the software legs could run the controls
   weekly only. **A standalone sensitivity probe tool is not worth building.** The harness used for this record
   needed a copied scene per golden and would drift from the tests it copies. The control belongs beside the
   scene it tests.
4. **Make edge outline coverage more than one cell** (priority/medium). `perspective_outline` sees the outline
   pass removed in one cell by 0.001, `scene3d` does not see it at all (0.0483), and no other row asserts
   `Post.Outline` pixels. Add an in-session presence row (outline on against off, 1427 pixels over 16/255 in
   `scene3d`) or record that item 1 is the fix.
5. **Check family agreement at bake time** (priority/medium). A device-free row asserting that each scene's
   three family grids agree within a bound (0.05 today, 0.02 after item 6) would have caught the divergent
   `scene3d.direct3d11` bake. It cannot catch a bug baked into all three alike, which is what item 3 is for.
6. **Pin the starfield off in the eight legacy goldens** (priority/medium). `scene3d`, `scene3d_fill`,
   `scene3d_hdr_off`, `scene3d_splat`, `scene3d_splat_distance`, `scene3d_textured`, `telegraph_ground` and
   `telegraph_modern` render the default starfield. The grid cannot see it deleted (0.0355), yet it carries all
   of the cross-family spread (0.039 to 0.047, against 0.0135 or less on the other 36), and these are exactly
   the eight scenes that drifted in #18. Pin it off as every newer golden does, and rebake under the controlled
   comparison. `StarfieldGpuTests` stays the net for the stars.
7. **Correct the golden coverage claims** (priority/low). The `Golden3D_CascadeHandoff` comment says a dropped
   blend moves cells well past tolerance. The `Golden3D_Bloom` comment says the golden locks bloom on.
   `docs/USING-KHAOZENGINE.md` says the `scene3d_hdr_msaa` golden proves the resolve. The `GoldenCompare` class
   summary says a blend regression moves a cell well past tolerance. `Golden3D_FixedAsymmetricScene` still says
   it is expected to fail locally until a rebake lands. Each should state what section 3 measured.
8. **Evaluate a per-channel structure term for the grid** (priority/low). Only if items 1 and 3 leave blind
   spots. A per-cell, per-channel standard deviation sees the starfield (0.154), the iso-luminant debug ring
   (0.168) and the outline (0.100) with zero same-backend noise, where luma SSIM and a luma gradient miss the
   ring entirely. It changes the committed format (a versioned header and 6 floats a cell), so it wants a design
   note before code.
9. **Settle where `metal-native` may be baked** (priority/low). `docs/CONTRIBUTOR-RULES.md` says a changed
   golden is baked by the relevant CI leg, #724 and `42566f7f` forbid baking `metal-native` on the hosted leg,
   and the 19.7.0 set did exactly that. Hosted captures are bit-identical to the owning M2 Max on 18 of 18
   probed scenes and within 0.0005 on all 44 grids. Pick one rule and state it in one place.

## 8. Limits of this record

- The deletions ran on Metal only. Direct3D 11 and Vulkan numbers come from CI artifacts, which carry the
  worst delta per scene and the bake images, not ablations.
- No pair of captures across a toolchain change exists, so pixel-level movement at a toolchain bump is
  unmeasured. The coarse-grid movement is recorded in #692 and #724.
- The property goldens (`SplatTerrainGoldenTests`, `SplatTerrainDistanceGoldenTests` and similar) assert
  thresholds rather than a committed grid and were not ablated.
- The carry-over comment on #19 cites MM6 for the same blindness seen from the other direction. MM6 in the
  Metal design is the two-uniform-buffer probe and does not involve the goldens. The recorded measurement of
  that blindness from the intermediate-target side is #603.
