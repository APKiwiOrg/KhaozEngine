# Hosted support cost verdict for #1334, SG-12 phase 2

**Verdict: FAIL.** Ordinary median rows pass **133 of 139** and reject **6**. Five oracle-correct envelope rows
count as neither. The 90 dense rows carry no time verdict under SC-1 and pass allocation in every iteration.
All 18 ordinary E/L rows pass under SC-18. Four Finds and two hidden-crate substeps exceed their unchanged
SG-12 time limits. The #1334 ordinary gate does not pass and #1334 stays open.

## Run identity

- Owner-approved workflow [39068628267](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39068628267),
  `stress-test.yml`, project `KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj`, filter
  `FullyQualifiedName~SupportGateCostTests.OneShotCostArtifact`, 20 iterations, `load=0`, Release,
  `support-cost=true`. The workflow succeeded in 24 m 39 s.
- Measured candidate `c527b7a5408b144dccd9a48ddbd307d6f242f5f1` on engine main. Its support code equals
  `5db229087`, the phase 2 implementation. A diff of `KhaozEngine.Locomotion`, `KhaozEngine.Physics.Bepu`
  and `KhaozEngine.Game.Tests/Locomotion/Contacts` between those commits is empty. The intervening work is bug reports.
- Recorded source baseline `06b734c5722bc2cc95cea459255d6ec93e9236c7`.
- Evaluation code `b547cf342` on `fix/support-cost-hosted3`, including the committed reference `5627c3c7e`
  and the SC-18 verdict implementation.
- GitHub-hosted AMD EPYC 9V74 80-Core Processor, X64, Ubuntu 26.04.1 LTS, .NET 10.0.13.
  The previous run used an AMD EPYC 7763. Cross-run time differences include both code and hardware effects,
  with the approved SC-2 normalization applied independently to each iteration.
- `DOTNET_TieredCompilation=0`. One first-use sample, one unmeasured priming call and one warm sample per row.
- `run-1.json` through `run-20.json`, schema 2, no pending finalization, every `ProcessExitStatus=0`,
  recording label `hosted-support-cost`, 418 samples each. The added `DiscardedLeading/Find` takes the first
  measured position and carries no gate or calibration verdict (SC-16).
- Capture interval `2026-10-17T20:57:31.3126858+00:00` through `2026-10-17T21:18:05.6866867+00:00`.
  The raw artifacts and workflow log were downloaded after `gh run watch 39068628267 --exit-status` exited 0.

## Reference and evaluation

- Reference section `reference-c527b7a54` of [2026-10-11-support-gates-cost.json](2026-10-11-support-gates-cost.json),
  captured `2026-10-17T20:58:58.755951+00:00` at the exact measured commit on an Apple M2 Max, Arm64,
  macOS 27.0.1, .NET 10.0.13, Release, `DOTNET_TieredCompilation=0`. It contains 418 samples,
  finalized with actual exit 0. The reference invocation ran once at this commit, before code changes.
- The brief initially starts at `071f8440e`, which adds only the SC-18 ruling. A first reference recorded that
  real checkout SHA. The owner approved one replacement at `c527b7a54`. The first finalized artifact is retained
  separately, and the reference used here records the exact hosted SHA without relabelling it.
- The committed reference adds a section without changing any existing section. Its field set equals
  `reference-5c373abe9`. The newer raw `LeadingRow` section metadata is retained in the raw artifact and
  omitted from the committed section to meet that field-set requirement. All 418 Samples remain present.
  Its Command records the actual invocation with a `~/` home path.
- `SupportCostHostedRecordTests.HostedRecordWithCorrectedCalibration` evaluated the downloaded artifacts with
  `KHAOZ_SUPPORT_COST_HOSTED_RUNS` naming their directory, `KHAOZ_SUPPORT_COST_REFERENCE_COMMIT=c527b7a54`
  and `KHAOZ_SUPPORT_COST_HOSTED_REPORT` naming its output. The helper passed one test, exit 0.
- Every iteration and median verdict uses `SupportCostVerdict.TryJudgeHosted`, `JudgeRow` and `Check`.
  Evaluation reads data and does not execute a locomotion operation. Limits and tolerances are unchanged.
- SC-2 takes the median hosted/M2 ratio over successful matching legacy rows with M2 warm time above 0.5 us.
  There are 55 eligible rows in every iteration, above the required 40. Quartiles interpolate at rank `(n + 1) p`.
- Each candidate and paired legacy time is divided by that iteration's k. Their per-row medians are judged at
  `k=1`. Allocation fields and envelope classification agree across all 20 iterations, as the evaluator asserts.
  Times below are M2-equivalent us, rounded to three decimals. `Margin = Limit - C` uses the shown values.
- Ordinary Find and ground-substep limits remain 50 and 200 us. A comparable whole tick is bounded by both
  1000 us and `legacy + max(100 us, 0.25 legacy)`. Absolute limits and the additive 100 us scale by k on hosted runs.
  A dry tick permits 208 warm bytes. An explicit tick permits its own recorded lifetime inventory, 312 bytes.

## SC-18 deterministic bound

An L row has unsuccessful legacy geometry and uses only the absolute tick ceiling. An E row is outside the
comparable moving single-mode envelope and uses the absolute tick ceiling plus the deterministic count bound.
Both remain ordinary rows in the 139-row gate. The separate five SC-3 envelope rows count as neither.
Missing pairs, malformed legacy timing, candidate geometry, first-use geometry, oracle agreement, refusal
and warm allocation preconditions retain their existing behavior. Comparable rows retain the relative term.

`SupportCostTickBound.For` derives the bounds from controller constants, never from observed maxima. The
constants keep their values and are exposed internally for this derivation.

| Source | Constant | Value | Role |
| --- | --- | ---: | --- |
| [ContactController.cs](../../KhaozEngine.Locomotion/Contacts/ContactController.cs) line 16 | `MaxSegments` | 8 | Mode segments in one tick |
| [GroundCore.cs](../../KhaozEngine.Locomotion/Contacts/GroundCore.cs) line 62 | `MaxSubsteps` | 1024 | Ground or slide substeps per pass |
| [AirPass.cs](../../KhaozEngine.Locomotion/Contacts/AirPass.cs) line 40 | `MaxSubsteps` | 1024 | Air or water substeps per pass |
| [ShellMotion.cs](../../KhaozEngine.Locomotion/Contacts/ShellMotion.cs) lines 24 and 25 | `MaxRecoveryPasses`, `MaxSlides` | 4, 4 | Recovery and shell slide loops |
| [MovementCapsuleResolver.cs](../../KhaozEngine.Locomotion/MovementCapsuleResolver.cs) line 13 | `MaximumCorrections` | 8 | Medium retries and explicit segment correction |
| [GroundSignals.cs](../../KhaozEngine.Locomotion/Contacts/GroundSignals.cs) line 16 | `Samples` | 3 | Look-ahead Finds |

The derivation budgets both slide passes, each wall retry, lifted and standing attempts, capped medium
retries, start/seat/pushed-placement Finds, landing Finds, per-segment water and rounding reads, final clamp,
and look-ahead. A flight can try its planned route, a terrain-wall tangent and a vertical route. Shell loops
include their final clearance or slide iteration. Explicit sweeps include certification plus primitive reads.
Explicit penetration queries include the capacity-64 certification, its one maximum-capacity retry and the
primitive read. Explicit segment resolution additionally budgets its initial trace and each correction's
full/prefix traces and contact/clearance reads. These are conservative structural envelopes, not actor CPU budgets.

With `S=8`, `G=2*S*1024=16384`, `A=S*1024=8192`, `P=2*S=16`, `H=MaxSlides+1=5`, and
`R=MaximumCorrections=8`, the ground-attempt bound is `T=4*G` for dry ticks and `T=4*G*(1+R)` for explicit ticks.
The Find bound is `F=2*T+2*A+P+2*S+Samples+4`. Shell moves are `Q=H*(T+3*A)`.
Dry sweep and penetration bounds are `2*F+P+Q+2` and `(MaxRecoveryPasses+1)*(P+S)+(H+2)*T+(3*H+2)*A+2`.
Explicit sweeps use `2*F+2*(P+Q+2)` and explicit penetrations triple the primitive penetration bound.
Both add `(1+2*(R+1))*Q` for explicit segment resolution.

