# Task 4 certified capsule sweep prerequisite

Status: source-audited design completion for the existing approved G1a collision prerequisite.
No implementation or runtime repro is claimed. This does not open native G1b or change legacy
locomotion. The [generic plan](../superpowers/plans/2026-10-06-swimming-generic-foundations.md)
and accepted F3 require conservative swept completeness, not only complete point contacts.

## Source facts

The engine pins BepuPhysics 2.4.0. `BepuPhysicsWorld.Queries.cs` calls the default `Simulation.Sweep`
overload and exposes only a boolean plus `SweepHit`. The nearest-hit handler keeps one upper time
and discards the lower bracket. No termination or numerical completeness result reaches callers.

In pinned [Simulation_Queries.cs](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Simulation_Queries.cs),
the default progression distance is 0.1 times its shape-size estimate, convergence distance is
0.00001 times that estimate and the iteration limit is 25. For an upright capsule with radius
0.3 m and half-height 0.75 m, the progression distance is 0.03 m. The capsule's expansion data
is defined in [Capsule.cs](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Capsule.cs).

Pinned [ConvexSweepTaskCommon.cs](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/SweepTasks/ConvexSweepTaskCommon.cs)
uses forced progression to bridge gaps between sample-safe intervals. Its source explicitly allows
small intersections to be skipped. Termination can also occur because the interval stops shrinking
or reaches the iteration limit. The final boolean reports whether a sampled intersection was found.
The raw convex task also returns the remaining lower and upper time bracket, but the simulation-wide
dispatcher does not publish it. Missing registered sweep tasks are skipped by that dispatcher.

These are source facts, not a measured claim that a particular Grimhollow bridge or released route
failed for this reason. They do show that the existing boolean is insufficient as F3's complete-clear
certificate. Task 2 contact enumeration at start/end cannot repair an unproved path between them.

## Chosen direction within G1a

Add an optional certified sweep capability in Physics and implement it on the real Bepu owner and
restricted query view. Keep the existing `IPhysicsWorld.SweepCapsule` contract and behavior unchanged.
The explicit mover consumes the new capability under the existing read lease. Missing capability or
uncertifiable work returns an unresolved whole-step outcome with no accepted prefix.

| Option | Complete-clear distinction | Legacy compatibility | Single backend implementation | Total |
|---|---:|---:|---:|---:|
| Optional certified capability over real Bepu leaf sweeps | 9 | 10 | 9 | 28 |
| Retune and continue using the existing boolean | 3 | 3 | 9 | 15 |
| Separate generic analytic solid solver | 7 | 10 | 2 | 19 |

The optional capability keeps termination evidence while preserving existing callers. Retuning the
boolean cannot express a remaining uncertain interval. A separate solid solver would duplicate
backend geometry and diverge between runtime and navigation.

## Proposed result and ownership

The proposed ownership and result shape below will be pinned to exact signatures before code or
a missing-capability RED:

- Physics owns `IPhysicsCapsuleSweep` and an immutable `CapsuleSweepResult` in a cohesive
  `CapsuleSweep.cs` file. Input is an upright capsule, pose, finite displacement and the existing query
  filter. The displacement fixes metre units without a caller-supplied direction normalization.
- Result status is `Unresolved = 0`, `Clear` or `Hit`. A Clear result certifies the whole displacement.
  A Hit result retains a conservative clear-through distance, an impact-distance upper bound and a
  certified spatial error. Unresolved exposes no usable distance or hit prefix.
- A nearest sweep normal is not a complete active constraint set. At the certified earliest bracket,
  the resolver must still call the existing complete contact query, include all simultaneous contacts
  within the accepted skin/error budget, project against the whole set and retrace each correction.
- Bepu owns the actual shape/leaf traversal and raw sweep calls in `BepuPhysicsWorld.CapsuleSweep.cs`.
  QueryView forwards the exact captured exclusion selection. No MapDoc or water-region query is added.
- `MovementQueryLease.Solids.cs` validates readiness, frame/currentness and the whole swept scope before
  forwarding. `MovementCapsuleResolver.cs` consumes the result. Native adapters remain geometry/medium
  producers and do not implement a second collision solver.

## Required backend proof before implementation acceptance

World authoring reviewed immutable `17c191c2c` and found no native ownership conflict. That was a
semantic ownership review, not numerical verification, exact API approval or G1b acceptance.
Its boundary conditions are part of this completion:

- Sweep and active-contact queries use the exact captured restricted PhysicsView, exclusions,
  mobility/filter and physical read gate. Check lease currentness before and after. Configured callers
  cannot escape to the unrestricted source world or fall back to the legacy boolean.
- Clear covers every relevant supported leaf over the complete displacement. Missing tasks,
  unsupported geometry, incomplete brackets or one unproved child refuse the whole result.
- Endpoint inclusion and start overlap are explicit result invariants. Zero or tangent results do
  not establish a valid placement by themselves.
- Numerical, work and capability policy belongs in query/navigation identity. Native solids must prove
  that policy at G1b. Existing contact caps and R2's 65536-face patch limit are not a sweep certificate.

