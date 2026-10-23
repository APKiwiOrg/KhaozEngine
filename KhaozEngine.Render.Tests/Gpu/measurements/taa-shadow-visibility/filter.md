# Bilinear 3x3 comparison PCF

## Scope and provenance

Task 2 replaces the cascade filter `pcfCascade` with the bilinear 3x3 comparison filter of design section 4.
The filter now lives in `KhaozEngine.Render3D/Internal/ShaderSources.ShadowVisibility.cs` as
`CascadeShadowFilterGlsl`, spliced verbatim into `LightingCommonGlsl`. The move was made first as a pure text
move, and the MSL, SPIR-V and HLSL byte-equality pins passed unchanged on it before the filter changed.

The signature `pcfCascade(texture2D, sampler, int cascade, int count, vec2 uv, float depth, float texelStep)` is
unchanged, and so are the `sampleKeyShadow` call sites. Bias, normal offset, strength, cascade selection, coverage
fade and hand-off blend are untouched. No resolve source, tuning value, bound, golden or package version changed.

The archive diagnostic `shadow-bilinear-3x3` on `fix/taa-grain-switches` (commit `a787c4076`) was read for
reference. Its preprocessor switch was not carried over. The shipped filter has no `#ifdef` and no diagnostic token.

## The filter

For texel coordinate `t = uv / texelStep - 0.5`, `base = floor(t)` and `f = t - base`. The nine comparison taps at
offsets `{-1,0,1}` squared each interpolate four point comparisons. Together they read the 4x4 texels
`base - 1` to `base + 2` on each axis, weighted `[1-f, 1, 1, f]`. The X and Y weights multiply, the sixteen
weighted 0 or 1 results sum, and the sum is divided by nine. Stored depths are never interpolated.

Every texel centre clamps to `[halfTexel, 1 - halfTexel]` inside its own cascade column before mapping to atlas U,
exactly as the point filter clamped its taps. On a texel centre `f` is zero, the fourth texel carries no weight and
the result is the old 3x3 point kernel. The fetch is `textureLod` at level 0. The atlas has one level, so this reads
the same texel as `texture` did, and it stays defined in the hand-off branch where implicit derivatives are not.

`projectCascade` rejects any lookup within two texels of a column border, for the selected and the blended
cascade. That keeps `base` in `[1, resolution - 3]`, so in shipped use the footprint never reaches a clamp. The clamp
is retained as the defensive guarantee the old filter gave, and the oracle exercises it directly.

## Oracle and case set

`KhaozEngine.Render.Tests/Gpu/ShadowFilterOracle.cs` writes the definition out longhand on the CPU. Its atlas is
four 8x8 cascades in one 32x8 R32F texture. Depths are 0.25, 0.5, 0.75 and 1.0 (the clear value), so lit texels,
occluded texels and clear holes all appear. Both edges of an even column are occluders and both edges of an odd
column are clear, so a tap that crossed a column instead of clamping changes the answer.

The 122 cases are dyadic and exact in float. Per cascade they cover a texel centre, offsets of plus and minus a
quarter texel on each axis, half and three quarter texel fractions, both corner centres, two lookups past the
corners, and eight positions across every column border on each axis, including half a texel past the last centre.
Four cases put the receiver exactly on a stored depth, and a CPU fact proves each one lands on a weighted texel that
counts as lit. Six cases blend each adjacent cascade pair at weights 0.25 and 0.5 through the `sampleKeyShadow` mix.

`TemporalShadowFilterGpuTests` splices the shipped filter into a fullscreen kernel, binds the atlas through the
receivers' clamp and point sampler, evaluates one case per pixel and stores the float result's bits across four
RGBA8 bytes. The readback is exact. The tolerance of 0.000001 covers the final division and the mix only. The
smallest step between candidate filters on this atlas is 1/144.

