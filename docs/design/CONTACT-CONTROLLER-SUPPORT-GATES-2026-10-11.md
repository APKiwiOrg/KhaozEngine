# Contact controller support gates: hidden support and cost

Draft for [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438),
[#1347](https://github.com/APKiwiOrg/KhaozEngine/issues/1347) and
[#1334](https://github.com/APKiwiOrg/KhaozEngine/issues/1334).
Source baseline: `06b734c5722bc2cc95cea459255d6ec93e9236c7`, phases 1 through 4b.
Status: approved after three independent review rounds, under the owner's standing instruction to carry #438 to the release gate.
Every choice is a recorded ruling for the owner's review. Rulings SG-19 to SG-24 settle the draft's open
questions, the orchestrator review and the independent spec review. No build, test, timing run or stress run was performed for this spec.

## Contract and remedy

**Ruling SG-1. Keep the body, planar support rule and all nine invariants, with named exceptions.**
This work repairs candidate completeness and pays for certification. It changes which ground the
character may stand on only through the result classes SG-23 lists, chief among them SG-19's curved
witness, which is the primary contract here, with a per-leaf virtual probe contact as the reviewed
fallback. Everything else below is unchanged.
The canonical contracts are the [program](CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md),
[phase 2b](CONTACT-CONTROLLER-PHASE-2B-SUPPORT-NEIGHBORHOOD-2026-10-10.md),
[phase 4a](CONTACT-CONTROLLER-PHASE-4A-GROUND-SIGNALS-2026-10-11.md) and
[phase 4b](CONTACT-CONTROLLER-PHASE-4B-SWIMMING-HANDOFF-2026-10-11.md).

- The shell blocks and recovers. The footprint supplies support. Clearance cannot push along support.
- For a planar surface, the contribution is the minimum of its plane at the axis and its highest
  point inside the closed foot disc. Certified convex joins retain their plane caps. A steep face
  joined to a walkable face remains suppressed. Analytic terrain remains the exact axis sample.
- Keep inclusive reach, slope hysteresis, the highest-midpoint selection and its released tie order.
  Keep the contributing raw static and feature even when a joined plane supplies its normal.
- Keep the 1 mm arrival rule, shell skin and explicit placement skin. Keep the 0.1 mm join/contact
  band. None becomes a tunable accuracy escape. Arithmetic error is an enclosure, not extra reach.
- Determinism, support-height rise/drop accounting, navigation/resting-height correspondence,
  bounded unfooted ascent, shared resolve/clamp support, legal footing and partition independence
  remain required. This draft does not perform navigation capture or open adoption.

Evidence: `SupportCertification.cs:23`, `:61`, `:83`, `:94`, `:108` and `FootSupport.cs:209`, `:218`,
`:235` under `KhaozEngine.Locomotion/Contacts/`. All nine invariants are at
`docs/design/CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md:175`.
Cost if wrong: a completeness repair silently becomes another ground model and invalidates phase 5.

**Ruling SG-2. Use one complete footprint-volume certification per Find, with the two existing
probes retained as proposals and refusal guards.** Do not add a second probe below the steep hit.
Its new first hit can hide a third layer. Ignoring the first static misses a lower face of the same
mesh or compound. A finite stack of such retries proves neither completeness nor partition independence.

The current defect is at `KhaozEngine.Locomotion/Contacts/FootSupport.cs:133`, `:148` and `:149`.
The backend enumerates only elements near that one contact pose. Its collector's membership test is
at `KhaozEngine.Physics.Bepu/SupportNeighborhoodCollector.cs:150`. The new query enumerates geometry
throughout the footprint's possible support volume, regardless of what stopped either probe.
It also certifies the two contact neighborhoods in that same operation. It returns their proposal
verdicts separately from the complete in-band contributions. No extra first-contact query remains.

| Option | Completeness | Determinism and partitioning | Bounded cost potential | Integration fit | Total |
| --- | --- | --- | --- | --- | --- |
| One extra downward proposal | 2 | 3 | 7 | 8 | 20 |
| Repeated probes, skipping prior statics | 4 | 2 | 3 | 6 | 15 |
| Complete volume plus existing guards | 9 | 9 | 8 | 8 | 34 |

Scores are 1 to 10, equally weighted. The complete volume costs new geometry kernels and a capability.
It gives a finite, inspectable completeness certificate. The retained guards preserve the established
roof and above-band refusal behavior. Cost if wrong: the new volume can cost more than two local
neighborhoods, so both gates stay closed until the proof and the cost budget pass together.

**Ruling SG-3. Admit every face the foot column can reach, bounded by the retained probes' reach.**
Write `a` for the axis, `r` for foot radius, `D` for its closed XZ disc, `e` for the unchanged 0.1 mm
contact band and `B = [L, U]` for the inclusive contribution band (`L = FeetY - ReachDown`,
`U = FeetY + ReachUp`). Let `c` be the query's cosine slope gate and `T = sqrt(1 - c*c) / c`, evaluated
outward. For `c = 0` no finite expansion exists and the query returns Unsupported. Never substitute a
chosen maximum slope. Every shell-valid tuning has `c > 0`.

The query requires `r >= AxisProbeRadius` (0.01 m, `FootSupport.cs:21`), which is also the backend's
proven minimum probe radius (`BepuPhysicsWorld.SupportNeighborhood.cs:57`). Below it, the backend cannot
certify the foot probe's neighborhood whenever that neighborhood is requested. Some smaller queries work
today, those on analytic terrain alone and those whose foot sweep misses everything, and this design
deliberately rejects them too, as SG-23 class 6 records. `FootSupportQuery` validation and the ground and shell tuning validators
reject `FootRadiusFraction * CapsuleRadius < 0.01` at configuration, not mid-tick. With that bound the
axis probe lies inside the foot probe's column, so one column encloses both probes.

The admission volume is the vertical column `V` over `D` expanded by `e`, from
`Vbottom = L - max(r*T, BandMargin) - e` to `Vtop = S + e`. `S` is the highest point of the foot probe
at its start pose, `U + BandMargin + 2*r + ProbeLength` in `FootSupport.cs:129`, so `V` contains the
whole region either retained probe can sweep (the sweep ends at `L - BandMargin`). A face is a member
iff it has a certified point in `V`. This expands search bounds, not the disc or reach used for
acceptance. A member with no certified point in the closed disc `D` itself, one that reaches only the
`e` ring, is a context member: it joins, caps and feeds the proposal guards, and publishes no
contribution, because it has no footprint maximum `m`.

Nothing above `S` was ever admitted: both probes start below it and sweep down, so the released
neighborhood can only hold faces within `e` of a probe pose inside `V`. Every released member is
therefore a member here. The difference is the faces the probes could reach but never touched: support
under the first hit (#1347) and rim support near `L`, below the rounded probe bottom.

For a polygon `P`, let `p` be its plane at `a`, `m = max height(P intersect vertical-column(D))`, and
`j` the axis planes of its admitted joined members of the same kind. Its contribution is
`h = min(p, m, j...)`. Every polygon that the unchanged rule could select, with `h` in `B` and a point
in `D` at or below `S`, is a member:

1. A walkable polygon's points in `D` lie within `p +/- r*T`. With `p >= h >= L`, its lowest point in
   `D` is at least `L - r*T`, so its point at or below `S` lies in `V`.
2. Any polygon with `h` in `B` has `m >= L`. Its heights in `D` form a connected interval reaching `m`,
   so a point at or below `S` gives a point in `[L, S]`, inside `V`. This covers steep polygons.

A polygon whose every point in `D` lies above `S` is not a member, exactly as today. The review's
construction, a steep sliver at 3.1 to 3.25 m whose plane meets the axis at 0.125, is ignored now and
was ignored before. Oracle rows pin that case on both sides of `S`.

Capture the whole member polygon and compute `m` on its full intersection with `D`, not on a vertically
clipped fragment. A walkable ramp fragment whose points in `D` lie above `U` while its plane at `a` is
in `B` shows why: clipping it loses valid support.

Joins keep the released whole-polygon predicate (`SupportNeighborhoodJoins.cs:15`, `:35`) over every
admitted member pair, unchanged. Mutual-below and meeting are tested on whole polygons, so two members
that meet only at a remote vertex still cap each other, as the review's disconnected-patch construction
shows they do today. A face with no point in `V` is not a member and caps nothing, as in the released
query, where admission precedes joining. No closure beyond `V` and no localization of the predicate is
allowed. Fast paths in SG-14 must reproduce this verdict exactly.

**Ruling SG-4. Certify membership and the height maximum independently of a sweep witness.**
For boxes, hull faces and mesh triangles, project the captured convex polygon into XZ and intersect
it with `D`. Membership is SG-3 admission, not this test. After admission, a certified nonempty
intersection with `D` is the domain of `m`. A height maximum on this compact set
occurs at a polygon vertex inside `D`, an edge/disc intersection, or the disc's uphill extremum
when that point lies inside the polygon. Enumerate all these cases with outward arithmetic and
exact sign fallback. A flat polygon uses its constant plane height and a deterministic valid witness.
Uncertain intersection includes a bounded candidate or refuses. It cannot be dropped with an epsilon.

`SupportElement.Witness` currently means closest point to a probe segment, not this maximum
(`KhaozEngine.Physics/SupportNeighborhoodQuery.cs:9`). Reusing that field without a new certificate
would repeat the incompleteness in another form. Mesh terrain is the same per-triangle calculation.
One triangle's back face, a vertical face or a proved zero-area triangle cannot support. Selected
compound leaves are independent. A crate below a wedge is still enumerated. An overhang cannot
occlude enumeration, and its underside cannot become a floor.

Curved leaves must also be visited independently of the first blocking static. Retain the admitted
sphere/capsule/cylinder surface and tangent convention, including independent cylinder caps and
side, rather than polygonizing it. Ruling SG-19 fixes which tangent points a curved part publishes.
A closest point on one arbitrary height segment is not a completeness proof. Curved completeness is a
required task and #1347 stays open until it passes, even if the box repro passes.

**Ruling SG-19. Curved witnesses are fixed by geometry, which applies model A to curved props.**
This settles the draft's first open question and the review's F2 and F4. The phase 2b tangent rule,
`min(tangent plane at the axis, witness height)`, walkable or steep by the witness normal and never
joined, keeps its form. Its witness changes. Today the witness is the retained probe's contact, the
point nearest a rounded probe. That is round-bottom tangent support, the model the program scored 22
against footprint support's 34 and the owner rejected. Polygons already take their witness from the
footprint (`m`, SG-4). Curved parts now do the same, so a mesh boulder and a sphere boulder of the same
shape support the body the same way, and no probe contact, proposal order or other leaf decides either.

Each raw curved part (sphere, capsule, cylinder side, cylinder cap) is solved against its own leaf,
never occluded by another leaf, and publishes at most two witnesses:

1. **Axis witness.** The upper-surface point above the axis, when the axis column meets the part. This
   keeps the phase 2b boulder-top behavior, where the axis probe certified the top directly.
2. **Footprint witness.** Let `x_n` be the highest upper-surface point of the part whose XZ lies in `D`.
   Its certified normal classifies it as usual, with equality walkable as in
   `SupportCertification.cs:72`. If `x_n` is walkable, it is the witness. If `x_n` is steep and the part's
   certified-walkable region meets `D`, the witness is the highest point of that walkable region inside
   `D`. Otherwise `x_n` is the witness, as a steep element.

Every witness is attained, so the open steep region never needs a supremum. The upper surface of each
part is a concave height field, and each needed region intersected with `D` is convex for the poses
where a closed form is proved, so each maximum has a closed form: on a sphere or capsule end, the point
of the region nearest the center in XZ, and on a horizontal cylinder side, the point nearest the axis
line. On these two the height falls and the slope rises monotonically with that XZ distance, so when
`x_n` is steep the walkable region misses `D` entirely and the second case never arises. Other poses (tilted capsules and cylinders) need their own written proof in
the curved proof task, or a certified bounded optimization, before the backend may publish them. A pose
phase 2b admits today may not regress to a refusal.

The per-point reading of the rule, the supremum over upper-surface points `x` in `D` of
`min(T_x(a), h(x))`, equals `min(p, m)` for a plane. The footprint witness attains it on a horizontal
cylinder side (a concave height field varying in one direction, where the tangent height at the axis
rises monotonically toward the crest), and did so on a sphere in the orchestrator's numerical check.
It is not attained on every concave height field: for `h = -(x - 1)^2 - 100 z^2` and `r = 0.2` at the
origin, the disc maximum `(0.2, 0)` gives -0.96 while `(0.18, 0.03)` gives -0.8776. Oracle rows compute
both readings on every admitted pose so any difference is visible, and the geometric witness is the rule.

This changes released results, listed in SG-23. The review's sphere flank row
(`SphereFlankBeyondTheSlopeLimitIsSteep`, sphere center `(0, -0.25, 0)`, radius 0.25, axis X 0.35,
foot radius 0.2) becomes Walkable at -0.2: `x_n` is X 0.15, height -0.05, normal Y 0.8, tangent at the
axis -0.2. Today's probe contact gives Steep near -0.285. The row is renamed and its expectation derived
from that geometry. Cost if wrong: the owner meant curved props to keep round-bottom witnesses. The
fallback is a per-leaf virtual probe contact, which keeps today's curved results and still fixes
occlusion, and it touches only the curved witness kernel.

Reusable captured owners exist at `SupportNeighborhoodPolyhedra.cs:59`,
`SupportNeighborhoodMesh.cs:32` and `SupportNeighborhoodCurved.cs:36`, `:115` under
`KhaozEngine.Physics.Bepu/`. Cost if wrong: polygon or curved boundary support floats, sinks or disappears
behind another primitive. A passing crate row alone cannot close #1347.

**Ruling SG-5. Add a separate optional footprint capability and keep the released neighborhood API.**
Proposed home: `KhaozEngine.Physics/SupportFootprintQuery.cs`. Proposed capability:
`IPhysicsSupportFootprint`, with `QuerySupportFootprint` and `AssertFootprintCurrent`.
These are proposed names, not APIs present at the source baseline.

The request carries the exact axis, radius, contribution band, cosine slope gate, unchanged contact
band and the two proposal records. It has no gameplay body, canonical room, cache policy or tolerance knob.
The complete result carries receiver, source, lease, origin and geometry generation, the published
member count, join stride, and per-proposal verdict. A footprint member carries raw Static/ElementId,
normal/error, a certified disc-height maximum and witness/error, or a certified tangent contribution.
Keep plane evaluation and walkable/steep selection in locomotion. The backend owns installed geometry,
disc intersection, extrema, contacts and joins. It must not know walk/climb pacing rules.

Use new types for the changed witness semantics. Do not add a required method to
`IPhysicsSupportNeighborhood` or change `QueryCapsuleFeature`. Bepu and its restricted query view
implement the new optional capability. Contact-controller callers require it and refuse its absence.
There is no fallback to incomplete first-contact support. Share captured geometry and certified
predicates between queries, not a second implementation of box, hull or mesh installation.
Cost if wrong: outside backends gain an explicit capability to implement, but released callers keep their contracts.

## Safety, capacity and numerical boundaries

**Ruling SG-6. Distinguish complete discovery from physical refusal and preserve roof protections.**
Keep both downward sweeps, their back-face policy, start positions and proposal margins initially.
Keep a raised start inside geometry refused. Keep a stopped proposal above the reach band refused.
The complete query can close an in-band steep proposal's missing-lower-support gap. It cannot turn
a too-high step, invalid start, unsupported member or stale interval into permission to stand.
Use certified contact bounds for any new above/below decision. If the sign cannot be established,
refuse. Do not compare a new boundary against raw sweep rounding and call it certified.

Preserve `ContactAboveTheBandRefuses`, `StepJustAboveTheBandRefuses`,
`GeometryInsideTheLegStartRefuses` and `OverhangAboveTheBandIsIgnored`.
Their anchors are `FootSupportNeighborhoodTests.cs:212` and `FootSupportBoundaryTests.cs:92`, `:128`,
`:141` under `KhaozEngine.Game.Tests/Locomotion/Contacts/`. An in-band lower crate is different from
these physical refusals. An ordinary steep hit must not poison a complete walkable contribution.
Keep steep selection and joined-steep suppression as SG-1 requires.
Cost if wrong: a cheap completeness fix seats the body through an above-band roof or tall step.

**Ruling SG-7. Keep the 256 raw-member cap and atomic refusal.** The new complete query's union of
footprint members, contact-context members and cap witnesses counts raw elements once by static/feature.
At exactly 256 it may complete. At 257 it returns CapacityExceeded and a diagnostic RequiredElements.
No prefix reaches either output span. No partial maximum, first 256, plane merging, pagination or
fallback may turn that refusal into a support result. The new volume can reach capacity sooner than
the old contact neighborhood. That is an explicit remaining content limit, not hidden truncation.

The cap counts raw members, each a static plus its released feature or element ID. A raw curved part
carries its SG-19 witnesses in two fixed slots tagged by role (axis, footprint), so 256 raw curved
parts need at most 512 witness records and never a third. Raw IDs and the released curved part
convention are unchanged. A role tag is local to the record, never a new feature ID. Records are
ordered by raw element order, then role. RequiredElements counts raw members. Scratch and stack
bounds are sized for 256 raw members with their witness slots and recorded. A many-curved-member row
proves atomic refusal at 257 raw parts.

A potentially relevant unsupported shape, unresolved bounded predicate or ambiguous hull refuses
the whole query. Ignore an element only with proof that it cannot contribute, cap a contribution or
establish a required proposal guard. Known non-supporting orientation is different from failed
certification. Keep the existing documented degenerate/sliver domain exclusions. Do not generalize
them into permission to drop an unresolved ordinary polygon. No fixed error threshold is added.

The existing atomic owner is `KhaozEngine.Physics.Bepu/SupportNeighborhoodCollector.cs:169`, `:177`,
`:201`. The public cap is `KhaozEngine.Physics/SupportNeighborhoodQuery.cs:27`.
A refused start remains Held. A refused target blocks the attempted move. A leased failure retains
whole-tick hold mapping. Optional signal samples retain their current omission behavior.
Cost if wrong: dense or refused content intermittently picks a lower floor, and partitioning changes that floor.

**Ruling SG-8. Borrow the one authenticated interval and guard the expanded query before calling it.**
All proposals, admission-volume reads, joins, analytic reads, owner binding, landing, signal look-ahead
and final-axis revalidation use the same selected view and physics lease as the tick. Do not acquire
another interval inside Find. Authenticate the issuer before invoking lease methods or taking its
monitor. Reject wrong receiver, thread, source, generation, origin, disposed view and expired lease.
Keep selected-static exclusions. Raw local handles remain lookup keys under that interval.

Update `MovementQueryLease.ContactQueries.cs:11` and `MovementQueryLease.ContactSupport.cs:142`
under `KhaozEngine.Locomotion/` to enclose the actual admission volume `V` and the retained proposal
paths, including float conversion. Preserve MaxRise/MaxDrop and transition-origin checks. An
undersized scope returns Unresolved before backend or producer work. It never widens itself.
Binding still validates the new witness and certified height within the caller's scope.

For unusual slopes, a required cap or witness outside the certified scope refuses. The new backend
cannot call an entire-world column under the old small envelope. Expanded normal scenes need a
headless explicit proof with an adequately acquired scope, not merely a dry-controller result.
Evidence: `BepuPhysicsWorld.CapsuleFeatures.cs:20`, `BepuPhysicsWorld.QueryView.cs:76` under
`KhaozEngine.Physics.Bepu/`, and `KhaozEngine.Locomotion/MovementQueryLease.ContactSupport.cs:90`.
Cost if wrong: dry tests pass while explicit movement reads geometry or owners outside its admitted space.

## Cost gate

**Ruling SG-9. Measure current call sites, not the phase 2 query count alone.** The historical
[cost record](../verification/2026-10-10-p2b-support-cost.json) reports warm Find means of 165 us
for a flat box, 93 us for a flat mesh, 129/105 us for stairs, 55 us for a sphere, 542 us for the
20,000-triangle grid and 102,298 us for the 96-triangle fan. Warm Find allocation is zero.
Those timings include suite contention and a repeated timing harness. They are historical evidence,
not a clean current-machine comparison. The fan performs 9,120 exact pair join tests per old Find.

The current dry ground path has one start Find and ordinarily one target Find per accepted substep.
Pushed placement can add one Find per attempt. Lifted/standing retries and the wall tangent can
make four attempts. Thus `F_ground <= 1 + 8*n` for the dry GroundCore path, with many calls absent
on clear paths. This is a ceiling, not the measured normal count. The current test's old-query
ceiling is `3 + 36*n` sweeps, `5 + 28*n` penetrations and `2 + 16*n` neighborhood queries, no rays.
Do not copy the older, smaller issue-comment ceiling.

| Caller | Support work to count |
| --- | --- |
| Ground start | One Find, including stationary ticks and carried lag |
| Ground seat | One Find per reached target attempt, with possible pushed-axis revalidation |
| GroundSignals | Zero ordinarily, up to three Finds on a triggered traveling run/step tick. Sample zero is reused |
| Air landing | One Find per descending air substep. Its band spans the swept vertical interval |
| Slide | Initial InContact, ground-slide work and the opposing-face look-ahead where taken |
| Explicit entry | One initial Find before the shared controller |
| Explicit constraints | Up to eight medium feasibility retries per Resolve, including their support reads |
| Explicit rounding/handoff | A changed rounded supported axis earns another Find. Otherwise BindSupport still calls the producer |
| Final clamp | A changed final axis earns a Find and can trigger pushed placement revalidation |
| Leased Find | Analytic sampling plus owner binding, not only the backend query |

Anchors under `KhaozEngine.Locomotion/Contacts/`: `GroundCore.cs:115`, `:317`, `:430`, `:466`,
`GroundPlacement.cs:48`, `GroundSignals.cs:16`, `:54`, `AirPass.cs:150`, `SlideCore.cs:62`, `:245`,
`ContactController.Leased.cs:63`, `GroundCore.Medium.cs:23`, `ContactController.Continuation.cs:176`,
`:187` and `ContactController.cs:270`. `KhaozEngine.Locomotion/MovementQueryLease.ContactQueries.cs:49`
and `:59` show analytic and binding work. Eight total mode segments are capped at
`KhaozEngine.Locomotion/Contacts/ContactController.cs:16`.

Add deterministic integer counters for Find, proposals, volume queries, selected broadphase statics,
mesh leaves visited, raw polygons considered, admitted members, exact extrema, interval/exact
predicate fallback, joins considered/proved/pruned, refusal kinds, producer samples/bindings and
lease acquisitions. Counters live in a test observer or opt-in diagnostics, with zero warm allocation.
Assert a code-derived bound for each actual route, including explicit retries and mode segments.
Report counters per Find, actual ground substep and whole tick. Never divide a whole tick by its
substep count and label the average an individual substep measurement.
Cost if wrong: a faster Find still loses an entire tick to look-ahead, retries or producer calls.

**Ruling SG-10. Use one-shot local samples and hosted repeat proof.** Build a dedicated cost fixture
with the same geometry for candidate and legacy. Scene setup, xUnit output, delegates, observer
installation and JSON writing stay outside measured regions. Use `Stopwatch.GetTimestamp` and
`GC.GetAllocatedBytesForCurrentThread` on the executing thread. Save status, height/error, achieved
motion and all counters alongside time. A refusal or stationary fallback cannot pass a cost row.

For each operation/scene/backend, record a first-use sample and warm samples. Each sample executes
the operation once. First use excludes world/mesh construction but includes lazy query scratch. Do
not run a timed warm-up loop, hundreds of Find calls, repeated tests or a local stress run. Cold JIT
versus cold scratch must be labeled separately.

**Ruling SG-20. Time once under fixed compilation, recorded rather than asserted.**
A single warm sample under tiered compilation measures tier 0 code, which is why the p2b harness
needed its quarter-second warm-up loop. The cost invocation instead runs with
`DOTNET_TieredCompilation=0`, so the first call compiles fully optimized code without dynamic PGO,
a conservative and repeatable configuration. The artifact records the setting. Locally each row
records one first-use sample and one warm sample, never more. Medians and repeated samples are
repeated timing, which runs only on the approved hosted proof (SG-24). Timing rows run only when
`KHAOZ_SUPPORT_COST_ARTIFACT` names an output path.
The ordinary suite asserts the deterministic counter bounds and warm allocation of the same rows and
never asserts wall time, which would be flaky on shared and hosted machines. The gate verdict on time
is read from the artifact. Cost if wrong: the conservative configuration overstates tick time by the
PGO gain, which can only make a pass harder.

The old local-loop method is `KhaozEngine.Game.Tests/Locomotion/Contacts/GroundScenarioTests.cs:485`.
Do not execute it to validate this gate. Retire its automatic repeated timing from the ordinary
suite when adding the new fixture. Keep deterministic allocation checks and geometric assertions.
Run one local measurement invocation through `build-slot`. Repeated timing goes only through
`.github/workflows/stress-test.yml`, on the shared GitHub-hosted workflow, with `load=0` and explicit
owner approval naming iterations and duration before dispatch. No workflow is launched by this spec.
Every build/test runs through build-slot. Full suites run once at final verification, in the heavy lane.

Measure flat box, flat hull, flat mesh, analytic-only and analytic-plus-props. Include box/hull/mesh
stairs, the hidden crate, sphere/capsule/cylinder, the 20,000-triangle grid, 96 and 256 triangle fans,
over-capacity refusal, and clusters of 8/32/128/256 raw support elements. Include identical density
as one mesh, compounds and split statics. Include vertically stacked floors and props with irrelevant
out-of-band layers. Include initial, moving, stationary, stair look-ahead, wall retry, pushed seat,
descending landing and dry/wading/swimming handoff ticks. Exercise the larger look-ahead bands.
Cost if wrong: one cold/JIT or contention sample is mistaken for a stable performance guarantee.

**Ruling SG-11. Keep the legacy down pass as a cost baseline, not an expectation oracle.** Time
`PropSupportFloor`, its LowPropSupport route and conditional tread-find work as the legacy down pass.
Also measure a whole legacy tick under the same physics lease, selected view, scene, input and
configuration. Label branches that do no physics work on analytic terrain. Use separate scene
instances and identical input states so one solver cannot warm or move the other's measured state.

The legacy home is `KhaozEngine.Locomotion/CharacterMovement.Collision.cs:700`, `:704`, `:717`,
`:733` and `:763`, called at `CharacterMovement.cs:345`. LowPropSupport adds its sweep at
`CharacterMovement.LowProp.cs:32`. The down pass is not the whole collide-and-slide solver or
the separate TryStepUp up/side/down search. Instrument the existing method with an internal test
observer or an internal entry to that same implementation. Do not duplicate its private code in a test.

Record absolute time and bytes, query counts, candidate/legacy ratio and whole-tick delta. Compare
performance directly on common semantic cases. On the hidden crate, record legacy's actual result
but keep the expected support geometry-derived. Distinct body and footprint models do not promise
bit equality of the two controllers. Cost if wrong: an inexpensive but incorrect legacy result
pressures the new controller to drop its completeness contract.

**Ruling SG-12. Adopt explicit time and allocation budgets, with no claim that they already pass.**
These are proposed acceptance limits for Release on the recorded M2 Max arm64 class of machine.
Record hosted x64 separately on named hardware. Do not compare unnormalized hosted timings directly
to the Mac, or claim a percentile from a one-shot run. Stable repeat-time acceptance needs the
approved hosted proof. Local one-shot results diagnose and can reject an obvious failure.

| Level | Ordinary scenes | Dense scenes within the 256-member cap |
| --- | --- | --- |
| Warm FootSupport.Find | At most 50 us, 0 managed bytes | At most 500 us, 0 managed bytes |
| Actual normal ground substep, shell/seat/placement included | At most 200 us, 0 support-path bytes | At most 1 ms, 0 support-path bytes |
| Whole moving tick, up to three ordinary timed substeps, triggered look-ahead included | At most 1 ms | At most 4 ms |
| Warm dry tick, acquisition/disposal included | At most the observed 208 bytes of unique lease overhead, zero growth with Find count | Same allocation limit |
| Warm borrowed explicit support path | 0 bytes per support read, sample and binding after bounded storage is provisioned | Same allocation limit |
| Warm explicit tick, acquisition and disposal included | Only the named one-use lifetime tokens, constant in Find, shell and sample count (SG-22) | Same allocation limit |

Retain the historical ordinary 50 us target. Tighten the old fan target from 5 ms to 500 us because
three signal samples plus start/target support can multiply one Find fivefold. At 30 Hz, 1 ms uses
3 percent of one 33.3 ms interval. The dense 4 ms ceiling uses 12 percent and is a content worst
case, not a scalable per-actor allowance. Ticks with more than three substeps or complex mixed-mode
retries report the full measured envelope and deterministic bound. They cannot be hidden in the
ordinary row. The aggregate actor budget is SG-21.

On semantically comparable ordinary scenes, a whole candidate tick must also be no more than
`legacy + max(100 us, 0.25 * legacy)`, while still satisfying the absolute ceiling. The additive
100 us avoids a meaningless ratio when an analytic legacy branch takes almost no time. Down-pass
ratios remain reported independently. A failed time row requires algorithm work, not raising its limit.

Record cold time, allocations, retained scratch high-water and stack separately. Require bounded
storage and no quadratic allocation in member count. No zero-allocation cold claim is made.
Provision maximum-cap scratch before a hot gameplay interval. Existing certification stack is
bounded by 4,352 bytes, measured structurally at `GroundScenarioTests.cs:453` under
`KhaozEngine.Game.Tests/Locomotion/Contacts/`. Record any new extremum-kernel stack separately.
Cost if wrong: these per-operation limits may still exceed a real multi-actor server's aggregate budget.

**Ruling SG-13. Account for the 208-byte ReadLease allocation without reactivating stale leases.**
#1334 and phase 4a report 208 bytes per warm dry tick before signals. The controller acquires once
at `KhaozEngine.Locomotion/Contacts/ContactController.cs:48`. The backend constructs a fresh
ReadLease at `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.QueryLease.cs:25`.
Measure acquisition/disposal alone and full ticks to attribute that total on current code and each runtime.
Do not assume the entire reported 208 bytes equals the object size of ReadLease.

Retain the unique, one-use lease for this gate. SG-12 explicitly permits that fixed overhead, rather
than claiming the whole tick is allocation-free. Reusing the same interface object is unsafe: an old
reference would become current again after reacquisition even with unchanged geometry generation.
The current disposed/current-owner/thread checks are at `BepuPhysicsWorld.QueryLease.cs:89`, `:98`,
`:108`. A future allocation-free value-token API would need an immutable acquisition serial captured
by every token/result, authenticated issuance, copy/disposal rules and changed query contracts.
That design is not replaced by resetting `_disposed` on a pooled object.

Measure explicit acquisition, borrowed execution, producer work and disposal separately and together.
It already constructs LeasedContactQueryContext and ExplicitContactModes per tick, and FindContactSupport
creates captured analytic callbacks when analytic facts exist (`ContactController.Leased.cs:55`, `:84`
under Contacts, and `KhaozEngine.Locomotion/MovementQueryLease.ContactQueries.cs:54`). Do not extrapolate
the dry 208-byte figure to that path. Replace per-Find captured callbacks with value analytic facts.
Reuse bounded tick workspace for explicit scratch without reusing public lease tokens.
Cost if wrong: accepting fixed dry overhead hides variable leased per-Find allocation or an ABA lifecycle bug.

**Ruling SG-22. A warm explicit tick allocates only its named one-use lifetime tokens.** This
settles the draft's third open question and the review's F6. The explicit profile is the authoritative
server path, so its per-actor garbage multiplies with actor count. A warm explicit tick, acquisition
and disposal included, measured on the real acquisition route and never on a borrowed lease, may
allocate only one-use lifetime tokens whose reuse would revive a stale reference: the composed
`MovementQueryLease` with its release binding (`MovementEnvironmentContext.cs:81`), the physics
`ReadLease` (`BepuPhysicsWorld.QueryLease.cs:25`) and the producer pin token. Their combined bytes are
constant in Find, shell and sample count, are named in the artifact, and are the ceiling. Every other
per-tick allocation is removed:

- The per-call `CapsuleShape` in certified sweeps and contact certification
  (`MovementQueryLease.Sweeps.cs:35`, `MovementQueryLease.Solids.cs:41`) and the shell's shape
  (`ShellGeometry.cs:37`) are cached by dimensions, as `FootSupport`'s probe already is.
- Per-tick contexts and modes become value types or a bounded workspace owned by the controller or pin.
  Dictionaries become bounded arrays or retained workspace. Captured callbacks become value analytic facts.
- Witness resource copies (`MovementScopeWitness.cs:46`, `:52`) are prepared once per immutable witness,
  not per tick.

Stale-reference rejection stays for the composed lease and the pin as well as `ReadLease`. A disposed
public token is never pooled, and acquisition is never excluded from the measured window to reach the
ceiling. If the named tokens cannot stay that small without an unsafe lifetime, the inventory task
reports the bytes and the decision returns for review. Cost if wrong: the lifetime work touches phase
4b types for a saving the server may not need.

## Meeting the budget

**Ruling SG-14. Remove redundant proposals and expensive join work before adding result caching.**
The cheaper proposal here is two retained sweeps plus one complete certification, not two sweeps
plus three neighborhoods. No sweep alone certifies absence of hidden support. Do not shrink the
0.1 mm contact band or the contribution reach to reduce membership. SG-3 is the smallest permitted
walkable search envelope until a stronger geometry bound proves a smaller one.

Implement lossless join fast paths in the shared geometric predicate: a certified common vertex or
edge proves meeting without repeated segment/polygon distance solves. Mutual-below certification
still runs. Coplanar flat fans have 4,560 pairs, but shared-apex equality should discharge meeting
cheaply. In the footprint evaluator, an exactly identical plane of the same certified kind cannot
lower a cap, so its cap calculation may be skipped without coalescing the two raw members.
Do not replace pairwise incidence with graph connectivity or mesh adjacency alone. Split statics,
non-manifold geometry, intersecting planes and convex versus concave creases still need geometry.

Anchors: `KhaozEngine.Physics.Bepu/SupportNeighborhoodCollector.cs:184` and
`SupportNeighborhoodJoins.cs:13`, `:15`, `:19`, `:35`. Preserve the released neighborhood's full
join matrix. Fast paths must prove the same verdict, including interval uncertainty and refusal.
Compare optimized old queries against their old equivalence fixture. New complete queries instead
compare against the full-volume oracle. Their extra members intentionally change hidden-support rows.
Cost if wrong: an apex speedup joins a nearby crack, suppresses a steep face or picks an owner's normal by accident.

**Ruling SG-15. Cache immutable installed geometry only if the new measurements still require it.**
Start with reused bounded scratch and the shared join fast paths. They are required by the known fan
hotspot. If the 50 us ordinary limit still fails, capture immutable local shape geometry once at
installation, then apply the certified live installed pose under the lease. Cache keys include owner,
shape installation identity and geometry generation. Evict on removal, handle reuse, shape replacement
or rebase. Receiver exclusions are applied per query. World-space materialization must match the
uncached arithmetic and ordering, not use a separately rounded transform.

Do not cache selected support across axes, ticks, bodies or reconciles. No rounded-position key,
previous winner, cached refusal or static-partition-dependent early exit is allowed. An exact repeat
request within one interval may eventually be memoized with all request bits and view identity, but
it is not needed to close either gate. Begin with no result cache. An optimized query must return the
same result with cold scratch, warm scratch, interleaved queries and a rebuilt world.

The current scratch is deliberately not retained geometry (`BepuPhysicsWorld.SupportNeighborhood.cs:81`
under `KhaozEngine.Physics.Bepu/`). Raw element order is in `SupportNeighborhoodCollector.cs:172`.
Invariant 1 applies to replay bits on identical installed inputs. Invariant 9 applies to certified
support geometry across equivalent static partitions. Raw local handle values naturally differ
between partitions and must not be forged into a shared owner.
Cost if wrong: a successful hot-path cache masks stale geometry or makes replay depend on query history.

## Suite and execution

**Ruling SG-16. Geometry supplies expectations, with the #1347 repro first.** Tests must compute
expected planes and extrema from installed float geometry through an independent double/exact oracle.
They do not snapshot the candidate, inherit a legacy result or enlarge an assertion epsilon.
Assert containment in HeightError, status, raw feature provenance, XZ and owner binding. Keep
the existing half-skin error ceiling for ordinary supported rows. Interval uncertainty is exercised
at boundaries using represented values on each side, not random decimal nudges.

| Order | Geometry and expected result |
| --- | --- |
| 1 | #1347. Axis at 0, r 0.2, floor 0, crate top 0.1 at x 0.05 to 0.12, wedge `y = 0.25 + tan(60 degrees) * (x - 0.2)` at the rim. Support is Walkable at the installed crate top, from the crate. Wedge plane at axis is about -0.0964 and is steep. The roughly 0.303 leg-probe centre is diagnostic only |
| 2 | Same case as independent boxes/hulls, one compound and one mesh. Add multiple hiding slopes and stacked tops. Highest in-band eligible top wins, regardless of first hit or element order |
| 3 | Walkable ramp fragment whose points in D are above U but whose axis plane is in B. Its exact `min(p, m)` wins when no retained proposal refuses, and the row also pins the pose where the foot probe stops above the band and refuses. Exercise both ends of `V` and remote joined caps |
| 4 | Off-axis finite ramp and a disc-clipped triangle. Compute m from vertex, edge/circle and uphill-disc candidates. Cases force each extremum class. No segment-closest witness substitutes for m |
| 5 | Existing valley, ridge, convex apex, saddle, plateau/steep neighbor and narrow bevel. Preserve plane caps and steep suppression. Two members meeting only at a remote vertex cap each other exactly as the released predicate does (review construction A/B) |
| 6 | Nosing versus in-band tread, shelf at 0.4007, top at 0.4005 and raised-start overlap. Preserve the named refusal/roof tests in SG-6. Back-facing and vertical fins do not hide a floor |
| 7 | Exact reach endpoints, radial rim tangency, one represented value inside/outside and slope-gate uncertainty. Inclusion, exclusion or refusal follows certified bounds, never a widened tolerance |
| 8 | Hidden sphere, upright/lying capsule and cylinder side/caps under the wedge. Off-axis sphere and log rows, including the axis column missing the solid, the sphere flank row re-derived per SG-19, and every admitted tilted pose. Derive expectations independently. Keep axis, edge, tangency and cap/side tie rows. The oracle also reports the per-point supremum |
| 9 | 96/256/257/300 raw-element fans, stacked clusters and 256/257 raw curved parts with both witness slots. 256 completes if supported. Above cap refuses atomically with sentinel spans unchanged. Refused members cannot leave a lower accepted support |
| 9a | Admission bounds. The review's steep sliver above `S` is ignored, and the same sliver lowered to straddle `S` is admitted. A rim floor near `L` that the rounded probe never touched is found. A below-band refused fan stays irrelevant (`RefusedSurfaceBelowTheBandIsIgnored`). A face reaching only the 0.1 mm ring beyond `D` caps a joined member and publishes nothing. A footprint below 0.01 m, such as the review's 0.005 m patch case, is rejected by `FootSupportQuery`, ground and shell validation |
| 10 | Same geometry in one mesh, compounds and split statics. Height/status agree within certified errors. Identical installed inputs replay bit-identically with cold, warm and interleaved scratch |
| 11 | Excluded winning crate, wrong lease/view/source/thread, disposed and reacquired same-generation lease, removal/reuse and rebase. No excluded or stale support/owner is published |
| 12 | Borrowed explicit initial support, grounded motion, pushed seat, landing, all three look-ahead samples, rounded-axis binding and medium retry. One interval, scope checked before calls, unchanged raw provenance and canonical binding |
| 13 | 1 mm arrival and existing phase 3/4a/4b regression selections. Headless movement reaches the intended axis without extra vertical lift or a tolerance change |
| 14 | Every cost scene in SG-10 at Find, individual substep and whole tick. Deterministic counters and warm allocation asserted after successful geometric results. One-shot timing recorded, repeat proof hosted |

Use new `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.cs` and `SupportFootprintOracle.cs`,
plus `KhaozEngine.Game.Tests/Locomotion/Contacts/FootSupportCompletenessTests.cs` and
`SupportGateCostTests.cs`. Extend the existing boundary, neighborhood, ground, signal and leased
query suites in their current owners. Physics tests cover capability atomicity and lifecycle.
Movement tests cover selection and every changed caller. Cost observer construction stays outside
allocation windows. Timing and allocation rows remain in a nonparallel collection where necessary.
Cost if wrong: tests mirror a flawed implementation and certify the same missing surfaces twice.

**Ruling SG-17. Execute in bounded tasks with independent verification and serial integration.**
This is the task list for the owning orchestrator. It authorizes no dispatch from this drafting task.
Each future writer receives the absolute support-gates worktree, its task branch and an initial
`git -C <path> rev-parse --show-toplevel` plus branch/status guard. Preserve unrelated changes.
One task owns a file at a time. Every brief states its synchronous focused verification command,
required evidence and prohibited release/tag/pin/game actions. Orchestrator reviews reconcile findings
against code and tests before integration. No local repeated timing is delegated.

1. **Contract and oracle.** Freeze the SG-3, SG-4, SG-7 and SG-19 request, admission, contribution,
   witness and cap semantics. Implement the independent volume, extremum and per-point-supremum oracle
   and the first failing crate row. Verify the focused completeness selection once through build-slot.
   Retain failure evidence, not a changed production expectation.
2. **Cost harness and baseline.** Own SupportGateCostTests, the deterministic counters and the
   observer seam into the actual legacy down pass. Remove the old automatic timing loops first. Record
   the baseline of the current phase 4b code in one SG-20 invocation, so every later change has a
   before and after. New artifact: `docs/verification/2026-10-11-support-gates-cost.json` with
   candidate and base SHA, machine, compilation mode, exact command, exit status, scene parameters,
   outcomes, counts, bytes, cold and warm times and legacy deltas. Do not rewrite the p2b record.
3. **Polygon footprint kernel.** Certified polygon and disc clipping, `m` from vertex, edge-circle and
   uphill-disc candidates, admission against `V`, with exact sign fallback. Physics oracle rows only.
4. **Footprint query.** The new capability, box, hull and mesh adapters, tree traversal over `V`, the
   released join predicate over all members, proposal verdicts, raw-member capacity with witness
   slots, atomic publication, receiver authentication and scope bounds. Verify focused Physics rows once.
5. **Curved extremum proofs.** Written proofs per primitive and pose class that phase 2b admits, the
   attained witnesses of SG-19 and their certified arithmetic, with oracle rows. No backend code.
6. **Curved footprint parts.** Implement SG-19 against task 5's proofs in a new extremum owner,
   extracting bounded arithmetic from the existing tangent kernel. Verify the curved rows once.
7. **Locomotion integration.** Own FootSupport, SupportCertification and the composed query wrapper.
   Switch to two proposals and one complete certification. Preserve SG-6 refusals, Selection and
   binding provenance. Re-derive the SG-23 rows. Update expanded scope guards. Verify completeness,
   boundary and leased selections once. No navigation capture or consumer adoption.
8. **Allocation inventory and lifetime tokens.** SG-22 on the real acquisition route: the inventory
   first, then shape caching, value contexts, workspace and witness preparation. Assert the warm
   explicit ceiling. Return the named token bytes.
9. **Lossless cost work.** Join fast paths, then installed-geometry caching only if SG-12 still fails.
   Check old-query equivalence and new-query oracle rows once per changed algorithm. Prove
   cold/warm/interleaved and static-partition equality. Re-record the artifact in one invocation.
   Never solve a budget by losing raw members.
10. **Hosted measurement plumbing.** SG-24's caller and, if needed, shared-workflow change, verified
    without dispatching a repeat run.
11. **Gate review and verification.** Merge current main into the task branch. Run the Release build
    and full suite once through build-slot, with the full suite heavy. Run repository doc, style, size
    and version guards. The capability is additive public API, so it rides the staged minor version and
    its changelog entry, which lists the SG-23 changes. Inspect diffs and exit codes. Close #1347 only
    after all shape completeness rows pass. #1334 follows SG-24. Update the program and phase 2b known
    limits and the docs index. No release, tag or pin.

Expected production owners include proposed `KhaozEngine.Physics/SupportFootprintQuery.cs`,
`KhaozEngine.Physics.Bepu/BepuPhysicsWorld.SupportFootprint.cs`, new footprint geometry types,
existing `SupportNeighborhoodJoins.cs`/capture owners, `BepuPhysicsWorld.QueryView.cs`,
`KhaozEngine.Locomotion/Contacts/FootSupport.cs`, `SupportCertification.cs`, `ContactQueryContext.cs`,
and `KhaozEngine.Locomotion/MovementQueryLease.ContactQueries.cs`/`.ContactSupport.cs`.
Use new cohesive files for the new concern. Do not grow a collector into a body/controller owner.
Cost if wrong: simultaneous geometry, movement and cost edits hide a broken boundary or a biased measurement.

## Risks, limits and budgets

**Ruling SG-18. Keep both adoption gates closed until their separate proof obligations pass.**
The admission volume can admit many more raw members than a contact ball. The 256 cap remains a known
content limit. Curved boundary completeness, rotated/sliver arithmetic and the membership-dependent caps of the whole-polygon join predicate
are the principal geometric risks. Wider look-ahead and explicit scopes are the principal integration
risks. Dense pair work, cold scratch growth and actor multiplication are the principal cost risks.

Existing scaled-mesh and unproved-pose refusals remain. No new general dynamic-body support is added.
The interpenetrating steep box-roof limit in the phase 2b spec remains separate, as does the phase 4a
presentation switch-point issue #1354. This task does not redesign those geometries or signals.
Any inability to certify relevant geometry returns an explicit refusal, not a lower plausible answer.
No local stress, release, tag, engine pin, game edit or navigation capture is part of this work.
Cost if wrong: a partially proven primitive is adopted and phase 5 bakes its hidden error into navigation.

**Ruling SG-23. Every released result this gate changes is named, derived and reviewable.** Applying
the stated footprint rule completely changes six classes of released results, each pinned by oracle
rows and none by a snapshot:

1. Support hidden under a first hit is found (#1347, row 1).
2. Rim support near `L`, below the rounded probe bottom, is found (row 9a).
3. A polygon's witness height becomes its exact disc maximum `m` instead of the point nearest the
   probe. They differ only on sloped edges and apexes inside `D`.
4. Curved witnesses follow SG-19. The sphere flank row becomes Walkable at -0.2.
5. A larger member set admits more released-predicate joins, so a cap can come from a face the probe
   never touched.
6. Support reach ends at the closed disc. A face that reaches only the 0.1 mm ring beyond `D`, which
   today's probe witness could still publish, is a context member (SG-3). Footprints below 0.01 m are
   rejected at configuration.

An existing row's expectation changes only by geometric derivation, recorded in its task report and
the changelog. Phase 5 bakes navigation from the complete rule. Cost if wrong: the owner disagrees
with one class, and that class alone is reverted with its rows.

**Ruling SG-24. The hosted proof is plumbed, normalized and approved before it gates #1334.** The
repository's stress caller (`.github/workflows/stress-test.yml`) cannot yet set
`KHAOZ_SUPPORT_COST_ARTIFACT` or `DOTNET_TieredCompilation=0`, write one artifact per iteration or
upload them. A bounded task adds that, and changes the pinned shared workflow in its own repository if
it cannot forward them. Hosted runners differ from the M2 Max, so hosted acceptance is normalized by
the legacy controller measured in the same invocation: `k = legacy hosted / legacy M2 Max` on the flat
box whole tick, and every absolute SG-12 ceiling scales by `k` there. The legacy-relative row becomes
`candidate_hosted <= legacy_hosted + max(k * 100 us, 0.25 * legacy_hosted)`, since its additive term is
in M2 Max units and its ratio is not. Task 10 checks the verdict code on a faster and a slower host
example without dispatching a run. Iterations and duration are named in an explicit owner approval before dispatch, requested at
the phase 6 stop. Until the approved run passes, #1334 stays open with the local evidence linked. Cost
if wrong: the legacy controller is a noisy yardstick, and a borderline row needs a second approved run.

**Ruling SG-21. Per-operation limits gate this work, the aggregate budget gates adoption.** This
settles the draft's second open question. SG-12's per-Find, per-substep and per-tick limits, dense rows
included, are this gate's acceptance. An actor count and server tick CPU share depend on Grimhollow's
server tick, its actor density and the open creature body decision (#1344), none of which this engine
work can measure. Phase 6 adoption sets that aggregate budget from the game server's measured actor
counts and stops for the owner, as the release gate already does. Cost if wrong: a gate that meets
every per-operation limit can still fail the game's aggregate budget, which phase 6 then reopens.

The draft's three open questions are settled by SG-19 (curved witnesses), SG-21 (aggregate budget)
and SG-22 (explicit tick allocation). They remain open to the owner's review like every other ruling.
