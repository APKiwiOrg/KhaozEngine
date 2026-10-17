# Hosted support cost verdict for #1334, SG-12 option C

**Verdict: FAIL.** The ordinary median rows pass 109 of 139 and reject 30. Five rows are envelope rows,
outside both counts. The 90 dense rows carry no time verdict (SC-1) and pass their allocation limits.
13 ordinary rows exceed a time limit and 17 reject on a geometry, envelope or legacy precondition.
The #1334 ordinary gate does not pass and #1334 stays open.

## Run identity

- Owner-approved workflow [39010313012](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39010313012).
- `stress-test.yml`, project `KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj`, filter
  `FullyQualifiedName~SupportGateCostTests.OneShotCostArtifact`, 20 iterations, `load=0`, Release,
  `support-cost=true`. The workflow succeeded.
- Measured candidate: `5c373abe95ebc527cd57e636aee6b354dcc1556c` on engine main, the staged option C work.
- Recorded source baseline: `06b734c5722bc2cc95cea459255d6ec93e9236c7`.
- Evaluation checkout: branch `fix/support-cost-hosted2`, which adds only the reference section below to `5c373abe9`.
- GitHub-hosted AMD EPYC 7763 64-Core Processor, X64, Ubuntu 26.04.1 LTS, .NET 10.0.13.
- Release, `DOTNET_TieredCompilation=0`. One first-use sample, one unmeasured priming call and one warm sample per row.
- Inputs: `run-1.json` through `run-20.json` from the workflow artifacts, 417 samples each, recording label
  `hosted-support-cost`. Each has schema 2, no pending finalization and `ProcessExitStatus=0`.
  Hosted runs record only (T1-R6), so they carry no in-run `HostScale`. The verdict below is computed offline.
- Capture interval: `2026-10-17T06:37:32.8159889+00:00` through `2026-10-17T07:03:21.594459+00:00`.
- These are data-only inputs. Evaluation does not invoke a locomotion operation or repeat the timing proof.

## Reference and evaluation

- Reference: section `reference-5c373abe9` of [2026-10-11-support-gates-cost.json](2026-10-11-support-gates-cost.json),
  recorded at the measured candidate in one SG-20 invocation of `SupportGateCostTests.OneShotCostArtifact` on an
  Apple M2 Max, Arm64, macOS 27.0.1, .NET 10.0.13, Release, `DOTNET_TieredCompilation=0`.
  Captured `2026-10-17T07:03:44.587168+00:00`, finalized with exit 0, 417 samples.
  Its section fields match `reference-376971dda`. Its samples also carry the `Envelope` field the current harness writes.
- Its flat box legacy tick is 6.583 us. Against it, the retired single-row `k` would range 2.246 to 5.867
  (median 2.309). Iterations 8 and 18 read that row at 38.622 and 22.733 us. The median scale is unaffected
  by either outlier.
- `k` is the median of hosted/M2 warm time over every legacy row present and successful in both sections,
  matched by scene and operation, with M2 warm time above 0.5 us (SC-2). Each iteration has 57 such rows.
  Quartiles interpolate at rank (n + 1) p.
- Evaluator: `SupportCostHostedRecordTests.HostedRecordWithCorrectedCalibration`, run with
  `KHAOZ_SUPPORT_COST_HOSTED_RUNS` naming the artifact directory, `KHAOZ_SUPPORT_COST_REFERENCE_COMMIT=5c373abe9`
  and `KHAOZ_SUPPORT_COST_HOSTED_REPORT` naming the table output. It selects the reference by commit, requires each
  run's `CandidateSha` to match it, and judges each iteration with `SupportCostVerdict.TryJudgeHosted` using that
  run's own lifetime inventory. Every row and median verdict, limit and failure code comes from the shared
  `SupportCostVerdict.JudgeRow` and `SupportCostVerdict.Check`. No verdict code, limit or tolerance changed.
- Aggregation is that of the 376971dda record: each candidate and paired legacy time is divided by its own
  iteration's `k`, the per-row median is taken, and the median row is judged at `k = 1`.
  Warm bytes and envelope classification are identical across all 20 iterations, which the evaluator asserts.

## Calibration

`k` ranges from **1.973 to 2.073**, median **1.997**, with 57 rows in every iteration. The lower quartile runs
0.914 to 0.990 and the upper quartile 2.302 to 2.367. Legacy rows still do not move by one common factor
between the M2 Max and the hosted runner. SC-2 names this spread as the cost-if-wrong signal.
Every iteration passes 109 ordinary rows and rejects 30, except iteration 9, which passes 110.
The extra pass is the flat hull dry tick, the only ordinary row whose iteration verdicts disagree.

| Iteration | k | Rows | Lower quartile | Upper quartile | Ordinary pass | Ordinary reject | Envelope |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1.983667 | 57 | 0.938682 | 2.344552 | 109 | 30 | 5 |
| 2 | 2.005588 | 57 | 0.914009 | 2.334347 | 109 | 30 | 5 |
| 3 | 2.072848 | 57 | 0.943585 | 2.325071 | 109 | 30 | 5 |
| 4 | 1.991934 | 57 | 0.926315 | 2.325781 | 109 | 30 | 5 |
| 5 | 1.983694 | 57 | 0.947191 | 2.302013 | 109 | 30 | 5 |
| 6 | 1.984747 | 57 | 0.918765 | 2.309918 | 109 | 30 | 5 |
| 7 | 2.004393 | 57 | 0.927027 | 2.350840 | 109 | 30 | 5 |
| 8 | 2.003429 | 57 | 0.935343 | 2.362642 | 109 | 30 | 5 |
| 9 | 1.997836 | 57 | 0.927858 | 2.351709 | 110 | 29 | 5 |
| 10 | 1.995303 | 57 | 0.933189 | 2.332077 | 109 | 30 | 5 |
| 11 | 2.001579 | 57 | 0.951796 | 2.323232 | 109 | 30 | 5 |
| 12 | 1.994763 | 57 | 0.938427 | 2.367283 | 109 | 30 | 5 |
| 13 | 2.013055 | 57 | 0.930193 | 2.326276 | 109 | 30 | 5 |
| 14 | 1.973381 | 57 | 0.914314 | 2.318651 | 109 | 30 | 5 |
| 15 | 1.986933 | 57 | 0.963050 | 2.330583 | 109 | 30 | 5 |
| 16 | 1.976917 | 57 | 0.936175 | 2.313066 | 109 | 30 | 5 |
| 17 | 2.020197 | 57 | 0.926468 | 2.338254 | 109 | 30 | 5 |
| 18 | 1.979422 | 57 | 0.936919 | 2.326381 | 109 | 30 | 5 |
| 19 | 2.061808 | 57 | 0.932330 | 2.355549 | 109 | 30 | 5 |
| 20 | 2.016652 | 57 | 0.989831 | 2.342426 | 109 | 30 | 5 |

