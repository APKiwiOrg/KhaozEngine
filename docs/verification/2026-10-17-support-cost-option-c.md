# SG-12 option C support cost record (#1334)

## Scope and method

Local evidence for the option C rulings SC-4 to SC-8 of
[CONTACT-CONTROLLER-SUPPORT-COST-2026-10-17.md](../design/CONTACT-CONTROLLER-SUPPORT-COST-2026-10-17.md).
It is not the hosted SG-12 verdict. SC-9 makes a second approved hosted run the repeat-time acceptance.

- After: `47ded3e11`, the branch head with every task of the plan, staged 21.1.1. Apple M2 Max, Arm64,
  macOS 27.0.1, .NET 10.0.13, Release, `DOTNET_TieredCompilation=0`.
- Before: the research artifact at `e0dbb6da4`, the same harness and machine, recorded for the
  research report behind the spec. Its Find figures are the ones the spec's cause table quotes.
- Every time below is one warm sample, before and after (SG-10 one-shot). Earlier research runs moved
  single samples by up to about 2x, and this run has larger outliers (see below). Where a total disagrees
  with its counters or with sibling rows doing identical work, the counters and phase shares are the
  better reading. The verdicts still follow the totals as recorded. No row is excused here.
- Verdicts use the shared `SupportCostVerdict.Check` at `k = 1`: Find 50 us, substep 200 us, whole tick
  the lesser of 1000 us and legacy plus max(100 us, a quarter of legacy). Failure codes are those of the
  #1334 record: `T` time, `G` geometry, oracle or refusal, `E` outside the comparable moving single-mode
  envelope, `L` unsuccessful ordinary legacy, `A` allocation. Envelope rows follow SC-3.
- Raw JSON and logs stay in the task's ignored SDD workspace, the before artifacts under `before-e0dbb6da4/`.
  The before cost artifact was never finalized and still carries `pendingFinalization`. Only this record is
  committed.

Commands, one at a time:

```sh
DOTNET_TieredCompilation=0 KHAOZ_SUPPORT_COST_ARTIFACT=<task-workspace>/cost-47ded3e11.json \
  build-slot --heavy --label sg2-baseline -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj \
  -c Release --no-build --filter "FullyQualifiedName~SupportGateCostTests.OneShotCostArtifact"
python3 scripts/record-support-cost-exit.py <task-workspace>/cost-47ded3e11.json 0
KHAOZ_SUPPORT_ATTRIBUTION=<task-workspace>/attribution-task6-47ded3e11.json DOTNET_TieredCompilation=0 \
  build-slot --heavy --label sg12-t6-attr -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj \
  -c Release --no-build --filter "FullyQualifiedName~SupportCostAttributionTests.OneShotPhaseAttribution"
```

The cost invocation passed in 2 m 51 s and was finalized with exit 0. The attribution test covers flat
box, flat mesh, the curved rows and dense rows only. The stairs, hull, crate and props rows were attributed
once more through the same `SupportCostSampler` and `PhaseRecorder`, by reflection from a script outside the
repository, as the research report's fourth measurement was. Phase figures are self time in us.

## Verdict

**Not met.** Locally at `k = 1`, ordinary rows pass 95, reject 44 and 5 are envelope rows. The same rules
over the before artifact give 80 pass and 59 reject.

| Family | Before pass | Before reject | After pass | After reject | Envelope |
| --- | ---: | ---: | ---: | ---: | ---: |
| Find | 12 | 13 | 18 | 7 | 3 |
| Substep | 66 | 21 | 71 | 16 | 1 |
| Whole tick | 2 | 25 | 6 | 21 | 1 |

Warm allocation passes on every row, dense included: 0 bytes for every Find and substep, 64 bytes for every
dry tick against 208, 312 bytes for every explicit tick against the measured 312 byte lifetime inventory.
No warm row builds a world form: `WorldFormBuilds` is 0 on all 417 warm samples. The 71 cold samples that
publish forms build 1 to 256 of them.