| E tick | Recorded ground/slide substeps cap | Find cap | Sweep cap | Penetration cap |
| --- | ---: | ---: | ---: | ---: |
| Dry | 16384 | 147495 | 745568 | 598138 |
| Explicit | 16384 | 1196071 | 66904178 | 71172462 |

The Substeps field counts ground and slide substeps. Air/water work is separately included in the query caps.
All four gated counts must be nonnegative and at most their cap. L-only rows have no new count precondition.
Tests exercise each cap and one above it, negative counts, absolute time at and above its scaled ceiling,
retained preconditions, and the unchanged comparable legacy-relative ceiling.

## Calibration and iteration verdicts

k ranges **1.216746 to 1.241518**, median **1.225143**.
Lower quartiles range 1.127964 to 1.150942, upper quartiles 1.350494 to 1.401895.
All iterations reject the same six median-failing rows. Iterations 4, 5 and 16 also reject the flat-mesh dry
tick, which passes in the other 17 iterations. Its median is within 10 percent of its limit.

| Iteration | k | Rows | Lower quartile | Upper quartile | Ordinary pass | Ordinary reject | Envelope |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1.225076 | 55 | 1.143420 | 1.373228 | 133 | 6 | 5 |
| 2 | 1.221934 | 55 | 1.143030 | 1.358211 | 133 | 6 | 5 |
| 3 | 1.223828 | 55 | 1.139549 | 1.383139 | 133 | 6 | 5 |
| 4 | 1.225200 | 55 | 1.144571 | 1.375655 | 132 | 7 | 5 |
| 5 | 1.238323 | 55 | 1.142799 | 1.361116 | 132 | 7 | 5 |
| 6 | 1.224213 | 55 | 1.135338 | 1.368396 | 133 | 6 | 5 |
| 7 | 1.228480 | 55 | 1.136232 | 1.368907 | 133 | 6 | 5 |
| 8 | 1.241518 | 55 | 1.137856 | 1.379564 | 133 | 6 | 5 |
| 9 | 1.237261 | 55 | 1.148259 | 1.381069 | 133 | 6 | 5 |
| 10 | 1.227153 | 55 | 1.131996 | 1.350494 | 133 | 6 | 5 |
| 11 | 1.223147 | 55 | 1.133801 | 1.359187 | 133 | 6 | 5 |
| 12 | 1.225143 | 55 | 1.138857 | 1.367604 | 133 | 6 | 5 |
| 13 | 1.240413 | 55 | 1.142977 | 1.387966 | 133 | 6 | 5 |
| 14 | 1.226237 | 55 | 1.133318 | 1.366812 | 133 | 6 | 5 |
| 15 | 1.225143 | 55 | 1.150942 | 1.363776 | 133 | 6 | 5 |
| 16 | 1.216746 | 55 | 1.142150 | 1.356845 | 132 | 7 | 5 |
| 17 | 1.225032 | 55 | 1.144873 | 1.401895 | 133 | 6 | 5 |
| 18 | 1.224917 | 55 | 1.127964 | 1.385079 | 133 | 6 | 5 |
| 19 | 1.219568 | 55 | 1.131241 | 1.368428 | 133 | 6 | 5 |
| 20 | 1.236571 | 55 | 1.133542 | 1.364322 | 133 | 6 | 5 |

### Input identities

| Iteration | SHA-256 of input JSON |
| --- | --- |
| 1 | `17a6269c572577d2bcf920023423561b3b86e8a19351442451627d9945d55ba4` |
| 2 | `049c332129992bcae8032d35f51375bfc1068f3e3052919d440fa996eb772536` |
| 3 | `7e4069484a046b05f7cc2e3a4bb1f903228636b37a8dee7fbaea80ccc28b68a2` |
| 4 | `9d8f5c2820b402088c3204a9c4059445d72f2814ca71cb4f5555c2152decc58d` |
| 5 | `c69a418f064c3fe7b89c1f420909c7f25368349fe71f5e30da683a68001c3460` |
| 6 | `96be519823f8f6852862572b73b13f68db7100fdb5977868b4f04fe7fa77b5cd` |
| 7 | `dc24ff3d465c3a377ce071e0f6aadb388805cd063dad003074d5baf20d95c03f` |
| 8 | `d419b1d4680973de99cd0dad6d3695a1067973923a06ae439c96a2d869792b89` |
| 9 | `c2309c23d7c0a603a995d1fe98dbb4968fcb4908323e763a60d66b2dfd978731` |
| 10 | `18257c8432e839d0532a0de5e4fcb420dbd492ff14e194747faeeed8b6e5bfde` |
| 11 | `5677d826eb585e428bb547c850ff4680e548a482995f2d826abf2b2978f4a2fb` |
| 12 | `3579d904f3f27b2aaf4b5308918dc207f669c8d2928efb5f3c1a71a16f4263b9` |
| 13 | `6fd953bf14ad11321a6bc98f735f7b47693bade7e6e1ca28e70708f9086134e4` |
| 14 | `a74553d6cc1d77332cefd0d5684f86003563d414dc277cbcae2573a780844df8` |
| 15 | `531fe4dff30690dc117e7b5844fbb62d0803b14962d8df36d116d4f9e6ba9190` |
| 16 | `9b3b986a69e2b6f73fed63b93d39d6c201ac3f9095db1a6f27da51c974f30b65` |
| 17 | `c6368410a045f1f674120787a91c6f49f077fcf3746bb83df7cea9bb723e1d91` |
| 18 | `f032a857bc91da5a8e6acafe5e0775e8cec6b3067763d88a4ea2e99223d7f21c` |
| 19 | `4d2d1da9089d91643b19130f5faa7535054b8430dadf0780ca9973966742efcd` |
| 20 | `e814fddcc2a28a12ba79bef643770ab1ec567a3698ddd61543cf3e61307f2609` |

## Ordinary per-row verdicts

Median rows: **133 pass, 6 reject, 5 envelope**. There are 121 comparable ordinary rows and 18 E/L ordinary rows.

| Family | Rows | Pass | Reject | Median C / limit |
| --- | ---: | ---: | ---: | ---: |
| Find | 25 | 21 | 4 | 0.288 |
| Substep | 87 | 85 | 2 | 0.072 |
| Whole tick | 27 | 27 | 0 | 0.111 |

`B0` and `B1` are `GroundSubstep/GroundTickBorrowed/0` and `/1`. `D0` and `D1` are
`GroundSubstep/DryTick/0` and `/1`. `E0` and `E1` are `GroundSubstep/ExplicitTick/0` and `/1`.
`Dry` and `Explicit` are acquired whole ticks. `T` exceeds time, `G` fails geometry/oracle/refusal,
`E` exceeds the deterministic bound for an ordinary incomparable tick, `L` has malformed legacy timing,
and `A` fails allocation. The E/L Basis column below identifies SC-18 classification, not failure codes.
`Pass/20` counts the iterations whose own row passed.

### Find