| Iteration | SHA-256 of input JSON |
| --- | --- |
| 1 | `91a92d9ce715a7d2cc936b40817ff5a4d9894bb025fb9f4e453740656a1aeee7` |
| 2 | `e5d2833909979848ebc0cf0a20db2d0584e8a94928b0cb21916024fd2cea31ef` |
| 3 | `ca7cf2fa1d5354fc472c7bad1061249d0816254793832cdd7718ba55a16986e1` |
| 4 | `9c4c18cf6467a2f52d0cc0180112dcc61e04125863dfacbc66ae54d3d0f774b8` |
| 5 | `2cea9c8d0d8948a2e4fc0633413298fa17d38b4dc4e1dfa693c62acc20a03e61` |
| 6 | `d4d21bb51bcceca467af4d3e88385aa622fffa8ac9a55bebc061ee706e11b481` |
| 7 | `708dcb0da767ed840f01ed20a836c8a46057842341632434a52cc260faf1af3b` |
| 8 | `5b7d340512c2cdd040b6a4347be1b2ccdfb0edec51d621fb37b3206070bb41af` |
| 9 | `ed0b403d74790418510fb42350cee68fa5663324958adb17249e1a28d70039aa` |
| 10 | `8c0b0eb2884edd6a12b355fa0aa5aab8715d1e74a9d96e0c54ec1b9901d7776c` |
| 11 | `9e618fc492f5f8479df9489b1a57c78b8b7a81e695caa8baf351ed4f2860003d` |
| 12 | `c8fa950d22f4146e550c98a63dc87c19015a0f4479fa612e411662aaea529376` |
| 13 | `e33409f5a87bc92b662add48b4f38ad1c160d3731ddc95ffd83356a1988f0133` |
| 14 | `53ac8462e76fc0a1722a5608278f5390b7d0dde766ddaf1fdf36bf09e22eb193` |
| 15 | `3754948707ac9036807f21412ecc03d10b1118d1ff321055b1dc099ee3144f95` |
| 16 | `91ef50073f39269a0bb3789320c2e80ce2d876a0ec5883f21c24c29c4227b1a3` |
| 17 | `5bc769f5ec35e9cca2af37023b3befbef5c71bc45fb80f46fad6d7b922da4b1a` |
| 18 | `bb7ebfe18b7f34680c4a289e07257dec8dfb24ba67f1ec34ffacc73cbf730c3e` |
| 19 | `574bda5537da90b939b82290f2ff729408c018f4ab84b6a8641760a68189d8ae` |
| 20 | `7842079a98989d80764c8d842c4990ea26c90641b4d6cb6ec68b46ac89175bfa` |

## Ordinary rows

Median rows: 109 ordinary pass, 30 ordinary reject, 5 envelope, 90 dense without a time verdict.

| Family | Rows | Pass | Reject | Median C / limit |
| --- | ---: | ---: | ---: | ---: |
| Find | 25 | 21 | 4 | 0.294 |
| Substep | 87 | 81 | 6 | 0.073 |
| Whole tick | 27 | 7 | 20 | 0.513 |

All table times are M2 Max equivalent us. `C` is candidate, `L` is the paired legacy, `Limit` is the binding
median ceiling and `Margin = Limit - C`. Negative margin is an overrun. `Pass/20` counts the iterations whose
own row passed. The last column is the corrected 376971dda verdict and C / limit for the same row.

`Find` pairs with `LegacyDownPass`. Substeps and whole ticks pair with `LegacyTick`.
`B0` and `B1` mean `GroundSubstep/GroundTickBorrowed/0` and `/1`. `D0` and `D1` mean `GroundSubstep/DryTick/0`
and `/1`. `E0` and `E1` mean `GroundSubstep/ExplicitTick/0` and `/1`. `Dry` and `Explicit` name the acquired
whole ticks. Ordinary Find, substep and tick absolute limits are 50, 200 and 1000 us. An ordinary whole tick
also has the legacy-relative ceiling `L + max(100 us, 0.25 L)` at `k = 1`, and the table shows the smaller.

Failure codes: `T` exceeds a time limit, `G` fails a geometry, oracle or support precondition, `E` is outside
the comparable moving single-mode envelope, and `L` has an unsuccessful ordinary legacy geometry result.
These codes can overlap.

### Find

