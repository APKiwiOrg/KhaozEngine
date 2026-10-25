# SG-12 option C phase 2 support cost record (#1334)

## Scope and method

Local evidence for the phase 2 rulings SC-10 to SC-17 of
[CONTACT-CONTROLLER-SUPPORT-COST-2026-10-17.md](../design/CONTACT-CONTROLLER-SUPPORT-COST-2026-10-17.md).
It is not a hosted SG-12 verdict. Only an approved hosted run can judge repeat time.

- After: `15e3d0bfc`, the phase 2 branch head with every task, the parked review items and current main
  merged, staged 21.3.1. Apple M2 Max, Arm64, macOS 27.0.1, .NET 10.0.13, Release,
  `DOTNET_TieredCompilation=0`.
- Local before: `47ded3e11`, the phase 1 head, from the
  [phase 1 cost record](2026-10-17-support-cost-option-c.md), same harness and machine. The phase 2 task
  reports carry counters only (SG-20), so the phase 1 head is the last local timing before this work.
- Hosted: the medians of the second approved hosted run at `5c373abe9`, in M2 Max equivalent us, from
  [2026-10-17-support-cost-hosted-1334-option-c.md](2026-10-17-support-cost-hosted-1334-option-c.md).
  That code is phase 1 as merged, so the hosted column is the phase 1 baseline, not this branch.
- Every local time below is one warm sample (SG-10 one-shot). `build-slot` runs every command at utility
  QoS, which lets the scheduler place the thread on efficiency cores, so single local samples swing by 2x
  to 5x. Counters and phase shares are the better reading. The verdicts still follow the totals as
  recorded, and no row is excused here.
- Verdicts use the shared `SupportCostVerdict.Check` rules at `k = 1`: Find 50 us, substep 200 us, whole
  tick the lesser of 1000 us and legacy plus max(100 us, a quarter of legacy). Failure codes are those of
  the #1334 record: `T` time, `G` geometry, oracle or refusal, `E` outside the comparable moving
  single-mode envelope, `L` unsuccessful ordinary legacy, `A` allocation. Each recomputed verdict matched
  the artifact's own `GatePassed` on every gated ordinary row.
- Raw JSON and logs stay in the ignored task workspace. Only this record is committed.

Commands, one at a time:

```sh
DOTNET_TieredCompilation=0 KHAOZ_SUPPORT_COST_ARTIFACT=<task-workspace>/cost-15e3d0bfc.json \
  build-slot --heavy --label sg2-baseline -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj \
  -c Release --no-build --filter "FullyQualifiedName~SupportGateCostTests.OneShotCostArtifact"
python3 scripts/record-support-cost-exit.py <task-workspace>/cost-15e3d0bfc.json 0
KHAOZ_SUPPORT_ATTRIBUTION=<task-workspace>/attribution-15e3d0bfc.json DOTNET_TieredCompilation=0 \
  build-slot --heavy --label sg12-p2-attr -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj \
  -c Release --no-build --filter "FullyQualifiedName~SupportCostAttributionTests.OneShotPhaseAttribution"
```

The cost invocation passed in 56 s and was finalized with exit 0, 418 samples including the discarded
leading row of SC-16. It doubles as the full-matrix run of the rule 3 guard detector of SC-16: the detector
threw on no row. The attribution test passed. It covers flat box, flat mesh, the dense rows and the curved
rows only. Phase 1 attributed the stairs, hull, crate and seat rows through a reflection script around the
same sampler. That script is now refused by the allocation measurement guard, which requires a
non-parallel test collection, so those rows have no fresh attribution here. Their dominant phase below is
inferred from the phase 1 attribution and the counter changes, and says so.

## Verdict

**Not met.** Locally at `k = 1`, ordinary rows pass 117, reject 22 and 5 are envelope rows, against 95
and 44 at the phase 1 head. Nine ordinary rows miss time, all on the stairs, hidden crate and pushed seat
scenes. The other 13 rejects are `E` and `L` rows within their time limits.

| Family | Before pass | Before reject | After pass | After reject | Envelope | Hosted phase 1 pass, reject |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Find | 18 | 7 | 21 | 4 | 3 | 21, 4 |
| Substep | 71 | 16 | 87 | 0 | 1 | 81, 6 |
| Whole tick | 6 | 21 | 9 | 18 | 1 | 7, 20 |

