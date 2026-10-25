# Shadow visibility guard control

## Scope and provenance

Task 1 ports the useful guards from read-only archive `264bbc11c`. The unchanged renderer is
`8f11c913a`, with the temporal shader sources and `TemporalResolveTuning.cs` identical to `v21.4.0`.
No renderer, tuning, existing bound, golden, package version or changelog changed.

Source fixtures are `KhaozEngine.Render.Tests/Gpu/GameShadowGrainScene.cs`,
`GameShadowGrainMeasure.cs`, `TexturedShadowGrainCase.cs`, `TemporalGameShadowGrainGpuTests.cs`
and `TemporalTextureLightGpuTests.cs` at `264bbc11c`. Their control record is
`KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/lock-luma-kill/README.md` at that commit.
The implementation-specific assertions in `TemporalResolveLumaLockGpuTests.cs` and optional
`TemporalShadeEvidence.cs` were read without being ported.

The fixture is deterministic and headless. It renders at 384 x 256, measures frames 80 to 95,
uses a 4x-per-axis spatial reference without temporal resolve or held light, and preserves the
archive's receiver mask, texture, frame alignment and lighting. Every capture resets the scene
through an off frame and camera cut. Environment-controlled sway, evidence dumping and the
instrumented release mask were removed. Both shared-scene parity facts remain.

## Device and run

Measured on 2026-10-19 with `KE_GPU_TESTS=1` and `KE_GRAPHICS_BACKEND=metal-native`.
The fixture reported `MetalNative on Apple M2 Max, hardware`.

```bash
build-slot --label taa-render-build -- \
  dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release

build-slot --heavy --label taa-focus-gpu -- \
  env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native \
  dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj \
  -c Release --no-build \
  --filter 'FullyQualifiedName~TemporalGameShadowGrainGpuTests|FullyQualifiedName~TemporalTextureLightGpuTests' \
  --logger 'console;verbosity=detailed' \
  --logger 'trx;LogFileName=task-1-metal.trx' \
  --results-directory /tmp/khaozengine-taa-shadow-visibility/task-1-results
```

The Release build exited 0 with zero warnings and errors. The focused GPU command exited 1,
with four passed facts and the single intended textured-grain RED. TRX counters confirm
five selected, five executed, one failed and zero skipped or not executed.

## Textured shadow RED

The moving coarse shadow advances four display pixels per frame with shadow hold on.
Grain is mean absolute output luma minus its 3x3 median in a 12-pixel reference-edge band.
All values below are absolute luma values in 0..1, printed to six decimal places.

| Preset | Output grain | Error-image grain | Reference grain | Output change | Bound | Result |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Native | 0.012026 | 0.007782 | 0.009643 | 0.008048 | 0.011300 | RED |
| Quality | 0.010852 | 0.009786 | 0.009643 | 0.007377 | 0.008000 | RED |
| Balanced | 0.010526 | 0.006326 | 0.009643 | 0.001635 | Report only | Measured |

The failure is the intended behavioral prerequisite for later tasks, not a setup or compilation error.
The unchanged implementation exactly reproduces the archive's Native and Quality control values.
No skip or raised bound hides the failure. Balanced retains existing coverage elsewhere.

```text
Failed Texture_ridges_under_a_coarse_moving_shadow_do_not_hold_bright_grain
Native grain 0.012026 exceeds 0.011300.
Quality grain 0.010852 exceeds 0.008000.
Test Run Failed.
Total tests: 5
     Passed: 4
     Failed: 1
```

## Dark and local-light controls

Key and fill lights are off. Dark uses ambient 0.08 and point intensity 0.03. Flicker uses
ambient 0.03 and point intensity `0.4 + 0.35 sin(0.8 n)`. Moving uses ambient 0.03 and point
intensity 0.5 at `x = 0.6 sin(0.18 n)`.

Each error, error-image grain and temporal-error assertion remains `actual <= 1.1 * before`.
The six `before` tuples are copied verbatim from the archive's unchanged 21.4.0 hardware
controls, not inferred from a candidate. The actual values below match those controls at
six printed decimal places. Assertions use the unrounded measurements.

| Case | Preset | Mean absolute error | Error-image grain | Temporal error | Reference mean luma | Result |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| Dark | Native | 0.000360 | 0.000356 | 0.000045 | 0.004197 | Pass |
| Dark | Quality | 0.001003 | 0.000877 | 0.000085 | 0.004197 | Pass |
| Flicker | Native | 0.014685 | 0.005503 | 0.010689 | 0.024637 | Pass |
| Flicker | Quality | 0.016049 | 0.005340 | 0.011537 | 0.024637 | Pass |
| Moving | Native | 0.010694 | 0.003822 | 0.003290 | 0.021647 | Pass |
| Moving | Quality | 0.012462 | 0.004421 | 0.003293 | 0.021647 | Pass |

Dark reference mean luma is 0.004197 at both presets, within the required 0.001 to 0.08.
WARP and lavapipe still execute and report their metrics and dark non-black proof, but their
paired error/grain/temporal-error bounds remain report-only until unchanged-resolve controls
for those device classes are recorded. Hardware controls must not silently gate software devices,
and candidate output must never supply missing controls.

## Fact inventory and handoff

| Class | Fact | Result |
| --- | --- | --- |
| TemporalGameShadowGrainGpuTests | Coarse_shadow_texels_on_a_swaying_faceted_receiver_report_grain_against_fxaa | Pass |
| TemporalGameShadowGrainGpuTests | Texture_ridges_under_a_coarse_moving_shadow_do_not_hold_bright_grain | Expected RED |
| TemporalGameShadowGrainGpuTests | Shared_scene_matches_a_fresh_scene_after_changes_of_size_hold_and_temporal_mode | Pass |
| TemporalTextureLightGpuTests | Dark_texture_and_animated_local_light_keep_detail_stable | Pass |
| TemporalTextureLightGpuTests | Local_light_state_leaves_the_shared_scene_equal_to_a_fresh_scene | Pass |

The renderer remains unchanged. The open textured-grain prerequisite belongs to the later filter and
visibility tasks. This intermediate branch is not ready for main integration. Task 11 owns the final
wide gate, and later unchanged-resolve device controls remain required before software gating.
