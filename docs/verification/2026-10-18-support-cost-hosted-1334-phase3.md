# Hosted support cost verdict for #1334, SG-12 phase 3

**Verdict: FAIL.** The committed evaluator records **135 of 139 ordinary median rows passing numerically**
and **4 rejecting**. Five oracle-correct envelope rows count as neither. All 11 ordinary E rows pass.
All 90 dense rows pass allocation in every iteration and carry no per-operation time verdict under SC-1.
Stairs mesh and hidden crate each still miss their `Find` and `DryTick` limits. #1334 stays open.
SC-19 assigns those four rows and pushed seat `DryTick` to the phase 6 SG-21 aggregate actor CPU gate.
The pushed-seat median is within its limit but borderline. It contributes to the evaluator's 135 numerical
passes and is labelled `WITHIN LIMIT, SC-19` below. This record does not accept it as an SG-12 pass.

## Run identity

- Fourth owner-approved workflow [39084426457](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084426457),
  `stress-test.yml`, project `KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj`, filter
  `FullyQualifiedName~SupportGateCostTests.OneShotCostArtifact`, 20 iterations, `load=0`, Release,
  `support-cost=true`. It succeeded at `2026-10-18T01:25:15Z`, 31 m 59 s after workflow start.
- Measured candidate `d0eb300d9a1fe43c2344d7ffd24d700ad84fbef5`, branch `feature/support-cost-phase3`.
  Since `c527b7a54`, phase 3 adds the exact stratum for horizontal axis-aligned rectangles (`043c5bf59`),
  rectangle contact certificates (`42a038ee6`) and shared immutable member facts (`7f49ced18`).
  These preserve bit-identical support results under SC-9. The SC-14 reference-admission counter proof is
  pinned by `1c5b34bc0`. The orchestrator's handoff reports that the final whole-branch review had no findings.
- Recorded source baseline `06b734c5722bc2cc95cea459255d6ec93e9236c7`.
- GitHub-hosted AMD EPYC 7763 64-Core Processor, X64, Ubuntu 26.04.1 LTS, .NET 10.0.13.
  The phase 2 run used an AMD EPYC 9V74 80-Core Processor. Cross-run differences include code and hardware
  effects. SC-2 normalization is applied independently to every iteration, so deltas are not an isolated
  measurement of the phase 3 implementation's speedup.
- `DOTNET_TieredCompilation=0`. One first-use sample, one unmeasured priming call and one warm sample per row.
- `run-1.json` through `run-20.json`, schema 2, no pending finalization, every `ProcessExitStatus=0`,
  exact candidate SHA, recording label `hosted-support-cost`, 418 samples each. `DiscardedLeading/Find`
  carries no gate or calibration verdict under SC-16.
- Capture interval `2026-10-18T00:58:21.781729+00:00` through `2026-10-18T01:25:07.8694011+00:00`.
  Artifacts were downloaded after `gh run view` confirmed `conclusion=success`. The 20 JSON files were moved
  unchanged from the artifact's `support-cost` subdirectory into the evaluator input directory.

## Reference and evaluation

- Reference section `reference-d0eb300d9` of [2026-10-11-support-gates-cost.json](2026-10-11-support-gates-cost.json),
  committed at `91d3dbcbc`, captured `2026-10-18T00:55:11.701199+00:00` at the exact measured candidate
  on an Apple M2 Max, Arm64, macOS 27.0.1, .NET 10.0.13, Release, `DOTNET_TieredCompilation=0`.
  It has 418 samples and was finalized with actual exit 0 before this evidence task. No replacement or
  additional local timing reference was recorded.
- The reference commit adds one section without changing any existing section. Its field set and order
  match `reference-c527b7a54` from `5627c3c7e`. Raw `LeadingRow` section metadata remains in the source
  artifact and is omitted from the committed section to match that shape. All sample values remain present.
  Command metadata preserves the actual invocation with a `~/` home path.
- `SupportCostHostedRecordTests.HostedRecordWithCorrectedCalibration` evaluated the 20 downloaded files once
  through `build-slot`, with `KHAOZ_SUPPORT_COST_REFERENCE_COMMIT=d0eb300d9`. It passed one test, exit 0,
  with zero skips, 6 s test duration and 55.848 s build-slot execution.
- The helper reads saved data only. It uses committed `SupportCostVerdict.TryJudgeHosted`, `JudgeRow` and
  `Check`, with no locomotion measurement and no limit, tolerance or verdict-code change.
- SC-2 uses the median hosted/M2 ratio over successful matching legacy rows whose M2 warm time exceeds
  0.5 us. Every iteration has 55 eligible rows, above the required 40. Quartiles interpolate at rank `(n + 1) p`.
- Each candidate and paired legacy time is divided by its iteration's k, then per-row medians are judged
  at `k=1`. Allocation and envelope classification agree in all iterations, as the helper asserts.
  Times below are M2-equivalent us, rounded to three decimals. `Margin = Limit - C` uses the displayed values.
- Find and ground-substep ceilings remain 50 and 200 us. Comparable whole ticks remain bounded by both
  1000 us and `legacy + max(100 us, 0.25 legacy)`. Absolute ceilings and the additive 100 us scale by k
  on hosted runs. A dry tick permits 208 warm bytes. Explicit ticks use the recorded lifetime inventory,
  312 bytes. No permission for another timing proof is implied by this record.

## SC-18 scope and deterministic bound