Every substep row now passes, including the hidden crate and pushed seat rows that failed `G` before. No
row carries `G`. The SC-16 evidence corrections and the #1437 descent fix removed it, and no tolerance
or limit changed.

Warm allocation passes on every row, dense included: 0 bytes for every Find and substep, 64 bytes for
every dry tick, 312 bytes for every explicit tick. No warm row builds a world form (`WorldFormBuilds` 0 on
every warm sample), and no warm ordinary row captures a mesh triangle. The dense `grid 100` rows still
capture warm, since a query over 512 mesh candidates bypasses the bounded mesh form by design (SC-12).

## Per scene

Does the local one-shot warm Find, the slowest substep and the whole tick meet SG-12? One sample each.

| Scene | Find us | Find meets | Worst substep us | Substeps meet | Tick us / limit | Tick meets |
| --- | ---: | --- | ---: | --- | ---: | --- |
| flat box | 13.5 | yes | 15.3 | yes | 47.2 / 106.3 | yes |
| stairs box | 60.1 | no, T | 40.2 | yes | 133.6 / 111.3 | no, T,L |
| flat hull | 16.9 | yes | 17.4 | yes | 51.1 / 110.5 | yes |
| stairs hull | 60.2 | no, T | 44.8 | yes | 156.2 / 117.1 | no, T,L |
| flat mesh | 33.6 | yes | 39.7 | yes | 108.9 / 112.5 | yes |
| stairs mesh | 60.2 | no, T | 56.0 | yes | 168.0 / 119.5 | no, T,L |
| analytic only | 2.7 | yes | 3.0 | yes | 9.9 / 101.0 | yes |
| analytic plus props | 13.8 | yes | 14.0 | yes | 42.5 / 104.2 | yes |
| hidden crate | 75.9 | no, T | 188.8 | yes | 477.5 / 112.6 | no, T,L |
| sphere | 11.8 | yes | 12.4 | yes | 36.3 / 105.3 | no, L |
| capsule | 12.2 | yes | 14.5 | yes | 40.4 / 104.5 | no, L |
| cylinder | 14.7 | yes | 14.8 | yes | 46.8 / 106.7 | yes |
| stacked irrelevant layers | 13.6 | yes | 13.5 | yes | 41.2 / 109.1 | yes |
| stationary | 12.6 | yes | none | none | 14.2 / 103.8 | no, E |
| initial | 12.6 | yes | 13.2 | yes | 40.4 / 105.7 | yes |
| wall retry | 2.5 | yes | 5.6 | yes | 14.9 / 100.8 | yes |
| pushed seat | 13.0 | yes | 78.3 | yes | 196.2 / 123.1 | no, T,L |
| descending landing | 12.9 | yes | 16.4 | yes | 60.6 / 105.5 | no, E |
| slide opposing face | 2.5 | yes | 2.9 | envelope | 9.5 / 100.8 | envelope |
| explicit entry | 2.5 | yes | 12.8 | yes | 47.6 / 100.9 | no, E,L |
| explicit rounding handoff | 2.5 | envelope | 12.7 | yes | 61.7 / 101.3 | no, E |
| explicit changed-axis rounding | 2.4 | envelope | none | none | 62.5 / 101.3 | no, E,L |
| explicit wade | 2.5 | yes | 12.7 | yes | 31.6 / 100.8 | no, E,L |
| explicit swim | 2.4 | envelope | none | none | 26.7 / 101.1 | no, E |
| explicit medium descent retry | 2.5 | yes | 36.0 | yes | 65.5 / 101.8 | no, E,L |
| explicit dry swim handoff | 2.5 | yes | 12.6 | yes | 39.5 / 100.8 | no, E,L |
| explicit dry wade handoff | 2.5 | yes | 12.6 | yes | 44.0 / 100.8 | no, E,L |
| final clamp | 13.4 | yes | 13.2 | yes | 28.1 / 104.8 | no, E |

`E` and `L` codes were present at the phase 1 head and in the hosted record. None is a time or allocation
failure of the support query. The sphere and capsule ticks fail only `L`.

## Ordinary rows still over their limits

Every ordinary row this artifact rejects on time. The attribution test does not cover these scenes, so the
dominant phase is inferred from the phase 1 attribution at `47ded3e11` and the warm counter changes below.

