# Contact-classification character controller

Date: 2026-10-08, revised 2026-10-09 for ground ownership. Specifies [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438), the phase 2
direction chosen in [PHYSICS-LOCOMOTION-DESIGN-2026-08-02.md](PHYSICS-LOCOMOTION-DESIGN-2026-08-02.md).
Status: phases 1 and 2 are implemented and released in 20.30.0, and phases 2b and 3 are implemented and staged
for 20.31.0. Nothing consumes the controller yet. Phases 4 to 6 are specified at the level of contracts and exit
criteria, and each gets its own detailed spec before implementation. No game adopts before the two gates in the
phases table pass.

## Why now

[#1270](https://github.com/APKiwiOrg/KhaozEngine/issues/1270) needed a body walking toward a low lip to
arrive within the 1 mm navigation arrival ball. It stopped 6.75 mm short. The measured cause is in
`docs/verification/2026-10-08-low-lip-approach-mechanism.json` on `fix/low-lip-resting-proof`. Before each
sweep, the legacy stepper pushes a moving body off a walkable contact by a skin-inflated MTV. On a tilted
walkable contact that push has a horizontal part. Near the goal the commanded step shrinks until it only
regains that push, and the body parks short. [#1265](https://github.com/APKiwiOrg/KhaozEngine/issues/1265)
is the same mechanism on smooth physics slopes.

Making that clearance vertical fixed the approach (14 steps, 0.41 mm) and exposed defects the push was
masking. Evidence is `docs/verification/2026-10-08-low-lip-stepper-cascade.json` on
`fix/1270-stepper-cascade`.

- The legacy stepper already deadlocks on a shallow-tread staircase at 2 m/s from all 21 measured starts.
  Its one-radius step-up probe lands across the next nosing, so a walk only rises when the support sweep
  happens to read that nosing first.
- The staircase-base tread finder re-reads the top of a ledge the body is leaving and pops the feet back up.
- A ledge drop is measured from the feet after the rounded capsule bottom has rolled over the edge, so the
  documented boundary of StepHeight drifts with capsule radius.

Fixing each of those passed its own target and broke 17 other pinned stair, ledge and dome behaviours. The
stepper is a set of per-case gates tuned against one another. On 2026-10-08 the owner ruled to build this
controller properly instead of patching the stepper further.

## Intent and success

A kinematic controller in which support, slopes, walls, ceilings, step-up, step-down and ledges follow
from classified contacts rather than from a gate per case. It replaces the whole `StepCore` pipeline when a
game opts in. Grimhollow adopts first. Ruinborne (pinned at 20.2.0) adopts in its own release.

Success is the new controller passing a fresh first-principles suite, then each adopting game's own
playtest. The suite must include the measured defects above and the #1270 resting route.

## Binding constraints

These carry over unchanged from the 2026-08-02 design and #438.

- The step is a pure function of `(MoveState, MoveCommand, dt)` plus stateless world queries.
  `ClientPrediction.Reconcile` replays up to 256 commands. Any field that feeds the next tick rides
  `MovementState` with a wire generation bump.
- It lands opt-in behind a `MoveTuning` switch. The legacy stepper stays the default and stays unchanged.
  A game flips only after its own playtest.
- `WalkSpeed`, `RunSpeed`, `MaxSlopeRadians`, `JumpSpeed` and `AirMomentum` keep their meaning.
- The exports `ClimbRate`, `ClimbRateEwma`, `StepDeltaY`, `Swimming`, `Grounded`, `VerticalVelocity`,
  `FacingYaw`, `LandingImpactSpeed` and `SupportGranted` keep their meaning and wire form.
- Wade and swim behave as today. NPCs keep sharing the core through `StepTowards`.
- The player is never a Bepu rigid body. The physics world stays a statics query oracle on this path.

## Owner rulings, 2026-10-08

- Replace the whole `StepCore` pipeline, not only contact resolution.
- Acceptance is a fresh first-principles suite. Legacy suites are reference only. Their scenario
  geometries (stairs, lips, ledges, domes, creases, eaves, the #440 jump climb, the #470 steep step-down,
  the #475 hysteresis band) are mined into the new suite with physically derived expectations, never with
  legacy tuned numbers.
- Architecture A: up, side and down passes over classified contacts. It scored 40 against 36 for a
  general manifold solver and 34 for a classified legacy skeleton.
- Support model A: foot footprint support. It scored 34 against 22 for round-bottom tangent support.
- Proper fixes over scope. A known proper fix is built even when a plan's scope excludes it.
- 2026-10-09: this controller owns ground movement for every movement path, including swimming's explicit
  path. It scored 34 against 25 for moving this controller to round-bottom support and 24 for keeping two
  models (see Ownership with swimming).

## Ownership with swimming

Swimming's approved F3 required full-capsule solid clearance with round-bottom tangent support, and its
generic resolver could not take a normal 30 Hz step onto a 0.25 m bank from centre-column support. One
ground model governs the engine, so the boundary is:

- **This controller owns** support semantics and support-owner binding, the body model, the up, side and
  down passes, steps, ledges, walls, ceilings, air, ground state and the ground signals.
- **Swimming owns** medium classification, water policies, buoyancy, swim, wade and water entry and exit
  arcs, the medium half of every placement proof, transport and read admission, lease discipline, and
  sweep completeness with its error brackets.
- **Handoff.** Swimming's explicit grounded ticks call this controller's ground core through the phase 4
  handoff. `MovementSupportResolver`'s land selection is replaced by the support primitive. F3 is amended
  from full-capsule solid proof to shell solid proof, certified footprint support and medium proof.
- **Queries.** Every query in one tick runs under one authenticated read interval (`IPhysicsQueryLease`,
  and `MovementQueryLease` on the explicit path). A decision that depends on a sign uses certified error,
  never a tolerance chosen to make a case pass.
- **Canonical owners.** A support sample carries the installed static and its certified finite feature.
  The producer that installed the static maps it to the canonical owner. Support is evaluated at the axis
  column, so a candidate's feet always share the body's XZ. The touched point inside the disc is the
  witness attached to the candidate, never its feet.

## Body model

The body is two volumes with separate jobs.

**The shell** is the capsule above knee height. Knee height is `feet + StepHeight`. The shell has the
capsule radius and reaches the head. It is the only volume that blocks walls and ceilings and the only one
overlap recovery acts on. Geometry below the knee never pushes the body sideways, which removes the
measured push at its source. Tuning validation requires the shell to stay a real capsule:
`2 * CapsuleHalfHeight - StepHeight >= 2 * CapsuleRadius`.
The shell must also clear its own steepest walkable plane, including the 1 mm contact skin:
`(StepHeight + CapsuleRadius) * cos(MaxSlopeRadians) >= CapsuleRadius + ContactSkin`.
Its lower cap centre is `StepHeight + CapsuleRadius` above the feet. The left side is that centre's
perpendicular distance from the plane. Tuning that violates either constraint is rejected.

**The footprint** is a vertical disc of radius `FootRadiusFraction * CapsuleRadius` at the capsule axis.
`FootRadiusFraction` is a new `MoveTuning` field, default 0.5. Support comes only from the footprint, by
the rule below.

The default trades two visible effects. A smaller disc lets the legs overlap a low obstacle by up to
`CapsuleRadius - FootRadius` before stepping onto it. A larger disc lets the body stand further out over
an edge before it drops. Both are presentation-level, below the knee, and tunable per game.

## The support rule

Every walkable surface the footprint touches within the step band contributes

```text
contribution = min(height of the surface's plane at the axis, highest point of the surface inside the disc)
```

Support is the highest contribution, returned with its surface normal and its source. A surface is
walkable when its face normal passes `cos(MaxSlopeRadians)`. A steep surface contributes by the same rule
and is reported as steep support.

What the rule gives, case by case:

| Case | Result |
|---|---|
| Uniform slope | The plane at the axis. Feet sit exactly on the surface under the axis, with no `r(1/cos - 1)` float. |
| Lip or crate top inside the disc | The flat top. The body stands on it, so legs do not sink into its side. |
| Shallow treads | The highest tread the disc reaches. Treads chain without a step-up landing search. |
| Descending ramp starting inside the disc | The flat level. The ramp's plane extrapolated back would be higher, and the min removes it. |
| Ascending ramp starting inside the disc | The ground under the axis. The ramp's plane at the axis is lower. |
| Ledge being left | The ledge top until no part of it is under the disc, then the ground below. The drop is between surfaces. |
| Crack narrower than the disc | Bridged by the rims. |
| Analytic terrain | `h(axis)`. A smooth surface's plane at the axis is the surface there. |

The same primitive drives the controller's down pass and, in phase 5, the navigation column capture. A
captured height is the resting feet height by construction, so #1265 and #1270 need no resting anchors.

## Tick pipeline

Each later phase specifies its passes in detail. The order and ownership are fixed here.

1. **Intent.** Resolve the command into a desired horizontal velocity, with the existing speed, sector and
   facing rules. Integrate gravity, jump, coyote time, jump buffer and air momentum.
2. **Up pass.** Lift the shell by up to `StepHeight`, bounded by a ceiling sweep. Grounded ticks only.
3. **Side pass.** Sweep the lifted shell along the horizontal move in substeps of at most half a radius.
   Wall contacts slide the move along their plane. Low geometry passes under the shell.
4. **Down pass.** Sweep back down and query support within the band. Walkable support within `StepHeight`
   of the start is a step up or down. Steep support starts a slide. No support within the band makes the
   body airborne. A rise above `StepHeight` refuses the lifted move and retries it unlifted.
5. **Recovery.** Resolve shell overlap only, along the true MTV. No clearance push touches the footprint.
6. **State and signals.** Grounded, traction hysteresis, landing impact, commitment, climb signals and
   facing, from achieved motion and contact transitions.

## Contact classification

| Class | Source | Meaning |
|---|---|---|
| Support | Footprint, walkable face inside the band | The body may stand on it |
| Steep support | Footprint, face steeper than the slope limit | The body slides on it and gets no footing |
| Rising support | Shell, contact normal Y at least `cos(MaxSlopeRadians)` | Ground ahead rising faster than the lift, left to the down pass |
| Wall | Shell, contact normal Y from 0 up to `cos(MaxSlopeRadians)` | Blocks and slides the side move |
| Ceiling | Shell, contact normal Y below 0 | Bounds the up pass and a jump |

No class depends on whether a surface is analytic terrain or a physics prop.

## Program invariants

Every phase's suite asserts these.

1. Determinism. Identical inputs give bit-identical outputs, including across a reconcile replay.
2. Clearance never moves a body along its own support.
3. A rise or drop is measured between support heights, never from a rolled or sunk body.
4. A navigation captured height equals the resting feet height.
5. A tick without footing ends no higher than its own vertical motion allows, apart from rises onto or along walkable
   support, which the phase 3 spec bounds (#468).
6. The resolve and the ground clamp read the same surface (#468, round four).
7. A footed tick may not seat itself on ground it cannot stand on (#486).
8. Traction keeps a hysteresis band at the slope limit (#475).
9. Support is independent of how static geometry is partitioned into statics, for surfaces that meet within the
   contact band (phase 2b).

## Phases

| Phase | Delivers | Exit criteria |
|---|---|---|
| 1. Contact foundation | Integrated feature query, certified support primitive, contact classifier | Phase 1 suite green, including the swimming bank repro. Legacy stepper byte-unchanged. |
| 2. Ground core | Up, side, down passes, recovery, slopes, walls, step-up, step-down, ledges ([phase 2 spec](CONTACT-CONTROLLER-PHASE-2-GROUND-CORE-2026-10-10.md)) | Ground suite green, including the shallow-tread runs at 2, 3 and 4 m/s and the 0.41 m ledge. |
| 2b. Certified support coverage | A complete support neighborhood across statics, certified at concave creases, vertices, curved primitives and through one-sided back faces (#1329 to #1333, #1340, #1342, [phase 2b spec](CONTACT-CONTROLLER-PHASE-2B-SUPPORT-NEIGHBORHOOD-2026-10-10.md)) | Every phase 1 refusal row replaced by a certified result. Must land before any game adopts. Implemented, staged for 20.31.0. |
| Cost gate before adoption | Support query time ([#1334](https://github.com/APKiwiOrg/KhaozEngine/issues/1334)). A `FootSupport.Find` takes 55 to 165 microseconds on ordinary scenes, 542 microseconds on a 20,000-triangle grid and 102 ms on a 96-triangle fan, with zero allocation. Candidate approaches: filtered predicates with an exact fallback, lazy joins tested only where certification reads them, and a per-lease join cache. | Recorded time per `Find` within the #1334 budget on ordinary scenes and dense fans, results unchanged against the recorded equivalence fixture. No game adopts before it passes. |
| Hidden support gate before adoption | Support hidden under a sloped first contact ([#1347](https://github.com/APKiwiOrg/KhaozEngine/issues/1347)). `FootSupport` certifies only the neighborhood at each probe's first sweep contact, so a sloped surface that stops the leg probe can hide a lower walkable surface inside the disc. Candidate approaches: a neighborhood over the whole foot cylinder, or a second proposal below the first contact. | The wedge and crate case in the known limits gives the crate top's 0.1. Navigation capture and adoption wait for it. |
| 3. Air and state | Jump, coyote, momentum, landing impact, commitment, steep slide, traction hysteresis ([phase 3 spec](CONTACT-CONTROLLER-PHASE-3-AIR-AND-STATE-2026-10-10.md)) | Air and slide suite green with invariants 5 to 8. Implemented, staged for 20.31.0. |
| 4. Signals and fluids | Climb signals, step delta, support grant, facing, the swimming handoff contract | Signal suite green. Swimming's explicit grounded ticks run on this ground core. |
| 5. Switch, wire, navigation | `MoveTuning` selector, any wire fields, bake identity, traversal probe and capture on the selected controller | Both controllers selectable. Legacy bakes still load. A new-controller bake round-trips. |
| 6. Grimhollow adoption | Grimhollow opt-in, #1270 resting route, bridge Complete and 600-step proof | Engine release, game pin and playtest, each separately authorized. |

## Phase 1 design: contact foundation

### Physics seam

Phase 1 adds no sweep seam. `FootSupport` proposes candidates through the existing `IPhysicsWorld.SweepCapsule`
and certifies them through the optional `IPhysicsCapsuleFeatures` capability, in the established
optional-interface pattern. A probe sweep leaves the probe capsule exactly in contact, which is the pose the
feature query certifies, so a cylinder or general convex sweep would add a public API with no remaining use.
`IPhysicsWorld` itself is unchanged. `FootSupport` throws `NotSupportedException` for a non-null world without
`IPhysicsCapsuleFeatures`, and phase 5 makes a context selecting the new controller check it at creation. The
controller never silently degrades. Phase 2b moves certification to the separate optional capability
`IPhysicsSupportNeighborhood`, so `FootSupport` now requires that one instead.

### Support primitive

An internal Locomotion type, `FootSupport`, answers one question for one pose:

```csharp
SupportSample Find(Func<float, float, float>? groundHeight, Func<float, float, Vector3>? groundNormal,
    IPhysicsWorld? world, IPhysicsQueryLease? lease, in FootSupportQuery query);

readonly record struct FootSupportQuery(Vector2 Axis, float FeetY, float FootRadius, float ReachUp,
    float ReachDown, float CosMaxSlope);

readonly record struct SupportSample(SupportStatus Status, float Height, float HeightError, Vector3 Normal,
    StaticHandle? Static, int FeatureId, Vector3 Witness);
```

`SupportStatus` is `None`, `Walkable`, `Steep` or `Refused`. `Static` null means analytic terrain. The true
support height lies within `HeightError` of `Height`. Coordinates are the world's local frame, as `StepCore`
receives them. Candidates are proposed by bounded queries and bound to a face only by certification.

1. **Terrain.** `h(axis)` and its normal from the ground delegates, the same ones `StepCore` takes today.
   The analytic surface is exact at the axis and needs no certificate. It is steep when the normal's Y is
   below `cos(MaxSlopeRadians)`.
2. **Proposals.** Two downward probe capsules at the axis, each `CapsuleShape(radius, 0.01)` swept against
   statics from its lowest point at `feetY + reachUp + BandMargin` over
   `reachUp + reachDown + 2 * BandMargin`. `BandMargin` is 1 mm and expands proposals only, so surfaces on
   either inclusive band edge are reached inside the sweep. The axis probe has radius
   0.01, the neighborhood backend's minimum query radius, and proposes the surface under the axis. The leg probe
   has the footprint radius and proposes the first surface the disc meets, which also catches a rail or edge
   the axis probe would miss. The probes sweep with `QueryFilter.CullBackFaces`, so they pass a mesh triangle
   that does not face against the sweep, vertical ones included, as Bepu's one-sided mesh contacts do. A
   proposal is a contact pose, never a face. A probe that starts overlapped is a refused proposal at the band
   top.
3. **Certification.** Each probe's contact pose is queried with `QuerySupportNeighborhood` under the tick's
   lease and the existing 0.1 mm contact band. The backend publishes every front-facing element of every
   selected static that may lie within the band (box and hull faces, mesh triangles and tangent points on
   spheres, capsules and cylinders) with bounded normals and witnesses, plus a symmetric join matrix. Two
   polygons are joined when they may meet within the band and each lies on or below the other's plane within
   it, decided geometrically for every pair, inside one static and across statics. `SupportCertification`
   then certifies every member at the axis with outward-rounded intervals and no fixed error threshold. A
   member is walkable when its normal's lower Y bound reaches `cos(MaxSlopeRadians)`. A walkable polygon
   contributes `min(plane at axis, witness height, plane at axis of every joined walkable polygon)`, so a
   ridge supports its axis side whether or not it is split across statics, a concave valley gives its crease
   height and a vertex needs no classification. A steep polygon joined to any walkable polygon contributes
   nothing. Any other steep member contributes by the phase 1 steep rule over its joined steep polygons. A
   tangent element contributes `min(tangent plane at axis, witness height)`. A member that may face sideways
   or down contributes nothing. The neighborhood refuses only for capacity (256 elements) or a domain the
   backend cannot certify, and a refusal is a refused proposal at the probe's contact. A ray hit, sweep
   normal or nudged sample never stands in for a face. The 2026-10-07 measurements found corner rays missing
   the edge by 0.03 to 5.08 micrometres. Phase 1 certified one unique closest feature of the swept static
   instead and refused concave creases, vertices, curved primitives and ties. The
   [phase 2b spec](CONTACT-CONTROLLER-PHASE-2B-SUPPORT-NEIGHBORHOOD-2026-10-10.md) owns the neighborhood
   contract.

Contributions qualify when their certified interval overlaps the inclusive band
`[feetY - reachDown, feetY + reachUp]`, even when the midpoint lies outside it. The result is the qualifying
contribution with the highest midpoint, else `None`. Equal midpoints prefer walkable, then the witness
nearest the axis in XZ, then terrain, then the lower static handle. A member whose contribution lies wholly
above the band is not a candidate. A probe whose contact lies above the band top, or whose neighborhood has
no member supporting at or below the band top, is a refused proposal at its contact, because the surface
that stopped it can hide lower support. A refusal below the band bottom lies only in the
proposal margin and is ignored. A remaining refused proposal above the selected contribution's upper bound,
or a refusal with nothing selected, returns `Refused`, so the body never stands under a surface it could
not certify. `Height` is the selected midpoint and `HeightError` encloses both bounds from it.

Known limits:

- Before the backend sweep fix in 309c8e861, a capsule sweep could lose a nearer mesh static when another
  mesh static shared its edge. A 0.2 m leg probe over a floor mesh and a descending ramp mesh swept to
  t 0.40000004 on the floor alone and 0.42679498 on the ramp alone, yet returned the ramp with both present.
  The fix returns the nearest hit, and the mesh variants of the descending ramp and two-static ridge cases
  pass. Compound statics share the fix without a dedicated regression test (#1335).
- At an exact seam between coplanar statics, both statics are members and the owner follows the selection
  tie order, deterministic for a world built in the same order.
- A one-sided fin tilted very slightly upward within 1 cm of the axis is still hit by the probe, contributes
  below the band and hides the floor beneath, giving `None`. An exactly vertical fin is culled.
- Support query time misses its target and is the cost gate before adoption in the phases table.
- A surface lying under a higher sloped surface inside the same disc can still be hidden, because each probe
  certifies only the neighborhood at its first sweep contact. Over a floor at 0 with foot radius 0.2, a crate top
  at 0.1 spanning x 0.05 to 0.12 and a 60 degree wedge `y = 0.25 + tan60 (x - 0.2)` at the disc rim, the leg probe
  meets the wedge first with its centre at about 0.303. The crate top is not a member of that neighborhood, so
  support is Walkable at 0 where the support rule gives 0.1. This is the hidden support gate in the phases table
  ([#1347](https://github.com/APKiwiOrg/KhaozEngine/issues/1347)).
- A steep roof built from two interpenetrating box slabs pitched above 45 degrees reads Walkable at the ridge, as
  it did in phase 1.

Phase 2b closed the phase 1 limits where separate statics could not form a certified crease and where a probe
meeting a back face refused. It closed the hidden lower surface limit only for back faces.

### Shell queries

`ShellGeometry.Shape` and `ShellGeometry.Centre(feet)` place a `CapsuleShape` of the capsule radius spanning
from the knee at `feet + StepHeight` to the head, and `ShellGeometry.Validate` refuses a tuning whose span
cannot hold one diameter or cannot clear its own steepest walkable plane with the contact skin. Sweeps and
overlap use the existing `SweepCapsule` and `ComputePenetration`. Shell sweeps use `QueryFilter.StaticsOnly`.
Phase 1 adds the helper and its validation only. The passes that use it are phase 2.

### Classifier

`ContactClassifier` maps a support sample or a shell hit to the four classes above by normal, band and
volume. It reads no other state.

### Files

- No new physics file. `FootSupport` probes through the existing `SweepCapsule`, so the shape sweep seam
  of the first draft is not built.
- The capsule-feature query from `fix/low-lip-resting-proof`, integrated as reviewed: the contract,
  bounded geometry arithmetic, finite polyhedron and mesh backends, the installed-pose certificate and
  their proofs. Its eligibility predicate moves from the legacy stepper partial into
  `KhaozEngine.Locomotion/Contacts/SupportCertification.cs`. The legacy low-prop fallback and every legacy
  stepper change on that branch stay out.
- `KhaozEngine.Locomotion/Contacts/FootSupport.cs`, `SupportSample.cs`, `ShellGeometry.cs` and
  `ContactClassifier.cs`. All internal until a consumer needs them.
- Phase 2 adds `KhaozEngine.Locomotion/Contacts/GroundCore.cs` with `GroundCore`, `GroundCoreSettings`,
  `GroundFooting` and `GroundStepResult`, plus `ShellMotion.cs` and `GroundSeat.cs`. These types are internal,
  and nothing consumes them yet. The [phase 2 spec](CONTACT-CONTROLLER-PHASE-2-GROUND-CORE-2026-10-10.md) owns
  their tick contract.
- Tests in `KhaozEngine.Game.Tests` under the `KhaozEngine.Tests.Locomotion.Contacts` namespace, next to the
  existing Locomotion suites, following the test-project reference rules.

### Phase 1 suite

Every expectation is derived from the geometry, not from a stepper run. Each case runs on a box and on the
equivalent triangle mesh, and must give the same answer within float rounding.

- Flat ground, prop floor and terrain, separately and stacked. Support is the higher.
- Uniform slopes at 5, 20 and 40 degrees. Support equals the plane at the axis.
- A 4.25 cm lip with its edge inside, on and outside the disc. Lip top, lip top, ground.
- A crate top inside the disc. Crate top.
- Treads of 0.35 m under a 0.3 m and a 0.4 m capsule. The highest tread the disc reaches.
- Descending and ascending ramps starting inside the disc. Flat level and ground under the axis.
- Cracks of half and twice the disc diameter. Bridged, then the bottom or not found.
- A 2 cm rail inside the disc beside the axis. Caught by the leg probe.
- An overhang above `reachUp` and a floor below `reachDown`. Both ignored.
- A steep face under the whole disc. Found and not walkable.
- A selected query view with an excluded static. The excluded static never contributes.
- A rebased world origin. Results equal the unrebased world after translation.
- Determinism. Two identical queries return bit-identical samples.
- A world without the certification capability (`IPhysicsSupportNeighborhood` since phase 2b). `FootSupport`
  throws.
- The swimming repro: a 0.25 m bank at X 1, radius 0.25, half-height 0.75, walking at 4 m/s and 30 Hz.
  At X 0.881 the disc touches the bank top, the certified crease names the top face, and support is 0.25
  at the axis with feet XZ equal to the body's.
- A certification refusal at a proposed static leaves that static out, never a guessed plane.

### Phase 1 boundaries

No `MoveTuning` field is added until phase 2 needs it, so navigation bake identity is unchanged. No
legacy file changes. No wire change. The capsule-feature seam is public and additive, so
phase 1 rides the next minor engine version under the release ritual.

## Disposition of the #1270 branches

`fix/low-lip-resting-proof` carries the finite capsule-feature query, the legacy low-prop support fallback
and their proofs. Phase 1 integrates the feature query as this controller's certified face identity, with
both this controller and swimming's explicit path as consumers. The legacy low-prop fallback and the
stepper changes are not integrated. Swimming's earlier imports of shared arithmetic stay compatible because
the integrated arithmetic is the reviewed source they were taken from.
`fix/1270-stepper-cascade` stays pushed as evidence and is never integrated. #1270 and Grimhollow #416
stay open until phase 6 proves the resting route on the new controller.

## Risks

- **Presentation below the knee.** Legs may overlap low obstacles up to `CapsuleRadius - FootRadius`
  before stepping. Foot IK can hide it. The playtest decides the default.
- **Query cost.** The down pass runs two probe sweeps and two support neighborhood queries under a lease,
  against several sweeps in the stepper. Phase 2b made a warm `Find` allocation-free, and the cost gate in the
  phases table closes its time before adoption.
- **Analytic cliffs.** Steep analytic terrain is not in the physics world, so it cannot block the shell.
  Phase 2 blocks it through the down pass, where a rise above `StepHeight` refuses the move. Its suite
  covers a terrain cliff explicitly.