| Scene | Operation | Median C | Median L | Limit | Margin | C / limit | Verdict | Pass/20 | 376971dda |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- | ---: | --- |
| flat box | Find | 22.989 | 2.144 | 50.000 | 27.011 | 0.460 | PASS | 20 | FAIL T 1.121 |
| stairs box | Find | 101.866 | 2.935 | 50.000 | -51.866 | 2.037 | FAIL T | 0 | FAIL T 6.543 |
| flat hull | Find | 38.596 | 2.033 | 50.000 | 11.404 | 0.772 | PASS | 20 | FAIL T 1.762 |
| stairs hull | Find | 103.108 | 4.763 | 50.000 | -53.108 | 2.062 | FAIL T | 0 | FAIL T 7.302 |
| flat mesh | Find | 41.784 | 2.719 | 50.000 | 8.216 | 0.836 | PASS | 20 | PASS 0.862 |
| stairs mesh | Find | 102.669 | 6.088 | 50.000 | -52.669 | 2.053 | FAIL T | 0 | FAIL T 3.144 |
| analytic only | Find | 4.497 | 0.205 | 50.000 | 45.503 | 0.090 | PASS | 20 | PASS 0.106 |
| analytic plus props | Find | 14.717 | 1.076 | 50.000 | 35.283 | 0.294 | PASS | 20 | PASS 0.934 |
| hidden crate | Find | 142.709 | 4.335 | 50.000 | -92.709 | 2.854 | FAIL T | 0 | FAIL T 9.845 |
| sphere | Find | 13.242 | 0.372 | 50.000 | 36.758 | 0.265 | PASS | 20 | FAIL T 1.087 |
| capsule | Find | 15.004 | 0.379 | 50.000 | 34.996 | 0.300 | PASS | 20 | FAIL T 1.879 |
| cylinder | Find | 17.794 | 1.185 | 50.000 | 32.206 | 0.356 | PASS | 20 | FAIL T 2.241 |
| stacked irrelevant layers | Find | 15.038 | 7.712 | 50.000 | 34.962 | 0.301 | PASS | 20 | PASS 0.934 |
| stationary | Find | 14.478 | 2.051 | 50.000 | 35.522 | 0.290 | PASS | 20 | PASS 0.905 |
| initial | Find | 14.288 | 2.056 | 50.000 | 35.712 | 0.286 | PASS | 20 | PASS 0.906 |
| wall retry | Find | 4.459 | 0.166 | 50.000 | 45.541 | 0.089 | PASS | 20 | PASS 0.106 |
| pushed seat | Find | 14.733 | 0.890 | 50.000 | 35.267 | 0.295 | PASS | 20 | PASS 0.922 |
| descending landing | Find | 14.848 | 1.055 | 50.000 | 35.152 | 0.297 | PASS | 20 | PASS 0.944 |
| slide opposing face | Find | 4.453 | 1.060 | 50.000 | 45.547 | 0.089 | PASS | 20 | PASS 0.108 |
| explicit entry | Find | 4.541 | 0.173 | 50.000 | 45.459 | 0.091 | PASS | 20 | PASS 0.107 |
| explicit wade | Find | 4.416 | 0.173 | 50.000 | 45.584 | 0.088 | PASS | 20 | PASS 0.105 |
| explicit medium descent retry | Find | 4.410 | 0.169 | 50.000 | 45.590 | 0.088 | PASS | 20 | PASS 0.105 |
| explicit dry swim handoff | Find | 4.426 | 0.168 | 50.000 | 45.574 | 0.089 | PASS | 20 | PASS 0.106 |
| explicit dry wade handoff | Find | 4.407 | 0.171 | 50.000 | 45.593 | 0.088 | PASS | 20 | PASS 0.105 |
| final clamp | Find | 14.565 | 2.067 | 50.000 | 35.435 | 0.291 | PASS | 20 | PASS 0.923 |

### Ground substeps