| Scene | Row | After us / limit | Codes | Hosted phase 1 | Dominant phase after (inferred) | Evidence |
| --- | --- | ---: | --- | ---: | --- | --- |
| stairs box | Find | 60.1 / 50.0 | T | 101.9 | SupportContacts, then SupportExtrema | Joins fell from 21 pairs to 3 and admission extrema from 34 to 18, contact counters are unchanged. Phase 1 split: contacts 35, extrema 27, joins 20 percent. |
| stairs hull | Find | 60.2 / 50.0 | T | 103.1 | SupportContacts, then SupportExtrema | Counters equal stairs box. |
| stairs mesh | Find | 60.2 / 50.0 | T | 102.7 | SupportContacts, then SupportExtrema | Counters unchanged, but warm capture is now 0 triangles. Phase 1 split: capture 31, contacts 28, extrema 23 percent. |
| hidden crate | Find | 75.9 / 50.0 | T | 142.7 | SupportExtrema, then SupportContacts | Joins fell from 36 pairs to 3 and extrema from 39 to 23. Phase 1 split: joins 38, extrema 28, contacts 17 percent. |
| stairs box | Dry | 133.6 / 111.3 | T, L | 463.2 | SupportContacts, then SupportExtrema | The look-ahead skip leaves 3 Finds of 6. Extrema 144 to 38, join pairs 75 to 5. |
| stairs hull | Dry | 156.2 / 117.1 | T, L | 487.9 | SupportContacts, then SupportExtrema | Counters equal stairs box. |
| stairs mesh | Dry | 168.0 / 119.5 | T, L | 514.8 | SupportContacts, then SupportExtrema | 3 Finds of 6. Extrema 128 to 70. |
| hidden crate | Dry | 477.5 / 112.6 | T, L | 673.0 | SupportExtrema, then SupportContacts | 6 Finds, since the look-ahead is not decided with no input climb. Join pairs 164 to 16, extrema 178 to 130. |
| pushed seat | Dry | 196.2 / 123.1 | T, L | 291.9 | SupportExtrema, then SupportContacts | 7 reads, 1 from the memo. Join pairs 40 to 9, extrema 54 to 40, exact fallbacks 20 to 8. Phase 1 split: extrema 37, contacts 33 percent. |

The `L` on each tick predates this work. Two passing rows have thin margins: the flat mesh dry tick at
108.9 against 112.5 us, attributed at 112.0 us in the same run, and the hidden crate B1 and D1 substeps at
188.8 and 187.3 against 200 us.

## Rows lossless work cannot reach

Research 2 projected that lossless work clears the flat hull and flat mesh dry ticks and the crate B1 and
D1 time, and nothing else. This artifact agrees. The flat hull dry tick falls from 3468.8 to 51.1 us, the
flat mesh tick from 162.9 to 108.9 us, and the crate substeps pass. Every lossless fix SC-10 to SC-15 names
is now in, and these rows still miss:

- The stairs box, hull and mesh Finds and the hidden crate Find. Each covers 4 to 9 members, with nosing
  and riser contacts and admission extrema the completeness contract requires.
- The stairs, crate and pushed seat dry ticks. A tick runs 3 to 7 complete Finds, and meeting legacy plus
  100 us needs each Find well under the 50 us Find limit.

Closing them needs an owner ruling of the SC-9 kind, such as how many look-ahead samples a tick may take or
whether the legacy-relative ceiling applies to scenes whose candidate does complete multi-member
certification. No limit change is proposed here.

## Counters each task changed

Warm counters, phase 1 head against this head. A counter a task added reads 0 before it.

