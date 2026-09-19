# 19.7.0 rendering reference validation

This record explains the reference scope for the oldest-first backlog batch. The living rendering
contracts are in the Render3D README and `docs/USING-KHAOZENGINE.md`.

## Controlled comparison

Two strict Metal captures ran on the same machine with the same golden-test filter. The control used
`v19.5.0` at `96387259`. The new capture used the integrated batch code at `16eec4b4`, which was
staged as 19.6.0 at the time and ships as 19.7.0.
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
passed. The HDR ratio and surf-gradient probes passed. Four native streaming regressions were run on
Metal at that point and reported as passing. Two of them did not survive the verification run below,
so treat that particular line as superseded by the section that follows.

The GLSL edits change exactly four fragment programs in each HLSL, MSL and SPIR-V hash table:
`GroundDecal`, `PostTonemap`, `Water` and `WaterClipmap`. No vertex program or compiler option changed.

The bake completed all selected images. Metal and Vulkan passed their full bake legs. WARP failed one
existing HDR-disabled point-shadow seam probe after producing its images. That result is tracked in
[issue 1024](https://github.com/APKiwiOrg/KhaozEngine/issues/1024). Its bound and shader were not changed
as part of the reference update. The separate verification run also exercises that probe.

## Verification run

The comparison run this record asked for is
[run 35471553973](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/35471553973), on the batch
merged with a `main` that was at 19.6.0. It runs the full suite on every leg, which is what a
dispatch does,
and it verifies the committed references rather than writing them.

It passed on `metal-native`, on `vulkan-native` and on the Vulkan sync-validation leg. The
`direct3d11-native` leg ran 7,775 render tests with nothing skipped and failed exactly one, the
point-shadow seam probe covered in the section below, at the same residual that issue 1024 already
records. Every committed reference therefore verifies on all three backends, and both terrain
regressions are gone.

The first attempt at that run, [35446751621](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/35446751621)
on `cfec0023`, failed on all three backends with the same two headless terrain regressions:

- `Scene3DChunkSinkTests.ReLod_DecorToGameplay_GainsCollidersThenLosesThemOnRetreat`
- `PropClusterRendererTests.Decor_first_then_gameplay_refreshes_props_without_replacing_the_hlod_handle`

Both reproduce locally in Release. The cause was in `Scene3DChunkSink.ReLod`, which derived its
`ChunkBuildReason` by testing the tier before the ring. A chunk entering the gameplay ring almost
always moves to a finer tier in the same call, so that transition was classified `TierChange` and took
the placement-reuse path added for #101. A decor chunk holds no placements, so the promoted chunk
arrived with no props, no prop static bodies and no terrain collider. `TerrainStreamer` already
classified the ring first at both of its own sites, so the sink was the one that disagreed. The fix
puts the ring test first, and the apply side needed no change because it already branches on whether
the ring moved.

This is the case for running a dispatched full suite before a merge rather than trusting the push
legs. The `direct3d11-native` and `vulkan-native` legs carry `fullSuite: scheduled`, so a push or a
pull request runs the golden subset on them. Neither of these two tests is in the Golden family, and
neither is a golden image, so no push run on this branch would have gone red.

## WARP point-shadow probe

[Control run 35446943072](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/35446943072) checked
out `v19.5.0` on `windows-latest` under the same `KE_D3D11_ADAPTER=warp` pin and ran the point-shadow
fixture against that unchanged tree. It failed there too, which rules this batch out as the cause,
including the renderer resource retirement change that issue 1024 had not yet excluded. The bound and
the shader stay as they are. The control harness is kept on the `fix/oldest-warp-control` branch so
the next attempt at 1024 can re-run it.
