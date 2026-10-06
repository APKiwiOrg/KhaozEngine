# Low lip resting proofs

Status: proposed, not approved for implementation. The measured fixtures are preserved on
`fix/low-lip-resting-proof`. This proposal addresses
[#1270](https://github.com/APKiwiOrg/KhaozEngine/issues/1270) and the consumer failure in
[Grimhollow #416](https://github.com/APKiwiOrg/Grimhollow/issues/416).

## Evidence and contract

A captured surface height identifies authored ground. It is not necessarily the resting height of a
capsule whose footprint reaches a neighboring lip. The 4.25 cm fixture has a 0.3 m radius, 0.75 m
half-height, 0.4 m step height and 0.25 m cells. Its bank sample at x -0.125 rests about 15.2 mm above
its captured height. The unchanged 1 mm arrival test correctly distinguishes these two positions.

Canonical resting endpoints alone do not repair the route. The x -0.375 to -0.125 directed proof
exhausted its 64-step budget with all 66 footprint callbacks accepted. Its last four positions were
identical, at x -0.13796315 and feet Y zero. The reverse and both bank/deck directions passed.

Commit `23088a7dd` adds six one-Hold observations. Including or excluding the physics floor gave
identical results. At x -0.145 and -0.135 the feet remained at terrain height with 4.481375 mm and
9.257436 mm residual overlap. At x -0.128 they rose 13.821125 mm, leaving 1.43 micrometres of overlap.
This supports the source-derived gap between walkable sweep contacts and near-flat low-prop support.
It does not measure the internal branch taken or the consumer's directed approach.

The real captured footprint rejects the roof-squeezed bank column and keeps its legitimate rooftop
separate. A deliberately permissive predicate does not establish safe bake acceptance. The failed
broad upward-band and vertical-depenetration experiment in #1265 broke seven stair tests and remains
rejected prior art. The low-prop bands and known moderate-mound limitation are recorded in M16 of the
[NPC movement design](NPC-MOVEMENT-FIXES-DESIGN-2026-10-03.md).

## Recommended repair boundary

Two concerns must be addressed together. Neither changes the global arrival tolerance, terrain,
world geometry, a native authoring adapter or the general depenetration rule.

1. Recognize support from a low flat geometric top when the capsule meets its corner.
2. Prove legacy baked edges between physical resting anchors while retaining captured surface identity.

The likely production homes are `CharacterMovement.LowProp.cs` and `PhysicsNavBake.Profiles.cs`.
Swimming owns future explicit-mode dispatch and certified sweep work. World authoring owns native
canonical geometry and support producers. Both owners confirmed that a bounded legacy repair need
not depend on those unreleased capabilities. Reconcile shared profile dispatch and identity hashing
before integration. Future explicit movement profiles must not inherit a legacy Hold anchor implicitly.

## Geometric support classification gate

A capsule contact normal is not a flat-face certificate. Keep the existing near-flat sweep path.
A proposed fallback for the walkable, non-flat corner band must establish a corresponding near-flat
geometric face in the same selected movement view, preserve the slope gate and the existing low-rise
bound, and refuse an unrelated or unavailable face.

The existing ray and sweep results carry static-body handles. A bounded downward ray through the
contact location is a candidate discriminator, not an approved shortcut. Before implementation,
prove that its face normal and hit point correspond to the same static and contact height. Pin the
numeric agreement bound from the actual backend behavior. Do not substitute SkinWidth or an arbitrary
inset for that proof. A ray that hits a different face above the contact, an unresolved boundary, or a
dynamic hit without static identity cannot authorize the fallback.

Required controls include the flat lip, a sloped top outside the near-flat band, a dome flank,
a wall, an overhang and a mesh edge. Preserve the movement view's terrain exclusions. If the existing
query seam cannot establish the required correspondence, stop and review a specific Physics/query-view
capability with swimming. Do not import MapDoc into legacy locomotion or infer a face from feature IDs.

## Bake-local resting anchors

Keep raw captured heights, layer assignment, headroom and area identity unchanged. During legacy
profile construction, resolve a physical anchor from each candidate's exact captured position through
the existing movement context. Require finite grounded output, unchanged XZ within the existing arrival
tolerance, and acceptance of both raw and resting footprints. The raised capsule must fit the captured
headroom after its rise is accounted for and remain in the original surface's vertical ownership band.
A different deck or rooftop cannot become the bank's anchor.

Use these anchors only for the existing directed movement proof. Its initial Hold checks stability,
and its complete trajectory must still pass the real footprint predicate and unchanged arrival test.
No full-speed overshoot, target snap or fabricated graph edge may replace the slowing approach.

Raw graph heights can remain unchanged, so `IsFloatNode` can retain its current raw-height identity
lookup. An extra public surface-index API or bake format change is not assumed. If implementation
needs to expose anchors to route consumers, return that concrete requirement for review before changing
the format. Verify live traversal and stopping at the affected waypoint, not only graph reachability.

## Explicit test disposition required

The four tests hypothesizing that `GroundTraversalProbe` should accept a raw bank point are not the
same contract as bake-local anchor resolution. Under this proposal the strict probe continues to
refuse a raw point that its Hold moves by 15 mm. Do not weaken its 1 mm arrival check to make those
hypotheses green. An implementation approval must explicitly resolve this disposition, preserving the
original failing evidence and replacing the hypothesis with raw-point refusal plus anchor-resolution
coverage. Candidate retention and both route directions remain required positive proofs.

Keep the real-footprint roof and wrong-layer controls, original 2.5 cm case, finite bidirectional live
traversal, low-prop/dome controls and the seven-stair regression set. The game must then prove its
original bank-to-bank Complete route and unchanged 600-step crossing on a released engine pin.
No world ramp or navigation artifact is hand edited.

## Decision comparison

Scores use 1 to 10, higher is better. They compare delivery approaches, not correctness already proven.

| Criterion | Bounded legacy repair | Wait for native support adoption |
| --- | ---: | ---: |
| Addresses the measured current failure | 9 | 4 |
| Keeps surface identity and proof ownership explicit | 9 | 9 |
| Limits changes to the current consumer | 8 | 5 |
| Delivery independence from a larger migration | 9 | 2 |
| Total | 35 | 20 |

Recommend the bounded legacy direction, gated on geometric-face correspondence and the explicit
raw-probe test disposition above. This document authorizes no implementation, new test execution,
package, release, game pin or bake. Exact filters and shared ownership return for review before work.
