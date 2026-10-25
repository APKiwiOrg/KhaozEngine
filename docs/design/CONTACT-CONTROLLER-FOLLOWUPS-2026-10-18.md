# Contact controller follow-ups: plates, logs and joined edges

Design for [#1429](https://github.com/APKiwiOrg/KhaozEngine/issues/1429),
[#1455](https://github.com/APKiwiOrg/KhaozEngine/issues/1455) and
[#1454](https://github.com/APKiwiOrg/KhaozEngine/issues/1454).
Source baseline: `dcb0599e3a0bcd881cd268ac3b3a72b3ff49ba82`.
Status: implemented and verified headless in the dry head. #1429 landed at 4ecf2669b, #1455 at 6dfd94eba to 488952a58 with the crest
region at d32762c9a, and #1454 at ff9bfbb7d and 39e920072. `ContactFollowupTraversalTests` pins the CF-6 movement
proof. [General sweeps and acquired movement evidence](CERTIFIED-GENERAL-SWEEPS-2026-10-18.md)
complete #1355 for admitted families. [Lane C](CONTACT-CONTROLLER-FOLLOWUPS-C-2026-10-19.md)
resolves #1494 exact-edge support and #1487 bounded curved work. The historical evidence below is retained.
No timing, #1334, navigation, adoption or release
acceptance follows. The geometry refusals and their decisions are recorded below.

The binding contracts are [SG-1 to SG-24](CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md),
the [curved proofs](CONTACT-CONTROLLER-SUPPORT-CURVED-PROOFS-2026-10-11.md),
[SC-17](CONTACT-CONTROLLER-SUPPORT-COST-2026-10-17.md),
the [phase 2 ground core](CONTACT-CONTROLLER-PHASE-2-GROUND-CORE-2026-10-10.md) and
[ruling 4a-9](CONTACT-CONTROLLER-PHASE-4A-GROUND-SIGNALS-2026-10-11.md).
These follow-ups supply missing certificates and extend the existing continuous-ground rule.
They do not choose a new support model.

## Global constraints

- preserve the 1 mm arrival rule, roof and footprint protections and raw layer identity.
- No tolerance, band, epsilon or limit may be widened.
- A proper fix beats a workaround even when larger.
- No sampled maximum, no snapping a quarter turn, no small-normal threshold.
- Relevant uncertainty still refuses atomically (SG-7).
- No version or CHANGELOG change in the plan's tasks except the final one, which adds a CHANGELOG bullet riding the staged version in Directory.Build.props.

## Rulings

**Ruling CF-1. Exclude a whole polygon only with an orientation-independent separation certificate.**
For #1429, certify strict separation between the complete finite polygon projected into XZ and the
expanded admission disc before requiring the polygon's upward orientation. A separating halfplane
must enclose every installed source vertex and the whole disc, including projected point and segment
degeneracies. Midpoint separation is insufficient. Equality is closed contact, never separation.
Failure to prove separation continues into the complete kernel. Relevant unresolved orientation
there still refuses. Both reference and cached paths consume this same geometric contract.

This lets an ordinary plank at oblique yaw and 3 to 6 degrees pitch support the player when its
irrelevant side face is outside the admission disc. It applies to standalone props, split statics
and compounds, independently of joins. It does not admit a relevant unproved side, change vertical
admission or discard a ring-only context member. This implements SG-3, SG-4 and SG-7, preserves
SG-14/SG-15 and changes results only as CF-7 records under SG-23.
Cost if wrong: a side needed for a cap or roof guard disappears and a lower floor is accepted.

**Ruling CF-2. Prove candidate membership before evaluating its plane height.**
For #1429, the axis and uphill/downhill disc-extremum candidates first pass the certified projected
`Interior` predicate. A proved outside point requires no plane height. A retained point still needs
a resolved height and an attained witness. Preserve the full vertex and edge/disc enumeration,
source-polynomial fallbacks and the original-disc maximum separately from expanded-disc admission.
An orientation sign does not authorize division by a normal-Y enclosure containing zero.

The player can stand on the slightly pitched plate whose irrelevant outside extremum previously
refused the entire query. Uncertain membership or a relevant unbounded plane still refuses. No
small-normal shortcut or lower-support fallback is permitted. This implements SG-4 and SG-7 with
SG-16 independent expectations, and has CF-7's SG-23 disposition.
Cost if wrong: an outside extremum becomes a false maximum, or a real maximum is silently omitted.

**Ruling CF-3. A tilted side retains the equation that proves a closed-disc endpoint attained.**
For #1455, a candidate created by `q + r*z = -disc` or `q + r*z = +disc` carries that signed
source equation through `Candidate`, `Keep`, `Side` and `SidePoint`. Prove its parameter domain,
finite-axis membership and slope from the same installed source arithmetic. The equation proves
the transverse extent is exactly zero. Do not independently subtract two enclosures of the same
squared quantity and treat a negative lower bound as a physical miss. A clipped or uncertain
parameter interval does not acquire the equation by proximity.

The standard float quarter turns about X and Z have small nonzero installed tilt and remain tilted.
Their lying cylinders and capsules can supply flank support and be climbed when their geometry,
band and shell permit it. A feasible boundary supplies an attained lower bound. A stationary region
can enclose a higher maximum without being falsely declared attained. Retain all stationary and end
strata. A proved pole identity may retain exact zero normal, but cannot replace the walkable maximum.

This implements SG-19 and its RL/RO/RN proofs, SG-4 and SG-16. SG-7 still refuses an unproved relevant
source family or exhausted existing bound. Caps, hemisphere restrictions, raw part IDs and the
independent axis and footprint roles remain. CF-7 records the SG-23 change.
Cost if wrong: a merely possible root becomes an attained log surface and floats or sinks the body.

**Ruling CF-4. A support sample retains its selected member's directly certified joins.**
For #1454, while the complete footprint and join matrix are current, copy the selected polygon's
directly joined walkable raw static/element keys into its support sample. Use the existing
whole-polygon meeting and mutual-below predicate at the tick's traction gate. Preserve the
contributing static and element even when another plane supplies its normal.

The sample carries a small authenticated handle, not the rows themselves, and not a reference into
mutable scratch, a connected-component ID or a graph path. The handle holds the interval-owned arena,
a slot index and a non-repeating epoch, about 16 bytes, so the support sample stays near its previous
size. The arena keeps each complete row (the selected key and up to 255 distinct direct neighbors,
matching SG-7's raw-member bound) in storage provisioned once per read interval and immutable until
the interval closes. It is never a per-Find managed allocation. Do not replace a row with a truncated
neighbor prefix or a wider capacity limit. Amended 2026-10-18 (ruling FA-R5): the first implementation
embedded the 2,048-byte row inline, which grew every support sample from about 48 to 2,256 bytes and
every seat, placement and tick result to about 2.3 KB on the per-tick path.
Amended 2026-10-18 (ruling FA-R10): each arena retains at most 64 rows and 1,024 joined keys,
preserving the existing 21,040-byte memo provision while allowing four times the 16-entry repeat
memo's row count and four complete maximum-neighbor rows plus four keys. These are retention
budgets, not limits on support queries. If a complete new row exceeds either budget, its sample
carries no handle and uses the unjoined rule on both heads, without growing storage or truncating
the row. The dry door provisions four independent memo arenas together on first use, for an ordinary
tick and three nested intervals without warm allocation. A fifth simultaneous dry interval throws
`InvalidOperationException` before execution rather than silently changing continuity accounting.
Bind the selected key and its direct neighbor keys to the exact source query, receiver, source,
lease identity, origin and geometry generation. A later Find must not overwrite them. Exact-repeat
memoization copies the evidence with its sample and retains its existing exact query key.
Authenticate the handle (arena, slot and epoch) and both endpoint sources before reading evidence. A disposed lease, another selected
view, rebase, handle reuse or a changed query cannot revive it. No new lease, probe or support
search is added. Evidence is internal to the interval and changes neither geometric value equality
nor wire/replay state. Replay checks original geometric fields and fresh authenticated evidence.

Either endpoint's footprint can certify the selected raw pair. In particular, the preceding
footprint's direct join remains usable when that preceding raw member has left the target
admission volume. Two triangles or separate statics forming a certified continuous edge can then
share the existing continuous-ground calculation. Equal normals, adjacency or a transitive A-B-C
chain never establish the direct A-C join. Curved parts and analytic terrain gain no joins. If
neither endpoint certifies the pair, retain the existing across-member rule. An unresolved relevant
join still refuses the whole footprint under SG-7. This preserves SG-5, SG-8, SG-14, SG-15 and
SG-22 and enables CF-5's SG-23 movement change.
Cost if wrong: stale evidence excuses a real riser, while unmeasured value-copy cost can hurt a tick.

**Ruling CF-5. Directly joined walkable members use the same signed grade excess as one member.**
For #1454, `GroundSeat.StepPart` and `DiscreteStep` use the SC-17 same-member calculation when the
raw member is unchanged or CF-4 proves the selected pair directly joined. Write
`delta = h1 - h0`, `distance = |a1 - a0|`, `c = cos(traction gate)`,
`grade = sqrt(1 - c*c) / c * distance`, and
`excess = max(0, abs(delta) - grade - error0 - error1)`.
Use the signed excess. A rise charges its positive excess, rounded upward by the existing
`GroundPlacement.NotBelow`. A drop charges zero climb. A discrete step is the signed excess only
when its magnitude exceeds the unchanged 0.001 m contact skin, otherwise zero.

The sloped overhung edge can cross a mesh triangle or static boundary without a pacing hitch or
false step signal when the rise is explained by walkable grade. A faster-than-walkable change still
charges its excess, even with a join. A drop beyond that grade remains a negative discrete step.
Without a same-member or direct-join proof, retain the edge-aware `PlaneRise` and two-sided
residual rule, including real risers. Owed climb, placement and the tick's climb budget do not change.

This extends SC-17, the phase 2 down pass and ruling 4a-9 only at certified cross-member transitions.
It preserves SG-1's rise/drop and partition invariants and SG-6/SG-7 safety. CF-7 records the SG-23
disposition. Cost if wrong: a real step becomes free or descent signals disagree with paid movement.

**Ruling CF-6. The numerical and movement proofs use installed geometry.**
For #1429, pin yaw 0.37 with signed 0.05/0.1 rad pitch and yaw 0.77 with signed 3/6 degree pitch,
as floating, bottom-corner-resting and floor-intersecting plates. For #1455, pin cylinder and capsule
radii 0.1, 0.15 and 0.3 m, length 1 m, foot radius 0.2 m, both standard lying rotations and both
approach signs. Put the 0.3 m log's query feet at 0.3 m so support remains in band. Keep exact
horizontal, genuine positive/negative tilt, axis-hit/miss, end-seam and cap/side-tie controls.

Use independent double/exact geometry, not a production snapshot or the legacy stepper. Assert
status, contribution containment in HeightError, the existing half-skin ceiling of 0.0005 m,
witness/normal enclosures, both roles and raw owner/leaf/part identity. Reference, cold, warm,
interleaved, rebuilt and equivalent-partition results must agree as SG-15/SG-16 require.
The ideal horizontal radius-0.15 control has contribution about 0.22071071 m, not the 0.3 m crest.
The tiny installed tilt requires its own oracle, not that rounded control as an expectation.

For #1454, cross a shared sloped overhung edge in both directions as one polygon, two triangles in
one mesh and two statics. Check zero StepPart and DiscreteStep within grade, charged excess beyond
grade, negative descent, real risers, direct versus transitive joins, scratch reuse and stale leases.
Prove continuity from either endpoint, including a preceding member outside the target footprint.
Headless movement over 3 and 6 degree planks and radius-0.1/0.15/0.3 logs reaches the destination
within 0.001 m without extra lift or a changed tuning. A test failure remains a failure, never a
reason to increase the tick bound or arrival ball. This carries SG-16 and SG-23.
Amended 2026-10-18 (rulings FA-R8 and FA-R9): the 0.3 m log's 0.6 m crest is above the default 0.4 m
step height, so its approach from the floor is a blocked wall with no climb and no shell overlap. A
body seated on that crest walks along it and down its walkable flank within the arrival ball instead.
The original proof runs the dry head. The
[general-sweep acceptance](CERTIFIED-GENERAL-SWEEPS-2026-10-18.md) adds acquired ticks over admitted
rotated, hull, mesh and curved props. Lane C resolves the former #1494 tread-edge refusal
using the represented installed support source, with the original questions unchanged.
Cost if wrong: matching two implementations hides the same missing certificate from both.

**Ruling CF-7. Name every changed result under SG-23.**
Append these derived dispositions to SG-23 during implementation, before altering old expectations.
Keep previous gate evidence and cost results as history. The final implementation task adds one
CHANGELOG bullet under the staged version. This drafting commit changes neither version nor history.

| Issue | Before | Intended result and derivation | Still refused or charged |
| --- | --- | --- | --- |
| #1429 | Refused by an outside side face or disc extremum | Walkable at `min(own plane, closed-disc maximum, joined same-kind planes)` for the installed top face, after certified exclusions | Relevant unresolved faces, memberships, plane divisors and proposal guards |
| #1455 | Refused because no side candidate proves attainment | SG-19 axis/footprint support from a source-proved attained tilted-side boundary and complete maximum enclosure | Genuinely unproved source families, above-band or invalid starts, capacity and steep-only climbs |
| #1454 | One false residual climb and discrete step at a member crossing | Identical support selection, with zero charged/signal step within walkable grade for a directly joined pair | Owed debt, excess above grade and unjoined real risers retain their accounting |

These amend certification completeness for the first two defects and continuity accounting for the
third. They grant no new adoption, navigation, timing or release acceptance. Cost if wrong: a silent
expectation update conceals a broader ground-model change.

## What must not change

- The 1 mm arrival ball, 1 mm shell/placement skins, 0.1 mm contact/join band, original closed disc,
  inclusive reach, traction hysteresis, highest-midpoint selection and released tie order.
- Roof, above-band and raised-start protections, complete hidden-support discovery, context-only
  ring members, remote plane caps, convex/concave join semantics and steep suppression.
- Raw static, leaf and part IDs, contributing ownership, both curved witness roles and their order.
- The 256 raw-member and 512 witness bounds, existing root/cell/candidate bounds and atomic spans.
- Query authentication, selected exclusions, scope checks, one-use leases and refusal-to-hold mapping.
- Same-member grade arithmetic, unjoined residual arithmetic, owed climb, upward budget rounding,
  placement clearance, exact free motion, signals unrelated to the joined-edge correction and replay.
- Zero warm support-path allocation, bounded storage and the separate SG-12/SG-21 cost gates.
  Ordinary correctness tests assert no wall time. No timing, repeated test run or stress dispatch is
  authorized here. Existing #1334 dispositions remain independent.
- The default controller selector, legacy controller, tuning/wire/navigation identities, dynamic-body
  support limits, consumer content and engine pin. No release, tag, game edit or navigation bake.