| Task and ruling | Counter | Evidence |
| --- | --- | --- |
| Task 1, SC-16 | discarded leading row | The flat box Find, first measured before, reads 13.5 us against 2729.0. Substeps judge their own reads, so the crate and seat substeps lose `G`. The rule 3 guard detector threw on no row. |
| Task 2, SC-17 | none, behaviour | The #1437 descent fix and paced placement from the climb budget. The crate and seat ticks lose `G`. Every crate substep passes. |
| Task 3, SC-10 | `JoinsSkipped` | Stairs Find 18 of 21 pairs skipped, crate Find 33 of 36, crate dry tick 148 of 164, seat dry tick 28 of 37. `TopologyJoins` and `JoinsProved` fall with them. |
| Task 4, SC-13 | `Find` per tick | Stairs dry ticks take 3 Finds against 6, with 6 analytic samples against 9. Crate and seat ticks keep their look-ahead. |
| Task 5, SC-11 | `ExactFallbacks` | Flat hull Find 8 to 0, flat hull dry tick 24 to 0. Stairs Finds 8 to 4. |
| Task 6, SC-12 | `MeshTriangleCaptures` | 0 on every warm ordinary row. Flat mesh Find 111.7 to 33.6 us, flat mesh dry tick 162.9 to 108.9 us. |
| Task 7, SC-15 | `FindMemoHits` | 1 per tick on the pushed seat and slide opposing face dry ticks and five explicit ticks. The seat tick runs 6 volumes against 7. |
| Task 8, SC-14 | `AdmissionCertificates` | Stairs Find 4, crate Find 6, crate dry tick 18. Stairs Find extrema 34 to 18, crate Find 39 to 23. |

## Phase shares

From the attribution test, warm us, attributed phase sum before (`47ded3e11`) and after, with the leading
phases after.

| Row | Before | After | Leading phases after |
| --- | ---: | ---: | --- |
| flat box Find | 95.8 | 32.9 | Route 31, SupportExtrema 21, SupportProposals 15, SupportCapture 14 percent |
| flat box DryTick | 48.9 | 45.9 | SupportExtrema 37, Route 25, SupportProposals 12, SupportContacts 12 percent |
| flat mesh Find | 55.8 | 36.8 | SupportExtrema 32, SupportProposals 24, Route 12, SupportCapture 11 percent |
| flat mesh DryTick | 163.0 | 112.0 | SupportExtrema 28, SupportContacts 26, SupportProposals 20, Route 11 percent |
| sphere Find | 13.0 | 11.5 | SupportCurved 41, Route 25, SupportCurvedContact 16 percent |
| capsule Find | 59.8 | 12.1 | SupportCurved 34, SupportCurvedContact 24, Route 21 percent |
| cylinder Find | 15.0 | 13.5 | SupportCurved 28, SupportCurvedContact 25, Route 20, SupportProposals 20 percent |
| fan 96 Find (dense) | 6085.7 | 1559.4 | SupportExtrema 36, SupportJoins 18, SupportProposals 18 percent |
| cluster 256 mesh Find (dense) | 14119.4 | 9763.6 | SupportJoins 47, SupportIncidenceSetup 17, SupportExtrema 11 percent |

The flat box Find before was the first measured row of its process, which SC-16 now discards.

## All ordinary rows

Warm us, one sample each. Hosted is the phase 1 median at `5c373abe9` and its verdict there. Before is the
local phase 1 head `47ded3e11`, after is `15e3d0bfc`. `B`, `D` and `E` are the substeps of the borrowed,
dry and explicit ticks. `Dry` and `Explicit` are whole ticks. Limits are the local ones at `k = 1`.

