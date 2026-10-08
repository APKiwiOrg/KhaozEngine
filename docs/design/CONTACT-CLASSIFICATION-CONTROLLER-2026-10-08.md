# Contact-classification character controller

Date: 2026-10-08, revised 2026-10-09 for ground ownership. Specifies [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438), the phase 2
direction chosen in [PHYSICS-LOCOMOTION-DESIGN-2026-08-02.md](PHYSICS-LOCOMOTION-DESIGN-2026-08-02.md).
Status: written for owner review. Phase 1 is specified in full. Phases 2 to 6 are specified at the level
of contracts and exit criteria, and each gets its own detailed spec before implementation.

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
walkable when its face normal passes `cos(MaxSlopeRadians)`.

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
5. A tick without footing ends no higher than its own vertical motion allows (#468).
6. The resolve and the ground clamp read the same surface (#468, round four).
7. A footed tick may not seat itself on ground it cannot stand on (#486).
8. Traction keeps a hysteresis band at the slope limit (#475).

## Phases

| Phase | Delivers | Exit criteria |
|---|---|---|
| 1. Contact foundation | Shape sweep seam, integrated feature query, certified support primitive, contact classifier | Phase 1 suite green, including the swimming bank repro. Legacy stepper byte-unchanged. |
| 2. Ground core | Up, side, down passes, recovery, slopes, walls, step-up, step-down, ledges | Ground suite green, including the shallow-tread runs at 2, 3 and 4 m/s and the 0.41 m ledge. |
| 3. Air and state | Jump, coyote, momentum, landing impact, commitment, steep slide, traction hysteresis | Air and slide suite green with invariants 5 to 8. |
| 4. Signals and fluids | Climb signals, step delta, support grant, facing, the swimming handoff contract | Signal suite green. Swimming's explicit grounded ticks run on this ground core. |
| 5. Switch, wire, navigation | `MoveTuning` selector, any wire fields, bake identity, traversal probe and capture on the selected controller | Both controllers selectable. Legacy bakes still load. A new-controller bake round-trips. |
| 6. Grimhollow adoption | Grimhollow opt-in, #1270 resting route, bridge Complete and 600-step proof | Engine release, game pin and playtest, each separately authorized. |

## Phase 1 design: contact foundation

### Physics seam

Add an optional capability to `KhaozEngine.Physics`, in the established optional-interface pattern:

```csharp
public interface IPhysicsShapeSweeps
{
    bool SweepConvex(PhysicsShape shape, Pose pose, Vector3 direction, float maxDistance,
        out SweepHit hit, QueryFilter filter = default);
}
```

It accepts `SphereShape`, `CapsuleShape`, `CylinderShape` and `BoxShape` and throws
`NotSupportedException` for any other shape. The Bepu world and its selected query views implement it with
the same filter, exclusion, origin and lease rules as `SweepCapsule`. `IPhysicsWorld` itself is unchanged,
so existing implementers and decorators keep compiling. `FootSupport` throws `NotSupportedException` for a
non-null world without the capability, and phase 5 makes a context selecting the new controller check it at
creation. The controller never silently degrades.

### Support primitive

An internal Locomotion type, `FootSupport`, answers one question for one pose:

```csharp
SupportSample Find(Func<float, float, float> groundHeight, Func<float, float, Vector3>? groundNormal,
    IPhysicsWorld? world, Vector2 axis, float feetY, float footRadius, float reachUp, float reachDown,
    float cosMaxSlope);

readonly record struct SupportSample(bool Found, float Height, Vector3 Normal, SupportSource Source,
    bool Walkable);
```

`SupportSource` names analytic terrain or a `StaticHandle` with its certified finite feature. Candidates
are proposed by bounded queries and bound to a face only by certification.

1. **Terrain.** `h(axis)` and its normal from the ground delegates, the same ones `StepCore` takes today.
   The analytic surface is exact at the axis and needs no certificate.
2. **Proposals.** A downward ray at the axis proposes the surface under the axis. One downward
   `SweepConvex` of a thin cylinder of the footprint radius proposes the highest point in the disc, which
   also catches a rail or edge that a ray would miss. A proposal is a hint about which static and where,
   never a face.
3. **Certification.** Each proposed static is queried with `QueryCapsuleFeature` under the tick's lease,
   with the leg capsule (footprint radius, spanning the step band) at the proposed pose and the existing
   0.1 mm contact band. A `Complete` result names the incident faces with certified normals and witness.
   The supporting face is chosen by the existing eligibility predicate: every incident face of a face or
   open-boundary patch supports it, and a convex crease has exactly one supporting top. Its plane and
   witness give `min(plane at axis, highest point in disc)`. Any refusal (`Unsupported`, `Ambiguous`,
   uncertain sign) contributes nothing. A ray hit, sweep normal or nudged sample never stands in for a
   face. The 2026-10-07 measurements found corner rays missing the edge by 0.03 to 5.08 micrometres.

Candidates outside `[feetY - reachDown, feetY + reachUp]` are discarded. The result is the highest walkable
contribution, else the highest steep one flagged not walkable, else not found. Ties keep the lower source
index, so the order of queries fixes the answer.

Known limit: a surface lying under a higher sloped surface inside the same disc is not proposed, because
the disc sweep stops at the higher one. The phase 1 suite pins the case, and phase 2 decides whether a
second proposal below the first is needed.

### Shell queries

`ShellPose(feetY)` places a `CapsuleShape` of the capsule radius with its bottom at the knee. Sweeps and
overlap use the existing `SweepCapsule` and `ComputePenetration`. Phase 1 adds the helper and its
validation only. The passes that use it are phase 2.

### Classifier

`ContactClassifier` maps a support sample or a shell hit to the four classes above by normal, band and
volume. It reads no other state.

### Files

- `KhaozEngine.Physics/IPhysicsShapeSweeps.cs`, with the Bepu implementation beside the existing sweeps and
  forwarding in the selected query view, serialized with the swimming owner of that file.
- The capsule-feature query from `fix/low-lip-resting-proof`, integrated as reviewed: the contract,
  bounded geometry arithmetic, finite polyhedron and mesh backends, the installed-pose certificate and
  their proofs. Its eligibility predicate moves from the legacy stepper partial into
  `KhaozEngine.Locomotion/Contacts/SupportCertification.cs`. The legacy low-prop fallback and every legacy
  stepper change on that branch stay out.
- `KhaozEngine.Locomotion/Contacts/FootSupport.cs`, `SupportSample.cs`, `ShellPose.cs` and
  `ContactClassifier.cs`. All internal until a consumer needs them.
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
- A 2 cm rail between ray samples. Caught by the disc sweep.
- An overhang above `reachUp` and a floor below `reachDown`. Both ignored.
- A steep face under the whole disc. Found and not walkable.
- A selected query view with an excluded static. The excluded static never contributes.
- A rebased world origin. Results equal the unrebased world after translation.
- Determinism. Two identical queries return bit-identical samples.
- A world without `IPhysicsShapeSweeps`. `FootSupport` throws.
- The swimming repro: a 0.25 m bank at X 1, radius 0.25, half-height 0.75, walking at 4 m/s and 30 Hz.
  At X 0.881 the disc touches the bank top, the certified crease names the top face, and support is 0.25
  at the axis with feet XZ equal to the body's.
- A certification refusal at a proposed static leaves that static out, never a guessed plane.

### Phase 1 boundaries

No `MoveTuning` field is added until phase 2 needs it, so navigation bake identity is unchanged. No
legacy file changes. No wire change. The shape sweep and capsule-feature seams are public and additive, so
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
- **Query cost.** The down pass runs one ray, one disc sweep and up to two feature certifications under a
  lease, against several sweeps in the stepper. Phase 2 measures it with the existing allocation and timing
  guards before adoption.
- **Analytic cliffs.** Steep analytic terrain is not in the physics world, so it cannot block the shell.
  Phase 2 blocks it through the down pass, where a rise above `StepHeight` refuses the move. Its suite
  covers a terrain cliff explicitly.