| Scene | Operation | Median C | Median L | Limit | C / limit | Verdict | Pass/20 |
| --- | --- | ---: | ---: | ---: | ---: | --- | ---: |
| flat box | Find | 14.467 | 1.723 | 50.000 | 0.289 | PASS | 20 |
| stairs box | Find | 60.042 | 2.493 | 50.000 | 1.201 | FAIL T | 0 |
| flat hull | Find | 18.045 | 2.278 | 50.000 | 0.361 | PASS | 20 |
| stairs hull | Find | 60.798 | 5.923 | 50.000 | 1.216 | FAIL T | 0 |
| flat mesh | Find | 33.988 | 2.934 | 50.000 | 0.680 | PASS | 20 |
| stairs mesh | Find | 65.754 | 7.138 | 50.000 | 1.315 | FAIL T | 0 |
| analytic only | Find | 3.106 | 0.237 | 50.000 | 0.062 | PASS | 20 |
| analytic plus props | Find | 14.520 | 0.866 | 50.000 | 0.290 | PASS | 20 |
| hidden crate | Find | 85.003 | 3.623 | 50.000 | 1.700 | FAIL T | 0 |
| sphere | Find | 12.809 | 0.423 | 50.000 | 0.256 | PASS | 20 |
| capsule | Find | 14.830 | 0.434 | 50.000 | 0.297 | PASS | 20 |
| cylinder | Find | 16.892 | 1.411 | 50.000 | 0.338 | PASS | 20 |
| stacked irrelevant layers | Find | 14.632 | 6.397 | 50.000 | 0.293 | PASS | 20 |
| stationary | Find | 14.191 | 1.581 | 50.000 | 0.284 | PASS | 20 |
| initial | Find | 14.137 | 1.598 | 50.000 | 0.283 | PASS | 20 |
| wall retry | Find | 3.112 | 0.203 | 50.000 | 0.062 | PASS | 20 |
| pushed seat | Find | 14.418 | 0.865 | 50.000 | 0.288 | PASS | 20 |
| descending landing | Find | 14.738 | 0.855 | 50.000 | 0.295 | PASS | 20 |
| slide opposing face | Find | 3.080 | 1.422 | 50.000 | 0.062 | PASS | 20 |
| explicit entry | Find | 3.144 | 0.201 | 50.000 | 0.063 | PASS | 20 |
| explicit wade | Find | 3.047 | 0.197 | 50.000 | 0.061 | PASS | 20 |
| explicit medium descent retry | Find | 3.042 | 0.204 | 50.000 | 0.061 | PASS | 20 |
| explicit dry swim handoff | Find | 3.051 | 0.202 | 50.000 | 0.061 | PASS | 20 |
| explicit dry wade handoff | Find | 3.033 | 0.196 | 50.000 | 0.061 | PASS | 20 |
| final clamp | Find | 14.322 | 1.588 | 50.000 | 0.286 | PASS | 20 |

### Ground substeps

| Scene | Operation | Median C | Median L | Limit | C / limit | Verdict | Pass/20 |
| --- | --- | ---: | ---: | ---: | ---: | --- | ---: |
| flat box | B0 | 14.612 | 6.590 | 200.000 | 0.073 | PASS | 20 |
| flat box | B1 | 14.417 | 6.590 | 200.000 | 0.072 | PASS | 20 |
| flat box | D0 | 14.683 | 6.590 | 200.000 | 0.073 | PASS | 20 |
| flat box | D1 | 14.459 | 6.590 | 200.000 | 0.072 | PASS | 20 |
| stairs box | B0 | 41.173 | 11.315 | 200.000 | 0.206 | PASS | 20 |
| stairs box | B1 | 37.694 | 11.315 | 200.000 | 0.188 | PASS | 20 |
| stairs box | D0 | 41.430 | 11.315 | 200.000 | 0.207 | PASS | 20 |
| stairs box | D1 | 38.092 | 11.315 | 200.000 | 0.190 | PASS | 20 |
| flat hull | B0 | 18.134 | 11.951 | 200.000 | 0.091 | PASS | 20 |
| flat hull | B1 | 18.090 | 11.951 | 200.000 | 0.090 | PASS | 20 |
| flat hull | D0 | 18.287 | 11.951 | 200.000 | 0.091 | PASS | 20 |
| flat hull | D1 | 18.127 | 11.951 | 200.000 | 0.091 | PASS | 20 |
| stairs hull | B0 | 45.776 | 18.614 | 200.000 | 0.229 | PASS | 20 |
| stairs hull | B1 | 41.832 | 18.614 | 200.000 | 0.209 | PASS | 20 |
| stairs hull | D0 | 45.787 | 18.614 | 200.000 | 0.229 | PASS | 20 |
| stairs hull | D1 | 41.723 | 18.614 | 200.000 | 0.209 | PASS | 20 |
| flat mesh | B0 | 39.514 | 14.151 | 200.000 | 0.198 | PASS | 20 |
| flat mesh | B1 | 38.230 | 14.151 | 200.000 | 0.191 | PASS | 20 |
| flat mesh | D0 | 38.775 | 14.151 | 200.000 | 0.194 | PASS | 20 |
| flat mesh | D1 | 37.859 | 14.151 | 200.000 | 0.189 | PASS | 20 |
| stairs mesh | B0 | 52.312 | 22.123 | 200.000 | 0.262 | PASS | 20 |
| stairs mesh | B1 | 58.674 | 22.123 | 200.000 | 0.293 | PASS | 20 |
| stairs mesh | D0 | 52.487 | 22.123 | 200.000 | 0.262 | PASS | 20 |
| stairs mesh | D1 | 58.893 | 22.123 | 200.000 | 0.294 | PASS | 20 |
| analytic only | B0 | 3.335 | 1.057 | 200.000 | 0.017 | PASS | 20 |
| analytic only | B1 | 3.323 | 1.057 | 200.000 | 0.017 | PASS | 20 |
| analytic only | D0 | 3.388 | 1.057 | 200.000 | 0.017 | PASS | 20 |
| analytic only | D1 | 3.387 | 1.057 | 200.000 | 0.017 | PASS | 20 |
| analytic plus props | B0 | 14.437 | 4.711 | 200.000 | 0.072 | PASS | 20 |
| analytic plus props | B1 | 14.387 | 4.711 | 200.000 | 0.072 | PASS | 20 |
| analytic plus props | D0 | 14.474 | 4.711 | 200.000 | 0.072 | PASS | 20 |
| analytic plus props | D1 | 14.379 | 4.711 | 200.000 | 0.072 | PASS | 20 |
| hidden crate | B0 | 114.410 | 11.741 | 200.000 | 0.572 | PASS | 20 |
| hidden crate | B1 | 210.778 | 11.741 | 200.000 | 1.054 | FAIL T | 0 |
| hidden crate | D0 | 113.362 | 11.741 | 200.000 | 0.567 | PASS | 20 |
| hidden crate | D1 | 206.096 | 11.741 | 200.000 | 1.030 | FAIL T | 0 |
| sphere | B0 | 13.224 | 4.135 | 200.000 | 0.066 | PASS | 20 |
| sphere | B1 | 14.085 | 4.135 | 200.000 | 0.070 | PASS | 20 |
| sphere | D0 | 13.516 | 4.135 | 200.000 | 0.068 | PASS | 20 |
| sphere | D1 | 14.433 | 4.135 | 200.000 | 0.072 | PASS | 20 |
| capsule | B0 | 16.196 | 4.416 | 200.000 | 0.081 | PASS | 20 |
| capsule | B1 | 16.733 | 4.416 | 200.000 | 0.084 | PASS | 20 |
| capsule | D0 | 16.379 | 4.416 | 200.000 | 0.082 | PASS | 20 |
| capsule | D1 | 17.122 | 4.416 | 200.000 | 0.086 | PASS | 20 |
| cylinder | B0 | 18.474 | 7.166 | 200.000 | 0.092 | PASS | 20 |
| cylinder | B1 | 18.535 | 7.166 | 200.000 | 0.093 | PASS | 20 |
| cylinder | D0 | 18.671 | 7.166 | 200.000 | 0.093 | PASS | 20 |
| cylinder | D1 | 18.721 | 7.166 | 200.000 | 0.094 | PASS | 20 |
| stacked irrelevant layers | B0 | 14.407 | 9.375 | 200.000 | 0.072 | PASS | 20 |
| stacked irrelevant layers | B1 | 14.326 | 9.375 | 200.000 | 0.072 | PASS | 20 |
| stacked irrelevant layers | D0 | 14.481 | 9.375 | 200.000 | 0.072 | PASS | 20 |
| stacked irrelevant layers | D1 | 14.323 | 9.375 | 200.000 | 0.072 | PASS | 20 |
| initial | B0 | 14.438 | 6.175 | 200.000 | 0.072 | PASS | 20 |
| initial | B1 | 14.404 | 6.175 | 200.000 | 0.072 | PASS | 20 |
| initial | D0 | 14.412 | 6.175 | 200.000 | 0.072 | PASS | 20 |
| initial | D1 | 14.282 | 6.175 | 200.000 | 0.071 | PASS | 20 |
| wall retry | B0 | 6.538 | 1.008 | 200.000 | 0.033 | PASS | 20 |
| wall retry | B1 | 6.511 | 1.008 | 200.000 | 0.033 | PASS | 20 |
| wall retry | D0 | 6.683 | 1.008 | 200.000 | 0.033 | PASS | 20 |
| wall retry | D1 | 6.608 | 1.008 | 200.000 | 0.033 | PASS | 20 |
| pushed seat | B0 | 82.039 | 24.626 | 200.000 | 0.410 | PASS | 20 |
| pushed seat | B1 | 37.818 | 24.626 | 200.000 | 0.189 | PASS | 20 |
| pushed seat | D0 | 43.917 | 24.626 | 200.000 | 0.220 | PASS | 20 |
| pushed seat | D1 | 38.768 | 24.626 | 200.000 | 0.194 | PASS | 20 |
| descending landing | B0 | 14.581 | 6.154 | 200.000 | 0.073 | PASS | 20 |
| descending landing | B1 | 14.346 | 6.154 | 200.000 | 0.072 | PASS | 20 |
| descending landing | D0 | 14.428 | 6.154 | 200.000 | 0.072 | PASS | 20 |
| explicit entry | B0 | 3.354 | 1.029 | 200.000 | 0.017 | PASS | 20 |
| explicit entry | B1 | 3.374 | 1.029 | 200.000 | 0.017 | PASS | 20 |
| explicit entry | E0 | 12.706 | 1.029 | 200.000 | 0.064 | PASS | 20 |
| explicit entry | E1 | 12.426 | 1.029 | 200.000 | 0.062 | PASS | 20 |
| explicit rounding handoff | E0 | 12.794 | 1.701 | 200.000 | 0.064 | PASS | 20 |
| explicit wade | B0 | 3.364 | 1.065 | 200.000 | 0.017 | PASS | 20 |
| explicit wade | B1 | 3.383 | 1.065 | 200.000 | 0.017 | PASS | 20 |
| explicit wade | E0 | 12.989 | 1.065 | 200.000 | 0.065 | PASS | 20 |
| explicit medium descent retry | B0 | 3.338 | 2.759 | 200.000 | 0.017 | PASS | 20 |
| explicit medium descent retry | B1 | 3.329 | 2.759 | 200.000 | 0.017 | PASS | 20 |
| explicit medium descent retry | E0 | 12.629 | 2.759 | 200.000 | 0.063 | PASS | 20 |
| explicit medium descent retry | E1 | 34.982 | 2.759 | 200.000 | 0.175 | PASS | 20 |
| explicit dry swim handoff | B0 | 3.328 | 1.062 | 200.000 | 0.017 | PASS | 20 |
| explicit dry swim handoff | B1 | 3.302 | 1.062 | 200.000 | 0.017 | PASS | 20 |
| explicit dry swim handoff | E0 | 12.931 | 1.062 | 200.000 | 0.065 | PASS | 20 |
| explicit dry wade handoff | B0 | 3.325 | 1.054 | 200.000 | 0.017 | PASS | 20 |
| explicit dry wade handoff | B1 | 3.321 | 1.054 | 200.000 | 0.017 | PASS | 20 |
| explicit dry wade handoff | E0 | 12.914 | 1.054 | 200.000 | 0.065 | PASS | 20 |
| explicit dry wade handoff | E1 | 12.820 | 1.054 | 200.000 | 0.064 | PASS | 20 |
| final clamp | D0 | 14.375 | 5.136 | 200.000 | 0.072 | PASS | 20 |

