# Task 4 certified capsule sweep prerequisite

Status: source-audited design completion for the existing approved G1a collision prerequisite.
The value/interface seam is implemented with eight passing contract facts. No certified backend
implementation is claimed. This does not open native G1b or change legacy
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

### Exact value and capability surface

The value/read boundary is fixed independently of the still-unproved backend numerical domain:

```csharp
public enum CapsuleSweepStatus { Unresolved = 0, Clear = 1, Hit = 2 }

public readonly record struct CapsuleSweepResult
{
    public CapsuleSweepStatus Status { get; }
    public float ClearThroughDistance { get; }
    public float? ImpactDistance { get; }
    public float CertifiedErrorMetres { get; }
    public bool IsComplete { get; }
    public bool IsValid { get; }

    public CapsuleSweepResult(CapsuleSweepStatus status, float clearThroughDistance,
        float? impactDistance, float certifiedErrorMetres);
}

public interface IPhysicsCapsuleSweep
{
    CapsuleSweepResult SweepCapsuleCertified(CapsuleShape capsule, Pose pose,
        Vector3 displacement, QueryFilter filter = default);
}
```

All distances are finite and nonnegative. Unresolved requires zero clear-through distance, null
impact distance and zero certified error. Default therefore represents an unusable refusal. Clear
requires null impact distance. The consumer additionally checks its clear-through distance against
the full requested displacement length. Hit requires a finite impact distance greater than or equal
to the lower clear-through bound. A zero/zero Hit is valid data but provides no positive clear prefix
or placement permission. Unknown enum values are invalid. IsComplete is true only for valid Clear/Hit.

The Physics value validates numeric structure, not the truth of a backend certificate. Its error
field accepts any finite nonnegative value. The explicit locomotion consumer separately enforces
F3's 0.001 m accepted error ceiling and validates distances against its request, captured scope and
lease. This distinction avoids claiming that constructing a value proves a geometry domain.
No normal, source-world escape, native identity, serialized pointer or world mutation is introduced.

This slice comprises `KhaozEngine.Physics/CapsuleSweep.cs` and eight `CapsuleSweepValueTests` facts.
The tests cover defaults, clear/hit values, zero-hit meaning, refusal-prefix rejection, malformed
metrics, interval ordering and the optional interface signature. A compiled missing-type RED is
required before the value/interface implementation. No Bepu implementation or numerical certificate
is claimed by that slice. Real backend implementation remains behind the proof obligations below.

Ownership of this exact value surface and the later backend remains:

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

Constructor validation is fixed in the value section above. Backend distance rounding, combined
sweep/contact error accounting and the supported numerical domain still need proof. No caller should infer a stronger placement or native coverage guarantee from
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

Before pinning that numerical contract, a test-only `CapsuleSweepCharacterizationTests` fixture now
prepares 12 private one-box worlds: nine grazing intersections at three depths and three X offsets,
one head-on crossing, one separated control and one closed-endpoint contact. It compares the current
public sweep with the pinned library's explicit zero-progression/64-iteration overload, then queries
complete contacts at the independently known witness pose. Each case emits one bounded diagnostic row.

The raw-library diagnostic obtains the private fixture world's simulation by reflection, under its
physical read lease. The world has one static, no dynamics and no exclusions. Both legs select that
same static set. This is deliberate test-only inspection, not a permitted production escape from a
restricted PhysicsView. No configured native caller, runtime solver or production query is changed.
The first granted command stopped before test discovery with CS0118 at the diagnostic's Simulation
type name, which resolved to an enclosing namespace. No case or diagnostic row ran. The secondary slot
was explicitly released. The fixture qualifies the pinned Bepu type with the existing repository
alias convention. Cases and assertions remained unchanged. The separately granted executable run
passed all 12 cases, zero failures/skips, exit 0 in `/tmp/swim-sweep-characterization2.log`.
The secondary slot was explicitly released, with no duplicate invocation or extra run.
The earlier compilation failure remains distinct from capability RED or sweep behavior evidence.
Legacy hit/miss outcomes and closed-endpoint boolean outcomes are observations, not certificates of
clearance. The separated and contact witnesses and the zero-progression interior crossings are asserted.
This does not establish a sweep-wide numerical bound or the final optional capability implementation.