| Scene | Operation | Median C | Median L | Limit | Margin | C / limit | Verdict | Pass/20 | 376971dda |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- | ---: | --- |
| flat box | B0 | 14.484 | 7.620 | 200.000 | 185.516 | 0.072 | PASS | 20 | PASS 0.226 |
| flat box | B1 | 14.298 | 7.620 | 200.000 | 185.702 | 0.071 | PASS | 20 | PASS 0.225 |
| flat box | D0 | 14.670 | 7.620 | 200.000 | 185.330 | 0.073 | PASS | 20 | PASS 0.227 |
| flat box | D1 | 14.488 | 7.620 | 200.000 | 185.512 | 0.072 | PASS | 20 | PASS 0.229 |
| stairs box | B0 | 72.018 | 14.306 | 200.000 | 127.982 | 0.360 | PASS | 20 | PASS 0.984 |
| stairs box | B1 | 74.827 | 14.306 | 200.000 | 125.173 | 0.374 | PASS | 20 | PASS 0.984 |
| stairs box | D0 | 72.264 | 14.306 | 200.000 | 127.736 | 0.361 | PASS | 20 | PASS 0.994 |
| stairs box | D1 | 75.113 | 14.306 | 200.000 | 124.887 | 0.376 | PASS | 20 | PASS 0.986 |
| flat hull | B0 | 39.897 | 10.553 | 200.000 | 160.103 | 0.199 | PASS | 20 | PASS 0.443 |
| flat hull | B1 | 39.869 | 10.553 | 200.000 | 160.131 | 0.199 | PASS | 20 | PASS 0.440 |
| flat hull | D0 | 40.577 | 10.553 | 200.000 | 159.423 | 0.203 | PASS | 20 | PASS 0.444 |
| flat hull | D1 | 40.231 | 10.553 | 200.000 | 159.769 | 0.201 | PASS | 20 | PASS 0.442 |
| stairs hull | B0 | 75.345 | 16.198 | 200.000 | 124.655 | 0.377 | PASS | 20 | FAIL T 1.169 |
| stairs hull | B1 | 78.791 | 16.198 | 200.000 | 121.209 | 0.394 | PASS | 20 | FAIL T 1.147 |
| stairs hull | D0 | 75.570 | 16.198 | 200.000 | 124.430 | 0.378 | PASS | 20 | FAIL T 1.139 |
| stairs hull | D1 | 78.143 | 16.198 | 200.000 | 121.857 | 0.391 | PASS | 20 | FAIL T 1.146 |
| flat mesh | B0 | 53.022 | 12.317 | 200.000 | 146.978 | 0.265 | PASS | 20 | PASS 0.282 |
| flat mesh | B1 | 52.380 | 12.317 | 200.000 | 147.620 | 0.262 | PASS | 20 | PASS 0.282 |
| flat mesh | D0 | 53.449 | 12.317 | 200.000 | 146.551 | 0.267 | PASS | 20 | PASS 0.282 |
| flat mesh | D1 | 52.432 | 12.317 | 200.000 | 147.568 | 0.262 | PASS | 20 | PASS 0.281 |
| stairs mesh | B0 | 79.080 | 19.370 | 200.000 | 120.920 | 0.395 | PASS | 20 | PASS 0.563 |
| stairs mesh | B1 | 80.856 | 19.370 | 200.000 | 119.144 | 0.404 | PASS | 20 | PASS 0.616 |
| stairs mesh | D0 | 79.735 | 19.370 | 200.000 | 120.265 | 0.399 | PASS | 20 | PASS 0.579 |
| stairs mesh | D1 | 81.826 | 19.370 | 200.000 | 118.174 | 0.409 | PASS | 20 | PASS 0.615 |
| analytic only | B0 | 4.709 | 0.951 | 200.000 | 195.291 | 0.024 | PASS | 20 | PASS 0.028 |
| analytic only | B1 | 4.741 | 0.951 | 200.000 | 195.259 | 0.024 | PASS | 20 | PASS 0.028 |
| analytic only | D0 | 4.739 | 0.951 | 200.000 | 195.261 | 0.024 | PASS | 20 | PASS 0.028 |
| analytic only | D1 | 4.721 | 0.951 | 200.000 | 195.279 | 0.024 | PASS | 20 | PASS 0.028 |
| analytic plus props | B0 | 14.633 | 5.623 | 200.000 | 185.367 | 0.073 | PASS | 20 | PASS 0.224 |
| analytic plus props | B1 | 14.576 | 5.623 | 200.000 | 185.424 | 0.073 | PASS | 20 | PASS 0.223 |
| analytic plus props | D0 | 14.599 | 5.623 | 200.000 | 185.401 | 0.073 | PASS | 20 | PASS 0.224 |
| analytic plus props | D1 | 14.575 | 5.623 | 200.000 | 185.425 | 0.073 | PASS | 20 | PASS 0.222 |
| hidden crate | B0 | 166.728 | 14.120 | 200.000 | 33.272 | 0.834 | FAIL G | 0 | FAIL T,G 2.649 |
| hidden crate | B1 | 244.784 | 14.120 | 200.000 | -44.784 | 1.224 | FAIL T,G | 0 | FAIL T,G 3.325 |
| hidden crate | D0 | 163.743 | 14.120 | 200.000 | 36.257 | 0.819 | FAIL G | 0 | FAIL T,G 2.637 |
| hidden crate | D1 | 250.324 | 14.120 | 200.000 | -50.324 | 1.252 | FAIL T,G | 0 | FAIL T,G 3.334 |
| sphere | B0 | 13.792 | 3.326 | 200.000 | 186.208 | 0.069 | PASS | 20 | PASS 0.252 |
| sphere | B1 | 14.465 | 3.326 | 200.000 | 185.535 | 0.072 | PASS | 20 | PASS 0.254 |
| sphere | D0 | 13.938 | 3.326 | 200.000 | 186.062 | 0.070 | PASS | 20 | PASS 0.253 |
| sphere | D1 | 14.410 | 3.326 | 200.000 | 185.590 | 0.072 | PASS | 20 | PASS 0.258 |
| capsule | B0 | 16.049 | 3.619 | 200.000 | 183.951 | 0.080 | PASS | 20 | PASS 0.441 |
| capsule | B1 | 16.532 | 3.619 | 200.000 | 183.468 | 0.083 | PASS | 20 | PASS 0.446 |
| capsule | D0 | 16.166 | 3.619 | 200.000 | 183.834 | 0.081 | PASS | 20 | PASS 0.447 |
| capsule | D1 | 16.698 | 3.619 | 200.000 | 183.302 | 0.083 | PASS | 20 | PASS 0.449 |
| cylinder | B0 | 19.311 | 6.052 | 200.000 | 180.689 | 0.097 | PASS | 20 | PASS 0.543 |
| cylinder | B1 | 19.436 | 6.052 | 200.000 | 180.564 | 0.097 | PASS | 20 | PASS 0.550 |
| cylinder | D0 | 19.477 | 6.052 | 200.000 | 180.523 | 0.097 | PASS | 20 | PASS 0.549 |
| cylinder | D1 | 19.441 | 6.052 | 200.000 | 180.559 | 0.097 | PASS | 20 | PASS 0.553 |
| stacked irrelevant layers | B0 | 14.624 | 11.580 | 200.000 | 185.376 | 0.073 | PASS | 20 | PASS 0.223 |
| stacked irrelevant layers | B1 | 14.531 | 11.580 | 200.000 | 185.469 | 0.073 | PASS | 20 | PASS 0.222 |
| stacked irrelevant layers | D0 | 14.663 | 11.580 | 200.000 | 185.337 | 0.073 | PASS | 20 | PASS 0.226 |
| stacked irrelevant layers | D1 | 14.546 | 11.580 | 200.000 | 185.454 | 0.073 | PASS | 20 | PASS 0.223 |
| initial | B0 | 14.438 | 7.379 | 200.000 | 185.562 | 0.072 | PASS | 20 | PASS 0.225 |
| initial | B1 | 14.330 | 7.379 | 200.000 | 185.670 | 0.072 | PASS | 20 | PASS 0.223 |
| initial | D0 | 14.513 | 7.379 | 200.000 | 185.487 | 0.073 | PASS | 20 | PASS 0.225 |
| initial | D1 | 14.467 | 7.379 | 200.000 | 185.533 | 0.072 | PASS | 20 | PASS 0.223 |
| wall retry | B0 | 9.274 | 1.009 | 200.000 | 190.726 | 0.046 | PASS | 20 | PASS 0.056 |
| wall retry | B1 | 9.313 | 1.009 | 200.000 | 190.687 | 0.047 | PASS | 20 | PASS 0.055 |
| wall retry | D0 | 9.333 | 1.009 | 200.000 | 190.667 | 0.047 | PASS | 20 | PASS 0.056 |
| wall retry | D1 | 9.318 | 1.009 | 200.000 | 190.682 | 0.047 | PASS | 20 | PASS 0.056 |
| pushed seat | B0 | 91.904 | 29.434 | 200.000 | 108.096 | 0.460 | PASS | 20 | FAIL T 1.442 |
| pushed seat | B1 | 45.385 | 29.434 | 200.000 | 154.615 | 0.227 | PASS | 20 | PASS 0.730 |
| pushed seat | D0 | 93.989 | 29.434 | 200.000 | 106.011 | 0.470 | FAIL G | 0 | FAIL T,G 1.471 |
| pushed seat | D1 | 45.411 | 29.434 | 200.000 | 154.589 | 0.227 | FAIL G | 0 | FAIL G 0.724 |
| descending landing | B0 | 14.729 | 7.287 | 200.000 | 185.271 | 0.074 | PASS | 20 | PASS 0.227 |
| descending landing | B1 | 14.373 | 7.287 | 200.000 | 185.627 | 0.072 | PASS | 20 | PASS 0.226 |
| descending landing | D0 | 14.508 | 7.287 | 200.000 | 185.492 | 0.073 | PASS | 20 | PASS 0.223 |
| explicit entry | B0 | 4.688 | 0.945 | 200.000 | 195.312 | 0.023 | PASS | 20 | PASS 0.028 |
| explicit entry | B1 | 4.721 | 0.945 | 200.000 | 195.279 | 0.024 | PASS | 20 | PASS 0.028 |
| explicit entry | E0 | 13.403 | 0.945 | 200.000 | 186.597 | 0.067 | PASS | 20 | PASS 0.080 |
| explicit entry | E1 | 13.058 | 0.945 | 200.000 | 186.942 | 0.065 | PASS | 20 | PASS 0.078 |
| explicit rounding handoff | E0 | 13.689 | 1.477 | 200.000 | 186.311 | 0.068 | PASS | 20 | PASS 0.082 |
| explicit wade | B0 | 4.708 | 0.936 | 200.000 | 195.292 | 0.024 | PASS | 20 | PASS 0.028 |
| explicit wade | B1 | 4.725 | 0.936 | 200.000 | 195.275 | 0.024 | PASS | 20 | PASS 0.028 |
| explicit wade | E0 | 13.515 | 0.936 | 200.000 | 186.485 | 0.068 | PASS | 20 | PASS 0.081 |
| explicit medium descent retry | B0 | 4.716 | 2.184 | 200.000 | 195.284 | 0.024 | PASS | 20 | PASS 0.028 |
| explicit medium descent retry | B1 | 4.714 | 2.184 | 200.000 | 195.286 | 0.024 | PASS | 20 | PASS 0.028 |
| explicit medium descent retry | E0 | 13.396 | 2.184 | 200.000 | 186.604 | 0.067 | PASS | 20 | PASS 0.079 |
| explicit medium descent retry | E1 | 39.655 | 2.184 | 200.000 | 160.345 | 0.198 | PASS | 20 | PASS 0.236 |
| explicit dry swim handoff | B0 | 4.709 | 0.942 | 200.000 | 195.291 | 0.024 | PASS | 20 | PASS 0.028 |
| explicit dry swim handoff | B1 | 4.717 | 0.942 | 200.000 | 195.283 | 0.024 | PASS | 20 | PASS 0.028 |
| explicit dry swim handoff | E0 | 13.709 | 0.942 | 200.000 | 186.291 | 0.069 | PASS | 20 | PASS 0.081 |
| explicit dry wade handoff | B0 | 4.689 | 0.921 | 200.000 | 195.311 | 0.023 | PASS | 20 | PASS 0.028 |
| explicit dry wade handoff | B1 | 4.722 | 0.921 | 200.000 | 195.278 | 0.024 | PASS | 20 | PASS 0.028 |
| explicit dry wade handoff | E0 | 13.529 | 0.921 | 200.000 | 186.471 | 0.068 | PASS | 20 | PASS 0.081 |
| explicit dry wade handoff | E1 | 13.415 | 0.921 | 200.000 | 186.585 | 0.067 | PASS | 20 | PASS 0.080 |
| final clamp | D0 | 14.563 | 6.187 | 200.000 | 185.437 | 0.073 | PASS | 20 | PASS 0.227 |

