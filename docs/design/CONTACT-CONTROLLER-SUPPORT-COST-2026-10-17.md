# Contact controller support cost, SG-12 option C

Status: approved direction (owner, 2026-10-16, option C), rulings SC-1 to SC-9 open to owner review.
Rulings SC-1 to SC-9 shipped in 21.2.0, apart from the phase 6 aggregate budget of SC-1. Phase 2 rulings
SC-10 to SC-17 are implemented and staged for 21.3.1. The local
[phase 2 cost record](../verification/2026-10-17-support-cost-option-c-phase2.md) shows SG-12 not
met: ordinary rows pass 117, reject 22 and 5 are envelope rows, against 95 and 44 at the phase 1 head. Nine
rows miss time, all on the stairs, hidden crate and pushed seat scenes. #1334 stays open.
Issue: [#1334](https://github.com/APKiwiOrg/KhaozEngine/issues/1334). Parent program: #438.
Builds on [CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md](CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md)
(SG-10, SG-12, SG-15, SG-20, SG-21, SG-24). Evidence: the hosted record
[2026-10-14-support-cost-hosted-1334.md](../verification/2026-10-14-support-cost-hosted-1334.md) and the
local attribution in the [cost record](../verification/2026-10-17-support-cost-option-c.md).

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
| C4 The curved tangent contact kernel bisects to full resolution although the collector keeps only its sign, and curved collection sits outside any phase bracket | `SupportFootprintCollector.CurvedContactMember`, `SupportNeighborhoodCurved.Measure` | 28 to 53 us, 85 to 90 percent of a curved Find. `SupportFootprintCurved.Measure` is about 2 us |
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
Against that same section and hosted run 1, the 57 legacy rows that succeed in both and exceed 0.5 us
have a median hosted/M2 ratio of 1.98 (interquartile 1.46 to 2.25), while the single row gives 0.63. Raw hosted Find rows agree that the
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
pass by construction. The verdict reports such rows as envelope rows, outside both counts. A Find is
oracle-correct when its outcome is `None` and its saved oracle outcome is `None` with the oracle in
agreement. A substep or whole tick is oracle-blocked when it achieved no motion, was not refused, every
support read in it matched its oracle, and no read was `Walkable`, since a body whose every certified
support is steep or absent cannot walk. A stationary row with any `Walkable` read stays a rejection. A
row whose candidate disagrees with its oracle stays a `G` failure. From the attribution, `explicit rounding
handoff`, `explicit changed-axis rounding` and `explicit swim` (Find, feet over water, oracle `None`) and
`slide opposing face` D0 and its Dry whole tick (blocked against a 63 degree flank, every read steep,
legacy also stationary) are envelope rows.
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

**Ruling SC-8. The curved contact member stops once its verdict is certified.** The collector uses only
the three-valued sign of the curved tangent contact kernel (`SupportNeighborhoodCurved.Measure`) and discards
its element, yet the kernel bisects twice per Find through 16 levels to its full resolution, about 100
interval evaluations. Measured locally, that is 85 to 90 percent of a curved Find, while the source-tracking
`SupportFootprintCurved.Measure` the attribution first blamed takes about 2 us and stays unchanged. Give the
kernel a sign-only mode for the collector: it stops bisecting as soon as the verdict is certified to equal the
verdict the full-resolution run would return, and otherwise runs to full resolution. Negative and Positive
need separate arguments, since a final Positive also depends on the narrowed hull publishing resolved, and
every refusal the full run can reach must still be reachable or provably absent. Leaf pose-only constants may
come from the SC-4 world form. Curved collection and the contact kernel each get an allocation phase bracket
so attribution sees the split. SG-19 witnesses keep passing unchanged. Cost if wrong: an early verdict that
the full run would have refused or reversed, which the equivalence rows and an adversarial sweep at the
boundary catch.

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

Measured after SC-4 to SC-8, one local sample per row at `47ded3e11`, warm Find in us, with the attributed
phase sum where the total disagrees with its counters:

| Row | Projected | Measured | Attributed | Limit met |
| --- | --- | ---: | ---: | --- |
| flat box | 12 to 20 | 2729.0 | 78.5 | no, the first measured row reads high in every run (#1434), identical flat rows measure 13.9 to 14.4 |
| analytic plus props | 12 to 20 | 88.5 | 16.8 | no in this sample, its counters match the flat rows |
| sphere | 20 to 30 | 21.9 | 15.0 | yes |
| flat hull | 25 to 35 | 46.6 | 46.1 | yes, 8 exact contact fallbacks remain |
| capsule | 35 to 45 | 14.5 | 12.6 | yes |
| cylinder | not projected | 33.8 | 15.4 | yes |
| flat mesh | not projected | 111.7 | 48.2 | no, mesh capture is outside SC-4 |
| stairs mesh | 80 to 150 | 115.0 | 121.5 | no |
| stairs box | 80 to 150 | 125.2 | 117.2 | no |
| stairs hull | 80 to 150 | 652.3 | 126.5 | no |
| hidden crate | 300 to 600 | 179.7 | 167.6 | no |

On counters and sibling rows the curved and flat polyhedral Finds meet 50 us, yet the flat box and analytic
plus props rows miss it in this sample. The flat box is the first measured row in every run and reads high
there before and after this work, a repeatable first-row effect with no known cause (#1434). The stairs, mesh
and crate Finds miss on every reading, and so do their dry substeps and ticks. The flat hull and pushed seat
dry ticks also miss on their attributed phases, 133.3 against 122.7 us and 305.8 against 124.1 us. Contacts,
extrema and joins dominate them. Stairs dry substeps cost about 300 us more than borrowed substeps with
identical support counters, so part of that cost lies outside Find (#1435). Each gets a fresh attribution
and a further ruling under SC-9. The flat box dry tick passes at 104.8 against 106.7 us.

## Phase 2, after the second hosted run

The owner-approved second hosted run at 5c373abe9 ([record](../verification/2026-10-17-support-cost-hosted-1334-option-c.md))
passes 109 of 139 ordinary rows and breaks none. Thirteen rows miss time and seventeen reject on a fixed
precondition. A second attribution (#1334 ledger, research 2) found the remaining cost and refuted lead #1435:
dry and borrowed substeps cost the same on hosted runs, and local one-shot samples swing because build-slot runs
commands at utility QoS. Rulings SC-10 to SC-17 cover the lossless work and the evidence corrections. Rows that
lossless work cannot reach (the stairs and hidden crate Finds, and the stairs, crate and pushed seat dry ticks)
wait for an owner ruling and are not changed here.

**Ruling SC-10. The support path builds only the joins certification reads.** Certification reads a join only
between members whose upward lower bound is above zero (`SupportCertification.Footprint`). On the FootSupport
path, `Publish` builds incidence and evaluates `Separated`, `TopologyMeets` and `Joined` only for those pairs. The
public raw `IPhysicsSupportFootprint.QuerySupportFootprint` keeps its full join matrix, so no public output changes.
Published support stays bit-identical, with one deliberate exception: the full matrix refuses a whole query when any
pair's join is unresolved, and the support path no longer refuses for a pair certification never reads, since the
published support cannot depend on it. A pinned test covers that case. Join counters on the support path change. Cost if wrong: a later
certification change that reads another pair would see a missing join, which equivalence rows over every cost
scene catch.

**Ruling SC-11. Planar fast paths certify on exact coordinates.** Bepu installs a hull with its centre about
3e-8 m off the authored origin, so the flat hull's top face fails the binary32 orientation check in
`SupportPlanarProposal.Inside` and never takes a planar fast path. The fast paths and the join incidence
certificate use exact-expansion orientation on the double coordinates instead. The fast path stays a sufficient
certificate, so the kernel still decides every case it does not certify. Installed hull geometry is not moved.
Equivalence rows as SC-6. Cost if wrong: a certificate that accepts a face the kernel would refuse, which the rows
catch.

**Ruling SC-12. Mesh statics get a bounded world form and a coplanar-neighbour certificate.** SC-4 deferred mesh
caching for a bounded-storage design. Mesh triangles a query touches are captured into a per-static world form,
built by the existing arithmetic, published first-wins, checked against exact pose bits as SC-4, and bounded by an
explicit per-static and global capacity with eviction that never changes a result. A horizontal polygon whose plane
the probe meets outside its interior is certified by an exact 2D disc-to-polygon distance, otherwise the kernel
runs. Cost if wrong: unbounded memory on large terrain meshes, which the capacity test pins.

**Ruling SC-13. A tick skips look-ahead once ascent is decided.** `GroundSignals.Resolve` takes three
look-ahead Finds per dry tick, 46 to 49 percent of the stairs and seat ticks, but its outcome is already forced
when the input ascends with a rise seen, a lift over the lag or an owed climb. In that case the three Finds are
skipped. MoveState stays bit-identical. Skipped Finds raise no new failure, since their `ContactQueryFailure` was
already swallowed. The SG-16 look-ahead row moves to a scene where the samples decide the outcome, and a direct test
compares the short-circuit with the sampled path. The skip applies to dry and explicit ticks alike. On the
explicit path the skipped samples are also producer reads, so one deliberate exception follows: the workspace's
runtime check that a producer answers consistently within a tick or interval sees fewer reads, and an inconsistent
producer's later conflicting read can now succeed where a look-ahead would have recorded the conflict first. That
check guards against a faulty producer, it is not a contract that requires the reads, and a consistent producer
sees no difference. Cost if wrong: an edit to the ascent logic that breaks the equivalence, which that direct test
catches.

**Ruling SC-14. Non-supporting faces get a cheaper admission certificate.** A vertical face's projected segment is
tested against the expanded disc before the extrema kernel, which runs whenever the certificate does not decide.
The kernel's `Unresolved` outcomes stay reachable. Equivalence rows as SC-6.

**Ruling SC-15. An exact repeat inside one interval is memoized.** SG-15 allows memoizing an exact repeat request
within one interval when the key holds every request bit and the view identity. The pushed seat dry tick issues the
same query twice. The memo lives for one lease interval only, holds no refusal across intervals, and never crosses
ticks, bodies or axes. A memo hit skips only the geometric certification inside the Find. Producer samples,
bindings and the workspace's consistency records still run on every read. A consistent backend answers an identical
request the same way inside one interval, so a fault that a backend would raise only on the repeat of a request is
not observed. Tests that inject a fault target a read that is not a repeat.

**Ruling SC-16. Evidence corrections.** None changes a limit or a tolerance.
- A substep row's geometry precondition judges the substep's own reads and support, not its parent tick's end
  placement (`SupportCostSampler`). All four `G` substep rows read their own evidence correctly today.
- The look-ahead read check uses a guard-aware oracle that models the refusals SG-6 requires (a raised start inside
  geometry, a stopped proposal above the reach band). The pushed seat probe starts exactly on the ceiling slab top.
- The cost harness takes one discarded leading row before the first measured row (an SG-20 amendment), since the
  first measured row reads high in every run on both machines (#1434).
- A paced row's end placement must reach min(final support height, start feet + MaxStepClimbSpeed times dt, roof
  limit) less the existing 1 mm allowance. The roof limit is the lowest overhead surface over the footprint disc
  less the capsule's cap height at that point, the body height and the contact skin, computed by a harness oracle
  from installed geometry. It models the controller's real roof constraint and passes no lag the budget or a binding
  roof does not explain.

**Ruling SC-17. The hidden crate descent is investigated before the crate counts as paced.** While a paced climb
is owed, a substep lowers the feet 0.0577 m below the crate top the body stood on (#1437). A targeted geometry test
decides whether that is a controller defect. A defect gets its proper fix, keeping the 1 mm arrival rule, roof and
footprint protections and raw layer identity. Once the descent is refuted, or fixed and pinned by regression tests,
the harness derives `paced` from the climb budget (owed rise above MaxStepClimbSpeed times dt) instead of a scene
name list. The investigation confirmed a defect: `GroundSeat.StepPart` extrapolated a sloping face plane from a
height capped by an overhung edge and charged a false 0.0577 m rise. The shipped rule, matching ruling 4a-9:
- On one support member (the same Static and member element), the start member's height field explains a rise only
  up to the walkable grade over the horizontal move, tan of the tick's traction gate times the move plus both
  certified height errors. The excess is a step part charged to the climb budget and a discrete step past the
  contact skin. So neither a level nor a sloped overhung edge charges a step, while curvature steeper than walkable
  (a log or sphere flank) still does.
- A same-member drop beyond the walkable grade is a negative discrete step for the smoother and descent signals,
  and charges nothing to the climb budget.
- Across members the extrapolation uses the edge-aware `PlaneRise`. A sloped overhung edge split across two
  members still charges once at the crossing substep (#1454). CF-4 and CF-5 in
  [CONTACT-CONTROLLER-FOLLOWUPS-2026-10-18.md](CONTACT-CONTROLLER-FOLLOWUPS-2026-10-18.md) lift this limitation
  for a pair either endpoint's footprint directly joins. Members not certified as joined keep this rule.
The crate's remaining 0.22 m lag is the designed paced climb.

Measured after SC-10 to SC-17, one local sample per row at `15e3d0bfc`
([phase 2 cost record](../verification/2026-10-17-support-cost-option-c-phase2.md)): every substep row
passes and no row carries `G`. The flat hull and flat mesh dry ticks and the crate substeps now pass, as
research 2 projected. The stairs box, hull and mesh Finds (60 us), the hidden crate Find (75.9 us) and the
stairs, crate and pushed seat dry ticks (133.6 to 477.5 us against 111 to 124 us) still miss. These are the rows
lossless work cannot reach, and they wait for an owner ruling.

**Ruling SC-18. Rows SG-12 cannot compare with legacy are judged on absolute limits and a deterministic bound.**
Owner decision 2026-10-18, option B. Two kinds of ordinary row reject today on fixed codes while within their time:
- `L`: the saved paired legacy outcome shows no support at all on the scene, so there is no legacy result to
  compare against and the legacy-relative term `legacy + max(100 us, 0.25 legacy)` cannot be computed. Such a row
  is judged on the absolute SG-12 ceiling alone (scaled by `k` on hosted runs), with every other precondition
  unchanged. The classification reads the saved legacy outcome only and reconstructs nothing. On the phase 2
  hosted run no row qualifies, because the sphere and capsule dry tick legacy outcomes are Grounded. A legacy tick
  that finds support keeps the relative term and is judged on time against it, including where it lands somewhere
  the oracle disagrees (owner scope ruling 2026-10-18). Legacy disagreeing with the oracle is reported as an
  informational `L` and no longer rejects the candidate row on its own. Legacy is the controller Grimhollow runs
  today, so the term still measures the server cost of switching.
- `E`: the tick is outside the comparable moving single-mode envelope (stationary, descending landing and final
  clamp dry ticks, and the explicit ticks). SG-12 says such ticks "report the full measured envelope and
  deterministic bound" and "cannot be hidden in the ordinary row". They are judged on the absolute SG-12 tick
  ceiling plus a deterministic bound: the tick's substep count and its Find, sweep and penetration query counts
  stay within the controller's own caps, taken from the controller constants, never from the measured output. The
  record reports their full envelope in its own table, apart from the ordinary comparable rows.
No limit is raised and the legacy-relative term still binds every row where it can be computed. Cost if wrong: a
complex tick that is slow but under 1 ms passes without a legacy comparison, which the SG-21 aggregate budget at
phase 6 still judges.

**Ruling SC-19. Rows no lossless lever reaches are judged at phase 6.**
Owner decision 2026-10-18, after research 3. Four ordinary rows have no evaluated lossless route to their SG-12 limit:
the hidden crate `Find`, the hidden crate and pushed seat `DryTick`, and the stairs mesh `DryTick`. Their remaining
cost is the complete-footprint support work itself, and the only identified changes that close any of them alter
support, climb or collision results. These rows, and any phase 3 row that still misses its limit when phase 3 ends,
are judged at phase 6 against the SG-21 aggregate actor CPU budget measured on real Hollowmere actors. SG-12 still
measures and records their time and margin, and the #1334 record lists them as judged by SG-21 rather than as
SG-12 passes. No limit is raised. Cost if wrong: a deliberately adversarial scene runs slower than legacy on the
server, which SG-21 bounds in aggregate before any pin.

**Ruling SC-20. Phase 3 opens with one gated prototype of the exact stair stratum.**
Owner decision 2026-10-18. Research 3's lever L3 is the only evaluated lossless change large enough to matter, but its
saving is an estimate. Task 1 builds the stratum for exactly horizontal faces whose represented double vertices form
an axis-aligned rectangle, bit-identical to the reference evaluator (SC-9), and measures the stairs box `Find` with
two one-shot local artifact runs, base then head. Phase 3 continues to the other reachable rows only if that `Find`
saves at least 6 us M2-equivalent of its 60 us. A fourth hosted run needs its own owner approval. A failed gate
keeps the prototype on a `probe/*` branch, and the remaining misses fall under SC-19.

## Out of scope

Dense-row time (SC-1), cross-tick or cross-body result caching (SG-15
forbids it), any limit change, and the SG-21 aggregate budget (phase 6).