Hosted context. The corrected #1334 evaluation (SC-2, appended to
[2026-10-14-support-cost-hosted-1334.md](2026-10-14-support-cost-hosted-1334.md))
judged the pre-plan code at `376971dda`: ordinary 94 pass, 45 reject, 5 envelope, with `k` from 1.649 to
1.908 over 56 legacy rows per run. Those are medians of 20 hosted runs in M2 Max equivalent us, these are
single local samples. How the new code fares hosted is unknown until the second approved run.

## Per scene

Find, the slowest substep and the whole tick, after, against their limits. One sample each.

| Scene | Find us | Find | Worst substep us | Substeps | Tick us / limit | Tick |
| --- | ---: | --- | ---: | --- | ---: | --- |
| flat box | 2729.0 | FAIL T (outlier, see below) | 33.3 | PASS | 104.8 / 106.7 | PASS |
| stairs box | 125.2 | FAIL T | 392.7 | FAIL T (D0, D1) | 1592.0 / 138.5 | FAIL T,L |
| flat hull | 46.6 | PASS | 2981.1 | FAIL T (D0, D1) | 3468.8 / 122.7 | FAIL T |
| stairs hull | 652.3 | FAIL T | 1067.5 | FAIL T (B0, D0, D1) | 5028.0 / 117.5 | FAIL T,L |
| flat mesh | 111.7 | FAIL T | 140.7 | PASS | 162.9 / 134.7 | FAIL T |
| stairs mesh | 115.0 | FAIL T | 326.9 | FAIL T (B0, B1, D1) | 1430.5 / 179.5 | FAIL T,L |
| analytic only | 6.5 | PASS | 8.1 | PASS | 22.9 / 102.4 | PASS |
| analytic plus props | 88.5 | FAIL T (outlier, see below) | 104.7 | PASS | 45.0 / 104.4 | PASS |
| hidden crate | 179.7 | FAIL T | 730.3 | FAIL G on all, T on B1, D0, D1 | 2176.9 / 134.2 | FAIL T,G,L |
| sphere | 21.9 | PASS | 12.9 | PASS | 35.9 / 104.2 | FAIL L |
| capsule | 14.5 | PASS | 49.5 | PASS | 40.9 / 104.6 | FAIL L |
| cylinder | 33.8 | PASS | 48.8 | PASS | 267.6 / 115.7 | FAIL T |
| stacked irrelevant layers | 16.8 | PASS | 14.1 | PASS | 43.8 / 123.7 | PASS |
| stationary | 14.4 | PASS | none | none | 16.8 / 104.0 | FAIL E |
| initial | 14.0 | PASS | 14.2 | PASS | 42.7 / 106.2 | PASS |
| wall retry | 2.6 | PASS | 5.9 | PASS | 15.4 / 100.8 | PASS |
| pushed seat | 14.2 | PASS | 102.5 | FAIL G (D0, D1) | 312.4 / 124.1 | FAIL T,G,L |
| descending landing | 14.5 | PASS | 14.4 | PASS | 66.6 / 105.9 | FAIL E |
| slide opposing face | 2.7 | PASS | 3.1 | envelope | 12.5 / 100.8 | envelope |
| explicit entry | 2.6 | PASS | 13.9 | PASS | 56.1 / 100.9 | FAIL E,L |
| explicit rounding handoff | 2.7 | envelope | 13.6 | PASS | 65.9 / 103.1 | FAIL E |
| explicit changed-axis rounding | 2.6 | envelope | none | none | 67.7 / 101.4 | FAIL E,L |
| explicit wade | 2.5 | PASS | 13.5 | PASS | 36.7 / 100.9 | FAIL E,L |
| explicit swim | 2.5 | envelope | none | none | 28.4 / 101.2 | FAIL E |
| explicit medium descent retry | 4.0 | PASS | 40.8 | PASS | 77.9 / 101.9 | FAIL E,L |
| explicit dry swim handoff | 2.5 | PASS | 13.3 | PASS | 44.1 / 100.9 | FAIL E,L |
| explicit dry wade handoff | 2.7 | PASS | 13.4 | PASS | 49.1 / 100.9 | FAIL E,L |
| final clamp | 13.9 | PASS | 13.9 | PASS | 29.7 / 105.5 | FAIL E |