The [committed diagnostic rows](../verification/2026-10-07-swim-sweep-characterization.json) preserve
all inputs, observed sweep booleans/distances, analytic crossing intervals and contact controls.
Both sweep variants detected all nine grazing and the head-on interior crossings. This fixture did
not reproduce a grazing miss. Both reported no hit for the separated control, whose contact query
was empty. At the exact closed endpoint, both sweeps reported no hit while the complete contact query
reported one contact. Disabling forced progression alone therefore does not supply the required
closed-endpoint semantics. These results do not establish an all-shape numerical certificate or
attribute any Grimhollow route/support failure to sweeping.

The next prepared diagnostic is eight `CapsuleSweepBracketCharacterizationTests` cases: head-on,
grazing, separated and closed-endpoint scenes at declared outer iteration budgets 1 and 64.
Each case calls the pinned real convex task once, retaining its raw t0/t1 bracket, and performs one
complete-contact witness query. It adds observations unavailable through the simulation-wide API.
The one-iteration outcomes do not assert which termination branch fired. A noninverted miss remains
an unfinished-search observation, never a complete-clear certificate. The 64-iteration interior
crossings and independent geometry/contact controls remain assertions.

This requires unsafe compilation in the nonshipping Game.Tests project solely to pass the actual
capsule and registered shape-data pointers to the public raw Bepu task. The pointers are local to
the one-static private world and physical read lease, with no escaped pointer or world mutation.
The same source-world, no-dynamics/no-exclusions restrictions as the first diagnostic apply.
No production package, query API, native sampler or runtime pointer access is added. The test file
and test-project property passed peer source inspection and one granted eight-case run, exit 0 with
eight passes and zero failures/skips in `/tmp/swim-sweep-brackets.log`. The secondary slot was
explicitly released. No old twelve-case repeat or additional invocation ran. This is characterization,
not capability RED or a repeated confidence loop.

The [committed raw-bracket rows](../verification/2026-10-07-swim-sweep-brackets.json) show false results
with noninverted brackets at budget 1 for both actual interior crossings and the separated scene.
At budget 64, the interior crossings returned true with narrow brackets, while the separated scene
returned false with an inverted bracket. The exact endpoint returned false with an inverted bracket
at both budgets despite its complete contact witness. Inversion alone is not a closed-path certificate.
The grazing budget-64 lower value is about 0.31 micrometres beyond the analytic entry, so a raw lower
value is not a strictly conservative distance without numerical accounting. The declared budget is
an input, not evidence of which termination branch fired. No broad numerical domain is inferred.

After that evidence is inspected, pin the exact API/domain and author its missing-capability fixture,
followed by separately granted GREEN and named affected regressions. No command is authorized by this note.
Scheduling remains with pivot, with at most two explicitly assigned compatible slots. Native G1b,
whole-branch review, full final verification and release/adoption gates remain unchanged.

## Value-contract RED evidence

The granted `CapsuleSweepValueTests` Release run compiled and discovered all eight facts. All eight
failed at `ResultType` because `CapsuleSweepResult` is absent, with zero passes/skips and exit 1 in
`/tmp/swim-sweep-values-red.log`. There was no compilation or setup failure. The secondary slot was
explicitly released. This is the expected missing-capability RED, not backend geometry evidence.

World authoring reviewed the exact value section at `89c90fdde` and found no semantic conflict.
`IsComplete` describes valid Clear/Hit structure only. The lease must independently reject errors
above 0.001 m, validate both Hit distances against the requested displacement extent, and validate
Clear against the entire closed path, scope and currentness. Zero displacement requires a complete
stationary certificate. Unsupported pose orientation or numerical domain must refuse. This review
does not approve a backend numerical domain or native G1b.

