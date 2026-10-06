# Source and evidence anchors for the negative-control design

Read at worktree HEAD 511cfbe00f70fb0b12a954c26bf679b66ef695a3 (code checkpoint be72718673d79d83d34d3467a262e95bbe845151).
Read only. Nothing was built, run, captured or edited outside this SDD directory.

## Active receiver path

| Fact | Anchor |
| --- | --- |
| Every shadowed point light samples through `samplePointShadowCombined` | `KhaozEngine.Render3D/Internal/ShaderSources.Lighting.cs:532-534` |
| `params.w < 0` (no transient row) returns to the base-only `samplePointShadow` | `ShaderSources.Lighting.cs:416-417` |
| Soft is selected by `PointShadowFilter.x >= 0.5` | `ShaderSources.Lighting.cs:308-312` |
| Soft path: basis, dither rotation, search angle, early-out, kernel angle | `ShaderSources.Lighting.cs:245-271` |
| Blocker search, 6 taps, ring radii 0.55 and 1.0, 72 degree spacing | `ShaderSources.Lighting.cs:207-221` |
| Filter disc, 9 taps, ring radii 0.6 and 1.0, 45 degree spacing, compare then average | `ShaderSources.Lighting.cs:227-237` |
| Per-tap face select and half-texel inset clamp inside the chosen cell | `pointShadowDepthAt`, `ShaderSources.Lighting.cs:179-188` |
| Face convention table (faces 0..5 are +X, -X, +Y, -Y, +Z, -Z) | `pointShadowFace`, `ShaderSources.Lighting.cs:158-172` |
| Combined (transient) variants exist but are not reached by this scene | `ShaderSources.Lighting.cs:324-421` |
| Transient row is -1 unless a static request touches a skinned caster | `KhaozEngine.Render3D/Scene3D.SkinnedPointShadows.cs:77-99`, `Internal/PointShadowTransientRows.cs:15-16` |
| `ShadowParams.w` packs the transient row, -1 when absent | `Rendering/ModelRenderer.PointLightBuffer.cs:54-59`, `Rendering/ModelRenderer.PointShadowUniforms.cs:143-144` |
| Retained manifest: `resolved.transientAtlasRows` 0, `softShadowedLights` 1, `plainShadowedLights` 0 | artifact `point-shadow-seam.json`, section `scene` |

## Shader binding and mutation seams

| Fact | Anchor |
| --- | --- |
| `LightingCommonGlsl` is a `public const string` spliced into `ModelFrag` at compile time | `ShaderSources.Lighting.cs:26-42` |
| The model pipeline compiles const `ModelVert` and `ModelFrag`, no runtime source override | `Rendering/ModelRenderer.cs:291` |
| No supported source-override hook was found in the examined receiver construction path | `git grep` over `KhaozEngine.Render3D/*.cs` and `KhaozEngine.Gpu/*.cs` returned nothing |
| Existing tests read `ShaderSources` text only, they do not render a modified source through Scene3D | `KhaozEngine.Render.Tests/Render3D/PointShadowFilterShaderTests.cs:51-112`, `Gpu/MslBindingOrderGuardTests.cs:66,107` |
| Isolated never-integrated proof commit precedent | findings doc, proof 8632b57391f1a0cf6d67bf77430c578f8d4fba50 |

## Scene, camera, capture and evidence seams