Every `E`, `L` and `G` code above was already present at `e0dbb6da4` and in the hosted #1334 table. None
is a time or allocation failure of the support query.

## Ordinary rows still over their limits

Every ordinary row this artifact rejects, with the dominant phase of its scene's attribution. A substep has
no attribution of its own, so it takes its tick's split. Shares are of the attributed phase sum.

| Scene | Rows | Codes | Dominant phase after | Evidence on the total |
| --- | --- | --- | --- | --- |
| flat box | Find 2729.0 | T | SupportExtrema 31 percent, Route 24 percent of 78.5 us | Outlier. Counters equal those of initial, stationary, pushed seat and final clamp Finds at 13.9 to 14.4 us. Each flat box substep runs this Find and takes 32 to 33 us. The attribution test measured 95.8 us. |
| analytic plus props | Find 88.5 | T | SupportExtrema 38 percent of 16.8 us | Outlier. Same counters as the 14 us flat rows, attributed at 16.8 us. |
| stairs box | Find 125.2 | T | SupportContacts 35, SupportExtrema 27, SupportJoins 20 percent of 117.2 us | Consistent with the attribution. A real miss. |
| stairs box | D0 390.0, D1 392.7 | T | Tick split: SupportContacts 37, SupportExtrema 28, SupportJoins 17 percent | B0 and B1 do identical support work at 84 to 89 us and pass. |
| stairs box | Dry 1592.0 | T, L | SupportContacts 37, SupportExtrema 28, SupportJoins 17 percent of 507.1 us | Six Finds. The attributed sum alone is 3.7 times the limit. A real miss. |
| stairs hull | Find 652.3 | T | SupportContacts 33, SupportExtrema 25, SupportJoins 22 percent of 126.5 us | Counters equal stairs box. Attributed at 126.5 us, still over 50. A real miss, with a noisy total. |
| stairs hull | B0 212.1, D0 1067.5, D1 477.9 | T | Tick split: SupportContacts 36, SupportExtrema 27, SupportJoins 16 percent | B1 does identical work at 115.8 us and passes. |
| stairs hull | Dry 5028.0 | T, L | SupportContacts 36, SupportExtrema 27, SupportJoins 16 percent of 544.1 us | A real miss. |
| flat hull | D0 2981.1, D1 383.9 | T | Tick split: SupportContacts 62 percent, 8 exact fallbacks per Find | B0 and B1 do identical work at 42 to 45 us and pass. |
| flat hull | Dry 3468.8 | T | SupportContacts 62 percent of 133.3 us | The attributed sum alone is over the 122.7 us limit. A real miss. |
| flat mesh | Find 111.7 | T | SupportCapture 29, SupportExtrema 22 percent of 48.2 us | The two attributions read 48.2 and 55.8 us, either side of the limit. Mesh capture is out of SC-4. |
| flat mesh | Dry 162.9 | T | SupportContacts 31, SupportCapture 21 percent of 158.5 us | Consistent with the attribution. A real miss. |
| stairs mesh | Find 115.0 | T | SupportCapture 31, SupportContacts 28 percent of 121.5 us | Consistent. A real miss. |
| stairs mesh | B0 326.9, B1 309.7, D1 220.2 | T | Tick split: SupportContacts 30, SupportCapture 28 percent | B0 and B1 passed before at 103 and 113 us with the same counters. D0 passes at 198.4 us. |
| stairs mesh | Dry 1430.5 | T, L | SupportContacts 30, SupportCapture 28, SupportExtrema 24 percent of 552.3 us | A real miss. |
| hidden crate | Find 179.7 | T | SupportJoins 38, SupportExtrema 28, SupportContacts 17 percent of 167.6 us | Consistent. A real miss. |
| hidden crate | B0 197.6 (G), B1 285.8, D0 730.3, D1 706.8, Dry 2176.9 | T, G, L | Tick split: SupportJoins 29, SupportExtrema 28, SupportContacts 25 percent of 878.9 us | `G` predates the plan. Time is a real miss. |
| cylinder | Dry 267.6 | T | SupportCurved 31, SupportCurvedContact 23 percent of 44.2 us | Probable outlier. Its substeps take 37 to 49 us and the attributed tick is 44.2 us. |
| pushed seat | Dry 312.4, D0 102.5 (G), D1 49.9 (G) | T, G, L | SupportExtrema 37, SupportContacts 33 percent of 305.8 us | Consistent. `G` predates the plan. |

