# Contact controller support cost, SG-12 option C

Status: approved direction (owner, 2026-10-16, option C), rulings SC-1 to SC-9 open to owner review.
Issue: [#1334](https://github.com/APKiwiOrg/KhaozEngine/issues/1334). Parent program: #438.
Builds on [CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md](CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md)
(SG-10, SG-12, SG-15, SG-20, SG-21, SG-24). Evidence: the hosted record
[2026-10-14-support-cost-hosted-1334.md](../verification/2026-10-14-support-cost-hosted-1334.md) and the
local attribution in this change's cost record.

## Problem

The approved hosted run failed SG-12. The owner chose option C: ordinary rows keep every SG-12 limit and
get algorithm work until they pass, and dense rows become stress diagnostics judged against the SG-21
aggregate server budget at phase 6. No limit is raised.

Local attribution on the M2 Max (one-shot, SG-20) found five causes and one evidence defect:

| Cause | Where | Measured share |
| --- | --- | --- |
| C1 Installed polyhedra are re-materialized in world space on every Find | `InstalledSupportGeometry.Materialize`, `SupportNeighborhoodPolyhedra.Polygons` | 39 to 69 us per box or hull static, 55 to 66 percent of the flat floor |
| C2 Every non-planar member runs the full contact kernel, with no certified negative reject | `SupportFootprintCollector.ContactMember`, `ContactHeight` | stairs 140 to 177 us, hidden crate 1412 us |
| C3 Faces of one convex leaf are joined geometrically although topology already proves the shared edge | `SupportFootprintCollector.Publish`, `SupportNeighborhoodJoins.Joined` | stairs 49 + 16 us, crate 554 + 81 us |
| C4 Curved kernels run source-tracking arithmetic first and sit outside any phase bracket | `SupportFootprintCollector.CollectCurves`, `SupportFootprintCurved.Measure` | sphere 35, capsule 64, cylinder 58 us |
| C5 Query-independent polygon facts are computed before the cheap bounds reject | `SupportFootprintPolygon.Measure` | 9 to 13 us flat, 32 to 41 us stairs |
| E1 The hosted scale `k` comes from one single-shot M2 row that is an outlier | `SupportCostVerdict.TryHostedScale` | see SC-2 |

## Rulings

**Ruling SC-1. Option C splits the gate.** Ordinary rows (SG-12 "Ordinary scenes" column) gate #1334 on
time and allocation exactly as SG-12 and SG-24 define them. Dense rows (`grid 100`, every `fan N`, every
`cluster N`) stay in every cost run and record time, allocation and counters, but carry no per-operation
time verdict. Their allocation limits and the SG-12 bounded-storage rule still gate. Phase 6 judges their
time against the SG-21 aggregate budget set from Grimhollow's measured actor counts. This amends SG-21's
"dense rows included". Cost if wrong: a dense scene that is common in shipped content escapes a
per-operation limit until phase 6 measures it.

**Ruling SC-2. The hosted scale is the median over every comparable legacy row.** SG-24 scales by
`k = legacy hosted / legacy M2 Max` on the flat box whole tick alone. Its M2 reference is a single
one-shot sample (SG-10), and the flat box legacy tick in the `baseline` section (23.916 us) is an outlier.
Against that same section, the median hosted/M2 ratio over all 84 comparable legacy rows is 1.87
(interquartile 1.42 to 2.15), while the single row gives 0.63. Raw hosted Find rows agree that the
hosted runner is slower than the M2 Max, not faster: analytic-only Find 9.0 against 2.8 us, flat box
legacy down pass 4.0 against 1.8 us. So every hosted M2-equivalent time in the #1334 record is
overstated by about three times.

`k` becomes the median, over every legacy row present and successful in both sections with the same
scene and operation, of `hosted warm us / M2 warm us`. At least 40 rows are required or the verdict
refuses. The verdict records the row count and the interquartile range. Each hosted iteration still
computes its own `k`. The M2 reference is recorded fresh, in one SG-20 invocation at the commit of the
hosted run it judges, under the current harness, and is added as a new section. The SG-24
legacy-relative row keeps its form with the corrected `k`. The 20 approved #1334 artifacts are
re-evaluated with the corrected verdict and the result is appended to the hosted record without editing
its original verdict. This corrects a measurement, it changes no limit and widens no tolerance. Cost if
wrong: a systematic legacy-versus-candidate speed difference between architectures shifts `k`, which
the interquartile range exposes.

**Ruling SC-3. Oracle-correct refusals and stationary rows are envelope rows.** A row whose candidate
result equals its geometry oracle and is `None`, or whose substep is stationary because the oracle says
motion is blocked, cannot pass a cost row (SG-10). It also cannot fail the gate, since it can never
pass by construction. The verdict reports such rows as envelope rows, outside both counts. A row whose
candidate disagrees with its oracle stays a `G` failure. From the attribution, `explicit rounding
handoff`, `explicit changed-axis rounding` and `explicit swim` (Find, feet over water, oracle `None`) and
`slide opposing face` D0 (blocked against a 63 degree flank, legacy also stationary) are envelope rows.
Cost if wrong: a scene that should support the body but hides behind an oracle bug is excused, which
the SG-16 oracle tests guard.

**Ruling SC-4. Cache the pose-applied world form of installed polyhedra (SG-15, second step).** SG-15
allows capturing immutable local geometry and applying the live pose. The pose is fixed for a cache key,
since static pose, shape and description replacement advance the static-geometry version. Extend each
cached `InstalledSupportGeometry` with a lazily built, immutable world form: the certified world vertices,
pose operator results, validated face normals and every query-independent polygon fact that
`SupportNeighborhoodPolyhedra.Polygons` derives today. It is built once by the existing arithmetic in the
existing order and published first-wins under the same observer rule as the local capture. Each use
checks the exact bits of the static pose (and each compound child's local pose) against the published
form, and a mismatch materializes uncached into scratch as today without publishing. Per-query operand
state never enters the shared form. Curved leaves cache their pose-only constants the same way (SC-8).
Mesh statics are out of this ruling: their triangle set depends on query bounds, so a world-form cache
needs its own bounded-storage design after measurement. Cost if wrong: a pose writer that skips the
version bump would read stale geometry, which the exact pose-bit check refuses.

**Ruling SC-5. Reject before deriving.** `SupportFootprintPolygon.Measure` runs the bounds and column
reject before `BeginFootprintOperands`, and reads the cached normal-area sign and orientation from the
world form. Cost if wrong: none beyond the review, the reject was already certified.

**Ruling SC-6. A certified negative prefilter before the contact kernel.** Before the full
segment-to-polygon kernel, test the proposal's contact capsule (segment, probe radius and contact band)
against the polygon's certified bounds. When separation beyond the band is certified, the member is
`Negative` without the kernel. When it is not certified, the kernel runs unchanged. Equivalence rows
prove the prefilter never turns a kernel `Unresolved` or `Positive` into `Negative` on any SG scene.
Cost if wrong: a refusal silently becomes a non-member, which the equivalence rows catch.

**Ruling SC-7. Topology proves joins inside one convex leaf.** Two faces of one installed convex leaf
that share a certified local edge meet along it. That join is taken from the cached topology, and the
mutual-below certification still runs. Faces of different leaves or different statics keep the
geometric join. Cost if wrong: a degenerate leaf whose captured edge is not a geometric edge, which
capture validation already refuses.

**Ruling SC-8. Curved kernels evaluate interval-first.** `SupportFootprintCurved.Measure` evaluates
its admission and maxima in interval arithmetic first and falls back to the existing source-tracking
arithmetic only on an unresolved sign. Its pose-only constants come from the SC-4 world form. Curved
collection gets its own allocation phase bracket so attribution sees it. SG-19 witnesses keep passing
unchanged. Cost if wrong: an interval result that resolves a sign the source arithmetic would resolve
differently, which the SG-19 witnesses and the equivalence rows catch.

**Ruling SC-9. Proof shape.** Every optimization keeps the uncached evaluator reachable from tests as
the oracle and proves bit-identical results against it: status, height and error, raw feature
provenance, owner binding and XZ, over every SG cost scene, with cold scratch, warm scratch, interleaved
queries, a rebuilt world and concurrent readers under the parallel bake. The ordinary suite asserts
deterministic counters (for example zero world-form builds on a warm Find) and warm allocation, never
wall time (SG-20). Time is read from one local SG-20 cost artifact at the end of the work. If an
ordinary row still misses its limit, it gets a fresh attribution and a further ruling, never a raised
limit. The repeat-time acceptance is a second hosted run, which needs its own owner approval before
dispatch.

## Expected outcome

Projected warm Find on the M2 Max after SC-4 to SC-8 (inferred from the attribution, not measured):
flat box and analytic plus props 12 to 20 us, sphere 20 to 30, flat hull 25 to 35, capsule 35 to 45,
stairs 80 to 150, hidden crate 300 to 600. Flat and curved rows plausibly pass. Stairs and the hidden
crate likely stay over 50 us with lossless work alone. They will be re-attributed after the
measurement, and any further algorithm work gets its own ruling.

## Out of scope

Dense-row time (SC-1), mesh world-form caching (SC-4), cross-tick or cross-body result caching (SG-15
forbids it), any limit change, and the SG-21 aggregate budget (phase 6).