| Fact | Anchor |
| --- | --- |
| Seam case: light (0,5,0), radius 30, intensity 1, Soft, light size 0.5 m, max penumbra 16 texels, id 307 | `KhaozEngine.Render.Tests/Gpu/PointShadowFilterGpuTests.cs:245-257` |
| Across axis, window 0.9 m, 13 stations at x = 2.9 + 0.1 s on z = 1.5 x, 61 probes | `PointShadowFilterGpuTests.cs:261-286` |
| Probe read: `(int)` truncation of `fixture.Pixel` | `PointShadowFilterGpuTests.cs:69-77` |
| Reach `sum * 2 * window / probes`, detrend, bump stations 3 to 5, 8 mm assertion | `PointShadowFilterGpuTests.cs:283-321` |
| Each capture starts cold (atlas released, warm-up frame, then capture) | `KhaozEngine.Render.Tests/Gpu/PointShadowScene.cs:218-245` |
| `FrameOn(centre, halfExtent)` calls `Camera.Frame(centre, (2h, 0.2, 2h), margin 1)` | `PointShadowScene.cs:274-278` |
| `Frame` sets `Target = center` and fits `OrthoSize` from view-space corner offsets | `KhaozEngine.Render3D/Camera/IsoCamera3D.cs:79-97` |
| Screen mapping `px = (ndc.x*0.5+0.5)*W`, `py = (1-(ndc.y*0.5+0.5))*H`, top-left origin, y down | `KhaozEngine.Render3D/Camera/CameraProjection.cs:12-24` |
| Dither is anchored to absolute world position, not screen | `pointShadowDither`, `ShaderSources.Lighting.cs:197-200` |
| Per-directory evidence options from an injected lookup | `PointShadowSeamEvidenceOptions.Read(Func<string,string?>)`, `KhaozEngine.Render.Tests/Gpu/PointShadowSeamEvidenceOptions.cs:55` |
| Evidence run and record constructors used by the seam case | `PointShadowSeamEvidenceRun.cs:29-54`, `PointShadowSeamEvidence.cs:43` |
| Offline pixel-centre convention n+0.5 | `docs/superpowers/plans/proofs/2026-10-07-point-shadow-seam-offline.py:40-42` |

## Retained evidence values used as pins

| Value | Source |
| --- | --- |
| Run 37482238342, job 112333092389, artifact 11421552678, archive SHA-256 ef5ce8d9e72cf5f0d2638aedb0537e48dc80094b95ac73c5313b04d58a4743b5 | `2026-10-07-point-shadow-seam-hosted.json` |
| Device Direct3D11Native by environment override, Microsoft Basic Render Driver, software adapter, depth 0..1, clip Y not inverted | manifest `device` |
| Settings: faceResolution 256, bias 0.01, slopeBias 0.02, Soft, lightSizeMetres 0.5, maxPenumbraTexels 16, maxShadowedLights 8 | manifest `scene.settings` |
| Projection M11 = M22 = 0.5555556, VP M32 = -0.54206854, render origin (0,0,0), eye (3.6, 48.78617, 16.350332) | manifest `camera` |
| Plain capture SHA-256 47c21b30356519c648ec1da4df70d00bb21cb55aed9560f7bfb879d5eb8b98f7 | manifest `captures` |
| Soft capture SHA-256 ae1914c9d740bee9c730689c6451cf4262602c3def73247aedfd00cc518da405 | manifest `captures` |
| Bump -0.008728723 m, worst elsewhere 0.008491099 m | manifest `statistic` |
| Hosted job 2 min 11 s: restore and compile 90 s, single probe step 20 s | `gh run view 37482238342` job step timestamps |
| No clamped-kernel calibration was found in the examined origin commit and retained artifacts | e7966418877ccee4d2b756d33972adc57595a1c6 message and stat |

## Root verified additions for matched atlas data

- `Scene3D.PointShadowPass.cs:328`, `DebugReadPointShadowAtlas(out int width, out int height)`
  performs an R32Float staging copy and returns row-major float samples. This existing internal
  hook is called by `PointShadowPassGpuTests`, so no new production API is required.
- `Scene3D.SkinnedPointShadows.cs:37`, `PointShadowBaseSlotForLight(int lightIndex)` identifies
  the active base row. `Scene3D.PointShadowPass.cs:50,57` exposes allocated and bound atlas handles.
- `Rendering/PointShadowAtlas.cs:7,90` pins six face columns and R32Float. A 256 by 256 face
  row is 6 * 256 * 256 * 4 = 1,572,864 bytes. Eight active rows total 12 MiB.
- The existing RGBA-only `GpuReadback.ToRgba` cannot read this R32Float atlas and must not be used.

Root narrowed two claims in the design. Four chosen phases do not prove a continuous worst case.
Detecting a deliberately bad control and measuring a baseline exceedance still does not uniquely
identify the production shader as the cause. No new capture or shader compilation was performed.