CPU facts in `KhaozEngine.Render.Tests/Render3D/ShadowShaderContractTests.cs` pin the weights `[1-f, 1, 1, f]`, a
total weight of nine at every tested fraction, the divide by nine, the column clamp and the splice into the lighting
block. They also prove that the point filter equals the oracle on every texel centre case, at the borders included,
and misses it off centre in every cascade.

## RED and GREEN

All GPU runs used `KE_GPU_TESTS=1` and `KE_GRAPHICS_BACKEND=metal-native` on the device the fixture reported as
`MetalNative Apple M2 Max, hardware`. Each command ran once through `build-slot`.

| Run | Filter | Result |
| --- | --- | --- |
| CPU focus | point filter | 2 of 45 failed: the weight contract and the explicit fetch |
| GPU filter readback | point filter | Missed the oracle in 72 of 122 cases |
| CPU focus | bilinear filter | 45 of 45 passed |
| GPU filter readback and hand-off golden | bilinear filter | 4 of 4 passed, 0 misses in 122 cases, worst error 0.0000001 |

Examples of the point filter's misses, GPU against oracle: cascade 0 at +0.25 texel in y read 0.3333333 against
0.3055556, at +0.75 x and +0.25 y read 0.5555556 against 0.6041667, and half a texel past the last corner read
0.3333333 against 0.2083333.

## Gather disposition

Not adopted. The explicit sixteen fetches ship. The gather form stays as a test-only candidate,
`TemporalShadowGatherCompileTests.GatherFilterGlsl`, until the hosted runs settle it.

A gather always returns four distinct neighbouring texels, so it cannot repeat an edge texel the way the clamped
explicit footprint does. The candidate therefore clamps a 4x4 window into the column and folds each axis's four
weights onto the texel each clamped centre lands on. Four gathers on the window's interior texel corners then read
it once each, with no overlap and no read outside the column. In the range `projectCascade` admits the fold is the
identity.

| Evidence | Where it ran | Result |
| --- | --- | --- |
| GLSL to SPIR-V | macOS, shipped front end | `OpImageGather` present, no explicit fetch |
| SPIR-V to MSL | macOS, pinned SPIRV-Cross | `.gather(` emitted, binding order check passed |
| SPIR-V to HLSL | macOS, pinned SPIRV-Cross | `.GatherRed(` emitted |
| FXC `ps_5_0` | Windows only | Pending, `D3D11FxcValidationTests.TheShadowFilterKernels_ExplicitAndGather_SurviveFxc` |
| Component order, asymmetric 2x2 | Metal hardware | Read (153, 204, 102, 51), the documented x (i0,j1), y (i1,j1), z (i1,j0), w (i0,j0) |
| Footprint readback, 122 cases | Metal hardware | 0 misses, worst error 0.0000001 |
| Component order and readback | Direct3D 11 and Vulkan | Pending hosted correctness |

The design admits gathers only when all three native backends compile them and reproduce the oracle. Two of the
three are unproved here, and the hosted runs are the orchestrator's. Both gather facts are in the hosted selections
by name, so a later run can settle the disposition without new code. A gather source operation is not evidence of
fewer memory transactions or of a speedup.

## Source operation counts

Counts are per cascade lookup in the shader source. A blended receiver does two lookups. These are not timings and
no frame cost was measured.

| Filter | Depth reads | Comparisons | Blended receiver reads |
| --- | ---: | ---: | ---: |
| Point 3x3, before | 9 point fetches | 9 | 18 |
| Bilinear 3x3, shipped | 16 point fetches | 16 | 32 |
| Gather candidate, not shipped | 4 gathers of 4 texels | 16 | 8 gathers |

The gather candidate also spends four loop iterations per axis folding weights. In the readback kernel, which calls
the filter twice, the compiled SPIR-V holds 2 `OpImageSampleExplicitLod` for the shipped filter and 2 `OpImageGather`
for the candidate, one per inlined call with the loops not unrolled.

## Shadow and golden selection