Not time failures: the sphere and capsule ticks fail only `L` (their time now passes), and the `E` ticks
of stationary, descending landing, final clamp and the explicit rows are outside the comparable envelope.

Real time misses that remain on the counters and the phase evidence: the stairs box, stairs hull and stairs
mesh Finds, dry substeps and ticks, the hidden crate Find, substeps and tick, the flat mesh Find (near the
limit) and tick, the flat hull tick and the pushed seat tick. Under SC-9 each gets a fresh attribution and a further
ruling, never a raised limit.

## Phase shares

Find unless noted, attributed phase sum and the phases the plan targeted, before and after, in us.

| Row | Sum | Materialization | Contacts | Joins + meeting | Extrema | Curved + contact or Route |
| --- | --- | --- | --- | --- | --- | --- |
| flat box | 111.5 to 78.5 | 46.4 to 0.9 | 2.8 to 15.8 | 0.7 to 1.8 | 9.6 to 24.5 | Route 9.3 to 18.6 |
| analytic plus props | 113.7 to 16.8 | 69.4 to 0.3 | 3.8 to 2.0 | 0.6 to 0.2 | 13.7 to 6.3 | Route 14.7 to 3.5 |
| flat hull | 93.4 to 46.1 | 43.8 to 0.3 | 27.1 to 27.8 | 0.3 to 0.2 | 7.3 to 6.6 | Route 6.4 to 4.3 |
| stairs box | 423.0 to 117.2 | 104.3 to 1.3 | 177.5 to 41.2 | 64.3 to 23.9 | 40.7 to 31.5 | Route 11.1 to 5.4 |
| stairs hull | 347.0 to 126.5 | 118.2 to 1.5 | 140.5 to 41.2 | 34.1 to 27.4 | 32.5 to 32.1 | Route 4.8 to 5.9 |
| stairs mesh | 186.7 to 121.5 | none | 74.3 to 34.2 | 1.4 to 1.0 | 31.0 to 27.4 | capture 43.8 to 37.2 |
| flat mesh | 46.7 to 48.2 | none | 2.5 to 7.2 | 0.5 to 0.5 | 11.2 to 10.5 | capture 14.5 to 13.9 |
| hidden crate | 2515.2 to 167.6 | 241.4 to 1.3 | 1412.5 to 27.8 | 634.6 to 64.0 | 137.3 to 47.0 | Route 28.5 to 5.9 |
| sphere | 41.0 to 15.0 | 3.2 to 0.1 | none | 0.2 to 0.1 | none | Route 35.0 to 4.9, curved 7.1 |
| capsule | 69.9 to 12.6 | 3.2 to 0.1 | none | 0.2 to 0.2 | none | Route 63.8 to 2.7, curved 7.3 |
| cylinder | 75.8 to 15.4 | 13.3 to 0.0 | none | 0.1 to 0.2 | none | Route 58.2 to 3.5, curved 7.7 |
| flat box DryTick | 339.1 to 48.8 | 206.2 to 0.9 | 10.6 to 5.4 | 1.8 to 0.3 | 40.9 to 17.7 | Route 45.0 to 13.5 |
| stairs box DryTick | 1404.3 to 507.1 | 376.5 to 5.0 | 650.2 to 189.9 | 143.0 to 87.2 | 148.1 to 141.3 | Route 26.7 to 25.5 |

Before the brackets of SC-8, curved collection ran inside Route self time. The flat box row is the first
row of each process and reads higher than identical rows in this run and in the research run. Its Contacts
and Extrema rise is not seen on the analytic plus props row, which does the same work.

## Counters each task added

Warm Find counters after, with the reference evaluator's figure before where the counter replaced work.