The [phase 2 record](2026-10-18-support-cost-hosted-1334-phase2.md#sc-18-deterministic-bound) gives the
unchanged controller-derived derivation. All 16 comparable whole ticks save legacy `Outcome="Grounded"`,
so every one retains the relative term. No row receives the no-support L basis. Historical legacy geometry
disagreement is informational and cannot independently reject the candidate. Saved `None` or `Airborne`
would remove the relative term only on an otherwise comparable whole tick.

All 11 E ticks remain ordinary rows, distinct from the five SC-3 envelope rows. They retain the absolute
1000 us ceiling and nonnegative substep, Find, sweep and penetration counts within the structural caps
shown below. These caps come from controller constants, not observed maxima. The committed median helper
checks every iteration against the caps, including later iterations rather than only the first sample.
Candidate geometry, first-use geometry, oracle agreement, refusal, allocation and malformed-legacy
preconditions are unchanged.

## Calibration and iteration verdicts

k ranges **2.121600 to 2.235324**, median **2.169737**.
Lower quartiles range 1.965818 to 2.033534,
upper quartiles 2.404714 to 2.504000.
Iteration totals range from 133 to 136 numerical ordinary passes and 3 to 6 rejects, with five envelope rows.
Stairs mesh `Find` passes in 3 of 20 iterations. Pushed seat `DryTick` is within its time limit in 13 of 20,
and stairs hull `DryTick` in 18 of 20. The other three median-rejected rows fail in all 20 iterations.

| Iteration | k | Rows | Lower quartile | Upper quartile | Ordinary pass | Ordinary reject | Envelope |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 2.165682 | 55 | 2.009815 | 2.450804 | 135 | 4 | 5 |
| 2 | 2.121600 | 55 | 1.990047 | 2.410497 | 135 | 4 | 5 |
| 3 | 2.158286 | 55 | 1.991159 | 2.455515 | 134 | 5 | 5 |
| 4 | 2.189343 | 55 | 1.965818 | 2.452574 | 134 | 5 | 5 |
| 5 | 2.226857 | 55 | 2.033520 | 2.504000 | 136 | 3 | 5 |
| 6 | 2.169760 | 55 | 1.989791 | 2.455753 | 135 | 4 | 5 |
| 7 | 2.162869 | 55 | 1.994782 | 2.410392 | 133 | 6 | 5 |
| 8 | 2.169546 | 55 | 1.986012 | 2.440439 | 135 | 4 | 5 |
| 9 | 2.235324 | 55 | 2.033534 | 2.498824 | 136 | 3 | 5 |
| 10 | 2.175429 | 55 | 1.999490 | 2.454021 | 134 | 5 | 5 |
| 11 | 2.177797 | 55 | 1.978514 | 2.463175 | 135 | 4 | 5 |
| 12 | 2.145973 | 55 | 1.979061 | 2.460364 | 134 | 5 | 5 |
| 13 | 2.159034 | 55 | 1.978786 | 2.452974 | 135 | 4 | 5 |
| 14 | 2.174228 | 55 | 1.982724 | 2.468950 | 135 | 4 | 5 |
| 15 | 2.179886 | 55 | 1.986413 | 2.462551 | 135 | 4 | 5 |
| 16 | 2.170521 | 55 | 1.984176 | 2.436701 | 134 | 5 | 5 |
| 17 | 2.154673 | 55 | 1.982237 | 2.441919 | 134 | 5 | 5 |
| 18 | 2.147429 | 55 | 1.971274 | 2.435676 | 134 | 5 | 5 |
| 19 | 2.169714 | 55 | 1.979071 | 2.404714 | 135 | 4 | 5 |
| 20 | 2.193411 | 55 | 1.984176 | 2.494456 | 136 | 3 | 5 |

### Input identities

| Iteration | SHA-256 of input JSON |
| --- | --- |
| 1 | `888d37c105533fb92f4cf9e85bb26667bb0e773e985546bafd11f4d291d63c67` |
| 2 | `6468fe8903e8ebf2f833683017de6b4506d50ee7a4633e252e9b9916923d4ab4` |
| 3 | `d0fec43ad8833b6f8de7edd135f5bbfdf686b09e8893e976020119bbb1595f90` |
| 4 | `8611eb81c5cfb7c3ee7d2979e7395fda599ce4c7d15f034e8f8b3cda16579645` |
| 5 | `a524692236e76987db5b6b6db5542493887d90aeca71816f0e82c81841a53a5e` |
| 6 | `4f64c91b9e247b2307e5733a3f3367cb5bd17402297c39e4e43168737030b116` |
| 7 | `e80729ea3806bd671d2f3f27790c1a65a5612c6fdb2f26234c77d1cf89ab3510` |
| 8 | `a5c2e3c48a882203bc9f771fa3ed59d65098fafc8c43b6625a3079bc6992e500` |
| 9 | `514fb64f6fe1d8225149bf262ca50fa885a314cc750b76e3d560d6a6c93c20be` |
| 10 | `7de3d94d41ba57d699de9171cc54801373228b7c2c36321b9e2f26c48d849210` |
| 11 | `dbb9f1da6af1881d0a1193c7b1b030e52150a3c014f5b80612270bc948d74503` |
| 12 | `109ff2fd69ab43427c1c36f87a1080121150b406e53c3613ef4d6987a7bd097e` |
| 13 | `7a20b361275a8d8cb2aa28b3f24100dd71ab8af7df9abe636cbdefbff106dc7d` |
| 14 | `66fc3cf59c2f975af8c65c8adb9be7bb886d676d95ec19612222ebc8e2cacd5a` |
| 15 | `9f4dc58b458ced4be4b8cd1d0b7a29ea728bc0e5b17c79bfadbda56dd1cab2aa` |
| 16 | `a1f38dccd87d02de012904afa84c14e0202efa17ba4a90ff49f7a45bbdaa0cc7` |
| 17 | `9bdde06eb39717ee02d28494db71addb81f1866995c1da15826c034b44813723` |
| 18 | `bee37df6f6246bd3f02c8df0948d455f75416a71bb3b201f5eb8c4b4f08fe79c` |
| 19 | `bda1d6cb939968e2688d2e954038476c611b58563b9b770802656a536a3a4193` |
| 20 | `f7185bef95fad5bad7ae23c87017dbef0c7d46aadbb1a5e9aaf2b87527f3b3f2` |

## Ordinary per-row verdicts

Evaluator median counts are **135 numerical pass, 4 reject, 5 envelope**, including 128 comparable ordinary
rows and 11 E ordinary rows. The numerical count includes pushed seat `DryTick`, whose acceptance remains
with SG-21 under SC-19. No failed row is relabelled as a pass by that disposition.

| Family | Rows | Pass | Reject | Median C / limit |
| --- | ---: | ---: | ---: | ---: |
| Find | 25 | 23 | 2 | 0.213 |
| Substep | 87 | 87 | 0 | 0.063 |
| Whole tick | 27 | 25 | 2 | 0.297 |

`B0` and `B1` mean `GroundSubstep/GroundTickBorrowed/0` and `/1`. `D0` and `D1` mean
`GroundSubstep/DryTick/0` and `/1`. `E0` and `E1` mean `GroundSubstep/ExplicitTick/0` and `/1`.
`Dry` and `Explicit` are acquired whole ticks. `T` exceeds time, `G` fails geometry/oracle/refusal,
`E` exceeds the structural bound, `L` has malformed legacy timing and `A` fails allocation.
Historical L notes for legacy geometry disagreement are informational. `Pass/20` is the helper's numerical
per-iteration count, including for pushed seat. It does not grant SG-12 acceptance to an SC-19 row.

### Find

| Scene | Operation | Median C | Median L | Limit | C / limit | Verdict | Pass/20 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| flat box | Find | 10.744 | 1.878 | 50.000 | 0.215 | PASS | 20 |
| stairs box | Find | 33.685 | 2.971 | 50.000 | 0.674 | PASS | 20 |
| flat hull | Find | 12.539 | 1.869 | 50.000 | 0.251 | PASS | 20 |
| stairs hull | Find | 35.435 | 4.432 | 50.000 | 0.709 | PASS | 20 |
| flat mesh | Find | 28.289 | 2.367 | 50.000 | 0.566 | PASS | 20 |
| stairs mesh | Find | 50.398 | 12.254 | 50.000 | 1.008 | FAIL T | 3 |
| analytic only | Find | 4.120 | 0.196 | 50.000 | 0.082 | PASS | 20 |
| analytic plus props | Find | 10.566 | 0.892 | 50.000 | 0.211 | PASS | 20 |
| hidden crate | Find | 62.724 | 4.402 | 50.000 | 1.254 | FAIL T | 0 |
| sphere | Find | 12.198 | 0.341 | 50.000 | 0.244 | PASS | 20 |
| capsule | Find | 13.790 | 0.353 | 50.000 | 0.276 | PASS | 20 |
| cylinder | Find | 16.389 | 1.066 | 50.000 | 0.328 | PASS | 20 |
| stacked irrelevant layers | Find | 10.674 | 7.806 | 50.000 | 0.213 | PASS | 20 |
| stationary | Find | 10.464 | 1.766 | 50.000 | 0.209 | PASS | 20 |
| initial | Find | 10.378 | 1.752 | 50.000 | 0.208 | PASS | 20 |
| wall retry | Find | 4.123 | 0.155 | 50.000 | 0.082 | PASS | 20 |
| pushed seat | Find | 10.440 | 0.859 | 50.000 | 0.209 | PASS | 20 |
| descending landing | Find | 10.886 | 0.919 | 50.000 | 0.218 | PASS | 20 |
| slide opposing face | Find | 4.126 | 0.992 | 50.000 | 0.083 | PASS | 20 |
| explicit entry | Find | 4.155 | 0.162 | 50.000 | 0.083 | PASS | 20 |
| explicit wade | Find | 4.076 | 0.159 | 50.000 | 0.082 | PASS | 20 |
| explicit medium descent retry | Find | 4.061 | 0.157 | 50.000 | 0.081 | PASS | 20 |
| explicit dry swim handoff | Find | 4.087 | 0.158 | 50.000 | 0.082 | PASS | 20 |
| explicit dry wade handoff | Find | 4.067 | 0.160 | 50.000 | 0.081 | PASS | 20 |
| final clamp | Find | 10.715 | 1.767 | 50.000 | 0.214 | PASS | 20 |

### Ground substeps

| Scene | Operation | Median C | Median L | Limit | C / limit | Verdict | Pass/20 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| flat box | B0 | 10.575 | 7.124 | 200.000 | 0.053 | PASS | 20 |
| flat box | B1 | 10.471 | 7.124 | 200.000 | 0.052 | PASS | 20 |
| flat box | D0 | 10.597 | 7.124 | 200.000 | 0.053 | PASS | 20 |
| flat box | D1 | 10.379 | 7.124 | 200.000 | 0.052 | PASS | 20 |
| stairs box | B0 | 23.388 | 15.215 | 200.000 | 0.117 | PASS | 20 |
| stairs box | B1 | 28.990 | 15.215 | 200.000 | 0.145 | PASS | 20 |
| stairs box | D0 | 23.951 | 15.215 | 200.000 | 0.120 | PASS | 20 |
| stairs box | D1 | 29.520 | 15.215 | 200.000 | 0.148 | PASS | 20 |
| flat hull | B0 | 13.110 | 9.924 | 200.000 | 0.066 | PASS | 20 |
| flat hull | B1 | 13.105 | 9.924 | 200.000 | 0.066 | PASS | 20 |
| flat hull | D0 | 12.866 | 9.924 | 200.000 | 0.064 | PASS | 20 |
| flat hull | D1 | 12.877 | 9.924 | 200.000 | 0.064 | PASS | 20 |
| stairs hull | B0 | 26.517 | 14.828 | 200.000 | 0.133 | PASS | 20 |
| stairs hull | B1 | 31.489 | 14.828 | 200.000 | 0.157 | PASS | 20 |
| stairs hull | D0 | 27.084 | 14.828 | 200.000 | 0.135 | PASS | 20 |
| stairs hull | D1 | 32.258 | 14.828 | 200.000 | 0.161 | PASS | 20 |
| flat mesh | B0 | 31.569 | 11.333 | 200.000 | 0.158 | PASS | 20 |
| flat mesh | B1 | 30.977 | 11.333 | 200.000 | 0.155 | PASS | 20 |
| flat mesh | D0 | 31.370 | 11.333 | 200.000 | 0.157 | PASS | 20 |
| flat mesh | D1 | 30.787 | 11.333 | 200.000 | 0.154 | PASS | 20 |
| stairs mesh | B0 | 42.745 | 17.608 | 200.000 | 0.214 | PASS | 20 |
| stairs mesh | B1 | 46.529 | 17.608 | 200.000 | 0.233 | PASS | 20 |
| stairs mesh | D0 | 43.256 | 17.608 | 200.000 | 0.216 | PASS | 20 |
| stairs mesh | D1 | 46.933 | 17.608 | 200.000 | 0.235 | PASS | 20 |
| analytic only | B0 | 4.352 | 0.882 | 200.000 | 0.022 | PASS | 20 |
| analytic only | B1 | 4.330 | 0.882 | 200.000 | 0.022 | PASS | 20 |
| analytic only | D0 | 4.358 | 0.882 | 200.000 | 0.022 | PASS | 20 |
| analytic only | D1 | 4.364 | 0.882 | 200.000 | 0.022 | PASS | 20 |
| analytic plus props | B0 | 10.330 | 7.772 | 200.000 | 0.052 | PASS | 20 |
| analytic plus props | B1 | 10.306 | 7.772 | 200.000 | 0.052 | PASS | 20 |
| analytic plus props | D0 | 10.742 | 7.772 | 200.000 | 0.054 | PASS | 20 |
| analytic plus props | D1 | 10.652 | 7.772 | 200.000 | 0.053 | PASS | 20 |
| hidden crate | B0 | 83.290 | 18.191 | 200.000 | 0.416 | PASS | 20 |
| hidden crate | B1 | 157.846 | 18.191 | 200.000 | 0.789 | PASS | 20 |
| hidden crate | D0 | 83.528 | 18.191 | 200.000 | 0.418 | PASS | 20 |
| hidden crate | D1 | 152.513 | 18.191 | 200.000 | 0.763 | PASS | 20 |
| sphere | B0 | 12.483 | 3.042 | 200.000 | 0.062 | PASS | 20 |
| sphere | B1 | 12.970 | 3.042 | 200.000 | 0.065 | PASS | 20 |
| sphere | D0 | 12.652 | 3.042 | 200.000 | 0.063 | PASS | 20 |
| sphere | D1 | 13.040 | 3.042 | 200.000 | 0.065 | PASS | 20 |
| capsule | B0 | 14.623 | 3.330 | 200.000 | 0.073 | PASS | 20 |
| capsule | B1 | 14.945 | 3.330 | 200.000 | 0.075 | PASS | 20 |
| capsule | D0 | 14.808 | 3.330 | 200.000 | 0.074 | PASS | 20 |
| capsule | D1 | 15.148 | 3.330 | 200.000 | 0.076 | PASS | 20 |
| cylinder | B0 | 17.805 | 5.591 | 200.000 | 0.089 | PASS | 20 |
| cylinder | B1 | 17.760 | 5.591 | 200.000 | 0.089 | PASS | 20 |
| cylinder | D0 | 18.120 | 5.591 | 200.000 | 0.091 | PASS | 20 |
| cylinder | D1 | 18.021 | 5.591 | 200.000 | 0.090 | PASS | 20 |
| stacked irrelevant layers | B0 | 10.304 | 12.751 | 200.000 | 0.052 | PASS | 20 |
| stacked irrelevant layers | B1 | 10.246 | 12.751 | 200.000 | 0.051 | PASS | 20 |
| stacked irrelevant layers | D0 | 10.669 | 12.751 | 200.000 | 0.053 | PASS | 20 |
| stacked irrelevant layers | D1 | 10.561 | 12.751 | 200.000 | 0.053 | PASS | 20 |
| initial | B0 | 10.512 | 6.875 | 200.000 | 0.053 | PASS | 20 |
| initial | B1 | 10.475 | 6.875 | 200.000 | 0.052 | PASS | 20 |
| initial | D0 | 10.511 | 6.875 | 200.000 | 0.053 | PASS | 20 |
| initial | D1 | 10.346 | 6.875 | 200.000 | 0.052 | PASS | 20 |
| wall retry | B0 | 8.566 | 0.949 | 200.000 | 0.043 | PASS | 20 |
| wall retry | B1 | 8.557 | 0.949 | 200.000 | 0.043 | PASS | 20 |
| wall retry | D0 | 8.666 | 0.949 | 200.000 | 0.043 | PASS | 20 |
| wall retry | D1 | 8.594 | 0.949 | 200.000 | 0.043 | PASS | 20 |
| pushed seat | B0 | 45.151 | 37.187 | 200.000 | 0.226 | PASS | 20 |
| pushed seat | B1 | 19.751 | 37.187 | 200.000 | 0.099 | PASS | 20 |
| pushed seat | D0 | 24.954 | 37.187 | 200.000 | 0.125 | PASS | 20 |
| pushed seat | D1 | 20.784 | 37.187 | 200.000 | 0.104 | PASS | 20 |
| descending landing | B0 | 10.810 | 6.761 | 200.000 | 0.054 | PASS | 20 |
| descending landing | B1 | 10.497 | 6.761 | 200.000 | 0.052 | PASS | 20 |
| descending landing | D0 | 10.493 | 6.761 | 200.000 | 0.052 | PASS | 20 |
| explicit entry | B0 | 4.326 | 0.882 | 200.000 | 0.022 | PASS | 20 |
| explicit entry | B1 | 4.342 | 0.882 | 200.000 | 0.022 | PASS | 20 |
| explicit entry | E0 | 12.358 | 0.882 | 200.000 | 0.062 | PASS | 20 |
| explicit entry | E1 | 12.051 | 0.882 | 200.000 | 0.060 | PASS | 20 |
| explicit rounding handoff | E0 | 12.596 | 1.358 | 200.000 | 0.063 | PASS | 20 |
| explicit wade | B0 | 4.318 | 0.871 | 200.000 | 0.022 | PASS | 20 |
| explicit wade | B1 | 4.354 | 0.871 | 200.000 | 0.022 | PASS | 20 |
| explicit wade | E0 | 12.558 | 0.871 | 200.000 | 0.063 | PASS | 20 |
| explicit medium descent retry | B0 | 4.324 | 2.029 | 200.000 | 0.022 | PASS | 20 |
| explicit medium descent retry | B1 | 4.367 | 2.029 | 200.000 | 0.022 | PASS | 20 |
| explicit medium descent retry | E0 | 12.397 | 2.029 | 200.000 | 0.062 | PASS | 20 |
| explicit medium descent retry | E1 | 32.359 | 2.029 | 200.000 | 0.162 | PASS | 20 |
| explicit dry swim handoff | B0 | 4.342 | 0.884 | 200.000 | 0.022 | PASS | 20 |
| explicit dry swim handoff | B1 | 4.357 | 0.884 | 200.000 | 0.022 | PASS | 20 |
| explicit dry swim handoff | E0 | 12.570 | 0.884 | 200.000 | 0.063 | PASS | 20 |
| explicit dry wade handoff | B0 | 4.326 | 0.857 | 200.000 | 0.022 | PASS | 20 |
| explicit dry wade handoff | B1 | 4.342 | 0.857 | 200.000 | 0.022 | PASS | 20 |
| explicit dry wade handoff | E0 | 12.593 | 0.857 | 200.000 | 0.063 | PASS | 20 |
| explicit dry wade handoff | E1 | 12.366 | 0.857 | 200.000 | 0.062 | PASS | 20 |
| final clamp | D0 | 10.538 | 5.788 | 200.000 | 0.053 | PASS | 20 |

### Comparable whole ticks

| Scene | Operation | Median C | Median L | Limit | C / limit | Verdict | Pass/20 | Saved legacy outcome | Legacy geometry |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| flat box | Dry | 33.067 | 7.124 | 107.124 | 0.309 | PASS | 20 | Grounded | pass |
| stairs box | Dry | 89.922 | 15.215 | 115.215 | 0.780 | PASS | 20 | Grounded | L (informational) |
| flat hull | Dry | 39.091 | 9.924 | 109.924 | 0.356 | PASS | 20 | Grounded | pass |
| stairs hull | Dry | 100.026 | 14.828 | 114.828 | 0.871 | PASS | 18 | Grounded | L (informational) |
| flat mesh | Dry | 88.731 | 11.333 | 111.333 | 0.797 | PASS | 20 | Grounded | pass |
| stairs mesh | Dry | 147.731 | 17.608 | 117.608 | 1.256 | FAIL T | 0 | Grounded | L (informational) |
| analytic only | Dry | 14.026 | 0.882 | 100.882 | 0.139 | PASS | 20 | Grounded | pass |
| analytic plus props | Dry | 33.555 | 7.772 | 107.772 | 0.311 | PASS | 20 | Grounded | pass |
| hidden crate | Dry | 393.892 | 18.191 | 118.191 | 3.333 | FAIL T | 0 | Grounded | L (informational) |
| sphere | Dry | 39.119 | 3.042 | 103.042 | 0.380 | PASS | 20 | Grounded | L (informational) |
| capsule | Dry | 45.314 | 3.330 | 103.330 | 0.439 | PASS | 20 | Grounded | L (informational) |
| cylinder | Dry | 54.197 | 5.591 | 105.591 | 0.513 | PASS | 20 | Grounded | pass |
| stacked irrelevant layers | Dry | 33.463 | 12.751 | 112.751 | 0.297 | PASS | 20 | Grounded | pass |
| initial | Dry | 32.526 | 6.875 | 106.875 | 0.304 | PASS | 20 | Grounded | pass |
| wall retry | Dry | 22.690 | 0.949 | 100.949 | 0.225 | PASS | 20 | Grounded | pass |
| pushed seat | Dry | 128.879 | 37.187 | 137.187 | 0.939 | WITHIN LIMIT, SC-19 | 13 | Grounded | L (informational) |

## E and L rows

All 11 E rows pass in all 20 iterations. No row has the no-support L basis. The absolute limit is 1000
M2-equivalent us, or `1000*k` hosted us. Count ranges include all 20 iterations. The bound column checks
all four structural caps. The seven supported dry ticks with informational legacy disagreement retain
their relative limits in the comparable whole-tick table.

| E/L scene | Operation | Basis | Median C | Absolute limit | Substeps / cap | Find / cap | Sweeps / cap | Penetrations / cap | Bound | Verdict | Pass/20 | Saved legacy outcome | Legacy geometry |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| stationary | Dry | E | 11.409 | 1000.000 | 0 to 0 / 16384 | 1 to 1 / 147495 | 0 to 0 / 745568 | 1 to 1 / 598138 | PASS | PASS | 20 | Grounded | pass |
| descending landing | Dry | E | 45.418 | 1000.000 | 1 to 1 / 16384 | 5 to 5 / 147495 | 4 to 4 / 745568 | 4 to 4 / 598138 | PASS | PASS | 20 | Grounded | pass |
| explicit entry | Explicit | E | 44.507 | 1000.000 | 2 to 2 / 16384 | 4 to 4 / 1196071 | 6 to 6 / 66904178 | 6 to 6 / 71172462 | PASS | PASS | 20 | Grounded | L (informational) |
| explicit rounding handoff | Explicit | E | 65.746 | 1000.000 | 1 to 1 / 16384 | 6 to 6 / 1196071 | 8 to 8 / 66904178 | 8 to 8 / 71172462 | PASS | PASS | 20 | Grounded | pass |
| explicit changed-axis rounding | Explicit | E | 69.123 | 1000.000 | 0 to 0 / 16384 | 7 to 7 / 1196071 | 8 to 8 / 66904178 | 6 to 6 / 71172462 | PASS | PASS | 20 | Grounded | L (informational) |
| explicit wade | Explicit | E | 31.373 | 1000.000 | 1 to 1 / 16384 | 3 to 3 / 1196071 | 4 to 4 / 66904178 | 4 to 4 / 71172462 | PASS | PASS | 20 | Grounded | L (informational) |
| explicit swim | Explicit | E | 29.875 | 1000.000 | 0 to 0 / 16384 | 3 to 3 / 1196071 | 2 to 2 / 66904178 | 2 to 2 / 71172462 | PASS | PASS | 20 | Airborne | pass |
| explicit medium descent retry | Explicit | E | 62.979 | 1000.000 | 2 to 2 / 16384 | 6 to 6 / 1196071 | 10 to 10 / 66904178 | 6 to 6 / 71172462 | PASS | PASS | 20 | Grounded | L (informational) |
| explicit dry swim handoff | Explicit | E | 37.851 | 1000.000 | 1 to 1 / 16384 | 3 to 3 / 1196071 | 6 to 6 / 66904178 | 6 to 6 / 71172462 | PASS | PASS | 20 | Grounded | L (informational) |
| explicit dry wade handoff | Explicit | E | 43.914 | 1000.000 | 2 to 2 / 16384 | 4 to 4 / 1196071 | 6 to 6 / 66904178 | 6 to 6 / 71172462 | PASS | PASS | 20 | Grounded | L (informational) |
| final clamp | Dry | E | 22.571 | 1000.000 | 1 to 1 / 16384 | 2 to 2 / 147495 | 2 to 2 / 745568 | 2 to 2 / 598138 | PASS | PASS | 20 | Grounded | pass |

### Full E/L timing and route envelope

First-use times include optimized JIT and cold scratch. They are separate from the warm gate.
The artifacts retain complete states, support reads, cold allocation, scratch high-water and stack.

| Scene | Operation | Hosted warm us, min to max | Hosted first-use us, min to max | Warm bytes | Mode segments | Medium retries | G/S/A/W segments | Features | Raycasts |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| stationary | Dry | 24.116 to 58.019 | 503.158 to 572.130 | 64 | 1 | 0 | 1/0/0/0 | 1 | 0 |
| descending landing | Dry | 97.232 to 137.506 | 13545.087 to 14407.768 | 64 | 2 | 0 | 1/0/1/0 | 5 | 0 |
| explicit entry | Explicit | 95.689 to 97.855 | 105495.707 to 111079.837 | 312 | 1 | 0 | 1/0/0/0 | 3 | 0 |
| explicit rounding handoff | Explicit | 140.259 to 189.989 | 6611.482 to 6974.575 | 312 | 2 | 0 | 1/0/0/1 | 6 | 0 |
| explicit changed-axis rounding | Explicit | 147.135 to 202.327 | 1090.980 to 1235.142 | 312 | 2 | 0 | 0/0/0/2 | 7 | 0 |
| explicit wade | Explicit | 66.205 to 94.639 | 238.258 to 279.431 | 312 | 1 | 0 | 1/0/0/0 | 2 | 0 |
| explicit swim | Explicit | 63.841 to 68.960 | 277.922 to 339.393 | 312 | 1 | 0 | 0/0/0/1 | 3 | 0 |
| explicit medium descent retry | Explicit | 133.761 to 240.770 | 6329.203 to 7138.315 | 312 | 1 | 1 | 1/0/0/0 | 4 | 0 |
| explicit dry swim handoff | Explicit | 80.702 to 114.663 | 208.392 to 269.123 | 312 | 2 | 0 | 1/0/0/1 | 2 | 0 |
| explicit dry wade handoff | Explicit | 93.836 to 134.560 | 217.620 to 334.294 | 312 | 1 | 0 | 1/0/0/0 | 3 | 0 |
| final clamp | Dry | 48.090 to 94.237 | 3038.581 to 3421.950 | 64 | 1 | 0 | 1/0/0/0 | 2 | 0 |

### Supported legacy dry-tick timing and route envelope

These seven rows retain their full measured envelope and the relative term.

| Scene | Operation | Hosted warm us, min to max | Hosted first-use us, min to max | Warm bytes | Mode segments | Medium retries | G/S/A/W segments | Features | Raycasts |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| stairs box | Dry | 192.991 to 233.508 | 2057.372 to 2341.374 | 64 | 1 | 0 | 1/0/0/0 | 3 | 0 |
| stairs hull | Dry | 208.640 to 292.971 | 1805.202 to 1884.058 | 64 | 1 | 0 | 1/0/0/0 | 3 | 0 |
| stairs mesh | Dry | 311.232 to 364.652 | 657.269 to 763.222 | 64 | 1 | 0 | 1/0/0/0 | 3 | 0 |
| hidden crate | Dry | 823.248 to 881.171 | 2882.939 to 3022.817 | 64 | 1 | 0 | 1/0/0/0 | 6 | 0 |
| sphere | Dry | 82.846 to 108.513 | 264.523 to 352.168 | 64 | 1 | 0 | 1/0/0/0 | 3 | 0 |
| capsule | Dry | 96.453 to 145.087 | 277.220 to 421.869 | 64 | 1 | 0 | 1/0/0/0 | 3 | 0 |
| pushed seat | Dry | 273.900 to 491.889 | 2545.553 to 2865.980 | 64 | 1 | 0 | 1/0/0/0 | 6 | 0 |

## SC-3 envelope rows

The five oracle-correct refusal or blocked-motion rows count as neither ordinary passes nor rejects.

| Envelope scene | Operation | Outcome | Oracle | Reads | Median C |
| --- | --- | --- | --- | --- | ---: |
| slide opposing face | Dry | Grounded | Steep | Steep Steep Steep Steep | 13.999 |
| slide opposing face | D0 | SupportedSubstep | reads | Steep | 4.338 |
| explicit rounding handoff | Find | None | None | None | 4.094 |
| explicit changed-axis rounding | Find | None | None | None | 4.077 |
| explicit swim | Find | None | None | None | 4.038 |

## Dense rows, allocation only

All 90 dense rows pass allocation in every iteration. Times remain recorded without a per-operation time
verdict under SC-1. Their SG-21 aggregate actor CPU gate is separate and remains open for phase 6.

| Dense scene | Operation | Median C | Median L | Warm bytes | Allocation |
| --- | --- | ---: | ---: | ---: | --- |
| grid 100 | Find | 91533.079 | 16075.506 | 0 | PASS |
| grid 100 | Dry | 91044.837 | 25989.265 | 64 | PASS |
| fan 96 | Find | 1043.123 | 53.821 | 0 | PASS |
| fan 96 | B0 | 1337.298 | 224.122 | 0 | PASS |
| fan 96 | B1 | 1275.735 | 224.122 | 0 | PASS |
| fan 96 | Dry | 3617.681 | 224.122 | 64 | PASS |
| fan 96 | D0 | 1317.876 | 224.122 | 0 | PASS |
| fan 96 | D1 | 1257.417 | 224.122 | 0 | PASS |
| fan 256 | Find | 3816.393 | 138.202 | 0 | PASS |
| fan 256 | B0 | 5613.182 | 914.227 | 0 | PASS |
| fan 256 | B1 | 5507.124 | 914.227 | 0 | PASS |
| fan 256 | Dry | 14868.998 | 914.227 | 64 | PASS |
| fan 256 | D0 | 5524.286 | 914.227 | 0 | PASS |
| fan 256 | D1 | 5460.383 | 914.227 | 0 | PASS |
| fan 257 | Find | 1893.319 | 143.798 | 0 | PASS |
| fan 257 | Dry | 1901.317 | 735.671 | 64 | PASS |
| fan 300 | Find | 1987.781 | 168.488 | 0 | PASS |
| fan 300 | Dry | 1995.547 | 895.282 | 64 | PASS |
| cluster 8 mesh | Find | 79.222 | 11.511 | 0 | PASS |
| cluster 8 mesh | B0 | 77.339 | 33.605 | 0 | PASS |
| cluster 8 mesh | B1 | 78.734 | 33.605 | 0 | PASS |
| cluster 8 mesh | Dry | 235.117 | 33.605 | 64 | PASS |
| cluster 8 mesh | D0 | 75.311 | 33.605 | 0 | PASS |
| cluster 8 mesh | D1 | 76.534 | 33.605 | 0 | PASS |
| cluster 8 compound | Find | 53.919 | 15.131 | 0 | PASS |
| cluster 8 compound | B0 | 54.013 | 35.290 | 0 | PASS |
| cluster 8 compound | B1 | 53.226 | 35.290 | 0 | PASS |
| cluster 8 compound | Dry | 170.378 | 35.290 | 64 | PASS |
| cluster 8 compound | D0 | 56.319 | 35.290 | 0 | PASS |
| cluster 8 compound | D1 | 55.907 | 35.290 | 0 | PASS |
| cluster 8 split | Find | 51.545 | 13.765 | 0 | PASS |
| cluster 8 split | B0 | 51.768 | 51.086 | 0 | PASS |
| cluster 8 split | B1 | 51.108 | 51.086 | 0 | PASS |
| cluster 8 split | Dry | 162.518 | 51.086 | 64 | PASS |
| cluster 8 split | D0 | 53.463 | 51.086 | 0 | PASS |
| cluster 8 split | D1 | 52.882 | 51.086 | 0 | PASS |
| cluster 32 mesh | Find | 327.384 | 44.743 | 0 | PASS |
| cluster 32 mesh | B0 | 323.028 | 124.876 | 0 | PASS |
| cluster 32 mesh | B1 | 326.499 | 124.876 | 0 | PASS |
| cluster 32 mesh | Dry | 939.832 | 124.876 | 64 | PASS |
| cluster 32 mesh | D0 | 312.052 | 124.876 | 0 | PASS |
| cluster 32 mesh | D1 | 316.338 | 124.876 | 0 | PASS |
| cluster 32 compound | Find | 241.948 | 59.773 | 0 | PASS |
| cluster 32 compound | B0 | 243.976 | 138.141 | 0 | PASS |
| cluster 32 compound | B1 | 245.469 | 138.141 | 0 | PASS |
| cluster 32 compound | Dry | 754.798 | 138.141 | 64 | PASS |
| cluster 32 compound | D0 | 254.259 | 138.141 | 0 | PASS |
| cluster 32 compound | D1 | 247.551 | 138.141 | 0 | PASS |
| cluster 32 split | Find | 235.783 | 54.471 | 0 | PASS |
| cluster 32 split | B0 | 235.843 | 200.574 | 0 | PASS |
| cluster 32 split | B1 | 233.542 | 200.574 | 0 | PASS |
| cluster 32 split | Dry | 716.426 | 200.574 | 64 | PASS |
| cluster 32 split | D0 | 241.662 | 200.574 | 0 | PASS |
| cluster 32 split | D1 | 235.763 | 200.574 | 0 | PASS |
| cluster 128 mesh | Find | 1795.754 | 184.533 | 0 | PASS |
| cluster 128 mesh | B0 | 1806.747 | 486.699 | 0 | PASS |
| cluster 128 mesh | B1 | 1825.243 | 486.699 | 0 | PASS |
| cluster 128 mesh | Dry | 5250.828 | 486.699 | 64 | PASS |
| cluster 128 mesh | D0 | 1740.507 | 486.699 | 0 | PASS |
| cluster 128 mesh | D1 | 1757.169 | 486.699 | 0 | PASS |
| cluster 128 compound | Find | 1583.496 | 239.113 | 0 | PASS |
| cluster 128 compound | B0 | 1601.885 | 519.936 | 0 | PASS |
| cluster 128 compound | B1 | 1608.727 | 519.936 | 0 | PASS |
| cluster 128 compound | Dry | 4768.802 | 519.936 | 64 | PASS |
| cluster 128 compound | D0 | 1606.045 | 519.936 | 0 | PASS |
| cluster 128 compound | D1 | 1584.301 | 519.936 | 0 | PASS |
| cluster 128 split | Find | 1509.127 | 224.488 | 0 | PASS |
| cluster 128 split | B0 | 1543.216 | 806.114 | 0 | PASS |
| cluster 128 split | B1 | 1539.515 | 806.114 | 0 | PASS |
| cluster 128 split | Dry | 4646.179 | 806.114 | 64 | PASS |
| cluster 128 split | D0 | 1548.248 | 806.114 | 0 | PASS |
| cluster 128 split | D1 | 1543.557 | 806.114 | 0 | PASS |
| cluster 256 mesh | Find | 4996.899 | 358.452 | 0 | PASS |
| cluster 256 mesh | B0 | 5033.069 | 981.216 | 0 | PASS |
| cluster 256 mesh | B1 | 5068.465 | 981.216 | 0 | PASS |
| cluster 256 mesh | Dry | 14710.544 | 981.216 | 64 | PASS |
| cluster 256 mesh | D0 | 4869.362 | 981.216 | 0 | PASS |
| cluster 256 mesh | D1 | 4902.021 | 981.216 | 0 | PASS |
| cluster 256 compound | Find | 4903.563 | 435.857 | 0 | PASS |
| cluster 256 compound | B0 | 4993.005 | 1075.441 | 0 | PASS |
| cluster 256 compound | B1 | 4975.761 | 1075.441 | 0 | PASS |
| cluster 256 compound | Dry | 14285.737 | 1075.441 | 64 | PASS |
| cluster 256 compound | D0 | 4780.213 | 1075.441 | 0 | PASS |
| cluster 256 compound | D1 | 4777.892 | 1075.441 | 0 | PASS |
| cluster 256 split | Find | 4689.500 | 440.044 | 0 | PASS |
| cluster 256 split | B0 | 4785.390 | 1601.474 | 0 | PASS |
| cluster 256 split | B1 | 4763.240 | 1601.474 | 0 | PASS |
| cluster 256 split | Dry | 14100.164 | 1601.474 | 64 | PASS |
| cluster 256 split | D0 | 4701.723 | 1601.474 | 0 | PASS |
| cluster 256 split | D1 | 4707.347 | 1601.474 | 0 | PASS |

## Borderline rows

These two ordinary median rows are within 10 percent of the binding time limit. Stairs mesh `Find` is
still a failure, 0.398 us above its limit, despite three individual iterations being within it.
Pushed seat `DryTick` is 8.308 us below its median limit but misses in 7 of 20 iterations. It remains
under SC-19 rather than being accepted as an SG-12 pass. SG-24 requires another approved proof before
borderline acceptance. No additional same-candidate proof is part of this record and none was started.
Stairs hull `DryTick` passes its median by 14.802 us, outside this 10 percent band, but misses in two
iterations. The flat-mesh dry tick is no longer borderline and passes all 20 iterations.

| Scene | Operation | Median C | Limit | Margin | C / limit | Verdict | Pass/20 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| stairs mesh | Find | 50.398 | 50.000 | -0.398 | 1.008 | FAIL T | 3 |
| pushed seat | Dry | 128.879 | 137.187 | 8.308 | 0.939 | WITHIN LIMIT, SC-19 | 13 |

## Per-row comparison with c527b7a54

The [phase 2 hosted record](2026-10-18-support-cost-hosted-1334-phase2.md) has 128 ordinary passes,
11 rejects, five envelope rows and 90 dense rows passing allocation. This run has seven additional
numerical median passes and no pass-to-reject change. Stairs box and hull `Find` and `DryTick`, the two
hidden-crate substeps and pushed seat `DryTick` move within their median time limits. Pushed seat remains
an SC-19 row. Stairs mesh `Find` and `DryTick` and hidden crate `Find` and `DryTick` still fail.
All E rows, all five envelope classifications and all 90 dense allocation results retain their status.

The table compares every ordinary row, including E rows. Delta C is phase 3 minus phase 2 in normalized us.
Each run uses its own exact-commit M2 reference and per-iteration k. Hardware differs between runs,
so these deltas include calibration and host effects as well as code changes.

| Scene | Operation | Phase 2 C | Phase 3 C | Delta C | Phase 2 verdict | Phase 3 numerical verdict |
| --- | --- | --- | --- | --- | --- | --- |
| flat box | Find | 14.467 | 10.744 | -3.723 | PASS | PASS |
| flat box | B0 | 14.612 | 10.575 | -4.037 | PASS | PASS |
| flat box | B1 | 14.417 | 10.471 | -3.946 | PASS | PASS |
| flat box | Dry | 45.269 | 33.067 | -12.202 | PASS | PASS |
| flat box | D0 | 14.683 | 10.597 | -4.086 | PASS | PASS |
| flat box | D1 | 14.459 | 10.379 | -4.080 | PASS | PASS |
| stairs box | Find | 60.042 | 33.685 | -26.357 | FAIL T | PASS |
| stairs box | B0 | 41.173 | 23.388 | -17.785 | PASS | PASS |
| stairs box | B1 | 37.694 | 28.990 | -8.704 | PASS | PASS |
| stairs box | D0 | 41.430 | 23.951 | -17.479 | PASS | PASS |
| stairs box | D1 | 38.092 | 29.520 | -8.572 | PASS | PASS |
| flat hull | Find | 18.045 | 12.539 | -5.506 | PASS | PASS |
| flat hull | B0 | 18.134 | 13.110 | -5.024 | PASS | PASS |
| flat hull | B1 | 18.090 | 13.105 | -4.985 | PASS | PASS |
| flat hull | Dry | 55.054 | 39.091 | -15.963 | PASS | PASS |
| flat hull | D0 | 18.287 | 12.866 | -5.421 | PASS | PASS |
| flat hull | D1 | 18.127 | 12.877 | -5.250 | PASS | PASS |
| stairs hull | Find | 60.798 | 35.435 | -25.363 | FAIL T | PASS |
| stairs hull | B0 | 45.776 | 26.517 | -19.259 | PASS | PASS |
| stairs hull | B1 | 41.832 | 31.489 | -10.343 | PASS | PASS |
| stairs hull | D0 | 45.787 | 27.084 | -18.703 | PASS | PASS |
| stairs hull | D1 | 41.723 | 32.258 | -9.465 | PASS | PASS |
| flat mesh | Find | 33.988 | 28.289 | -5.699 | PASS | PASS |
| flat mesh | B0 | 39.514 | 31.569 | -7.945 | PASS | PASS |
| flat mesh | B1 | 38.230 | 30.977 | -7.253 | PASS | PASS |
| flat mesh | Dry | 108.496 | 88.731 | -19.765 | PASS | PASS |
| flat mesh | D0 | 38.775 | 31.370 | -7.405 | PASS | PASS |
| flat mesh | D1 | 37.859 | 30.787 | -7.072 | PASS | PASS |
| stairs mesh | Find | 65.754 | 50.398 | -15.356 | FAIL T | FAIL T |
| stairs mesh | B0 | 52.312 | 42.745 | -9.567 | PASS | PASS |
| stairs mesh | B1 | 58.674 | 46.529 | -12.145 | PASS | PASS |
| stairs mesh | D0 | 52.487 | 43.256 | -9.231 | PASS | PASS |
| stairs mesh | D1 | 58.893 | 46.933 | -11.960 | PASS | PASS |
| analytic only | Find | 3.106 | 4.120 | 1.014 | PASS | PASS |
| analytic only | B0 | 3.335 | 4.352 | 1.017 | PASS | PASS |
| analytic only | B1 | 3.323 | 4.330 | 1.007 | PASS | PASS |
| analytic only | Dry | 11.192 | 14.026 | 2.834 | PASS | PASS |
| analytic only | D0 | 3.388 | 4.358 | 0.970 | PASS | PASS |
| analytic only | D1 | 3.387 | 4.364 | 0.977 | PASS | PASS |
| analytic plus props | Find | 14.520 | 10.566 | -3.954 | PASS | PASS |
| analytic plus props | B0 | 14.437 | 10.330 | -4.107 | PASS | PASS |
| analytic plus props | B1 | 14.387 | 10.306 | -4.081 | PASS | PASS |
| analytic plus props | Dry | 44.540 | 33.555 | -10.985 | PASS | PASS |
| analytic plus props | D0 | 14.474 | 10.742 | -3.732 | PASS | PASS |
| analytic plus props | D1 | 14.379 | 10.652 | -3.727 | PASS | PASS |
| hidden crate | Find | 85.003 | 62.724 | -22.279 | FAIL T | FAIL T |
| hidden crate | B0 | 114.410 | 83.290 | -31.120 | PASS | PASS |
| hidden crate | B1 | 210.778 | 157.846 | -52.932 | FAIL T | PASS |
| hidden crate | D0 | 113.362 | 83.528 | -29.834 | PASS | PASS |
| hidden crate | D1 | 206.096 | 152.513 | -53.583 | FAIL T | PASS |
| sphere | Find | 12.809 | 12.198 | -0.611 | PASS | PASS |
| sphere | B0 | 13.224 | 12.483 | -0.741 | PASS | PASS |
| sphere | B1 | 14.085 | 12.970 | -1.115 | PASS | PASS |
| sphere | D0 | 13.516 | 12.652 | -0.864 | PASS | PASS |
| sphere | D1 | 14.433 | 13.040 | -1.393 | PASS | PASS |
| capsule | Find | 14.830 | 13.790 | -1.040 | PASS | PASS |
| capsule | B0 | 16.196 | 14.623 | -1.573 | PASS | PASS |
| capsule | B1 | 16.733 | 14.945 | -1.788 | PASS | PASS |
| capsule | D0 | 16.379 | 14.808 | -1.571 | PASS | PASS |
| capsule | D1 | 17.122 | 15.148 | -1.974 | PASS | PASS |
| cylinder | Find | 16.892 | 16.389 | -0.503 | PASS | PASS |
| cylinder | B0 | 18.474 | 17.805 | -0.669 | PASS | PASS |
| cylinder | B1 | 18.535 | 17.760 | -0.775 | PASS | PASS |
| cylinder | Dry | 55.590 | 54.197 | -1.393 | PASS | PASS |
| cylinder | D0 | 18.671 | 18.120 | -0.551 | PASS | PASS |
| cylinder | D1 | 18.721 | 18.021 | -0.700 | PASS | PASS |
| stacked irrelevant layers | Find | 14.632 | 10.674 | -3.958 | PASS | PASS |
| stacked irrelevant layers | B0 | 14.407 | 10.304 | -4.103 | PASS | PASS |
| stacked irrelevant layers | B1 | 14.326 | 10.246 | -4.080 | PASS | PASS |
| stacked irrelevant layers | Dry | 44.426 | 33.463 | -10.963 | PASS | PASS |
| stacked irrelevant layers | D0 | 14.481 | 10.669 | -3.812 | PASS | PASS |
| stacked irrelevant layers | D1 | 14.323 | 10.561 | -3.762 | PASS | PASS |
| stationary | Find | 14.191 | 10.464 | -3.727 | PASS | PASS |
| initial | Find | 14.137 | 10.378 | -3.759 | PASS | PASS |
| initial | B0 | 14.438 | 10.512 | -3.926 | PASS | PASS |
| initial | B1 | 14.404 | 10.475 | -3.929 | PASS | PASS |
| initial | Dry | 44.235 | 32.526 | -11.709 | PASS | PASS |
| initial | D0 | 14.412 | 10.511 | -3.901 | PASS | PASS |
| initial | D1 | 14.282 | 10.346 | -3.936 | PASS | PASS |
| wall retry | Find | 3.112 | 4.123 | 1.011 | PASS | PASS |
| wall retry | B0 | 6.538 | 8.566 | 2.028 | PASS | PASS |
| wall retry | B1 | 6.511 | 8.557 | 2.046 | PASS | PASS |
| wall retry | Dry | 17.761 | 22.690 | 4.929 | PASS | PASS |
| wall retry | D0 | 6.683 | 8.666 | 1.983 | PASS | PASS |
| wall retry | D1 | 6.608 | 8.594 | 1.986 | PASS | PASS |
| pushed seat | Find | 14.418 | 10.440 | -3.978 | PASS | PASS |
| pushed seat | B0 | 82.039 | 45.151 | -36.888 | PASS | PASS |
| pushed seat | B1 | 37.818 | 19.751 | -18.067 | PASS | PASS |
| pushed seat | D0 | 43.917 | 24.954 | -18.963 | PASS | PASS |
| pushed seat | D1 | 38.768 | 20.784 | -17.984 | PASS | PASS |
| descending landing | Find | 14.738 | 10.886 | -3.852 | PASS | PASS |
| descending landing | B0 | 14.581 | 10.810 | -3.771 | PASS | PASS |
| descending landing | B1 | 14.346 | 10.497 | -3.849 | PASS | PASS |
| descending landing | D0 | 14.428 | 10.493 | -3.935 | PASS | PASS |
| slide opposing face | Find | 3.080 | 4.126 | 1.046 | PASS | PASS |
| explicit entry | Find | 3.144 | 4.155 | 1.011 | PASS | PASS |
| explicit entry | B0 | 3.354 | 4.326 | 0.972 | PASS | PASS |
| explicit entry | B1 | 3.374 | 4.342 | 0.968 | PASS | PASS |
| explicit entry | E0 | 12.706 | 12.358 | -0.348 | PASS | PASS |
| explicit entry | E1 | 12.426 | 12.051 | -0.375 | PASS | PASS |
| explicit rounding handoff | E0 | 12.794 | 12.596 | -0.198 | PASS | PASS |
| explicit wade | Find | 3.047 | 4.076 | 1.029 | PASS | PASS |
| explicit wade | B0 | 3.364 | 4.318 | 0.954 | PASS | PASS |
| explicit wade | B1 | 3.383 | 4.354 | 0.971 | PASS | PASS |
| explicit wade | E0 | 12.989 | 12.558 | -0.431 | PASS | PASS |
| explicit medium descent retry | Find | 3.042 | 4.061 | 1.019 | PASS | PASS |
| explicit medium descent retry | B0 | 3.338 | 4.324 | 0.986 | PASS | PASS |
| explicit medium descent retry | B1 | 3.329 | 4.367 | 1.038 | PASS | PASS |
| explicit medium descent retry | E0 | 12.629 | 12.397 | -0.232 | PASS | PASS |
| explicit medium descent retry | E1 | 34.982 | 32.359 | -2.623 | PASS | PASS |
| explicit dry swim handoff | Find | 3.051 | 4.087 | 1.036 | PASS | PASS |
| explicit dry swim handoff | B0 | 3.328 | 4.342 | 1.014 | PASS | PASS |
| explicit dry swim handoff | B1 | 3.302 | 4.357 | 1.055 | PASS | PASS |
| explicit dry swim handoff | E0 | 12.931 | 12.570 | -0.361 | PASS | PASS |
| explicit dry wade handoff | Find | 3.033 | 4.067 | 1.034 | PASS | PASS |
| explicit dry wade handoff | B0 | 3.325 | 4.326 | 1.001 | PASS | PASS |
| explicit dry wade handoff | B1 | 3.321 | 4.342 | 1.021 | PASS | PASS |
| explicit dry wade handoff | E0 | 12.914 | 12.593 | -0.321 | PASS | PASS |
| explicit dry wade handoff | E1 | 12.820 | 12.366 | -0.454 | PASS | PASS |
| final clamp | Find | 14.322 | 10.715 | -3.607 | PASS | PASS |
| final clamp | D0 | 14.375 | 10.538 | -3.837 | PASS | PASS |
| stairs box | Dry | 138.418 | 89.922 | -48.496 | FAIL T | PASS |
| stairs hull | Dry | 149.304 | 100.026 | -49.278 | FAIL T | PASS |
| stairs mesh | Dry | 179.567 | 147.731 | -31.836 | FAIL T | FAIL T |
| hidden crate | Dry | 517.722 | 393.892 | -123.830 | FAIL T | FAIL T |
| sphere | Dry | 41.170 | 39.119 | -2.051 | PASS | PASS |
| capsule | Dry | 49.884 | 45.314 | -4.570 | PASS | PASS |
| stationary | Dry | 15.010 | 11.409 | -3.601 | PASS | PASS |
| pushed seat | Dry | 211.430 | 128.879 | -82.551 | FAIL T | WITHIN LIMIT, SC-19 |
| descending landing | Dry | 64.426 | 45.418 | -19.008 | PASS | PASS |
| explicit entry | Explicit | 45.598 | 44.507 | -1.091 | PASS | PASS |
| explicit rounding handoff | Explicit | 64.178 | 65.746 | 1.568 | PASS | PASS |
| explicit changed-axis rounding | Explicit | 67.958 | 69.123 | 1.165 | PASS | PASS |
| explicit wade | Explicit | 32.313 | 31.373 | -0.940 | PASS | PASS |
| explicit swim | Explicit | 27.870 | 29.875 | 2.005 | PASS | PASS |
| explicit medium descent retry | Explicit | 64.978 | 62.979 | -1.999 | PASS | PASS |
| explicit dry swim handoff | Explicit | 40.032 | 37.851 | -2.181 | PASS | PASS |
| explicit dry wade handoff | Explicit | 45.088 | 43.914 | -1.174 | PASS | PASS |
| final clamp | Dry | 30.182 | 22.571 | -7.611 | PASS | PASS |

## Disposition

**The #1334 ordinary gate FAILS.** Every failing ordinary median row and its unchanged limit is listed here.
All four misses are time-only. Negative margins remain failures, including the borderline stairs mesh Find.

| Scene | Full operation | Median C | Limit | Margin | Over limit | Pass/20 |
| --- | --- | --- | --- | --- | --- | --- |
| stairs mesh | `Find` | 50.398 | 50.000 | -0.398 | 0.796% | 3 |
| stairs mesh | `DryTick` | 147.731 | 117.608 | -30.123 | 25.613% | 0 |
| hidden crate | `Find` | 62.724 | 50.000 | -12.724 | 25.448% | 0 |
| hidden crate | `DryTick` | 393.892 | 118.191 | -275.701 | 233.267% | 0 |

### SC-19, judged at phase 6 against SG-21

[Ruling SC-19](../design/CONTACT-CONTROLLER-SUPPORT-COST-2026-10-17.md) assigns the hidden crate `Find`,
hidden crate `DryTick`, pushed seat `DryTick` and stairs mesh `DryTick`, plus any phase 3 row still missing
its limit, to the SG-21 aggregate actor CPU budget on real Hollowmere actors at phase 6. The remaining
phase 3 miss adds stairs mesh `Find`, giving this five-row list. No limit changes and none of these rows
is accepted here as an SG-12 pass. Pushed seat's positive median margin is recorded without overriding
its SC-19 disposition or its SG-24 borderline requirement.

| Scene | Full operation | Median C | Unchanged limit | Margin | Numerical result | Disposition |
| --- | --- | --- | --- | --- | --- | --- |
| hidden crate | `Find` | 62.724 | 50.000 | -12.724 | MISS | Phase 6 SG-21 |
| hidden crate | `DryTick` | 393.892 | 118.191 | -275.701 | MISS | Phase 6 SG-21 |
| pushed seat | `DryTick` | 128.879 | 137.187 | 8.308 | Within limit, borderline | Phase 6 SG-21 |
| stairs mesh | `DryTick` | 147.731 | 117.608 | -30.123 | MISS | Phase 6 SG-21 |
| stairs mesh | `Find` | 50.398 | 50.000 | -0.398 | MISS | Phase 6 SG-21 |

#1334 remains open. Phase 6 must judge aggregate actor CPU before any consumer pin. Dense time,
the SG-24 borderline proof and consumer adoption remain separate work. The hosted workflow's successful
completion proves that artifacts were captured successfully, not that these SG-12 time limits passed.

## Verification

- Initial worktree guard confirmed the exact named root, branch `feature/support-cost-phase3`, HEAD
  `d0eb300d9` and no tracked modifications. Integration remains with the orchestrator.
- `gh run view 39084426457 --repo APKiwiOrg/KhaozEngine --json conclusion` returned `success`, exit 0.
  Artifact download exited 0. All 20 schema-2 files are finalized with exit 0 and the exact candidate SHA.
- Reference validation checked the exact SHA and finalization, field shape, all 418 samples, unchanged
  existing JSON sections and an additions-only diff before committing the reference.
- The single data-only hosted evaluator passed one Release test, zero failures and zero skips, through
  `build-slot`. No extra local timing run, load test, loop or repeated test run was performed.
- Whole-tree dash, prose, file-size, agent-instruction and doc-version checks passed.