The unchanged eight assertions were converted to direct types after RED. The granted Release GREEN
passed all eight, zero failures/skips, exit 0 in `/tmp/swim-sweep-values-green.log`. The secondary
slot was explicitly released. Production is limited to `KhaozEngine.Physics/CapsuleSweep.cs`.
There is no Bepu capability implementation or leased sweep forwarding yet. These tests establish
value validation and the optional interface signature only. Whole-branch verification remains open.

## Next bounded consumer proof

After the value slice, the existing leased-solid adapter needs an internal sweep operation taking
`in MovementBodyQuery body`, `Vector3 displacement` and an `out CapsuleSweepResult`. It returns
`MovementAvailability`, leaving the output at default unless the whole acceptance check succeeds.
This is consumer validation under the existing lease, independent of the still-unimplemented backend.
It must forward only to `_view` when that exact object implements `IPhysicsCapsuleSweep`.

Readiness, valid body/world and finite displacement/end pose are checked before backend access.
The certified scope must contain the entire swept capsule AABB, expanded by the existing 2 mm
skin/error envelope. The request uses the actual capsule centre, upright pose and existing default
filter. Both currentness checks bracket the backend call. There is no source-world fallback.

Compute displacement length in double from the float components to avoid float squared-length
overflow. A backend value uses float metre distances, so complete Clear extent is the correctly
rounded float representation of that length. This representation rule does not make the endpoint
optional. The backend still certifies the original vector's complete closed path. Hit bounds must
lie in the actual request extent. The initial unit-axis consumer slice compared the represented
length. The non-axis regression below corrects the demonstrated rounded-up overrun by comparing Hit
bounds against the double length instead. This is not a mathematical norm enclosure, and the full
backend numerical contract remains required before physical-prefix use. Conversion uncertainty belongs in the backend's declared
numerical budget, not a new consumer tolerance. A nonrepresentable request length is refused.
The exact distance-rounding rule remains part of the backend numerical contract before its acceptance.

Proposed finite fixture inventory, using a scripted capability over an actual leased restricted view:

1. Clear forwards the exact centre, capsule dimensions and displacement and returns the value.
2. Ordered Hit in the request is accepted as data without moving a body.
3. Zero displacement Clear and zero Hit retain closed stationary semantics.
4. Short Clear coverage is invalid and publishes no prefix.
5. Overlong Clear coverage is invalid and publishes no prefix.
6. Hit upper bound beyond the request is invalid and publishes no prefix.
7. Both Hit bounds beyond the request are invalid and publish no prefix.
8. Error above 1 mm is unresolved, including the next representable value above the cap.
9. Error exactly at the cap is structurally accepted, without proving backend accuracy.
10. Default unresolved backend output remains unresolved with an empty out value.
11. Missing capability refuses without touching the underlying owner's legacy sweep.
12. Swept endpoint outside certified scope refuses before the backend call.
13. Cold unready selection refuses before the backend call.
14. A stale pin before the call prevents backend access.
15. A pin becoming stale during the call discards a structurally complete returned result.
16. Backend unsupported-domain refusal leaves output empty and the lease releasable.

These fixtures cannot establish geometric completeness, numerical accuracy or native support.
They prevent a later backend result from bypassing the accepted request/read boundary. Tests will
precede adapter implementation and need their own bounded compute grant. The prepared consumer fixtures and their first-run classification follow below.
The consumer implementation and focused evidence are recorded below.

The first granted 16-case `MovementCapsuleSweepLeaseTests` run compiled but all cases failed their
acquisition control, expected Known and actual Invalid, before looking up the missing sweep method.
`/tmp/swim-sweep-lease-red.log` exited 1, zero passes/skips. This is a fixture setup failure, not
capability RED. Source inspection found that the new selected-view decorator omitted the existing
`IPhysicsCapsuleContacts` capability required by `MovementEnvironmentContext.TryAcquire`. The real
wrapped Bepu view supplies it. Correct only that decorator's capability forwarding and preserve
every consumer assertion before a separately granted attempt. No production sweep method exists.