| Task and ruling | Counter | Evidence |
| --- | --- | --- |
| Task 2, SC-4 | `WorldFormBuilds` | 0 on every warm row. Materialization falls from 39 to 376 us to 0.0 to 5.0 us per Find or tick. |
| Task 3, SC-5 and SC-6 | `PolygonOperands`, `PrefilterRejects` | Flat box derives operands for 1 polygon of 6. Stairs box 7 of 18 with 11 prefilter rejects, hidden crate 11 of 18 with 16. Crate interval fallbacks fall from 70 to 0 per Find. |
| Task 4, SC-7 | `TopologyJoins` | Stairs box and hull take 4 of 6 joins from topology (geometric joins 6 to 2), hidden crate 13 of 14 (14 to 1). Cross-leaf and cross-static joins stay geometric. |
| Task 5, SC-8 | `TangentSamples` | 6 per curved Find against the full kernel's 102. Curved contact falls from 28 to 53 us to 1.9 to 9.5 us across both attributions. |
| Task 6 | warm bytes | The first warm Find in a process no longer allocates 88 bytes for lazily created cache key comparers. |

## All ordinary rows

Warm us, one sample each. Before is `e0dbb6da4`, after is `47ded3e11`. `B`, `D` and `E` are the substeps of
the borrowed, dry and explicit ticks. `Dry` and `Explicit` are whole ticks.