| Scene | Operation | Hosted | Hosted verdict | Before | After | Limit | After / limit | Verdict |
| --- | --- | ---: | --- | ---: | ---: | ---: | ---: | --- |
| flat box | Find | 23.0 | PASS | 2729.0 | 13.5 | 50.0 | 0.27 | PASS |
| flat box | B0 | 14.5 | PASS | 32.3 | 14.1 | 200.0 | 0.07 | PASS |
| flat box | B1 | 14.3 | PASS | 31.7 | 13.9 | 200.0 | 0.07 | PASS |
| flat box | Dry | 45.8 | PASS | 104.8 | 47.2 | 106.3 | 0.44 | PASS |
| flat box | D0 | 14.7 | PASS | 33.0 | 15.3 | 200.0 | 0.08 | PASS |
| flat box | D1 | 14.5 | PASS | 33.3 | 14.1 | 200.0 | 0.07 | PASS |
| stairs box | Find | 101.9 | FAIL T | 125.2 | 60.1 | 50.0 | 1.20 | FAIL T |
| stairs box | B0 | 72.0 | PASS | 84.1 | 40.2 | 200.0 | 0.20 | PASS |
| stairs box | B1 | 74.8 | PASS | 88.8 | 39.2 | 200.0 | 0.20 | PASS |
| stairs box | Dry | 463.2 | FAIL T,L | 1592.0 | 133.6 | 111.3 | 1.20 | FAIL T,L |
| stairs box | D0 | 72.3 | PASS | 390.0 | 40.2 | 200.0 | 0.20 | PASS |
| stairs box | D1 | 75.1 | PASS | 392.7 | 37.0 | 200.0 | 0.18 | PASS |
| flat hull | Find | 38.6 | PASS | 46.6 | 16.9 | 50.0 | 0.34 | PASS |
| flat hull | B0 | 39.9 | PASS | 44.7 | 17.4 | 200.0 | 0.09 | PASS |
| flat hull | B1 | 39.9 | PASS | 41.7 | 16.4 | 200.0 | 0.08 | PASS |
| flat hull | Dry | 122.5 | FAIL T | 3468.8 | 51.1 | 110.5 | 0.46 | PASS |
| flat hull | D0 | 40.6 | PASS | 2981.1 | 17.1 | 200.0 | 0.09 | PASS |
| flat hull | D1 | 40.2 | PASS | 383.9 | 16.1 | 200.0 | 0.08 | PASS |
| stairs hull | Find | 103.1 | FAIL T | 652.3 | 60.2 | 50.0 | 1.20 | FAIL T |
| stairs hull | B0 | 75.3 | PASS | 212.1 | 44.8 | 200.0 | 0.22 | PASS |
| stairs hull | B1 | 78.8 | PASS | 115.8 | 42.0 | 200.0 | 0.21 | PASS |
| stairs hull | Dry | 487.9 | FAIL T,L | 5028.0 | 156.2 | 117.1 | 1.33 | FAIL T,L |
| stairs hull | D0 | 75.6 | PASS | 1067.5 | 44.3 | 200.0 | 0.22 | PASS |
| stairs hull | D1 | 78.1 | PASS | 477.9 | 41.8 | 200.0 | 0.21 | PASS |
| flat mesh | Find | 41.8 | PASS | 111.7 | 33.6 | 50.0 | 0.67 | PASS |
| flat mesh | B0 | 53.0 | PASS | 140.7 | 39.7 | 200.0 | 0.20 | PASS |
| flat mesh | B1 | 52.4 | PASS | 140.4 | 38.6 | 200.0 | 0.19 | PASS |
| flat mesh | Dry | 150.5 | FAIL T | 162.9 | 108.9 | 112.5 | 0.97 | PASS |
| flat mesh | D0 | 53.4 | PASS | 58.0 | 39.1 | 200.0 | 0.20 | PASS |
| flat mesh | D1 | 52.4 | PASS | 58.2 | 37.5 | 200.0 | 0.19 | PASS |
| stairs mesh | Find | 102.7 | FAIL T | 115.0 | 60.2 | 50.0 | 1.20 | FAIL T |
| stairs mesh | B0 | 79.1 | PASS | 326.9 | 52.4 | 200.0 | 0.26 | PASS |
| stairs mesh | B1 | 80.9 | PASS | 309.7 | 56.0 | 200.0 | 0.28 | PASS |
| stairs mesh | Dry | 514.8 | FAIL T,L | 1430.5 | 168.0 | 119.5 | 1.41 | FAIL T,L |
| stairs mesh | D0 | 79.7 | PASS | 198.4 | 51.3 | 200.0 | 0.26 | PASS |
| stairs mesh | D1 | 81.8 | PASS | 220.2 | 55.5 | 200.0 | 0.28 | PASS |
| analytic only | Find | 4.5 | PASS | 6.5 | 2.7 | 50.0 | 0.05 | PASS |
| analytic only | B0 | 4.7 | PASS | 8.1 | 2.9 | 200.0 | 0.01 | PASS |
| analytic only | B1 | 4.7 | PASS | 6.7 | 2.9 | 200.0 | 0.01 | PASS |
| analytic only | Dry | 15.1 | PASS | 22.9 | 9.9 | 101.0 | 0.10 | PASS |
| analytic only | D0 | 4.7 | PASS | 6.8 | 3.0 | 200.0 | 0.02 | PASS |
| analytic only | D1 | 4.7 | PASS | 6.7 | 3.0 | 200.0 | 0.01 | PASS |
| analytic plus props | Find | 14.7 | PASS | 88.5 | 13.8 | 50.0 | 0.28 | PASS |
| analytic plus props | B0 | 14.6 | PASS | 104.7 | 14.0 | 200.0 | 0.07 | PASS |
| analytic plus props | B1 | 14.6 | PASS | 46.8 | 13.9 | 200.0 | 0.07 | PASS |
| analytic plus props | Dry | 45.3 | PASS | 45.0 | 42.5 | 104.2 | 0.41 | PASS |
| analytic plus props | D0 | 14.6 | PASS | 14.3 | 13.7 | 200.0 | 0.07 | PASS |
| analytic plus props | D1 | 14.6 | PASS | 14.0 | 13.6 | 200.0 | 0.07 | PASS |
| hidden crate | Find | 142.7 | FAIL T | 179.7 | 75.9 | 50.0 | 1.52 | FAIL T |
| hidden crate | B0 | 166.7 | FAIL G | 197.6 | 104.4 | 200.0 | 0.52 | PASS |
| hidden crate | B1 | 244.8 | FAIL T,G | 285.8 | 188.8 | 200.0 | 0.94 | PASS |
| hidden crate | Dry | 673.0 | FAIL T,G,L | 2176.9 | 477.5 | 112.6 | 4.24 | FAIL T,L |
| hidden crate | D0 | 163.7 | FAIL G | 730.3 | 104.4 | 200.0 | 0.52 | PASS |
| hidden crate | D1 | 250.3 | FAIL T,G | 706.8 | 187.3 | 200.0 | 0.94 | PASS |
| sphere | Find | 13.2 | PASS | 21.9 | 11.8 | 50.0 | 0.24 | PASS |
| sphere | B0 | 13.8 | PASS | 12.4 | 11.7 | 200.0 | 0.06 | PASS |
| sphere | B1 | 14.5 | PASS | 12.9 | 12.2 | 200.0 | 0.06 | PASS |
| sphere | Dry | 42.7 | FAIL L | 35.9 | 36.3 | 105.3 | 0.35 | FAIL L |
| sphere | D0 | 13.9 | PASS | 11.5 | 12.0 | 200.0 | 0.06 | PASS |
| sphere | D1 | 14.4 | PASS | 12.5 | 12.4 | 200.0 | 0.06 | PASS |
| capsule | Find | 15.0 | PASS | 14.5 | 12.2 | 50.0 | 0.24 | PASS |
| capsule | B0 | 16.0 | PASS | 13.2 | 13.7 | 200.0 | 0.07 | PASS |
| capsule | B1 | 16.5 | PASS | 49.5 | 14.5 | 200.0 | 0.07 | PASS |
| capsule | Dry | 49.4 | FAIL L | 40.9 | 40.4 | 104.5 | 0.39 | FAIL L |
| capsule | D0 | 16.2 | PASS | 13.2 | 13.2 | 200.0 | 0.07 | PASS |
| capsule | D1 | 16.7 | PASS | 14.2 | 14.1 | 200.0 | 0.07 | PASS |
| cylinder | Find | 17.8 | PASS | 33.8 | 14.7 | 50.0 | 0.29 | PASS |
| cylinder | B0 | 19.3 | PASS | 35.3 | 14.5 | 200.0 | 0.07 | PASS |
| cylinder | B1 | 19.4 | PASS | 34.2 | 14.0 | 200.0 | 0.07 | PASS |
| cylinder | Dry | 58.4 | PASS | 267.6 | 46.8 | 106.7 | 0.44 | PASS |
| cylinder | D0 | 19.5 | PASS | 48.8 | 14.8 | 200.0 | 0.07 | PASS |
| cylinder | D1 | 19.4 | PASS | 37.1 | 14.3 | 200.0 | 0.07 | PASS |
| stacked irrelevant layers | Find | 15.0 | PASS | 16.8 | 13.6 | 50.0 | 0.27 | PASS |
| stacked irrelevant layers | B0 | 14.6 | PASS | 14.0 | 13.5 | 200.0 | 0.07 | PASS |
| stacked irrelevant layers | B1 | 14.5 | PASS | 14.1 | 13.4 | 200.0 | 0.07 | PASS |
| stacked irrelevant layers | Dry | 45.3 | PASS | 43.8 | 41.2 | 109.1 | 0.38 | PASS |
| stacked irrelevant layers | D0 | 14.7 | PASS | 14.0 | 13.2 | 200.0 | 0.07 | PASS |
| stacked irrelevant layers | D1 | 14.5 | PASS | 14.0 | 13.1 | 200.0 | 0.07 | PASS |
| stationary | Find | 14.5 | PASS | 14.4 | 12.6 | 50.0 | 0.25 | PASS |
| stationary | Dry | 15.7 | FAIL E | 16.8 | 14.2 | 103.8 | 0.14 | FAIL E |
| initial | Find | 14.3 | PASS | 14.0 | 12.6 | 50.0 | 0.25 | PASS |
| initial | B0 | 14.4 | PASS | 13.9 | 13.2 | 200.0 | 0.07 | PASS |
| initial | B1 | 14.3 | PASS | 14.2 | 13.2 | 200.0 | 0.07 | PASS |
| initial | Dry | 45.1 | PASS | 42.7 | 40.4 | 105.7 | 0.38 | PASS |
| initial | D0 | 14.5 | PASS | 13.7 | 13.1 | 200.0 | 0.07 | PASS |
| initial | D1 | 14.5 | PASS | 13.6 | 13.0 | 200.0 | 0.06 | PASS |
| wall retry | Find | 4.5 | PASS | 2.6 | 2.5 | 50.0 | 0.05 | PASS |
| wall retry | B0 | 9.3 | PASS | 5.8 | 5.5 | 200.0 | 0.03 | PASS |
| wall retry | B1 | 9.3 | PASS | 5.7 | 5.6 | 200.0 | 0.03 | PASS |
| wall retry | Dry | 24.4 | PASS | 15.4 | 14.9 | 100.8 | 0.15 | PASS |
| wall retry | D0 | 9.3 | PASS | 5.8 | 5.6 | 200.0 | 0.03 | PASS |
| wall retry | D1 | 9.3 | PASS | 5.9 | 5.6 | 200.0 | 0.03 | PASS |
| pushed seat | Find | 14.7 | PASS | 14.2 | 13.0 | 50.0 | 0.26 | PASS |
| pushed seat | B0 | 91.9 | PASS | 99.1 | 78.3 | 200.0 | 0.39 | PASS |
| pushed seat | B1 | 45.4 | PASS | 50.0 | 36.2 | 200.0 | 0.18 | PASS |
| pushed seat | Dry | 291.9 | FAIL T,G,L | 312.4 | 196.2 | 123.1 | 1.59 | FAIL T,L |
| pushed seat | D0 | 94.0 | FAIL G | 102.5 | 40.0 | 200.0 | 0.20 | PASS |
| pushed seat | D1 | 45.4 | FAIL G | 49.9 | 35.2 | 200.0 | 0.18 | PASS |
| descending landing | Find | 14.8 | PASS | 14.5 | 12.9 | 50.0 | 0.26 | PASS |
| descending landing | B0 | 14.7 | PASS | 13.9 | 14.7 | 200.0 | 0.07 | PASS |
| descending landing | B1 | 14.4 | PASS | 14.2 | 16.4 | 200.0 | 0.08 | PASS |
| descending landing | Dry | 66.0 | FAIL E | 66.6 | 60.6 | 105.5 | 0.57 | FAIL E |
| descending landing | D0 | 14.5 | PASS | 14.4 | 13.2 | 200.0 | 0.07 | PASS |
| slide opposing face | Find | 4.5 | PASS | 2.7 | 2.5 | 50.0 | 0.05 | PASS |
| slide opposing face | Dry | envelope |  | 12.5 | 9.5 | 100.8 | 0.09 | ENVELOPE |
| slide opposing face | D0 | envelope |  | 3.1 | 2.9 | 200.0 | 0.01 | ENVELOPE |
| explicit entry | Find | 4.5 | PASS | 2.6 | 2.5 | 50.0 | 0.05 | PASS |
| explicit entry | B0 | 4.7 | PASS | 8.8 | 2.8 | 200.0 | 0.01 | PASS |
| explicit entry | B1 | 4.7 | PASS | 6.9 | 2.8 | 200.0 | 0.01 | PASS |
| explicit entry | Explicit | 53.0 | FAIL E,L | 56.1 | 47.6 | 100.9 | 0.47 | FAIL E,L |
| explicit entry | E0 | 13.4 | PASS | 13.9 | 12.8 | 200.0 | 0.06 | PASS |
| explicit entry | E1 | 13.1 | PASS | 13.5 | 12.5 | 200.0 | 0.06 | PASS |
| explicit rounding handoff | Find | envelope |  | 2.7 | 2.5 | 50.0 | 0.05 | ENVELOPE |
| explicit rounding handoff | Explicit | 72.1 | FAIL E | 65.9 | 61.7 | 101.3 | 0.61 | FAIL E |
| explicit rounding handoff | E0 | 13.7 | PASS | 13.6 | 12.7 | 200.0 | 0.06 | PASS |
| explicit changed-axis rounding | Find | envelope |  | 2.6 | 2.4 | 50.0 | 0.05 | ENVELOPE |
| explicit changed-axis rounding | Explicit | 74.3 | FAIL E,L | 67.7 | 62.5 | 101.3 | 0.62 | FAIL E,L |
| explicit wade | Find | 4.4 | PASS | 2.5 | 2.5 | 50.0 | 0.05 | PASS |
| explicit wade | B0 | 4.7 | PASS | 3.1 | 2.8 | 200.0 | 0.01 | PASS |
| explicit wade | B1 | 4.7 | PASS | 2.9 | 2.8 | 200.0 | 0.01 | PASS |
| explicit wade | Explicit | 38.0 | FAIL E,L | 36.7 | 31.6 | 100.8 | 0.31 | FAIL E,L |
| explicit wade | E0 | 13.5 | PASS | 13.5 | 12.7 | 200.0 | 0.06 | PASS |
| explicit swim | Find | envelope |  | 2.5 | 2.4 | 50.0 | 0.05 | ENVELOPE |
| explicit swim | Explicit | 32.4 | FAIL E | 28.4 | 26.7 | 101.1 | 0.26 | FAIL E |
| explicit medium descent retry | Find | 4.4 | PASS | 4.0 | 2.5 | 50.0 | 0.05 | PASS |
| explicit medium descent retry | B0 | 4.7 | PASS | 3.0 | 2.9 | 200.0 | 0.01 | PASS |
| explicit medium descent retry | B1 | 4.7 | PASS | 3.0 | 2.8 | 200.0 | 0.01 | PASS |
| explicit medium descent retry | Explicit | 76.8 | FAIL E,L | 77.9 | 65.5 | 101.8 | 0.64 | FAIL E,L |
| explicit medium descent retry | E0 | 13.4 | PASS | 13.5 | 12.5 | 200.0 | 0.06 | PASS |
| explicit medium descent retry | E1 | 39.7 | PASS | 40.8 | 36.0 | 200.0 | 0.18 | PASS |
| explicit dry swim handoff | Find | 4.4 | PASS | 2.5 | 2.5 | 50.0 | 0.05 | PASS |
| explicit dry swim handoff | B0 | 4.7 | PASS | 3.0 | 2.8 | 200.0 | 0.01 | PASS |
| explicit dry swim handoff | B1 | 4.7 | PASS | 3.0 | 2.8 | 200.0 | 0.01 | PASS |
| explicit dry swim handoff | Explicit | 45.1 | FAIL E,L | 44.1 | 39.5 | 100.8 | 0.39 | FAIL E,L |
| explicit dry swim handoff | E0 | 13.7 | PASS | 13.3 | 12.6 | 200.0 | 0.06 | PASS |
| explicit dry wade handoff | Find | 4.4 | PASS | 2.7 | 2.5 | 50.0 | 0.05 | PASS |
| explicit dry wade handoff | B0 | 4.7 | PASS | 3.0 | 2.8 | 200.0 | 0.01 | PASS |
| explicit dry wade handoff | B1 | 4.7 | PASS | 3.1 | 2.8 | 200.0 | 0.01 | PASS |
| explicit dry wade handoff | Explicit | 51.8 | FAIL E,L | 49.1 | 44.0 | 100.8 | 0.44 | FAIL E,L |
| explicit dry wade handoff | E0 | 13.5 | PASS | 13.4 | 12.5 | 200.0 | 0.06 | PASS |
| explicit dry wade handoff | E1 | 13.4 | PASS | 13.2 | 12.6 | 200.0 | 0.06 | PASS |
| final clamp | Find | 14.6 | PASS | 13.9 | 13.4 | 50.0 | 0.27 | PASS |
| final clamp | Dry | 30.9 | FAIL E | 29.7 | 28.1 | 104.8 | 0.27 | FAIL E |
| final clamp | D0 | 14.6 | PASS | 13.9 | 13.2 | 200.0 | 0.07 | PASS |