### Comparable whole ticks

| Scene | Operation | Median C | Median L | Limit | C / limit | Verdict | Pass/20 |
| --- | --- | ---: | ---: | ---: | ---: | --- | ---: |
| flat box | Dry | 45.269 | 6.590 | 106.590 | 0.425 | PASS | 20 |
| flat hull | Dry | 55.054 | 11.951 | 111.951 | 0.492 | PASS | 20 |
| flat mesh | Dry | 108.496 | 14.151 | 114.151 | 0.950 | PASS | 17 |
| analytic only | Dry | 11.192 | 1.057 | 101.057 | 0.111 | PASS | 20 |
| analytic plus props | Dry | 44.540 | 4.711 | 104.711 | 0.425 | PASS | 20 |
| cylinder | Dry | 55.590 | 7.166 | 107.166 | 0.519 | PASS | 20 |
| stacked irrelevant layers | Dry | 44.426 | 9.375 | 109.375 | 0.406 | PASS | 20 |
| initial | Dry | 44.235 | 6.175 | 106.175 | 0.417 | PASS | 20 |
| wall retry | Dry | 17.761 | 1.008 | 101.008 | 0.176 | PASS | 20 |

## E and L rows

All 18 pass in all 20 iterations. Every absolute tick limit is 1000 M2-equivalent us, or `1000*k` hosted us.
`n/a` means SC-18 applies only the absolute limit to an L-only row. E rows additionally enforce the shown caps.
The count cells show the full observed minimum and maximum over the 20 runs.

| E/L scene | Operation | Basis | Median C | Absolute limit | Substeps / cap | Find / cap | Sweeps / cap | Penetrations / cap | Bound | Verdict | Pass/20 |
| --- | --- | --- | ---: | ---: | --- | --- | --- | --- | --- | --- | ---: |
| stairs box | Dry | L | 138.418 | 1000.000 | 2 to 2 / n/a | 3 to 3 / n/a | 3 to 3 / n/a | 3 to 3 / n/a | not required | PASS | 20 |
| stairs hull | Dry | L | 149.304 | 1000.000 | 2 to 2 / n/a | 3 to 3 / n/a | 3 to 3 / n/a | 3 to 3 / n/a | not required | PASS | 20 |
| stairs mesh | Dry | L | 179.567 | 1000.000 | 2 to 2 / n/a | 3 to 3 / n/a | 3 to 3 / n/a | 3 to 3 / n/a | not required | PASS | 20 |
| hidden crate | Dry | L | 517.722 | 1000.000 | 2 to 2 / n/a | 6 to 6 / n/a | 3 to 3 / n/a | 3 to 3 / n/a | not required | PASS | 20 |
| sphere | Dry | L | 41.170 | 1000.000 | 2 to 2 / n/a | 3 to 3 / n/a | 3 to 3 / n/a | 3 to 3 / n/a | not required | PASS | 20 |
| capsule | Dry | L | 49.884 | 1000.000 | 2 to 2 / n/a | 3 to 3 / n/a | 3 to 3 / n/a | 3 to 3 / n/a | not required | PASS | 20 |
| stationary | Dry | E | 15.010 | 1000.000 | 0 to 0 / 16384 | 1 to 1 / 147495 | 0 to 0 / 745568 | 1 to 1 / 598138 | PASS | PASS | 20 |
| pushed seat | Dry | L | 211.430 | 1000.000 | 2 to 2 / n/a | 7 to 7 / n/a | 3 to 3 / n/a | 4 to 4 / n/a | not required | PASS | 20 |
| descending landing | Dry | E | 64.426 | 1000.000 | 1 to 1 / 16384 | 5 to 5 / 147495 | 4 to 4 / 745568 | 4 to 4 / 598138 | PASS | PASS | 20 |
| explicit entry | Explicit | E,L | 45.598 | 1000.000 | 2 to 2 / 16384 | 4 to 4 / 1196071 | 6 to 6 / 66904178 | 6 to 6 / 71172462 | PASS | PASS | 20 |
| explicit rounding handoff | Explicit | E | 64.178 | 1000.000 | 1 to 1 / 16384 | 6 to 6 / 1196071 | 8 to 8 / 66904178 | 8 to 8 / 71172462 | PASS | PASS | 20 |
| explicit changed-axis rounding | Explicit | E,L | 67.958 | 1000.000 | 0 to 0 / 16384 | 7 to 7 / 1196071 | 8 to 8 / 66904178 | 6 to 6 / 71172462 | PASS | PASS | 20 |
| explicit wade | Explicit | E,L | 32.313 | 1000.000 | 1 to 1 / 16384 | 3 to 3 / 1196071 | 4 to 4 / 66904178 | 4 to 4 / 71172462 | PASS | PASS | 20 |
| explicit swim | Explicit | E | 27.870 | 1000.000 | 0 to 0 / 16384 | 3 to 3 / 1196071 | 2 to 2 / 66904178 | 2 to 2 / 71172462 | PASS | PASS | 20 |
| explicit medium descent retry | Explicit | E,L | 64.978 | 1000.000 | 2 to 2 / 16384 | 6 to 6 / 1196071 | 10 to 10 / 66904178 | 6 to 6 / 71172462 | PASS | PASS | 20 |
| explicit dry swim handoff | Explicit | E,L | 40.032 | 1000.000 | 1 to 1 / 16384 | 3 to 3 / 1196071 | 6 to 6 / 66904178 | 6 to 6 / 71172462 | PASS | PASS | 20 |
| explicit dry wade handoff | Explicit | E,L | 45.088 | 1000.000 | 2 to 2 / 16384 | 4 to 4 / 1196071 | 6 to 6 / 66904178 | 6 to 6 / 71172462 | PASS | PASS | 20 |
| final clamp | Dry | E | 30.182 | 1000.000 | 1 to 1 / 16384 | 2 to 2 / 147495 | 2 to 2 / 745568 | 2 to 2 / 598138 | PASS | PASS | 20 |