### Acquired whole ticks

| Scene | Operation | Median C | Median L | Limit | Margin | C / limit | Verdict | Pass/20 | 376971dda |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- | ---: | --- |
| flat box | Dry | 45.808 | 7.620 | 107.620 | 61.812 | 0.426 | PASS | 20 | FAIL T 1.298 |
| stairs box | Dry | 463.207 | 14.306 | 114.306 | -348.901 | 4.052 | FAIL T,L | 0 | FAIL T,L 12.555 |
| flat hull | Dry | 122.536 | 10.553 | 110.553 | -11.983 | 1.108 | FAIL T | 1 | FAIL T 2.397 |
| stairs hull | Dry | 487.902 | 16.198 | 116.198 | -371.704 | 4.199 | FAIL T,L | 0 | FAIL T,L 13.922 |
| flat mesh | Dry | 150.549 | 12.317 | 112.317 | -38.232 | 1.340 | FAIL T | 0 | FAIL T 1.357 |
| stairs mesh | Dry | 514.786 | 19.370 | 119.370 | -395.416 | 4.313 | FAIL T,L | 0 | FAIL T,L 5.867 |
| analytic only | Dry | 15.068 | 0.951 | 100.951 | 85.883 | 0.149 | PASS | 20 | PASS 0.177 |
| analytic plus props | Dry | 45.322 | 5.623 | 105.623 | 60.301 | 0.429 | PASS | 20 | FAIL T 1.273 |
| hidden crate | Dry | 672.986 | 14.120 | 114.120 | -558.866 | 5.897 | FAIL T,G,L | 0 | FAIL T,G,L 17.408 |
| sphere | Dry | 42.671 | 3.326 | 103.326 | 60.655 | 0.413 | FAIL L | 0 | FAIL T,L 1.523 |
| capsule | Dry | 49.381 | 3.619 | 103.619 | 54.238 | 0.477 | FAIL L | 0 | FAIL T,L 2.695 |
| cylinder | Dry | 58.369 | 6.052 | 106.052 | 47.683 | 0.550 | PASS | 20 | FAIL T 3.178 |
| stacked irrelevant layers | Dry | 45.277 | 11.580 | 111.580 | 66.303 | 0.406 | PASS | 20 | FAIL T 1.210 |
| stationary | Dry | 15.669 | 4.939 | 104.939 | 89.270 | 0.149 | FAIL E | 0 | FAIL E 0.443 |
| initial | Dry | 45.104 | 7.379 | 107.379 | 62.275 | 0.420 | PASS | 20 | FAIL T 1.267 |
| wall retry | Dry | 24.380 | 1.009 | 101.009 | 76.629 | 0.241 | PASS | 20 | PASS 0.287 |
| pushed seat | Dry | 291.940 | 29.434 | 129.434 | -162.506 | 2.256 | FAIL T,G,L | 0 | FAIL T,G,L 7.548 |
| descending landing | Dry | 66.026 | 7.287 | 107.287 | 41.261 | 0.615 | FAIL E | 0 | FAIL T,E 1.996 |
| explicit entry | Explicit | 52.962 | 0.945 | 100.945 | 47.983 | 0.525 | FAIL E,L | 0 | FAIL E,L 0.620 |
| explicit rounding handoff | Explicit | 72.092 | 1.477 | 101.477 | 29.385 | 0.710 | FAIL E | 0 | FAIL E 0.844 |
| explicit changed-axis rounding | Explicit | 74.290 | 1.447 | 101.447 | 27.157 | 0.732 | FAIL E,L | 0 | FAIL E,L 0.885 |
| explicit wade | Explicit | 37.957 | 0.936 | 100.936 | 62.979 | 0.376 | FAIL E,L | 0 | FAIL E,L 0.453 |
| explicit swim | Explicit | 32.397 | 1.278 | 101.278 | 68.881 | 0.320 | FAIL E | 0 | FAIL E 0.381 |
| explicit medium descent retry | Explicit | 76.824 | 2.184 | 102.184 | 25.360 | 0.752 | FAIL E,L | 0 | FAIL E,L 0.888 |
| explicit dry swim handoff | Explicit | 45.124 | 0.942 | 100.942 | 55.818 | 0.447 | FAIL E,L | 0 | FAIL E,L 0.533 |
| explicit dry wade handoff | Explicit | 51.785 | 0.921 | 100.921 | 49.136 | 0.513 | FAIL E,L | 0 | FAIL E,L 0.616 |
| final clamp | Dry | 30.903 | 6.187 | 106.187 | 75.284 | 0.291 | FAIL E | 0 | FAIL E 0.872 |