| Scene | Operation | Before | After | Limit | After / limit | Verdict |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| flat box | Find | 56.0 | 2729.0 | 50.0 | 54.58 | FAIL T |
| flat box | B0 | 45.6 | 32.3 | 200.0 | 0.16 | PASS |
| flat box | B1 | 46.3 | 31.7 | 200.0 | 0.16 | PASS |
| flat box | Dry | 382.8 | 104.8 | 106.7 | 0.98 | PASS |
| flat box | D0 | 111.2 | 33.0 | 200.0 | 0.17 | PASS |
| flat box | D1 | 149.5 | 33.3 | 200.0 | 0.17 | PASS |
| stairs box | Find | 691.3 | 125.2 | 50.0 | 2.50 | FAIL T |
| stairs box | B0 | 204.9 | 84.1 | 200.0 | 0.42 | PASS |
| stairs box | B1 | 195.8 | 88.8 | 200.0 | 0.44 | PASS |
| stairs box | Dry | 3696.4 | 1592.0 | 138.5 | 11.50 | FAIL T,L |
| stairs box | D0 | 487.0 | 390.0 | 200.0 | 1.95 | FAIL T |
| stairs box | D1 | 435.5 | 392.7 | 200.0 | 1.96 | FAIL T |
| flat hull | Find | 199.8 | 46.6 | 50.0 | 0.93 | PASS |
| flat hull | B0 | 227.0 | 44.7 | 200.0 | 0.22 | PASS |
| flat hull | B1 | 293.8 | 41.7 | 200.0 | 0.21 | PASS |
| flat hull | Dry | 255.8 | 3468.8 | 122.7 | 28.28 | FAIL T |
| flat hull | D0 | 84.0 | 2981.1 | 200.0 | 14.91 | FAIL T |
| flat hull | D1 | 82.4 | 383.9 | 200.0 | 1.92 | FAIL T |
| stairs hull | Find | 355.5 | 652.3 | 50.0 | 13.05 | FAIL T |
| stairs hull | B0 | 222.5 | 212.1 | 200.0 | 1.06 | FAIL T |
| stairs hull | B1 | 220.1 | 115.8 | 200.0 | 0.58 | PASS |
| stairs hull | Dry | 4457.7 | 5028.0 | 117.5 | 42.81 | FAIL T,L |
| stairs hull | D0 | 676.4 | 1067.5 | 200.0 | 5.34 | FAIL T |
| stairs hull | D1 | 715.2 | 477.9 | 200.0 | 2.39 | FAIL T |
| flat mesh | Find | 108.5 | 111.7 | 50.0 | 2.23 | FAIL T |
| flat mesh | B0 | 122.7 | 140.7 | 200.0 | 0.70 | PASS |
| flat mesh | B1 | 164.9 | 140.4 | 200.0 | 0.70 | PASS |
| flat mesh | Dry | 168.7 | 162.9 | 134.7 | 1.21 | FAIL T |
| flat mesh | D0 | 58.7 | 58.0 | 200.0 | 0.29 | PASS |
| flat mesh | D1 | 55.7 | 58.2 | 200.0 | 0.29 | PASS |
| stairs mesh | Find | 155.4 | 115.0 | 50.0 | 2.30 | FAIL T |
| stairs mesh | B0 | 103.2 | 326.9 | 200.0 | 1.63 | FAIL T |
| stairs mesh | B1 | 113.1 | 309.7 | 200.0 | 1.55 | FAIL T |
| stairs mesh | Dry | 6493.8 | 1430.5 | 179.5 | 7.97 | FAIL T,L |
| stairs mesh | D0 | 260.8 | 198.4 | 200.0 | 0.99 | PASS |
| stairs mesh | D1 | 573.2 | 220.2 | 200.0 | 1.10 | FAIL T |
| analytic only | Find | 2.8 | 6.5 | 50.0 | 0.13 | PASS |
| analytic only | B0 | 3.0 | 8.1 | 200.0 | 0.04 | PASS |
| analytic only | B1 | 2.9 | 6.7 | 200.0 | 0.03 | PASS |
| analytic only | Dry | 10.2 | 22.9 | 102.4 | 0.22 | PASS |
| analytic only | D0 | 3.2 | 6.8 | 200.0 | 0.03 | PASS |
| analytic only | D1 | 3.0 | 6.7 | 200.0 | 0.03 | PASS |
| analytic plus props | Find | 47.1 | 88.5 | 50.0 | 1.77 | FAIL T |
| analytic plus props | B0 | 159.7 | 104.7 | 200.0 | 0.52 | PASS |
| analytic plus props | B1 | 129.2 | 46.8 | 200.0 | 0.23 | PASS |
| analytic plus props | Dry | 335.1 | 45.0 | 104.4 | 0.43 | PASS |
| analytic plus props | D0 | 102.5 | 14.3 | 200.0 | 0.07 | PASS |
| analytic plus props | D1 | 99.7 | 14.0 | 200.0 | 0.07 | PASS |
| hidden crate | Find | 1148.3 | 179.7 | 50.0 | 3.59 | FAIL T |
| hidden crate | B0 | 2245.5 | 197.6 | 200.0 | 0.99 | FAIL G |
| hidden crate | B1 | 2356.5 | 285.8 | 200.0 | 1.43 | FAIL T,G |
| hidden crate | Dry | 9595.7 | 2176.9 | 134.2 | 16.22 | FAIL T,G,L |
| hidden crate | D0 | 5497.5 | 730.3 | 200.0 | 3.65 | FAIL T,G |
| hidden crate | D1 | 1782.8 | 706.8 | 200.0 | 3.53 | FAIL T,G |
| sphere | Find | 105.9 | 21.9 | 50.0 | 0.44 | PASS |
| sphere | B0 | 88.4 | 12.4 | 200.0 | 0.06 | PASS |
| sphere | B1 | 88.6 | 12.9 | 200.0 | 0.06 | PASS |
| sphere | Dry | 814.1 | 35.9 | 104.2 | 0.34 | FAIL L |
| sphere | D0 | 108.6 | 11.5 | 200.0 | 0.06 | PASS |
| sphere | D1 | 91.7 | 12.5 | 200.0 | 0.06 | PASS |
| capsule | Find | 141.8 | 14.5 | 50.0 | 0.29 | PASS |
| capsule | B0 | 168.2 | 13.2 | 200.0 | 0.07 | PASS |
| capsule | B1 | 248.8 | 49.5 | 200.0 | 0.25 | PASS |
| capsule | Dry | 436.2 | 40.9 | 104.6 | 0.39 | FAIL L |
| capsule | D0 | 144.5 | 13.2 | 200.0 | 0.07 | PASS |
| capsule | D1 | 145.9 | 14.2 | 200.0 | 0.07 | PASS |
| cylinder | Find | 99.3 | 33.8 | 50.0 | 0.68 | PASS |
| cylinder | B0 | 78.3 | 35.3 | 200.0 | 0.18 | PASS |
| cylinder | B1 | 77.5 | 34.2 | 200.0 | 0.17 | PASS |
| cylinder | Dry | 679.2 | 267.6 | 115.7 | 2.31 | FAIL T |
| cylinder | D0 | 198.2 | 48.8 | 200.0 | 0.24 | PASS |
| cylinder | D1 | 262.6 | 37.1 | 200.0 | 0.19 | PASS |
| stacked irrelevant layers | Find | 109.3 | 16.8 | 50.0 | 0.34 | PASS |
| stacked irrelevant layers | B0 | 104.9 | 14.0 | 200.0 | 0.07 | PASS |
| stacked irrelevant layers | B1 | 158.2 | 14.1 | 200.0 | 0.07 | PASS |
| stacked irrelevant layers | Dry | 127.6 | 43.8 | 123.7 | 0.35 | PASS |
| stacked irrelevant layers | D0 | 42.0 | 14.0 | 200.0 | 0.07 | PASS |
| stacked irrelevant layers | D1 | 42.0 | 14.0 | 200.0 | 0.07 | PASS |
| stationary | Find | 44.2 | 14.4 | 50.0 | 0.29 | PASS |
| stationary | Dry | 44.7 | 16.8 | 104.0 | 0.16 | FAIL E |
| initial | Find | 43.5 | 14.0 | 50.0 | 0.28 | PASS |
| initial | B0 | 43.2 | 13.9 | 200.0 | 0.07 | PASS |
| initial | B1 | 43.1 | 14.2 | 200.0 | 0.07 | PASS |
| initial | Dry | 125.7 | 42.7 | 106.2 | 0.40 | PASS |
| initial | D0 | 41.6 | 13.7 | 200.0 | 0.07 | PASS |
| initial | D1 | 41.6 | 13.6 | 200.0 | 0.07 | PASS |
| wall retry | Find | 2.7 | 2.6 | 50.0 | 0.05 | PASS |
| wall retry | B0 | 5.8 | 5.8 | 200.0 | 0.03 | PASS |
| wall retry | B1 | 30.3 | 5.7 | 200.0 | 0.03 | PASS |
| wall retry | Dry | 15.5 | 15.4 | 100.8 | 0.15 | PASS |
| wall retry | D0 | 5.9 | 5.8 | 200.0 | 0.03 | PASS |
| wall retry | D1 | 5.8 | 5.9 | 200.0 | 0.03 | PASS |
| pushed seat | Find | 97.4 | 14.2 | 50.0 | 0.28 | PASS |
| pushed seat | B0 | 284.6 | 99.1 | 200.0 | 0.50 | PASS |
| pushed seat | B1 | 333.5 | 50.0 | 200.0 | 0.25 | PASS |
| pushed seat | Dry | 968.8 | 312.4 | 124.1 | 2.52 | FAIL T,G,L |
| pushed seat | D0 | 275.9 | 102.5 | 200.0 | 0.51 | FAIL G |
| pushed seat | D1 | 137.5 | 49.9 | 200.0 | 0.25 | FAIL G |
| descending landing | Find | 43.4 | 14.5 | 50.0 | 0.29 | PASS |
| descending landing | B0 | 46.3 | 13.9 | 200.0 | 0.07 | PASS |
| descending landing | B1 | 43.9 | 14.2 | 200.0 | 0.07 | PASS |
| descending landing | Dry | 242.0 | 66.6 | 105.9 | 0.63 | FAIL E |
| descending landing | D0 | 43.2 | 14.4 | 200.0 | 0.07 | PASS |
| slide opposing face | Find | 3.0 | 2.7 | 50.0 | 0.05 | PASS |
| slide opposing face | Dry | 15.6 | 12.5 | 100.8 | 0.12 | ENVELOPE |
| slide opposing face | D0 | 3.4 | 3.1 | 200.0 | 0.02 | ENVELOPE |
| explicit entry | Find | 2.6 | 2.6 | 50.0 | 0.05 | PASS |
| explicit entry | B0 | 3.0 | 8.8 | 200.0 | 0.04 | PASS |
| explicit entry | B1 | 2.9 | 6.9 | 200.0 | 0.03 | PASS |
| explicit entry | Explicit | 52.6 | 56.1 | 100.9 | 0.56 | FAIL E,L |
| explicit entry | E0 | 13.4 | 13.9 | 200.0 | 0.07 | PASS |
| explicit entry | E1 | 13.0 | 13.5 | 200.0 | 0.07 | PASS |
| explicit rounding handoff | Find | 2.6 | 2.7 | 50.0 | 0.05 | ENVELOPE |
| explicit rounding handoff | Explicit | 64.6 | 65.9 | 103.1 | 0.64 | FAIL E |
| explicit rounding handoff | E0 | 13.1 | 13.6 | 200.0 | 0.07 | PASS |
| explicit changed-axis rounding | Find | 3.0 | 2.6 | 50.0 | 0.05 | ENVELOPE |
| explicit changed-axis rounding | Explicit | 139.4 | 67.7 | 101.4 | 0.67 | FAIL E,L |
| explicit wade | Find | 3.0 | 2.5 | 50.0 | 0.05 | PASS |
| explicit wade | B0 | 2.9 | 3.1 | 200.0 | 0.02 | PASS |
| explicit wade | B1 | 2.8 | 2.9 | 200.0 | 0.01 | PASS |
| explicit wade | Explicit | 35.3 | 36.7 | 100.9 | 0.36 | FAIL E,L |
| explicit wade | E0 | 12.9 | 13.5 | 200.0 | 0.07 | PASS |
| explicit swim | Find | 3.1 | 2.5 | 50.0 | 0.05 | ENVELOPE |
| explicit swim | Explicit | 79.5 | 28.4 | 101.2 | 0.28 | FAIL E |
| explicit medium descent retry | Find | 7.0 | 4.0 | 50.0 | 0.08 | PASS |
| explicit medium descent retry | B0 | 6.8 | 3.0 | 200.0 | 0.01 | PASS |
| explicit medium descent retry | B1 | 6.8 | 3.0 | 200.0 | 0.01 | PASS |
| explicit medium descent retry | Explicit | 220.9 | 77.9 | 101.9 | 0.76 | FAIL E,L |
| explicit medium descent retry | E0 | 37.8 | 13.5 | 200.0 | 0.07 | PASS |
| explicit medium descent retry | E1 | 107.7 | 40.8 | 200.0 | 0.20 | PASS |
| explicit dry swim handoff | Find | 10.9 | 2.5 | 50.0 | 0.05 | PASS |
| explicit dry swim handoff | B0 | 6.9 | 3.0 | 200.0 | 0.01 | PASS |
| explicit dry swim handoff | B1 | 6.8 | 3.0 | 200.0 | 0.01 | PASS |
| explicit dry swim handoff | Explicit | 135.5 | 44.1 | 100.9 | 0.44 | FAIL E,L |
| explicit dry swim handoff | E0 | 43.7 | 13.3 | 200.0 | 0.07 | PASS |
| explicit dry wade handoff | Find | 6.3 | 2.7 | 50.0 | 0.05 | PASS |
| explicit dry wade handoff | B0 | 6.8 | 3.0 | 200.0 | 0.02 | PASS |
| explicit dry wade handoff | B1 | 6.8 | 3.1 | 200.0 | 0.02 | PASS |
| explicit dry wade handoff | Explicit | 135.1 | 49.1 | 100.9 | 0.49 | FAIL E,L |
| explicit dry wade handoff | E0 | 36.2 | 13.4 | 200.0 | 0.07 | PASS |
| explicit dry wade handoff | E1 | 35.5 | 13.2 | 200.0 | 0.07 | PASS |
| final clamp | Find | 64.8 | 13.9 | 50.0 | 0.28 | PASS |
| final clamp | Dry | 92.8 | 29.7 | 105.5 | 0.28 | FAIL E |
| final clamp | D0 | 45.1 | 13.9 | 200.0 | 0.07 | PASS |