### Full E/L timing and route envelope

First-use times include optimized JIT and cold scratch. They are recorded separately from the warm gate.
All rows retain full states, support reads, cold allocation, scratch high-water and stack in the raw artifacts.

| Scene | Operation | Hosted warm us, min to max | Hosted first-use us, min to max | Warm bytes | Mode segments | Medium retries | G/S/A/W segments | Features | Raycasts |
| --- | --- | --- | --- | ---: | --- | --- | --- | --- | --- |
| stairs box | Dry | 166.402 to 186.833 | 1737.810 to 1937.003 | 64 | 1 | 0 | 1/0/0/0 | 3 | 0 |
| stairs hull | Dry | 180.673 to 254.991 | 1554.774 to 1680.334 | 64 | 1 | 0 | 1/0/0/0 | 3 | 0 |
| stairs mesh | Dry | 213.443 to 251.471 | 501.920 to 596.684 | 64 | 1 | 0 | 1/0/0/0 | 3 | 0 |
| hidden crate | Dry | 626.679 to 657.105 | 2195.333 to 2359.602 | 64 | 1 | 0 | 1/0/0/0 | 6 | 0 |
| sphere | Dry | 49.265 to 79.491 | 206.182 to 242.378 | 64 | 1 | 0 | 1/0/0/0 | 3 | 0 |
| capsule | Dry | 59.480 to 76.285 | 215.346 to 246.985 | 64 | 1 | 0 | 1/0/0/0 | 3 | 0 |
| stationary | Dry | 18.107 to 24.587 | 406.015 to 767.632 | 64 | 1 | 0 | 1/0/0/0 | 1 | 0 |
| pushed seat | Dry | 251.971 to 290.230 | 2156.556 to 2325.410 | 64 | 1 | 0 | 1/0/0/0 | 6 | 0 |
| descending landing | Dry | 78.279 to 100.402 | 10922.454 to 11506.359 | 64 | 2 | 0 | 1/0/1/0 | 5 | 0 |
| explicit entry | Explicit | 54.933 to 80.673 | 86216.303 to 89034.182 | 312 | 1 | 0 | 1/0/0/0 | 3 | 0 |
| explicit rounding handoff | Explicit | 77.237 to 94.703 | 5522.032 to 5730.656 | 312 | 2 | 0 | 1/0/0/1 | 6 | 0 |
| explicit changed-axis rounding | Explicit | 79.731 to 100.712 | 820.663 to 929.517 | 312 | 2 | 0 | 0/0/0/2 | 7 | 0 |
| explicit wade | Explicit | 39.009 to 57.518 | 157.007 to 193.433 | 312 | 1 | 0 | 1/0/0/0 | 2 | 0 |
| explicit swim | Explicit | 33.681 to 36.505 | 148.655 to 180.283 | 312 | 1 | 0 | 0/0/0/1 | 3 | 0 |
| explicit medium descent retry | Explicit | 79.300 to 96.196 | 5239.491 to 5693.439 | 312 | 1 | 1 | 1/0/0/0 | 4 | 0 |
| explicit dry swim handoff | Explicit | 48.143 to 50.356 | 159.491 to 185.581 | 312 | 2 | 0 | 1/0/0/1 | 2 | 0 |
| explicit dry wade handoff | Explicit | 54.102 to 70.647 | 161.715 to 229.607 | 312 | 1 | 0 | 1/0/0/0 | 3 | 0 |
| final clamp | Dry | 36.285 to 50.537 | 2484.161 to 2733.438 | 64 | 1 | 0 | 1/0/0/0 | 2 | 0 |

## SC-3 envelope rows

These five rows are outside pass and reject counts, with the oracle-correct refusal or blocked-motion rules unchanged.

| Envelope scene | Operation | Outcome | Oracle | Reads | Median C |
| --- | --- | --- | --- | --- | ---: |
| slide opposing face | Dry | Grounded | Steep | Steep Steep Steep Steep | 11.289 |
| slide opposing face | D0 | SupportedSubstep | reads | Steep | 3.280 |
| explicit rounding handoff | Find | None | None | None | 3.050 |
| explicit changed-axis rounding | Find | None | None | None | 3.032 |
| explicit swim | Find | None | None | None | 3.006 |

## Dense rows, allocation only

All 90 dense rows pass allocation in every iteration. Their time is recorded without a per-operation time verdict
under SC-1. SG-21 aggregate actor CPU remains a separate phase 6 gate.