### Failing ordinary rows

| Scene | Operation | Median C | Limit | Margin | C / limit | Verdict | Pass/20 |
| --- | --- | ---: | ---: | ---: | ---: | --- | ---: |
| stairs box | Find | 101.866 | 50.000 | -51.866 | 2.037 | FAIL T | 0 |
| stairs box | Dry | 463.207 | 114.306 | -348.901 | 4.052 | FAIL T,L | 0 |
| flat hull | Dry | 122.536 | 110.553 | -11.983 | 1.108 | FAIL T | 1 |
| stairs hull | Find | 103.108 | 50.000 | -53.108 | 2.062 | FAIL T | 0 |
| stairs hull | Dry | 487.902 | 116.198 | -371.704 | 4.199 | FAIL T,L | 0 |
| flat mesh | Dry | 150.549 | 112.317 | -38.232 | 1.340 | FAIL T | 0 |
| stairs mesh | Find | 102.669 | 50.000 | -52.669 | 2.053 | FAIL T | 0 |
| stairs mesh | Dry | 514.786 | 119.370 | -395.416 | 4.313 | FAIL T,L | 0 |
| hidden crate | Find | 142.709 | 50.000 | -92.709 | 2.854 | FAIL T | 0 |
| hidden crate | B0 | 166.728 | 200.000 | 33.272 | 0.834 | FAIL G | 0 |
| hidden crate | B1 | 244.784 | 200.000 | -44.784 | 1.224 | FAIL T,G | 0 |
| hidden crate | Dry | 672.986 | 114.120 | -558.866 | 5.897 | FAIL T,G,L | 0 |
| hidden crate | D0 | 163.743 | 200.000 | 36.257 | 0.819 | FAIL G | 0 |
| hidden crate | D1 | 250.324 | 200.000 | -50.324 | 1.252 | FAIL T,G | 0 |
| sphere | Dry | 42.671 | 103.326 | 60.655 | 0.413 | FAIL L | 0 |
| capsule | Dry | 49.381 | 103.619 | 54.238 | 0.477 | FAIL L | 0 |
| stationary | Dry | 15.669 | 104.939 | 89.270 | 0.149 | FAIL E | 0 |
| pushed seat | Dry | 291.940 | 129.434 | -162.506 | 2.256 | FAIL T,G,L | 0 |
| pushed seat | D0 | 93.989 | 200.000 | 106.011 | 0.470 | FAIL G | 0 |
| pushed seat | D1 | 45.411 | 200.000 | 154.589 | 0.227 | FAIL G | 0 |
| descending landing | Dry | 66.026 | 107.287 | 41.261 | 0.615 | FAIL E | 0 |
| explicit entry | Explicit | 52.962 | 100.945 | 47.983 | 0.525 | FAIL E,L | 0 |
| explicit rounding handoff | Explicit | 72.092 | 101.477 | 29.385 | 0.710 | FAIL E | 0 |
| explicit changed-axis rounding | Explicit | 74.290 | 101.447 | 27.157 | 0.732 | FAIL E,L | 0 |
| explicit wade | Explicit | 37.957 | 100.936 | 62.979 | 0.376 | FAIL E,L | 0 |
| explicit swim | Explicit | 32.397 | 101.278 | 68.881 | 0.320 | FAIL E | 0 |
| explicit medium descent retry | Explicit | 76.824 | 102.184 | 25.360 | 0.752 | FAIL E,L | 0 |
| explicit dry swim handoff | Explicit | 45.124 | 100.942 | 55.818 | 0.447 | FAIL E,L | 0 |
| explicit dry wade handoff | Explicit | 51.785 | 100.921 | 49.136 | 0.513 | FAIL E,L | 0 |
| final clamp | Dry | 30.903 | 106.187 | 75.284 | 0.291 | FAIL E | 0 |

Time failures, 13 rows:

- Find, 4 rows: stairs box -51.866 us, stairs hull -53.108 us, stairs mesh -52.669 us and hidden crate -92.709 us
  against 50 us.
- Substeps, 2 rows: hidden crate B1 -44.784 us and D1 -50.324 us against 200 us. Both also fail `G`.
- Dry whole ticks, 7 rows: stairs box -348.901 us, stairs hull -371.704 us, stairs mesh -395.416 us,
  hidden crate -558.866 us, pushed seat -162.506 us, flat mesh -38.232 us and flat hull -11.983 us.
  The stairs, hidden crate and pushed seat ticks also fail `L`, and hidden crate and pushed seat also fail `G`.

Precondition failures within their time limit, 17 rows:

- `G`: hidden crate B0 (margin 33.272 us) and D0 (36.257 us), pushed seat D0 (106.011 us) and D1 (154.589 us).
- `L`: sphere Dry (60.655 us) and capsule Dry (54.238 us).
- `E`: stationary Dry (89.270 us), descending landing Dry (41.261 us), final clamp Dry (75.284 us),
  explicit rounding handoff Explicit (29.385 us) and explicit swim Explicit (68.881 us).
- `E,L`: the Explicit ticks of explicit entry (47.983 us), explicit changed-axis rounding (27.157 us),
  explicit wade (62.979 us), explicit medium descent retry (25.360 us), explicit dry swim handoff (55.818 us)
  and explicit dry wade handoff (49.136 us).

These codes come from deterministic fields and reject in all 20 iterations. Faster support code does not clear them.

## Comparison with the corrected 376971dda verdict

The 376971dda record judged the previous candidate against `reference-376971dda` under the same SC-2 scale and
SC-3 envelope rule. Both records list the same 139 ordinary rows. 15 rows move from fail to pass and none move
from pass to fail. Every row that passed at 376971dda passes here.

| Scene | Operation | 376971dda C / limit | 376971dda verdict | 5c373abe9 C / limit | 5c373abe9 verdict |
| --- | --- | ---: | --- | ---: | --- |
| flat box | Find | 1.121 | FAIL T | 0.460 | PASS |
| flat box | Dry | 1.298 | FAIL T | 0.426 | PASS |
| flat hull | Find | 1.762 | FAIL T | 0.772 | PASS |
| stairs hull | B0 | 1.169 | FAIL T | 0.377 | PASS |
| stairs hull | B1 | 1.147 | FAIL T | 0.394 | PASS |
| stairs hull | D0 | 1.139 | FAIL T | 0.378 | PASS |
| stairs hull | D1 | 1.146 | FAIL T | 0.391 | PASS |
| analytic plus props | Dry | 1.273 | FAIL T | 0.429 | PASS |
| sphere | Find | 1.087 | FAIL T | 0.265 | PASS |
| capsule | Find | 1.879 | FAIL T | 0.300 | PASS |
| cylinder | Find | 2.241 | FAIL T | 0.356 | PASS |
| cylinder | Dry | 3.178 | FAIL T | 0.550 | PASS |
| stacked irrelevant layers | Dry | 1.210 | FAIL T | 0.406 | PASS |
| initial | Dry | 1.267 | FAIL T | 0.420 | PASS |
| pushed seat | B0 | 1.442 | FAIL T | 0.460 | PASS |