The corrected decorator forwards the mandatory existing complete-contact capability to its exact
inner view. A separately granted run of the unchanged 16 facts compiled, passed acquisition controls
and failed all 16 at the absent `QuerySolidSweep` lookup, zero passes/skips, exit 1 in
`/tmp/swim-sweep-lease-red2.log`. No acquisition-constructor failure remained. This is valid
missing-method RED. The secondary slot was explicitly released before consumer implementation.

## Candidate independent swept-clear certificate

This is a mathematical design candidate, not an implemented or accepted backend domain. It addresses
why a raw Bepu miss or inverted bracket cannot itself certify Clear. No geometry helper is copied
from the separately owned feature-query implementation. Shared arithmetic ownership is being
coordinated before either lane creates a second interval/transform kernel.

For the upright capsule, let A and B be its axis endpoints, r its radius and D its requested
displacement. Its complete swept volume is the convex hull of the starting and ending capsules.
For any finite nonzero direction n, its minimum projection is

```text
min(dot(n, A), dot(n, B)) - r * length(n) + min(0, dot(n, D))
```

For a convex solid leaf S, subtract its maximum support projection in n. If an outward-rounded
enclosure proves this gap strictly positive, the whole closed swept capsule and S are disjoint.
The direction may come from approximate Bepu output because the certificate verifies the projection
independently. Correctness does not require that proposed direction to be the exact separating
normal. Zero-containing or negative gap intervals prove nothing and cannot become Clear.

Every relevant convex leaf must receive a certificate over the entire request, or over a finite
cover of closed subintervals with no gaps. A thin collision interval cannot be skipped by sample
spacing. The derivation follows from support projections of a convex hull, rather than a claim about
Bepu's termination branch. A conservative certificate against a triangle's full finite geometry is
sufficient for disjointness even when the collision surface is one-sided. It is not sufficient for
classifying a Hit or a back-face interaction.

Still required before implementation acceptance:

- Enclosures must cover actual installed transforms, shape support and float output conversion.
  Arithmetic helper reuse does not import another query's error ceiling or domain.
- Candidate and child exclusion need independent conservative bounds. An unproved broadphase or
  child-tree bound cannot hide geometry before certificate evaluation.
- Hit needs an independently proved contact/intersection witness and earliest-bracket construction.
  A failure to find a separating axis is not a Hit. Initial overlap, closed tangency and one-sided
  mesh behavior remain explicit cases, with no arbitrary safe placement.
- A lower prefix certificate and upper witness must enclose the earliest event across all leaves.
  Complete active contacts at the selected query pose remain a separate requirement.
- Supported shape/transform ranges, finite axis/interval/candidate work limits and numeric error
  accounting must be pinned in backend/profile identity. Unsupported or exhausted work refuses.

This direction may use existing raw sweeps as proposals without pretending they carry conservative
bounds. It does not authorize a second native sampler, a local stress proof, a changed F3 tolerance
or an implicit dependency on the low-lip face query's future runtime implementation.

## Initial leased-consumer GREEN

`MovementQueryLease.Sweeps.cs` implements the optional selected-view consumer after valid RED.
The unchanged 16 facts passed under the separately granted Release run, zero failures/skips, exit 0
in `/tmp/swim-sweep-lease-green.log`. The secondary slot was explicitly released. This covers the
original unit-axis/scripted acceptance, scope/readiness/currentness and atomic out-result rules.
It does not establish a backend certificate or the outstanding non-axis distance-rounding boundary.
The latter is an explicit next test-first correction before any physical-prefix use.

Shared arithmetic ownership is agreed directly with pivot: its internal
`KhaozEngine.Physics.Bepu/BoundedGeometryArithmetic.cs` will own outward double intervals, bounded
dyadic predicates and represented-transform enclosures. Feature-specific membership stays in its
`CapsuleFeaturePredicates.cs`. No helper implementation or diff is available yet. Swimming retains
continuous swept coverage, candidate completeness, bracket and error policy, and will review the
actual helper interface before consuming it. No numerical domain or certificate is inherited.