| Dense scene | Operation | Median C | Median L | Warm bytes | Allocation |
| --- | --- | ---: | ---: | ---: | --- |
| grid 100 | Find | 124590.031 | 20327.102 | 0 | PASS |
| grid 100 | Dry | 124384.731 | 33715.871 | 64 | PASS |
| fan 96 | Find | 1440.506 | 69.100 | 0 | PASS |
| fan 96 | B0 | 1853.124 | 287.792 | 0 | PASS |
| fan 96 | B1 | 1766.388 | 287.792 | 0 | PASS |
| fan 96 | Dry | 5021.044 | 287.792 | 64 | PASS |
| fan 96 | D0 | 1841.361 | 287.792 | 0 | PASS |
| fan 96 | D1 | 1755.494 | 287.792 | 0 | PASS |
| fan 256 | Find | 5322.798 | 174.750 | 0 | PASS |
| fan 256 | B0 | 7878.348 | 1265.926 | 0 | PASS |
| fan 256 | B1 | 7709.532 | 1265.926 | 0 | PASS |
| fan 256 | Dry | 20938.850 | 1265.926 | 64 | PASS |
| fan 256 | D0 | 7852.596 | 1265.926 | 0 | PASS |
| fan 256 | D1 | 7706.821 | 1265.926 | 0 | PASS |
| fan 257 | Find | 2555.297 | 184.991 | 0 | PASS |
| fan 257 | Dry | 2577.260 | 992.212 | 64 | PASS |
| fan 300 | Find | 2669.021 | 214.543 | 0 | PASS |
| fan 300 | Dry | 2697.980 | 1196.362 | 64 | PASS |
| cluster 8 mesh | Find | 98.972 | 13.359 | 0 | PASS |
| cluster 8 mesh | B0 | 98.104 | 41.200 | 0 | PASS |
| cluster 8 mesh | B1 | 97.471 | 41.200 | 0 | PASS |
| cluster 8 mesh | Dry | 291.400 | 41.200 | 64 | PASS |
| cluster 8 mesh | D0 | 97.384 | 41.200 | 0 | PASS |
| cluster 8 mesh | D1 | 96.129 | 41.200 | 0 | PASS |
| cluster 8 compound | Find | 95.653 | 13.504 | 0 | PASS |
| cluster 8 compound | B0 | 95.848 | 35.251 | 0 | PASS |
| cluster 8 compound | B1 | 94.262 | 35.251 | 0 | PASS |
| cluster 8 compound | Dry | 298.873 | 35.251 | 64 | PASS |
| cluster 8 compound | D0 | 100.319 | 35.251 | 0 | PASS |
| cluster 8 compound | D1 | 98.318 | 35.251 | 0 | PASS |
| cluster 8 split | Find | 91.436 | 11.091 | 0 | PASS |
| cluster 8 split | B0 | 91.910 | 29.198 | 0 | PASS |
| cluster 8 split | B1 | 90.396 | 29.198 | 0 | PASS |
| cluster 8 split | Dry | 274.985 | 29.198 | 64 | PASS |
| cluster 8 split | D0 | 91.653 | 29.198 | 0 | PASS |
| cluster 8 split | D1 | 89.509 | 29.198 | 0 | PASS |
| cluster 32 mesh | Find | 420.055 | 52.310 | 0 | PASS |
| cluster 32 mesh | B0 | 408.943 | 156.276 | 0 | PASS |
| cluster 32 mesh | B1 | 416.149 | 156.276 | 0 | PASS |
| cluster 32 mesh | Dry | 1228.122 | 156.276 | 64 | PASS |
| cluster 32 mesh | D0 | 405.444 | 156.276 | 0 | PASS |
| cluster 32 mesh | D1 | 411.722 | 156.276 | 0 | PASS |
| cluster 32 compound | Find | 430.820 | 52.822 | 0 | PASS |
| cluster 32 compound | B0 | 430.623 | 130.404 | 0 | PASS |
| cluster 32 compound | B1 | 425.889 | 130.404 | 0 | PASS |
| cluster 32 compound | Dry | 1318.641 | 130.404 | 64 | PASS |
| cluster 32 compound | D0 | 447.732 | 130.404 | 0 | PASS |
| cluster 32 compound | D1 | 440.464 | 130.404 | 0 | PASS |
| cluster 32 split | Find | 413.055 | 43.602 | 0 | PASS |
| cluster 32 split | B0 | 415.638 | 109.660 | 0 | PASS |
| cluster 32 split | B1 | 414.063 | 109.660 | 0 | PASS |
| cluster 32 split | Dry | 1233.743 | 109.660 | 64 | PASS |
| cluster 32 split | D0 | 415.584 | 109.660 | 0 | PASS |
| cluster 32 split | D1 | 407.458 | 109.660 | 0 | PASS |
| cluster 128 mesh | Find | 2404.490 | 208.276 | 0 | PASS |
| cluster 128 mesh | B0 | 2377.244 | 592.891 | 0 | PASS |
| cluster 128 mesh | B1 | 2394.791 | 592.891 | 0 | PASS |
| cluster 128 mesh | Dry | 7174.700 | 592.891 | 64 | PASS |
| cluster 128 mesh | D0 | 2372.240 | 592.891 | 0 | PASS |
| cluster 128 mesh | D1 | 2391.141 | 592.891 | 0 | PASS |
| cluster 128 compound | Find | 2601.420 | 210.143 | 0 | PASS |
| cluster 128 compound | B0 | 2615.615 | 511.857 | 0 | PASS |
| cluster 128 compound | B1 | 2588.500 | 511.857 | 0 | PASS |
| cluster 128 compound | Dry | 7931.382 | 511.857 | 64 | PASS |
| cluster 128 compound | D0 | 2683.649 | 511.857 | 0 | PASS |
| cluster 128 compound | D1 | 2662.063 | 511.857 | 0 | PASS |
| cluster 128 split | Find | 2543.363 | 174.264 | 0 | PASS |
| cluster 128 split | B0 | 2548.955 | 435.118 | 0 | PASS |
| cluster 128 split | B1 | 2524.551 | 435.118 | 0 | PASS |
| cluster 128 split | Dry | 7593.880 | 435.118 | 64 | PASS |
| cluster 128 split | D0 | 2559.933 | 435.118 | 0 | PASS |
| cluster 128 split | D1 | 2516.902 | 435.118 | 0 | PASS |
| cluster 256 mesh | Find | 6805.707 | 418.406 | 0 | PASS |
| cluster 256 mesh | B0 | 6753.238 | 1174.653 | 0 | PASS |
| cluster 256 mesh | B1 | 6768.650 | 1174.653 | 0 | PASS |
| cluster 256 mesh | Dry | 20378.833 | 1174.653 | 64 | PASS |
| cluster 256 mesh | D0 | 6727.238 | 1174.653 | 0 | PASS |
| cluster 256 mesh | D1 | 6776.886 | 1174.653 | 0 | PASS |
| cluster 256 compound | Find | 7825.031 | 452.905 | 0 | PASS |
| cluster 256 compound | B0 | 7655.235 | 1027.972 | 0 | PASS |
| cluster 256 compound | B1 | 7601.482 | 1027.972 | 0 | PASS |
| cluster 256 compound | Dry | 22831.631 | 1027.972 | 64 | PASS |
| cluster 256 compound | D0 | 7640.423 | 1027.972 | 0 | PASS |
| cluster 256 compound | D1 | 7591.595 | 1027.972 | 0 | PASS |
| cluster 256 split | Find | 7472.934 | 349.911 | 0 | PASS |
| cluster 256 split | B0 | 7493.967 | 870.325 | 0 | PASS |
| cluster 256 split | B1 | 7443.832 | 870.325 | 0 | PASS |
| cluster 256 split | Dry | 22335.342 | 870.325 | 64 | PASS |
| cluster 256 split | D0 | 7513.006 | 870.325 | 0 | PASS |
| cluster 256 split | D1 | 7471.186 | 870.325 | 0 | PASS |

## Borderline rows

These median rows are within 10 percent of their binding time limit. The two rejected rows remain failures.
The flat-mesh dry tick passes its median limit by 5.655 us and fails in 3 of 20 iterations. SG-24 requires
a second approved proof before borderline acceptance. No such additional proof is part of this record.

| Scene | Operation | Median C | Limit | Margin | C / limit | Verdict | Pass/20 |
| --- | --- | ---: | ---: | ---: | ---: | --- | ---: |
| flat mesh | Dry | 108.496 | 114.151 | 5.655 | 0.950 | PASS | 17 |
| hidden crate | B1 | 210.778 | 200.000 | -10.778 | 1.054 | FAIL T | 0 |
| hidden crate | D1 | 206.096 | 200.000 | -6.096 | 1.030 | FAIL T | 0 |

## Per-row comparison with 5c373abe9

The original [5c373abe9 record](2026-10-17-support-cost-hosted-1334-option-c.md) is unchanged at 109 pass and
30 reject. Re-evaluating its original 20 artifacts under SC-18 gives 125 pass and 14 reject. Only the 18
previously E/L row codes change. Re-evaluating the corrected 376971dda artifacts likewise changes only those
18 row codes, from 94 pass and 45 reject to 108 pass and 31 reject. Both data-only evaluator invocations passed.
The committed regression tests independently check the corrected historical tables against their recorded
geometry and verify that every non-E/L row retains its verdict code.

This run gains 24 passes against the original 5c373abe9 verdict, or 8 against its SC-18 re-evaluation,
with no pass-to-reject change. Hidden-crate B1 and D1 still fail time after their geometry failures are removed.
The table shows every ordinary row, with the original verdict and the SC-18 re-evaluation kept distinct.
Both runs have the same five SC-3 envelope rows and 90 dense rows passing allocation.