The 30 rows that still fail all failed at 376971dda. Six of them lose `T` and keep only their precondition code:
hidden crate B0 and D0, pushed seat D0, sphere Dry, capsule Dry and descending landing Dry. No row gains a code.
Median C / limit falls from 0.923 to 0.294 for Find, from 0.227 to 0.073 for substeps and from 1.267 to 0.513
for whole ticks. The stairs box, stairs hull, hidden crate and pushed seat overruns shrink by about three times.
The stairs mesh rows shrink less, from 3.144 to 2.053 for Find and from 5.867 to 4.313 for Dry. The flat hull dry
tick falls from 2.397 to 1.108 and the flat mesh dry tick barely moves, from 1.357 to 1.340. The remaining time
failures run 1.108 to 5.897 times their limit.

## Borderline rows

No ordinary row has a median within 10 percent of its limit. The nearest is the flat hull dry tick at 1.108
of its 110.553 us limit, which passes in 1 of 20 iterations and fails at the median. The highest passing ratios
are the flat mesh Find at 0.836 and the flat hull Find at 0.772. Every median pass passes in all 20 iterations. The hidden crate B0 and D0 substeps sit at 0.834 and 0.819
but fail `G` regardless of time.

## Envelope rows

An envelope row is a Find whose outcome and saved oracle outcome are both `None` with the oracle in
agreement, or a stationary substep or whole tick that was not refused, whose every support read matched
its oracle, and whose reads are all steep or absent (SC-3). `Oracle` shows the saved outcome, or `reads` for a
substep, whose oracle is checked per read. These are the same five rows as at 376971dda.

| Envelope scene | Operation | Outcome | Oracle | Reads | Median C |
| --- | --- | --- | --- | --- | ---: |
| slide opposing face | Dry | Grounded | Steep | Steep Steep Steep Steep | 19.266 |
| slide opposing face | D0 | SupportedSubstep | reads | Steep | 4.667 |
| explicit rounding handoff | Find | None | None | None | 4.443 |
| explicit changed-axis rounding | Find | None | None | None | 4.421 |
| explicit swim | Find | None | None | None | 4.371 |

## Dense rows

Dense rows have no per-operation time verdict (SC-1). Phase 6 judges their time against the SG-21 aggregate
budget. Their allocation limits still gate: zero warm bytes for Find and substeps, 208 bytes for a dry tick.
All 90 pass in every iteration. Find and substep rows allocate 0 warm bytes and dry ticks 64.