## Non-axis consumer boundary RED

Eight new `MovementCapsuleSweepNumericTests` compiled and ran under a separate grant. Seven controls
passed and `HitAtARoundedUpNormCannotExtendBeyondTheActualRequest` failed, expected Invalid and
actual Known, with zero skips and exit 1 in `/tmp/swim-sweep-numeric-red.log`. For D=(1,2,0), the
independent squared-value assertion proves that the float representation of sqrt(5) is greater than
the exact request norm. The current Hit guard compared against that rounded-up float and accepted
the overlong bound. All acquisition controls passed. The secondary slot was explicitly released.
This is a consumer extent defect, not evidence about any physical backend sweep or feature query.

The consumer now compares Hit bounds to its double-precision length rather than the rounded float
encoding reserved for Clear. The separately granted numeric GREEN passed all eight facts, then the
conditional no-build affected run passed the original 16 consumer facts, both zero failures/skips
and exit 0. Logs are `/tmp/swim-sweep-numeric-green.log` and `/tmp/swim-sweep-lease-regressions.log`.
Sources were unchanged between commands and the secondary slot was explicitly released. No old
value or full-suite rerun was included. This fixes the demonstrated float rounding overrun only.
A mathematical norm enclosure, supported backend geometry and continuous sweep certificate remain
separate obligations, not consequences of these finite tests.

## Landed lease reconciliation

The lease prerequisite landed independently on main at `c3c076a22`, staged 20.29.0. Swimming merged
that source at `c65946989`, retaining its contacts and sweep work and taking the main lease-test
formatter change. All Physics/Bepu/Locomotion runtime files remained identical to pre-merge swimming
source. The separately granted five-fixture Release check passed 82 cases, zero failures/skips,
exit 0. The [reconciliation evidence](../verification/2026-10-07-swimming-main-reconciliation.json)
records the exact tested commit, command and log hash. The secondary slot was explicitly released.
This is focused reconciliation, not final whole-branch or native/backend certification.

## Independent active-constraint projection slice

While the separately owned backend arithmetic kernel is being implemented, Task 4 can implement
its generic displacement projection without importing Bepu geometry. This is the existing F3
requirement to consider the whole active constraint set, rather than successively choosing one
convenient wall. It is not a swept-clear certificate or permission to commit a position.

The internal helper takes a requested displacement and all active outward unit normals, returning
a candidate displacement or explicit refusal. In three dimensions, projection onto the homogeneous
half-space intersection has zero, one or two independent active planes unless the result is zero.
Evaluate the unchanged request, each plane projection, each pair's intersection-line projection,
and zero. Validate each candidate against the whole set and choose the closest one to the request.
Canonical normal ordering makes ties independent of insertion order. Opposing parallel planes are
handled by the single-plane candidates, not division by a zero cross product.

Exact duplicate normals are coalesced after validation. Bound input to the existing 16,384 solid
plus 256 water contacts, and use at most 64 distinct normals for this first policy. More distinct
normals returns CapacityExceeded, never a subset. That operational bound must enter the eventual
profile/solver policy identity and be proved at native G1b. With 64 unique normals there are at
most 2,082 candidates including the unchanged request and zero. No local stress proof is implied.

Use double intermediate vector algebra and track float output rounding. The helper's candidate
still requires complete solid and water retracing. Arithmetic uncertainty or output rounding above
the existing 1 mm query skin refuses, rather than enlarging a physical tolerance. This vector-algebra
helper is in Locomotion and does not duplicate the backend interval/transform kernel or certify
geometric normals. No body, support selection or physics object changes during projection.

Initial finite tests cover unchanged free/away/tangent movement, one inward wall, two-wall corner,
opposing planes, a closed corner, a nonorthogonal plane, reversed input order, duplicate normals,
invalid normals and explicit capacity refusal. Tests precede implementation and require their own
bounded grant. The complete resolver and runtime/nav integration remain separate following work.