One Metal process ran the archive's guard selection
`((FullyQualifiedName~Temporal&FullyQualifiedName~GpuTests)|FullyQualifiedName~Shadow|FullyQualifiedName~Golden)`
with `KE_GOLDEN_EVIDENCE_DIR` set and nothing rebaked. TRX: 1394 total, 1394 executed, 1392 passed, 2 failed,
0 not executed.

The golden worst-cell deltas for the 58 goldens in the archive record equal the archive's `shadow-bilinear-3x3`
record line for line. The one extra line, `temporal_mipbias_checker_performance`, is a golden newer than the archive
and reads 0. The largest are `scene3d_sky` and `scene3d_shadow_map` at 0.0007, `scene3d_splat_shadow`,
`scene3d_skinned_normalmap` and `scene3d_cascade_wide` at 0.0005, and `scene3d_cascade_handoff` at 0.0004. Every
golden passed its tolerance.

The two failures are expected and their assertions are unchanged.

`TemporalGameShadowGrainGpuTests` is the Task 1 RED by design. Native grain is 0.011770 against 0.0113 and Quality
is 0.010593 against 0.008, down from 0.012026 and 0.010852 under the point filter. Balanced reads 0.010441. These
equal the archive's diagnostic 0.01177 and 0.010593.

`TemporalMovingShadeRimGpuTests` misses on the old point-filter input, which is the reason for Task 3. All 56 rim
rows equal the archive's `shadow-bilinear-3x3` rows. The acceptance miss is Quality M 0.5 leading flat, error
0.010485 against its derived bound `(0.009307 + 0.0000005) * 1.1 = 0.01023825`. The pinned spatial references
change in 24 rows, identically at Native and Quality:

| Row | Point-filter reference | Bilinear reference |
| --- | ---: | ---: |
| M 0.5 trailing textured | 0.018943 | 0.018951 |
| M 1.0 trailing textured | 0.035586 | 0.035615 |
| M 2.0 leading flat | 0.006865 | 0.006928 |
| M 2.0 trailing textured | 0.056131 | 0.056194 |
| M 4.0 leading flat | 0.031935 | 0.032271 |
| M 4.0 trailing textured | 0.066148 | 0.066177 |
| M 8.0 leading flat | 0.088200 | 0.088301 |
| M 8.0 trailing textured | 0.063109 | 0.063160 |
| MS 1.0 leading flat | 0.008146 | 0.008074 |
| MS 1.0 trailing textured | 0.038593 | 0.038637 |
| MS 2.0 leading flat | 0.013942 | 0.014041 |
| MS 2.0 trailing textured | 0.058579 | 0.058518 |

The references are the non-temporal supersampled scene, so they move because the filter input moved, not because
the resolve did. Task 3 re-records them and the rim baseline on the bilinear input before any signal work.

## Softness

The archive measured softness as FXAA display-pixel transect widths between 10% and 90% attenuation in the real game
scene. Its back15 widths were 12.41 character and 6.27 ground pixels for the point filter against 12.33 and 6.24
for shared 3x3. That procedure needs the game probe and was not repeated here. The engine-side evidence that the
look is preserved is the identical golden deltas and rim rows above, and the oracle's proof that the footprint and
its weights are the archive filter's.

## Shader hash pins

The MSL, SPIR-V and HLSL byte-equality tables were rebaked with `KE_UPDATE_MSL_HASHES`, `KE_UPDATE_SPIRV_HASHES`
and `KE_UPDATE_HLSL_HASHES`. Each moved the fragment stage of the same 15 lit programs and nothing else: `Foliage`,
`FoliageMotion`, `Model`, `ModelCpuSkinnedDissolveMotion`, `ModelCpuSkinnedMotion`, `ModelDissolve`, `ModelMotion`,
`SkinnedModel`, `SkinnedModelDissolve`, `SkinnedModelDissolveMotion`, `SkinnedModelMotion`, `Splat`, `SplatMotion`,
`TileGround` and `TileGroundMotion`. These are the programs that splice the lighting block. No vertex stage moved.
