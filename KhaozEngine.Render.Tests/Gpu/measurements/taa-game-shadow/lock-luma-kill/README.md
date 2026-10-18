# Lock luma kill implementation checkpoint

Status: blocked, not ready to integrate. The source on this branch is the third candidate described below.
No bound was raised. The engine version remains 21.5.0. No real-scene package or game repin was made for a
candidate that still fails correctness guards.

The [diagnostic archive at c1d32238a](https://github.com/APKiwiOrg/KhaozEngine/commit/c1d32238a) established
that thin-feature locks hold the bright shadow-edge dots. Its eight-fact filter did not include
`TemporalResolveLockGpuTests` or entry-point identity. The new regression reproduces its baseline exactly.

## Mechanism and candidates

All candidates compare this frame's mean weighted luma over the prepared 3x3 with the mean of four diagonal
bilinear history taps plus a bilinear centre at the reprojected position. The primary kill requires an absolute
change greater than 0.02 and a relative change greater than 0.1. It clears the lock before computing its hold.
It stores no new history, adds no target and adds no uniform.

| Candidate | Additional rule | Passing facts | Failed facts |
| --- | --- | ---: | ---: |
| floor02 | Mean change only, every preset | 78 | 39 |
| pairs02 | Each opposite-corner pair confirms at 0.02, Native and Quality | 103 | 17 |
| corners005 | Each corner confirms at 0.005, Native and Quality | 112 | 9 |

The latter two keep the lock arithmetic `precise`. This removes the entry-point lock discrepancies, formerly
one to four half-float steps. All four entry identity facts pass in both candidates. The existing control,
texture-detail and stable-peak bounds were retained.

The corner refinement gathers the four current corner lumas alongside the existing moments, then compares each
with its history ring tap on the side of the mean change. Pair means can hide an unchanged corner behind one
that changed. Below Quality the kill stays outside `ShadingMaxUpscale`, retaining the existing lower-preset behavior.

The 0.02 absolute floor equals the existing absolute ridge step. It prevents the new dark guard's low-luma
texture from losing locks to relative changes. The current corner confirmation is 0.005, a quarter of that floor.
These are experimental constants, not accepted production tuning.

## Synthetic grain

Mean absolute output luma minus its 3x3 median, in a 12-pixel band on the reference's drawn edge. The new case
ports the archive's textured torso, moving four display pixels a frame with shadow hold on. The original
faceted case remains unchanged. The regression bounds are Native 0.011300 and Quality 0.008000.

| Resolve | Native | Quality | Balanced, report only |
| --- | ---: | ---: | ---: |
| 21.4.0 behavior | 0.012026, fail | 0.010852, fail | 0.010526 |
| Archive kill 0.1 | 0.010442 | 0.006745 | Not measured |
| floor02 | 0.010732, pass | 0.007940, pass | 0.005758 |
| pairs02 | 0.011677, fail | 0.010045, fail | 0.010526 |
| corners005 | 0.010803, pass | 0.009229, fail | 0.010526 |

The dark and local-light fixtures use the same textured receiver and a 4x per-axis spatial reference, with key
and fill off. Dark uses ambient 0.08 and a still point intensity 0.03. Flicker uses ambient 0.03 and point intensity
`0.4 + 0.35 sin(0.8 n)`. Moving uses ambient 0.03 and point intensity 0.5 at `x = 0.6 sin(0.18 n)`.
Frames 80 to 95 are measured. Reference mean luma is 0.004197, 0.024637 and 0.021647 respectively. All six cases
bound mean absolute error, error-image grain and frame-to-frame error within a tenth of the measured pre-fix values.
The dark fixture was raised from a quantized-black initial attempt before accepting its baseline.

| Case | Before error / grain / temporal error | corners005 error / grain / temporal error | Result |
| --- | --- | --- | --- |
| Dark Native | 0.000360 / 0.000356 / 0.000045 | 0.000360 / 0.000356 / 0.000045 | Pass |
| Dark Quality | 0.001003 / 0.000877 / 0.000085 | 0.001003 / 0.000877 / 0.000085 | Pass |
| Flicker Native | 0.014685 / 0.005503 / 0.010689 | 0.016223 / 0.006545 / 0.011183 | Error and grain fail |
| Flicker Quality | 0.016049 / 0.005340 / 0.011537 | 0.017858 / 0.006138 / 0.011595 | Error and grain fail |
| Moving Native | 0.010694 / 0.003822 / 0.003290 | 0.010692 / 0.003824 / 0.003289 | Pass |
| Moving Quality | 0.012462 / 0.004421 / 0.003293 | 0.012533 / 0.004559 / 0.003301 | Pass |

## Fast tails and camera pan

Absolute textured tail errors on Metal hardware. None of the fast-tail ratio bounds was restated because the
absolute 8-pixel tails regress. All rows, including Quality and the other movement cases, are retained in
[moving-shadow-rows.json](moving-shadow-rows.json) and the logs.

| Native direction | Before 1 px | Before 8 px | corners005 1 px | corners005 8 px | Current ratio bound |
| --- | ---: | ---: | ---: | ---: | ---: |
| Advancing | 0.003323 | 0.004442 | 0.003204 | 0.004724 | 1.5 x 0.003204, passes |
| Retreating | 0.005670 | 0.006978 | 0.005460 | 0.008256 | 1.5 x 0.005460, fails |

Quality camera pan 2-pixel band is 0.005550 before, 0.006139 for floor02, 0.005934 for pairs02 and 0.005846 for
corners005, against the unchanged 0.006050 bound. For floor02, 3,321 of the band's 30,608 pixels change. Within
four pixels of the reference edge are 3,063 changed pixels and 99.5 percent of the added absolute error. The
change is on both sides of the edge, not spread throughout the texture panel. The corner checks reduce it.

## Why this needs a design decision

A current 3x3 at weighted luma 0.10, with a history centre at 0.35 and four history ring taps at 0.10, has history
mean 0.15. The mean predicate sees an absolute change 0.05 and relative change one third. This is a valid missed
bright thin feature, which must retain its lock. A uniformly shaded history at 0.15 produces the same two mean
values, and should release on a shading change. The mean statistic alone cannot distinguish them.

The existing thin-feature suite proves the conflict with rendered coverage, rather than a source-text assertion.
The direct port reduces a still sub-texel line's coverage to a few percent instead of its expected 37.5 percent.
Corner coherence restores many cases, but the remaining swaying and near-surface cases still lose their held detail.
The current nine failed facts are six thin-feature facts, the textured regression, the local-light fact and the
moving-shadow fact. No fourth shader candidate was attempted.

The next decision is the shading evidence that may invalidate a thin-feature lock. It must distinguish changed
illumination from a jitter phase missing geometry or texture, keep near-surface ring samples from supplying
another surface's evidence, and preserve reconvergence behind a fast retreating shadow. A broader experiment
should be designed against all temporal correctness tests before accepting a threshold from the eight-fact subset.

## Full local verification

The final Metal correctness run selected all temporal facts except the resolve and outline timing benchmarks.
It ran 918 tests with zero skips, 907 passed and 11 failed. In addition to the focused filter's nine failures,
Quality upscaling loses 1.2-pixel bar contrast (0.413 against 0.500) and the Quality M 1 trailing textured rim
has temporal error 0.008895 against 0.008060 plus a tenth (0.00886655, including its existing rounding allowance). No assertion was loosened.
The complete list is [local-gpu-failures.json](local-gpu-failures.json), with all metrics and assertions in
[the Metal log](logs/full-temporal-metal.txt).

The full Release solution build passed with zero warnings and errors. The full headless suite passed with
35,343 passed, 1,426 skipped and zero failures across 41 assemblies. Shader hash baking passed all six facts.
All five whole-tree governance checks passed. Full format verification exits 2 on 115 diagnostics in eight
files unchanged from the merged HEAD. Those files were not edited under this task. The list is
[format-existing-files.json](format-existing-files.json), with the [format log](logs/format-verify.txt).
The required two-file format command and the owned-file formatting both exited zero.

A follow-up attribution run restored only the two shader source files to their original merged-HEAD contents,
leaving the new test fixtures in place. All 46 selected facts passed with zero skips, covering the existing thin-feature, upscaling-chart and
moving-rim guards and the local-light fixture parity check. Both candidate sources were restored
byte for byte in a `finally` block. [The original-shader log](logs/existing-guards-original-shaders.txt) confirms
that these candidate failures are regressions. No fourth candidate was introduced. The candidate render DLL was then rebuilt byte-identically to the full
Metal run (SHA-256 in [verification.json](verification.json)). A 74-fact follow-up passed all tuning mirrors,
entry identity, direct luma-lock cases and the added shared-scene parity fact. The added parity fact passed
with both the original and candidate shaders. The final added-test format check also exits zero.

## Historical backend comparison

The successful [21.4.0 release gate 39076952301](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39076952301)
ran at exact tag commit `4e05462bec95cd49e47c5592c1ce8c0fcb004f4d`. Its passed moving-shading fact retains all 38
cases on each backend in the TRX artifacts. [before-21.4-moving.json](before-21.4-moving.json) preserves their
summary rows, and the `before-21.4-*-moving.txt` logs preserve the full measurement output. The archived values
have four decimal places. They are independent release measurements, not a rerun or a new workflow dispatch.

The [guard-value exports](guard-values/) list each printed measurement against its bound, including per-device
baselines where the tests already distinguish hardware, WARP and lavapipe. Each backend has 394 quality-guard
rows covering synthetic grain, new lighting cases, moving shadow bands and tails, the original shadow-edge test,
stable detail, stable peaks, diagonal detail and moving rims. Printed borderline rows defer to the test's
unrounded assertions. The raw grain metric includes legitimate texture grain, so it is reviewed alongside the
error-image grain and the fidelity guards, not used as a sole acceptance signal.

## Texture fetch cost

The current candidate reuses the four ring taps if the existing shading release later needs them.

| Pixel class | Added texture fetches |
| --- | ---: |
| No usable history or no positive lock | 0 |
| Positive lock at Native or Quality, old release already sampled its ring | 1 bilinear centre |
| Positive lock at Native or Quality, old release skipped its ring, including carried flat neighborhoods | 4 bilinear ring taps plus 1 centre |
| Balanced and lower | 0 |

The four current corner values come from the existing 3x3 gather, with no new texture fetch. Extra arithmetic,
register pressure and the changed frequency of the old release have not been timed. The billed T4 bench was not run.

## Real scene and commands

[The archive table](archive-real-scene.md) and [archive-reference.json](archive-reference.json) preserve all four requested conditions' original 21.4.0 and
kill-0.1 character and ground grain, p95, change mean and dot counts. Each region's array is in that order.
There is no new candidate real-scene measurement, crop or probe repin. Those remain blocked on correctness.
The snapshot tool currently supports Native, Quality and FXAA, without a Balanced capture arm.

Run from the repository root. Every command below is a single verification run, not a flake loop.

```bash
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native build-slot --heavy --label grain-check -- \
  dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~Temporal&FullyQualifiedName!~CostPerfGpuTests' \
  --logger 'console;verbosity=detailed'
```

`KE_TEMPORAL_SHADE_EVIDENCE_DIR` optionally writes paired output and reference PNGs from the moving-shadow test.
The initial three-fact baseline filter covered the new textured case, `TemporalTextureLightGpuTests` and
`TemporalMovingShadeLagGpuTests`. The candidate filter added `TemporalStableDetailGpuTests`,
`TemporalStablePeakGpuTests`, `TemporalResolveShaderTests`, `TemporalEntryIdentityGpuTests`,
`TemporalResolveLockGpuTests` and `TemporalResolveLumaLockGpuTests`. The [command record](commands.md) lists the invocations, and [logs](logs/) retain assertions and metrics. The full temporal correctness filter excludes the separately timed resolve and outline cost benchmarks.

## Verification record

[verification.json](verification.json) records every local exit code and count. The whole-solution format failure
is pre-existing and separate from the candidate's GPU failures. This branch remains blocked from integration.
Hosted run [39119425055](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39119425055) tests candidate commit `507f0d135`. Its results will be appended when it finishes. No private-feed package, real-scene
capture or game repin is justified by these red correctness guards.
