# 19.6.0 rendering reference validation

This record explains the reference scope for the oldest-first backlog batch. The living rendering
contracts are in the Render3D README and `docs/USING-KHAOZENGINE.md`.

## Controlled comparison

Two strict Metal captures ran on the same machine with the same golden-test filter. The control used
`v19.5.0` at `96387259`. The new capture used the integrated 19.6.0 code at `16eec4b4`.
Both used `KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1` with `FullyQualifiedName~Golden`.
The control passed 124 tests and the new capture passed 125. Each skipped only the cross-backend
comparison, which is deliberately disabled while references are being written.

The comparison uses the maximum absolute RGB-channel difference between corresponding 32 by 18 grid
cells. These are normalized channel values, not distances or a perceptual quality score. Twenty-one
of the 42 scenes changed on the same GPU. That list, rather than every difference against an older CI
reference, determines which images this batch may replace.

| Scene | Maximum same-GPU channel difference |
|---|---:|
| `collision_overlay` | 0.0035 |
| `perspective_outline` | 0.0646 |
| `scene3d` | 0.0677 |
| `scene3d_beam` | 0.0319 |
| `scene3d_bloom` | 0.0886 |
| `scene3d_cascade_wide` | 0.0082 |
| `scene3d_distortion` | 0.2863 |
| `scene3d_hdr_bloom` | 0.2275 |
| `scene3d_hdr_msaa` | 0.1726 |
| `scene3d_particles_attractor` | 0.0006 |
| `scene3d_particles_flipbook` | 0.0823 |
| `scene3d_particles_modern` | 0.0129 |
| `scene3d_shadow_blob` | 0.0860 |
| `scene3d_shadow_map` | 0.0755 |
| `scene3d_sky` | 0.0755 |
| `scene3d_splat_shadow` | 0.0354 |
| `scene3d_texbillboard` | 0.0653 |
| `scene3d_textured` | 0.0351 |
| `silhouette_box` | 0.0110 |
| `telegraph_ground` | 0.3253 |
| `telegraph_modern` | 0.0064 |

HDR-off, water, sky-world-sun, the 2D scenes and the other unchanged controls retain their existing
references. In particular, the CI bake differed slightly from the old HDR-off reference even though the
same-machine old/new pair did not change it. Copying the whole artifact would have absorbed that drift.

## Backend evidence

The per-backend reference bytes come from the three-family
[bake run 35444309146](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/35444309146), pinned to
`93eca9c4`, which contains the same rendering and shader changes. Later streaming/editor integration
changes do not change these fixed-scene shader inputs. Only the selected scene names are copied from
its artifacts. A separate comparison run must verify the committed references before merging main.

The native Metal cliff-region, horizontal-ground-dip, rigid-top, rigid-side and CPU/GPU-skinned tests
passed. The HDR ratio and surf-gradient probes passed. Four native streaming regressions also passed,
including live LOD-table placement and collider-handle retention.

The GLSL edits change exactly four fragment programs in each HLSL, MSL and SPIR-V hash table:
`GroundDecal`, `PostTonemap`, `Water` and `WaterClipmap`. No vertex program or compiler option changed.

The bake completed all selected images. Metal and Vulkan passed their full bake legs. WARP failed one
existing HDR-disabled point-shadow seam probe after producing its images. That result is tracked in
[issue 1024](https://github.com/APKiwiOrg/KhaozEngine/issues/1024). Its bound and shader were not changed
as part of the reference update. The separate verification run also exercises that probe.