| Scene | Operation | 5c373abe9 C | Phase 2 C | Delta C | Original 5c373abe9 | 5c373abe9 with SC-18 | Phase 2 verdict |
| --- | --- | ---: | ---: | ---: | --- | --- | --- |
| flat box | Find | 22.989 | 14.467 | -8.522 | PASS | PASS | PASS |
| flat box | B0 | 14.484 | 14.612 | 0.128 | PASS | PASS | PASS |
| flat box | B1 | 14.298 | 14.417 | 0.119 | PASS | PASS | PASS |
| flat box | Dry | 45.808 | 45.269 | -0.539 | PASS | PASS | PASS |
| flat box | D0 | 14.670 | 14.683 | 0.013 | PASS | PASS | PASS |
| flat box | D1 | 14.488 | 14.459 | -0.029 | PASS | PASS | PASS |
| stairs box | Find | 101.866 | 60.042 | -41.824 | FAIL T | FAIL T | FAIL T |
| stairs box | B0 | 72.018 | 41.173 | -30.845 | PASS | PASS | PASS |
| stairs box | B1 | 74.827 | 37.694 | -37.133 | PASS | PASS | PASS |
| stairs box | D0 | 72.264 | 41.430 | -30.834 | PASS | PASS | PASS |
| stairs box | D1 | 75.113 | 38.092 | -37.021 | PASS | PASS | PASS |
| flat hull | Find | 38.596 | 18.045 | -20.551 | PASS | PASS | PASS |
| flat hull | B0 | 39.897 | 18.134 | -21.763 | PASS | PASS | PASS |
| flat hull | B1 | 39.869 | 18.090 | -21.779 | PASS | PASS | PASS |
| flat hull | Dry | 122.536 | 55.054 | -67.482 | FAIL T | FAIL T | PASS |
| flat hull | D0 | 40.577 | 18.287 | -22.290 | PASS | PASS | PASS |
| flat hull | D1 | 40.231 | 18.127 | -22.104 | PASS | PASS | PASS |
| stairs hull | Find | 103.108 | 60.798 | -42.310 | FAIL T | FAIL T | FAIL T |
| stairs hull | B0 | 75.345 | 45.776 | -29.569 | PASS | PASS | PASS |
| stairs hull | B1 | 78.791 | 41.832 | -36.959 | PASS | PASS | PASS |
| stairs hull | D0 | 75.570 | 45.787 | -29.783 | PASS | PASS | PASS |
| stairs hull | D1 | 78.143 | 41.723 | -36.420 | PASS | PASS | PASS |
| flat mesh | Find | 41.784 | 33.988 | -7.796 | PASS | PASS | PASS |
| flat mesh | B0 | 53.022 | 39.514 | -13.508 | PASS | PASS | PASS |
| flat mesh | B1 | 52.380 | 38.230 | -14.150 | PASS | PASS | PASS |
| flat mesh | Dry | 150.549 | 108.496 | -42.053 | FAIL T | FAIL T | PASS |
| flat mesh | D0 | 53.449 | 38.775 | -14.674 | PASS | PASS | PASS |
| flat mesh | D1 | 52.432 | 37.859 | -14.573 | PASS | PASS | PASS |
| stairs mesh | Find | 102.669 | 65.754 | -36.915 | FAIL T | FAIL T | FAIL T |
| stairs mesh | B0 | 79.080 | 52.312 | -26.768 | PASS | PASS | PASS |
| stairs mesh | B1 | 80.856 | 58.674 | -22.182 | PASS | PASS | PASS |
| stairs mesh | D0 | 79.735 | 52.487 | -27.248 | PASS | PASS | PASS |
| stairs mesh | D1 | 81.826 | 58.893 | -22.933 | PASS | PASS | PASS |
| analytic only | Find | 4.497 | 3.106 | -1.391 | PASS | PASS | PASS |
| analytic only | B0 | 4.709 | 3.335 | -1.374 | PASS | PASS | PASS |
| analytic only | B1 | 4.741 | 3.323 | -1.418 | PASS | PASS | PASS |
| analytic only | Dry | 15.068 | 11.192 | -3.876 | PASS | PASS | PASS |
| analytic only | D0 | 4.739 | 3.388 | -1.351 | PASS | PASS | PASS |
| analytic only | D1 | 4.721 | 3.387 | -1.334 | PASS | PASS | PASS |
| analytic plus props | Find | 14.717 | 14.520 | -0.197 | PASS | PASS | PASS |
| analytic plus props | B0 | 14.633 | 14.437 | -0.196 | PASS | PASS | PASS |
| analytic plus props | B1 | 14.576 | 14.387 | -0.189 | PASS | PASS | PASS |
| analytic plus props | Dry | 45.322 | 44.540 | -0.782 | PASS | PASS | PASS |
| analytic plus props | D0 | 14.599 | 14.474 | -0.125 | PASS | PASS | PASS |
| analytic plus props | D1 | 14.575 | 14.379 | -0.196 | PASS | PASS | PASS |
| hidden crate | Find | 142.709 | 85.003 | -57.706 | FAIL T | FAIL T | FAIL T |
| hidden crate | B0 | 166.728 | 114.410 | -52.318 | FAIL G | FAIL G | PASS |
| hidden crate | B1 | 244.784 | 210.778 | -34.006 | FAIL T,G | FAIL T,G | FAIL T |
| hidden crate | D0 | 163.743 | 113.362 | -50.381 | FAIL G | FAIL G | PASS |
| hidden crate | D1 | 250.324 | 206.096 | -44.228 | FAIL T,G | FAIL T,G | FAIL T |
| sphere | Find | 13.242 | 12.809 | -0.433 | PASS | PASS | PASS |
| sphere | B0 | 13.792 | 13.224 | -0.568 | PASS | PASS | PASS |
| sphere | B1 | 14.465 | 14.085 | -0.380 | PASS | PASS | PASS |
| sphere | D0 | 13.938 | 13.516 | -0.422 | PASS | PASS | PASS |
| sphere | D1 | 14.410 | 14.433 | 0.023 | PASS | PASS | PASS |
| capsule | Find | 15.004 | 14.830 | -0.174 | PASS | PASS | PASS |
| capsule | B0 | 16.049 | 16.196 | 0.147 | PASS | PASS | PASS |
| capsule | B1 | 16.532 | 16.733 | 0.201 | PASS | PASS | PASS |
| capsule | D0 | 16.166 | 16.379 | 0.213 | PASS | PASS | PASS |
| capsule | D1 | 16.698 | 17.122 | 0.424 | PASS | PASS | PASS |
| cylinder | Find | 17.794 | 16.892 | -0.902 | PASS | PASS | PASS |
| cylinder | B0 | 19.311 | 18.474 | -0.837 | PASS | PASS | PASS |
| cylinder | B1 | 19.436 | 18.535 | -0.901 | PASS | PASS | PASS |
| cylinder | Dry | 58.369 | 55.590 | -2.779 | PASS | PASS | PASS |
| cylinder | D0 | 19.477 | 18.671 | -0.806 | PASS | PASS | PASS |
| cylinder | D1 | 19.441 | 18.721 | -0.720 | PASS | PASS | PASS |
| stacked irrelevant layers | Find | 15.038 | 14.632 | -0.406 | PASS | PASS | PASS |
| stacked irrelevant layers | B0 | 14.624 | 14.407 | -0.217 | PASS | PASS | PASS |
| stacked irrelevant layers | B1 | 14.531 | 14.326 | -0.205 | PASS | PASS | PASS |
| stacked irrelevant layers | Dry | 45.277 | 44.426 | -0.851 | PASS | PASS | PASS |
| stacked irrelevant layers | D0 | 14.663 | 14.481 | -0.182 | PASS | PASS | PASS |
| stacked irrelevant layers | D1 | 14.546 | 14.323 | -0.223 | PASS | PASS | PASS |
| stationary | Find | 14.478 | 14.191 | -0.287 | PASS | PASS | PASS |
| initial | Find | 14.288 | 14.137 | -0.151 | PASS | PASS | PASS |
| initial | B0 | 14.438 | 14.438 | 0.000 | PASS | PASS | PASS |
| initial | B1 | 14.330 | 14.404 | 0.074 | PASS | PASS | PASS |
| initial | Dry | 45.104 | 44.235 | -0.869 | PASS | PASS | PASS |
| initial | D0 | 14.513 | 14.412 | -0.101 | PASS | PASS | PASS |
| initial | D1 | 14.467 | 14.282 | -0.185 | PASS | PASS | PASS |
| wall retry | Find | 4.459 | 3.112 | -1.347 | PASS | PASS | PASS |
| wall retry | B0 | 9.274 | 6.538 | -2.736 | PASS | PASS | PASS |
| wall retry | B1 | 9.313 | 6.511 | -2.802 | PASS | PASS | PASS |
| wall retry | Dry | 24.380 | 17.761 | -6.619 | PASS | PASS | PASS |
| wall retry | D0 | 9.333 | 6.683 | -2.650 | PASS | PASS | PASS |
| wall retry | D1 | 9.318 | 6.608 | -2.710 | PASS | PASS | PASS |
| pushed seat | Find | 14.733 | 14.418 | -0.315 | PASS | PASS | PASS |
| pushed seat | B0 | 91.904 | 82.039 | -9.865 | PASS | PASS | PASS |
| pushed seat | B1 | 45.385 | 37.818 | -7.567 | PASS | PASS | PASS |
| pushed seat | D0 | 93.989 | 43.917 | -50.072 | FAIL G | FAIL G | PASS |
| pushed seat | D1 | 45.411 | 38.768 | -6.643 | FAIL G | FAIL G | PASS |
| descending landing | Find | 14.848 | 14.738 | -0.110 | PASS | PASS | PASS |
| descending landing | B0 | 14.729 | 14.581 | -0.148 | PASS | PASS | PASS |
| descending landing | B1 | 14.373 | 14.346 | -0.027 | PASS | PASS | PASS |
| descending landing | D0 | 14.508 | 14.428 | -0.080 | PASS | PASS | PASS |
| slide opposing face | Find | 4.453 | 3.080 | -1.373 | PASS | PASS | PASS |
| explicit entry | Find | 4.541 | 3.144 | -1.397 | PASS | PASS | PASS |
| explicit entry | B0 | 4.688 | 3.354 | -1.334 | PASS | PASS | PASS |
| explicit entry | B1 | 4.721 | 3.374 | -1.347 | PASS | PASS | PASS |
| explicit entry | E0 | 13.403 | 12.706 | -0.697 | PASS | PASS | PASS |
| explicit entry | E1 | 13.058 | 12.426 | -0.632 | PASS | PASS | PASS |
| explicit rounding handoff | E0 | 13.689 | 12.794 | -0.895 | PASS | PASS | PASS |
| explicit wade | Find | 4.416 | 3.047 | -1.369 | PASS | PASS | PASS |
| explicit wade | B0 | 4.708 | 3.364 | -1.344 | PASS | PASS | PASS |
| explicit wade | B1 | 4.725 | 3.383 | -1.342 | PASS | PASS | PASS |
| explicit wade | E0 | 13.515 | 12.989 | -0.526 | PASS | PASS | PASS |
| explicit medium descent retry | Find | 4.410 | 3.042 | -1.368 | PASS | PASS | PASS |
| explicit medium descent retry | B0 | 4.716 | 3.338 | -1.378 | PASS | PASS | PASS |
| explicit medium descent retry | B1 | 4.714 | 3.329 | -1.385 | PASS | PASS | PASS |
| explicit medium descent retry | E0 | 13.396 | 12.629 | -0.767 | PASS | PASS | PASS |
| explicit medium descent retry | E1 | 39.655 | 34.982 | -4.673 | PASS | PASS | PASS |
| explicit dry swim handoff | Find | 4.426 | 3.051 | -1.375 | PASS | PASS | PASS |
| explicit dry swim handoff | B0 | 4.709 | 3.328 | -1.381 | PASS | PASS | PASS |
| explicit dry swim handoff | B1 | 4.717 | 3.302 | -1.415 | PASS | PASS | PASS |
| explicit dry swim handoff | E0 | 13.709 | 12.931 | -0.778 | PASS | PASS | PASS |
| explicit dry wade handoff | Find | 4.407 | 3.033 | -1.374 | PASS | PASS | PASS |
| explicit dry wade handoff | B0 | 4.689 | 3.325 | -1.364 | PASS | PASS | PASS |
| explicit dry wade handoff | B1 | 4.722 | 3.321 | -1.401 | PASS | PASS | PASS |
| explicit dry wade handoff | E0 | 13.529 | 12.914 | -0.615 | PASS | PASS | PASS |
| explicit dry wade handoff | E1 | 13.415 | 12.820 | -0.595 | PASS | PASS | PASS |
| final clamp | Find | 14.565 | 14.322 | -0.243 | PASS | PASS | PASS |
| final clamp | D0 | 14.563 | 14.375 | -0.188 | PASS | PASS | PASS |
| stairs box | Dry | 463.207 | 138.418 | -324.789 | FAIL T,L | PASS | PASS |
| stairs hull | Dry | 487.902 | 149.304 | -338.598 | FAIL T,L | PASS | PASS |
| stairs mesh | Dry | 514.786 | 179.567 | -335.219 | FAIL T,L | PASS | PASS |
| hidden crate | Dry | 672.986 | 517.722 | -155.264 | FAIL T,G,L | FAIL G | PASS |
| sphere | Dry | 42.671 | 41.170 | -1.501 | FAIL L | PASS | PASS |
| capsule | Dry | 49.381 | 49.884 | 0.503 | FAIL L | PASS | PASS |
| stationary | Dry | 15.669 | 15.010 | -0.659 | FAIL E | PASS | PASS |
| pushed seat | Dry | 291.940 | 211.430 | -80.510 | FAIL T,G,L | FAIL G | PASS |
| descending landing | Dry | 66.026 | 64.426 | -1.600 | FAIL E | PASS | PASS |
| explicit entry | Explicit | 52.962 | 45.598 | -7.364 | FAIL E,L | PASS | PASS |
| explicit rounding handoff | Explicit | 72.092 | 64.178 | -7.914 | FAIL E | PASS | PASS |
| explicit changed-axis rounding | Explicit | 74.290 | 67.958 | -6.332 | FAIL E,L | PASS | PASS |
| explicit wade | Explicit | 37.957 | 32.313 | -5.644 | FAIL E,L | PASS | PASS |
| explicit swim | Explicit | 32.397 | 27.870 | -4.527 | FAIL E | PASS | PASS |
| explicit medium descent retry | Explicit | 76.824 | 64.978 | -11.846 | FAIL E,L | PASS | PASS |
| explicit dry swim handoff | Explicit | 45.124 | 40.032 | -5.092 | FAIL E,L | PASS | PASS |
| explicit dry wade handoff | Explicit | 51.785 | 45.088 | -6.697 | FAIL E,L | PASS | PASS |
| final clamp | Dry | 30.903 | 30.182 | -0.721 | FAIL E | PASS | PASS |