### Result invariants to pin in the exact API

The proposed distances are metres along the supplied displacement, never coordinates or timing in
seconds. A Hit's clear-through distance is a conservative lower bound before the possible impact,
not a safe inclusive placement at that bound. The impact distance is the upper end of the certified
earliest bracket and is a query pose, not a position the caller may commit. At lower distance zero,
the certified forward-clear interval is empty. Initial overlap or closed tangency must be resolved
using complete current contacts, not the first sweep normal or a guessed safe t=0.

A Clear result includes both request endpoints and every point between them, within its explicitly
declared numerical domain. A zero-displacement Clear result therefore requires a complete certified
stationary query. A zero-displacement Hit can describe touch/overlap but cannot authorize movement or
placement. The result defaults to Unresolved, and an unresolved result exposes no usable prefix.
Closed endpoint/tangency classification must account for the backend tester differences below.

The exact constructor/factory validation, distance rounding and combined sweep/contact error accounting
are still being pinned. No caller should infer a stronger placement or native coverage guarantee from
these proposed result fields. Final invariants and numerical domain return to world authoring if they
change a shared boundary before implementation.

### Distance-tester audit checkpoint

Pinned [CapsuleBoxDistanceTester](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/SweepTasks/CapsuleBoxDistanceTester.cs)
uses a strict negative-distance intersection test, while
[CapsulePairDistanceTester](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/SweepTasks/CapsulePairDistanceTester.cs)
includes zero distance. Their boolean flags therefore do not share closed-tangency semantics. The
capsule-pair normal also divides by segment distance, so coincident-axis results require explicit
handling rather than trusting a finite normal at an initial hit.

Pinned [GJKDistanceTester](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/SweepTasks/GJKDistanceTester.cs)
terminates on nonprogress or a progress tolerance scaled by simplex size. It then adjusts the returned
distance by a containment margin that can include the shapes' real support margins. Neither that
margin nor the existing point-contact error estimate is automatically a bound on sweep error.
The supported geometry/scale and conservative numerical certificate still need to be derived and
tested. No guessed tolerance or assumed all-shape guarantee has been selected from this audit.

1. Enumerate the complete candidate/convex-leaf set intersecting the conservative swept bounds under
   the physical gate, with the same view/mobility filters and explicit work limits. Unknown shape/task,
   unsupported scale or filter, over-capacity or uncertain geometry must refuse the whole query.
2. Use the raw convex sweep task with forced progression disabled. Retain each bracket and distinguish
   a proved disjoint interval from no intersection found before convergence/budget exhaustion.
   A false boolean with an unresolved bracket is not Clear. Bounding-sphere early exits also need an
   explicit conservative certificate rather than inferring meaning from an ambiguous default bracket.
3. Nonconvex parent sweeps cannot silently discard uncertain child results. Enumerate supported convex
   leaves explicitly and aggregate the earliest complete bracket. One unresolved child invalidates a
   tentative result from every other child. This uses actual Bepu geometry, not synthetic collision data.
4. Derive and validate a sweep-specific numerical budget for the supported local envelope. The contact
   query's numerical budget is not automatically a sweep certificate. Iteration exhaustion, nonshrinking
   brackets or error beyond the accepted skin/contact margin return Unresolved.
5. Bound candidate/leaf work using the existing 4096 limits. Mesh/contact smoothing limits from Task 2
   still apply when gathering the active constraints. No native partition assumption is permitted.
   Iteration/error policy affects connectivity and must enter the backend/profile policy identity.
6. Start overlap and zero displacement use complete overlap facts and cannot manufacture a safe t=0
   placement. An unresolved initial or final placement leaves the original framed state uncommitted.

The pinned raw algorithm and each supported distance tester still need the focused numerical/source
audit for item 4. A finite suite will validate a declared supported domain, not prove arbitrary geometry.
If the real backend cannot meet the accepted contract, report the concrete unsupported condition and
retain refusal. Do not weaken F3, increase an unapproved behavior tolerance or relabel unresolved as clear.

## Scoped files and verification preparation

Expected production files are the new Physics result/capability, the new Bepu query concern,
QueryView forwarding and the existing leased solid-query adapter. Any shared convex-leaf extraction
refactor must keep the already-validated complete contact path behavior and run its affected fixtures.
No existing legacy solver, game rule, native sampler, protocol, package pin or shared feed changes here.

Before production edits, pin the final result invariants, numerical/work limits and finite fixture
inventory against the actual raw API. The planned facts cover thin collision intervals, complete clear,
tied impacts in reversed insertion order, initial overlap/tangency, live-view newly added walls,
compound children, explicit unsupported/refused work, incomplete bracket termination and both
supported-frame edges. They must exercise the real backend. Source characterization is not RED.

The first requested run will be one exact finite missing-capability fixture after authoring, followed
by separately granted GREEN and named affected regressions. No command is authorized by this note.
Scheduling remains with pivot, with at most two explicitly assigned compatible slots. Native G1b,
whole-branch review, full final verification and release/adoption gates remain unchanged.