| Dense scene | Operation | Median C | Median L | Warm bytes | Allocation |
| --- | --- | ---: | ---: | ---: | --- |
| grid 100 | Find | 95965.727 | 17259.376 | 0 | PASS |
| grid 100 | Dry | 95916.688 | 27729.326 | 64 | PASS |
| fan 96 | Find | 1678.848 | 59.043 | 0 | PASS |
| fan 96 | B0 | 2272.525 | 242.479 | 0 | PASS |
| fan 96 | B1 | 2196.699 | 242.479 | 0 | PASS |
| fan 96 | Dry | 6110.979 | 242.479 | 64 | PASS |
| fan 96 | D0 | 2265.291 | 242.479 | 0 | PASS |
| fan 96 | D1 | 2183.821 | 242.479 | 0 | PASS |
| fan 256 | Find | 5214.386 | 152.256 | 0 | PASS |
| fan 256 | B0 | 8580.571 | 990.461 | 0 | PASS |
| fan 256 | B1 | 8459.828 | 990.461 | 0 | PASS |
| fan 256 | Dry | 22216.585 | 990.461 | 64 | PASS |
| fan 256 | D0 | 8526.665 | 990.461 | 0 | PASS |
| fan 256 | D1 | 8434.816 | 990.461 | 0 | PASS |
| fan 257 | Find | 3189.384 | 160.348 | 0 | PASS |
| fan 257 | Dry | 3193.183 | 792.743 | 64 | PASS |
| fan 300 | Find | 3447.903 | 186.445 | 0 | PASS |
| fan 300 | Dry | 3445.438 | 972.004 | 64 | PASS |
| cluster 8 mesh | Find | 128.746 | 13.743 | 0 | PASS |
| cluster 8 mesh | B0 | 132.065 | 37.041 | 0 | PASS |
| cluster 8 mesh | B1 | 131.916 | 37.041 | 0 | PASS |
| cluster 8 mesh | Dry | 397.919 | 37.041 | 64 | PASS |
| cluster 8 mesh | D0 | 130.147 | 37.041 | 0 | PASS |
| cluster 8 mesh | D1 | 129.801 | 37.041 | 0 | PASS |
| cluster 8 compound | Find | 84.494 | 15.375 | 0 | PASS |
| cluster 8 compound | B0 | 86.251 | 60.295 | 0 | PASS |
| cluster 8 compound | B1 | 84.457 | 60.295 | 0 | PASS |
| cluster 8 compound | Dry | 267.065 | 60.295 | 64 | PASS |
| cluster 8 compound | D0 | 85.777 | 60.295 | 0 | PASS |
| cluster 8 compound | D1 | 84.740 | 60.295 | 0 | PASS |
| cluster 8 split | Find | 82.678 | 13.482 | 0 | PASS |
| cluster 8 split | B0 | 83.280 | 35.848 | 0 | PASS |
| cluster 8 split | B1 | 82.270 | 35.848 | 0 | PASS |
| cluster 8 split | Dry | 250.120 | 35.848 | 64 | PASS |
| cluster 8 split | D0 | 82.432 | 35.848 | 0 | PASS |
| cluster 8 split | D1 | 81.164 | 35.848 | 0 | PASS |
| cluster 32 mesh | Find | 526.778 | 53.835 | 0 | PASS |
| cluster 32 mesh | B0 | 530.170 | 136.494 | 0 | PASS |
| cluster 32 mesh | B1 | 535.195 | 136.494 | 0 | PASS |
| cluster 32 mesh | Dry | 1578.277 | 136.494 | 64 | PASS |
| cluster 32 mesh | D0 | 521.694 | 136.494 | 0 | PASS |
| cluster 32 mesh | D1 | 530.180 | 136.494 | 0 | PASS |
| cluster 32 compound | Find | 363.995 | 60.506 | 0 | PASS |
| cluster 32 compound | B0 | 372.104 | 234.026 | 0 | PASS |
| cluster 32 compound | B1 | 365.392 | 234.026 | 0 | PASS |
| cluster 32 compound | Dry | 1103.360 | 234.026 | 64 | PASS |
| cluster 32 compound | D0 | 370.436 | 234.026 | 0 | PASS |
| cluster 32 compound | D1 | 366.273 | 234.026 | 0 | PASS |
| cluster 32 split | Find | 355.353 | 53.512 | 0 | PASS |
| cluster 32 split | B0 | 357.817 | 139.401 | 0 | PASS |
| cluster 32 split | B1 | 355.845 | 139.401 | 0 | PASS |
| cluster 32 split | Dry | 1067.355 | 139.401 | 64 | PASS |
| cluster 32 split | D0 | 356.167 | 139.401 | 0 | PASS |
| cluster 32 split | D1 | 352.087 | 139.401 | 0 | PASS |
| cluster 128 mesh | Find | 2602.413 | 217.967 | 0 | PASS |
| cluster 128 mesh | B0 | 2596.967 | 538.399 | 0 | PASS |
| cluster 128 mesh | B1 | 2630.777 | 538.399 | 0 | PASS |
| cluster 128 mesh | Dry | 7767.787 | 538.399 | 64 | PASS |
| cluster 128 mesh | D0 | 2576.456 | 538.399 | 0 | PASS |
| cluster 128 mesh | D1 | 2596.056 | 538.399 | 0 | PASS |
| cluster 128 compound | Find | 2106.070 | 245.018 | 0 | PASS |
| cluster 128 compound | B0 | 2119.890 | 929.344 | 0 | PASS |
| cluster 128 compound | B1 | 2108.183 | 929.344 | 0 | PASS |
| cluster 128 compound | Dry | 6328.787 | 929.344 | 64 | PASS |
| cluster 128 compound | D0 | 2119.798 | 929.344 | 0 | PASS |
| cluster 128 compound | D1 | 2102.577 | 929.344 | 0 | PASS |
| cluster 128 split | Find | 2040.174 | 215.111 | 0 | PASS |
| cluster 128 split | B0 | 2048.223 | 552.622 | 0 | PASS |
| cluster 128 split | B1 | 2045.378 | 552.622 | 0 | PASS |
| cluster 128 split | Dry | 6107.822 | 552.622 | 64 | PASS |
| cluster 128 split | D0 | 2042.722 | 552.622 | 0 | PASS |
| cluster 128 split | D1 | 2018.219 | 552.622 | 0 | PASS |
| cluster 256 mesh | Find | 6615.876 | 434.047 | 0 | PASS |
| cluster 256 mesh | B0 | 6652.133 | 1073.507 | 0 | PASS |
| cluster 256 mesh | B1 | 6706.499 | 1073.507 | 0 | PASS |
| cluster 256 mesh | Dry | 19799.387 | 1073.507 | 64 | PASS |
| cluster 256 mesh | D0 | 6553.711 | 1073.507 | 0 | PASS |
| cluster 256 mesh | D1 | 6609.079 | 1073.507 | 0 | PASS |
| cluster 256 compound | Find | 5925.201 | 513.914 | 0 | PASS |
| cluster 256 compound | B0 | 5971.843 | 1161.624 | 0 | PASS |
| cluster 256 compound | B1 | 5946.408 | 1161.624 | 0 | PASS |
| cluster 256 compound | Dry | 17731.884 | 1161.624 | 64 | PASS |
| cluster 256 compound | D0 | 5939.967 | 1161.624 | 0 | PASS |
| cluster 256 compound | D1 | 5896.072 | 1161.624 | 0 | PASS |
| cluster 256 split | Find | 5801.004 | 428.788 | 0 | PASS |
| cluster 256 split | B0 | 5830.235 | 1108.214 | 0 | PASS |
| cluster 256 split | B1 | 5779.520 | 1108.214 | 0 | PASS |
| cluster 256 split | Dry | 17300.403 | 1108.214 | 64 | PASS |
| cluster 256 split | D0 | 5763.556 | 1108.214 | 0 | PASS |
| cluster 256 split | D1 | 5722.357 | 1108.214 | 0 | PASS |

## Disposition

**#1334's ordinary gate does not pass.** The option C work clears 15 rows that failed at 376971dda and breaks
none, but 30 of 139 ordinary rows still reject at the median. 29 of them reject in all 20 iterations and the
flat hull dry tick passes in one.

Thirteen rows miss a time limit. The stairs box, stairs hull and stairs mesh Finds overrun 50 us by 51.866,
53.108 and 52.669 us. The hidden crate Find overruns by 92.709 us. The hidden crate B1 and D1 substeps overrun
200 us by 44.784 and 50.324 us. The dry whole ticks of stairs box, stairs hull, stairs mesh, hidden crate and
pushed seat overrun by 348.901, 371.704, 395.416, 558.866 and 162.506 us. The flat mesh and flat hull dry ticks
overrun by 38.232 and 11.983 us. These are the rows the option C local record already named as missing SG-12,
and they need the further SC-9 rulings it calls for.

Seventeen rows are within their time limit and reject on `G`, `E` or `L`: four hidden crate and pushed seat
substeps on geometry, the sphere and capsule dry ticks on legacy geometry, and three dry ticks and all eight
explicit ticks outside the comparable envelope. Each carried the same code at 376971dda. The codes come from
deterministic fields, not time, so #1334 cannot close on time work alone while they stand.

Dense rows and warm allocation pass. SG-21 aggregate actor CPU and consumer adoption remain separate.
No limit, tolerance, verdict code, test or production code was changed.

## Verification

- The reference invocation passed 1 of 1 test, exit 0, in 15 m 54 s through `build-slot --heavy`, and its finalizer
  `scripts/record-support-cost-exit.py` exited 0. Other heavy build-slot jobs shared the machine during it,
  against 11 m 48 s for the 376971dda reference.
- The evaluator passed 1 of 1 test through `build-slot`, exit 0. `OneShotCostArtifact` ran once locally for the
  reference and never for evaluation.
- Staged dash, prose and file-size checks passed before each commit. This is a documentation-only record.