## Disposition

**The #1334 ordinary gate FAILS.** These six ordinary median rows exceed their time limit, and all six fail
in every iteration. `Margin = Limit - C` is negative for each miss.

| Scene | Full operation | Median C | Limit | Margin | Over limit |
| --- | --- | ---: | ---: | ---: | ---: |
| stairs box | `Find` | 60.042 | 50.000 | -10.042 | 20.084% |
| stairs hull | `Find` | 60.798 | 50.000 | -10.798 | 21.596% |
| stairs mesh | `Find` | 65.754 | 50.000 | -15.754 | 31.508% |
| hidden crate | `Find` | 85.003 | 50.000 | -35.003 | 70.006% |
| hidden crate | `GroundSubstep/GroundTickBorrowed/1` | 210.778 | 200.000 | -10.778 | 5.389% |
| hidden crate | `GroundSubstep/DryTick/1` | 206.096 | 200.000 | -6.096 | 3.048% |

SC-18 resolves the incomparable-row precondition failures without changing any time limit. It does not resolve
these six time failures. #1334 remains open. The flat-mesh dry tick also retains its SG-24 borderline acceptance
requirement. Dense time, SG-21 aggregate actor CPU and consumer adoption remain separate.

## Verification

- The reference Release build passed with zero warnings through `build-slot --heavy`. The exact-commit
  replacement reference passed one test, exit 0, in 56 s test duration and 58.671 s build-slot elapsed.
  `scripts/record-support-cost-exit.py` finalized its actual zero exit. The first reference is retained with
  its real `071f8440e` SHA, and the owner explicitly approved the replacement before it ran.
- SC-18 RED proof failed 21 of 31 new cases against the old verdict, as expected. A historical expected-total
  typo was traced to the 718.666 us old stairs-mesh dry tick and corrected from source data.
- The focused `SupportCostArtifactTests`, `SupportCostSamplerTests`, `SupportGateCostTests` and
  `SupportCostUncomparableVerdictTests` passed all 221 tests. The explicit certification factor audit then
  demonstrated two failing boundary assertions before correcting the derived sweep and penetration caps.
- The final Release `KhaozEngine.Game.Tests` suite passed 5269 tests, zero failures, one existing skip,
  through `build-slot --heavy`, 90.211 s elapsed. The existing skipped test is
  `WallContactOwnColumnTests.The_perturbation_control_for_every_sensitivity_claim_in_this_chain`.
- All three data-only hosted evaluator invocations passed one test each through `build-slot`. They did not
  record another local timing artifact. Run 3 evaluation took 3.041 s build-slot elapsed.
- Whole-tree dash, prose, file-size, agent-instruction and doc-version checks passed. Staged dash, prose,
  file-size and diff checks passed before each commit.