The initial 12 projection facts compiled and failed at the absent helper type, zero passes/skips,
exit 1 in `/tmp/swim-constraint-projection-red.log`. No setup or compile failure occurred. The
secondary slot was explicitly released before implementation. Comparison slack in the fixture is
not permission to move through a plane. Candidate retracing remains mandatory.

## Shared arithmetic primitive compatibility checkpoint

Read the complete first primitive slice in the owned low-lip worktree:

- `BoundedGeometryArithmetic.cs`, SHA-256 `f9f2b5cba0b60ad40d394c90c9dc204c6e98d0c8ed77ff996d789d1e8d94a365`.
- `BoundedGeometryArithmeticTests.cs`, SHA-256 `d3bdeb5eeae7e6bec4f7f3343149bd08a839f6b409f88ce789c83be9a6f7b7ac`.
- `CAPSULE-FEATURE-NUMERICAL-PROOF-2026-10-07.md`, SHA-256 `ef2468a223a68d5ff25bad0b4f4885902b472cfcc195831fc462cb766e5d318b`.

No blocking primitive compatibility finding in its stated IEEE binary64 domain. The finite outward
endpoint and unresolved rules are coherent. Four-corner multiplication/division enclosures cover
the continuous zero-free denominator domain. Square handles zero crossing, and square-root
publication is gated on exact dyadic squared-endpoint inequalities. Predicate operand growth is
checked before shifting, with bounded initial mantissa products. `Exact`/`Enclose` assert the
supplied represented numbers, not earlier measurement or transformation accuracy.

This source review did not rerun or independently verify the reported 14-case runtime result. It
does not accept transforms, finite-feature membership, candidate enumeration, a vector-norm
enclosure for the generic consumer, or geometric sweep error ceilings. Reuse waits for the exact
committed/tested source handoff, preserving one arithmetic implementation.

## Next swept-support kernel fixture inventory

After the immutable arithmetic handoff, the first sweep-specific kernel will enclose the projection
gap derived above. It accepts the actual represented upright capsule centre, radius and cylindrical
half-length, displacement, a nonzero finite separating-axis proposal and an already enclosed maximum
solid support projection. The support interval is a premise whose installed-geometry derivation is
owned by the later backend adapter. This kernel neither invents that premise nor labels a nonpositive
gap as a Hit. Only a resolved strictly positive lower gap can prove separation along the supplied axis.

Ten finite facts are prepared as the next test inventory, before implementation:

1. Positive gap for a capsule whose entire sweep stops before a plane.
2. Opposite axis/displacement signs choose the correct path endpoint.
3. A thin interior crossing cannot become Clear from clear start/end samples.
4. Exact closed tangency has no strictly positive lower gap.
5. A non-unit axis preserves the separation classification without guessed normalization.
6. The cylindrical half-length participates in a vertical projection.
7. Zero displacement retains stationary full-capsule extent.
8. Reversing the same swept volume preserves the enclosed mathematical minimum.
9. An unresolved support premise and zero axis cannot produce a certificate.
10. Non-finite or malformed capsule/path inputs refuse.

Use dyadic inputs with independently written exact expected gaps. Tests must inspect enclosing bounds
and refusal, not call the production calculation to construct expectations. There is no geometric
query, native sampler, physical mutation or automatic compute run in this preparation.

The projection implementation passed the unchanged 12 facts under its separately granted Release
GREEN, zero failures/skips, exit 0 in `/tmp/swim-constraint-projection-green.log`. The secondary
slot was explicitly released. It enumerates the complete candidate set above under canonical normal
ordering, returning no output on invalid/capacity/uncertain arithmetic. `PolicyVersion = 1`, the
input/distinct caps and arithmetic screen require follow-through in the eventual solver/profile
identity. Its 64 binary64-ULP scale and measured float-rounding screen are not mathematical geometry
certificates. A Known candidate can never bypass the mandatory complete solid and water retrace.
