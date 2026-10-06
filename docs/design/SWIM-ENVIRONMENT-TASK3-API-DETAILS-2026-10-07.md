# Task 3 scope witness and pin details

Status: accepted by migration and approved by the coordinator on 2026-10-07 under the owner's
delegated authority, with all six clarification points incorporated below. This fills the
scope-witness and pin-property signatures left descriptive in the approved generic plan.
It does not replace F3 or authorize native producers. No heavy command starts without an
explicit slot grant. Task 3 remains in progress, with evidence tracked below.

## Scope witness

F3 requires a witness naming the bounded space, geometry and medium resources, including proved
empty regions. The plan names the certified scope and portable identity but does not give the
witness a concrete value type. Approved dependency-free Locomotion value:

```csharp
public sealed class MovementScopeWitness
{
    public MovementQueryScope Scope { get; }
    public MovementQueryIdentity Identity { get; }
    public IReadOnlyList<string> ResourceIds { get; }
    public bool CoversKnownEmptyRegions { get; }

    public static MovementAvailability TryCreate(in MovementQueryScope scope,
        in MovementQueryIdentity identity, IReadOnlyList<string> resourceIds,
        bool coversKnownEmptyRegions, out MovementScopeWitness? witness);
}
```

The factory validates all limits before copying or sorting. Successful construction copies the
resource identifiers into an immutable ordinally sorted collection,
rejects blanks/duplicates and requires at least one canonical directory/resource identity even
for an empty region. Identifiers are opaque adapter-owned names. No MapDoc page format or native
manifest encoding is imposed. Identifiers may name semantic resources or local backing evidence.
Physical page regrouping does not change portable world/scope identity. Local backing-ID collections
need not match across heads and are never compared for portable compatibility. The producer binds
meaning/content to the portable closure and scope digest, independently of physical repacking.
The producer certifies completeness under its lease. Generic code checks the exact identity,
scope containment, frame and binding, without implementing a competing resource-directory query.
`CoversKnownEmptyRegions == false` cannot produce a usable lease, even for a known wet centre.
This flag attests complete classification of the declared scope, including empty portions when
present. It is vacuously true for an entirely known nonempty scope. It is neither a global-world
empty proof nor a requirement that empty geometry exists. Actual proof remains producer-owned.

### Finite witness and dependency bounds

| Limit | Concrete value | Reason |
| --- | --- | --- |
| Resource identifiers per witness | 256 | Bounds one movement-query resource set without enumerating a whole world |
| UTF-8 bytes per identifier | 1024 | Allows descriptive names while rejecting an unbounded individual string |
| Aggregate UTF-8 identifier bytes | 65536 | Bounds copying, retained evidence and ordinal sort inputs to 64 KiB |
| Dependency entries visited per producer prepare/pin attempt | 4096 combined | Permits bounded directory traversal around a local scope, with refusal before a whole-world scan |

These are generic query-policy limits and enter the producer's query policy version. They are
not native page-size or partition rules. Generic code validates witness count and sizes. The
producer enforces the dependency-work budget and returns CapacityExceeded if exhausted.
Pin performs no I/O. No retry-until-resident or whole-world enumeration occurs under the gate.

Read collection Count first and reject an over-capacity count before visiting its entries.
Bound each string's UTF-16 length before counting UTF-8 bytes, then accumulate bytes with checked
arithmetic. Reject malformed UTF-16 rather than using replacement encoding. Do not allocate an
output copy or sort until all size/identity checks pass. Over-capacity returns CapacityExceeded
and null witness. Invalid identifiers/duplicates return Invalid and null witness. Nothing truncates.
An incomplete classification attestation returns Unresolved and null witness. The factory validates
the copied values again after the first bounded pass, so a mutable input cannot evade byte limits
between validation and copying. Published witness storage remains immutable afterward.

Capacity fixtures cover 257 entries without indexing them, one 1025-byte identifier, aggregate
65537 bytes, exact boundaries, duplicate/blank identifiers, malformed UTF-16, and immutable copying.
The all-known-nonempty fixture sets the attestation true without manufacturing an empty region.

### Query output working limits

The generic consumer uses private bounded scratch buffers before publishing caller outputs.
The initial limits are 256 support candidates, 64 coverage spans and 256 domain contacts.
The latter two are F3's limits. The support cap bounds one local candidate enumeration while
allowing substantially more than the ordinary few stacked support surfaces. These are query-policy
limits, not world-size limits or assumptions about native partitioning. Larger caller spans do not
enlarge the producer's work allowance. Required capacity beyond a limit returns an explicit refusal,
without a copied prefix. Native G1b must prove its canonical queries fit or explicitly refuse.

The producer receives cleared scratch spans bounded by both the caller's capacity and these limits.
The consumer checks availability, identity, counts, complete span ordering and every returned value
before copying either buffer. Missing entries therefore cannot borrow valid leftovers from a prior
query. Coverage error must fit F3's 0.001 m bound. All failure paths preserve caller sentinels.

## Generic support placement result

The generic selector returns `MovementAvailability` from
`MovementSupportResolver.Select(in MovementSupportRequest request, MovementQueryLease queries,
out MovementSupportPlacement? placement)`. The immutable placement retains the original
`MovementSupportCandidate Candidate` and the profile-derived `Vector3 Centre` separately, so
capsule clearance does not overwrite canonical surface provenance. Known with null placement
means no supplied candidate was eligible. Unresolved/Invalid/CapacityExceeded cannot return a placement.

Apply the declared legal-space/link, rise/drop, slope and complete capsule-clearance filters before
choosing the highest eligible candidate. Same canonical owners may coalesce exact-height seam
references. Independent exact-height owners remain ambiguous even when one was current. A missing
physical support proof or incomplete backend query is not Known empty support. The real-backend
fixtures cover flat/tilted support, bed plus wall, ceiling, higher eligible support, aliases,
independent owners and capacity refusal. Actual movement to a selected placement still needs the
shared swept resolver and water-path proof in Task 4.

The initial composition keeps XZ fixed at the queried vertical support line. It derives a candidate
upright-capsule pose from the canonical normal with 1 mm separation, then queries all real backend
contacts within 1 mm plus the certified numerical error. Maximum solid-contact scratch is 16384,
matching the accepted backend bound and participating in policy identity. Scope validation includes
the additional error margin. A definite penetration rejects the candidate. Uncertain clearance,
missing matching physical support or uncertifiable contact coverage refuses the selection rather
than accepting a floating or penetrating pose. Curved or discontinuous geometry for which that
candidate pose cannot be certified does not inherit the planar fixture's proof.

## Pin properties

Complete `IMovementEnvironmentPin` with these read-only properties in addition to the already
approved query methods and `AssertCurrent`/`Dispose`:

```csharp
IPhysicsWorldQueryView PhysicsView { get; }
long GeometryGeneration { get; }
long EnvironmentGeneration { get; }
MovementFrameDescriptor Frame { get; }
MovementScopeWitness Witness { get; }
```

`PhysicsView` must be the exact view passed to the context, and its source must be the lease's
exact owner. Both comparisons use ReferenceEquals, never overloaded Equals or generated record
equality. Geometry generation and frame match the active physics lease. Environment generation
is local lifetime evidence only. The pin enforces eviction/publication fencing under F3's ordered
read interval. Portable compatibility never compares these local references or generations.

The public local lease identity records the actual source object reference, geometry generation,
environment generation and frame epoch. It is not serialized, hashed into a nav profile or sent
across heads. Using the actual reference avoids manufacturing a second source-ID registry.
If this local value is hashed, equality and hashing use reference identity consistently. Tests
include distinct source objects and distinct view objects that compare equal by value.

## Consumer validation and tests

`TryAcquire` requires Witness.Identity == Witness.Scope.Identity, also equal to the context,
prepared and requested identity. It rejects mismatches instead of rewriting an expected identity.
One acquired immutable witness feeds both pin validation and subsequent result validation.
Exact source/view/frame binding, containment of the full requested AABB plus rise/drop envelope,
and current pin/physics leases are required.

Any refusal disposes the pin before releasing the physics gate. A finally path always releases
the physics lease even if pin disposal or validation throws. Caller-visible output spans are
written only after result identity, counts and invariants pass. A misbehaving adapter cannot
publish a partial prefix through the context wrapper.
Sentinel-buffer tests cover failed pinning, mismatched result identity, partial/over-capacity adapter
output and throwing cleanup. A successful physics mutation afterward proves the gate was released.

Tests use a finite synthetic witness and actual Bepu owner/view. They prove pin attempts occur
under the physical fence, failed pinning releases it, mismatched source/generation/frame and
incomplete/undersized scope refuse, disposed/stale pins refuse, and old-frame state is converted
or returned unchanged as a framed refusal. The native resource and geometry proof remains G1b.

## Finite volumetric fixture preparation

The remaining test adapter is `Fixtures/AnalyticMovementEnvironment.cs`. It owns only a fixed,
small collection of explicitly declared spaces, interval bounds and legal connections. It has no
MapDoc reader, page directory, residency system or terrain reconstruction. Each scenario acquires
the existing context and immutable witness. Matching Bepu solids establish the floor, ceiling or
bridge where the scenario claims physical support. Centre sampling and full capsule tracing are
asserted separately through the lease.

The following is an assertion matrix, not executed evidence or a ready test-count request:

| Scenario | Required independent assertions |
|---|---|
| Dry cave below an ocean | Centre is known dry in the cave. The entire capsule path remains dry despite overlapping ocean XZ coordinates. The ocean domain never appears in contacts. |
| Two vertically stacked water bodies | Each body's centre and whole path identify its own space, domain and clipped interval. A shared XZ projection cannot merge their identities. |
| Partly flooded chamber | Paths wholly above the local free surface are dry. A lower path is wet. A crossing path reports the analytic entry fraction and simultaneous dry coverage. |
| Fully flooded low ceiling | Interval upper containment is the ceiling, below nominal SurfaceY, with UpperIsFreeSurface false. No free-surface contact is manufactured at the remote nominal level. |
| Shaft connected to a flooded chamber | Only the explicit legal connection exposes the shaft interval and its actual free surface. A lateral path through the chamber ceiling cannot acquire that surface by projection. |
| Supported bridge and water beneath | Above-deck capsule coverage is dry. A separate legal below-deck query is wet. Neither outcome uses a bridge exemption or subtracts global depth. |
| Dry centre with wet outer capsule | Centre sampling is dry while tracing includes the wetted cap or side and HasDryCoverage. Use actual capsule geometry, including rounded caps. |
| Adjacent water domains | A footprint straddling a declared boundary reports both legitimate domains without ambiguous-membership refusal. Reverse declaration order and preserve semantic results. |
| Vertical and oblique motion | Entry and exit fractions follow the capsule clipped by local containment, not only its centre or horizontal disc. Assert each ordered span and its contact range. |
| Start tangency and zero displacement | Closed skin tangency remains a contact at fraction zero. Stationary coverage is complete. Contact normal is outward and supports later inward/tangent/away policy checks. |
| Artificial producer partition seam | Splitting one semantic region changes neither interval membership nor shore classification. Backing identifiers stay local. This is not a native tiled-bake proof. |
| Missing containment dependency | Return Unresolved with both caller buffers unchanged. Never return a known dry span or a usable partial path. |

Use independently calculated plane/capsule intersection fractions for the finite paths, with the
F3 skin and certified error stated in the assertion. Do not derive expected fractions by invoking
the adapter's intersection helper. Unsupported analytic geometry refuses explicitly rather than
approximating a complete answer. Tests of this adapter prove the generic contract and its consumers
on declared finite geometry. They cannot establish completeness of a future native producer.
Jump-through-ceiling prevention and movement acceptance remain later mover/jump proofs, not
conclusions from interval facts alone. Each executable subset gets its own exact count and filter
request after authoring. This matrix does not authorize a heavy command.

The first executable subset is now authored as 25 finite cases in
`KhaozEngine.Game.Tests/Locomotion/MovementEnvironmentVolumeTests.cs`, selected by
`FullyQualifiedName~MovementEnvironmentVolumeTests`. Its test-only
`AnalyticMovementEnvironment` and `AnalyticCapsuleVolume` use at most eight rooms, eight water
cells and eight declared connections. The exercised capsule has radius 0.25 m and half-height
0.75 m, centre coordinates within 16 m and displacement components within 8 m. Other capsule
families/envelopes refuse. Double-precision piecewise quadratics compute rounded capsule versus
box intersection. Contact fractions round outward and the declared geometric error is 0.00001 m.
Assertions independently calculate crossing thresholds and inspect every returned span, including
closed start/end tangencies. The first validation result and correction are recorded below.

This subset covers axis-aligned clipped containment, both vertical crossing directions, oblique
shore entry, actual rounded-cap overlap, adjacent domains, declared shaft access, rectangular
partition invariance and refusal atomicity. A small extension to the existing acquisition fixture
adds an optional body-aware sample callback, preserving its original fixed-value fallback.
There are no production changes in this preparation. Arbitrary nonrectangular unions refuse when
their dry coverage cannot be certified. Variable sloped floors/ceilings, complete impact constraints
and movement acceptance are not proved by this subset. They remain required later proofs rather
than a native producer claim or a change to F3. No shared signature amendment is needed for these
25 cases. The first validation request is a compile-enabled finite run, not a missing-capability RED.

## VC1 proposed interval-column locus clarification

Status: proposed for coordinator and migration review, not approved. No shared production edit
or native adapter follows from this proposal. Accepted F3 remains the semantic authority.

Source boundary: `MovementDomainContact` in `MovementWaterCoverage.cs` carries one
`MovementWaterInterval`, a path fraction, a normal and an opaque `CoverageRegionHandle`.
`IMovementEnvironmentPin` has no operation that resolves that handle to varying geometry.
The box fixture can truthfully supply a constant interval over a region. A floor such as
`LowerY = 0.5 * X - 2` changes continuously across the capsule footprint and path. Neither the
centre column nor a minimum/maximum envelope is the actual interval at every covered column.
The current fields do not state which column the supplied interval describes. An outer wet
footprint with a dry centre prevents defining that column implicitly as the body's centre.

Recommended bounded completion: add required `Vector2 IntervalColumnXZ` to the unreleased
`MovementDomainContact` value and constructor. It is in the acquired physics frame, at the
contact's declared fraction within its certified spatial error. `Interval` is the canonical
vertical column fact at that explicit XZ and selected space/domain. It is not a constant-depth
certificate for the span or region. `CoverageRegionHandle` stays opaque, pin-local provenance.
The producer still certifies the entire capsule trace and all relevant geometry dependencies.
No geometry fetch method, native sampler, global registry or wire field is introduced.

The lease validates finite column coordinates, containment in its certified scope and compatibility
with the queried capsule at the contact fraction, including the existing skin/error budget.
It must not extrapolate that local fact to other columns. Existing source constructors/tests would
supply their actual column explicitly. There is no zero-column default or legacy overload that
fabricates one. Deliberately unconfigured legacy locomotion remains unchanged.

| Option | Correct location semantics | Preserves producer ownership | Bounded implementation | Total |
|---|---:|---:|---:|---:|
| Required column fact, complete trace stays producer-owned | 9 | 10 | 9 | 28 |
| Add a region-geometry evaluation API to the facade | 10 | 5 | 3 | 18 |

The required column makes the existing fact auditable with a small explicit addition. It does
not let generic consumers reconstruct varying geometry. A region-evaluation API could provide
that power, but needs a new geometry/lifetime contract and risks duplicating native query work.
That expansion is not justified for this bounded clarification. A single unlocated sampled
interval or a bounding-height approximation is excluded by F3, not offered as a fallback.

If accepted, the concrete change is confined to `MovementWaterCoverage.cs`, contact-result guards
in `MovementQueryLease.Buffers.cs`, the existing fact/buffer/volume test constructors and living
Locomotion API documentation. New finite cases would cover sloped floor and ceiling in both travel
directions, a flooded ceiling below nominal SurfaceY, a wet outer column with dry centre, coordinate
rebinding and refusal of an out-of-footprint column. Expected eight cases, exact filter/count to be
pinned after authoring. All clipping remains in a finite analytic test producer using shared real
physics for solid prerequisites. This clarification alone does not prove slope clipping or the
later generic depth-threshold/mover policy. Those require their own measured tests.

Independent nonrectangular preparation needs no shared signature change. Seven finite cases will
use a constant-depth L-shaped union of two boxes: a dry notch inside its overall bounding box,
mixed outer-footprint coverage in both declaration orders, full coverage spanning both arms while
neither arm alone contains the capsule, a moving dry-notch crossing, simultaneous start tangency
and missing-neighbor refusal. The planned test-only extension classifies the finite complement
cells instead of replacing the L with its bounding box. Existing complete-buffer and work limits
remain unchanged. Its first run should expose the current analytic fixture's explicit unsupported
union refusal, not a production movement defect. No slope/contact-column implementation starts
before the proposed shared completion receives its required joint approval.

## Implementation evidence

The initial value RED compiled and exited 1 with 27 missing-runtime-type failures. After value
implementation, the same 27 assertions using direct public types passed in Release with no skips,
exit 0, in `/tmp/swim-environment-values-green.log`. This validates keys, body/interval/point
invariants and the unresolved default, not environment acquisition or support composition.

The separately selected 13-case witness RED then exited 1 with no skips, at the absent
MovementQueryScope type. It used the successful compilation above with `--no-build`.
`/tmp/swim-environment-witness-red.log` is missing-capability evidence, not a proof of witness
capacity/refusal behavior. Each granted window explicitly released the shared compute slot.

Witness GREEN subsequently compiled and exited 0 with all 13 direct-type assertions passing,
no failures and no skips, in `/tmp/swim-environment-witness-green.log`. It proves the finite
count/byte boundaries, malformed UTF-16 and invalid-name refusal, immutable ordinal copying,
scope identity mismatch refusal and equal portable identity with differing backing IDs.
No other fixture ran in that window. Actual lease/pin composition, throwing cleanup, caller
sentinel protection, support selection and coverage remain outstanding before Task 3 completion.

Context lifecycle RED compiled and exited 1 with 17 missing MovementEnvironmentContext failures,
including cleanup fixtures that saw the missing-type assertion instead of their expected cleanup
exception. After typed lifecycle implementation and direct fixture conversion, the same 17 cases
passed in Release with no skips, exit 0, in `/tmp/swim-environment-context-green.log`.
They prove actual Bepu preparation/pin ordering, exact reference binding despite value-equal
sources/views, one captured witness, partial-pin refusal cleanup and throwing validation/disposal.
The slot was released immediately. Span forwarding, support, coverage, frame rebinding and the
remaining lease-thread/local-ID cases are not inferred from this lifecycle-only run.

The next query/value RED compiled with five existing lifecycle controls passing and 27 missing
capability failures, seven for centre sampling and twenty for value types. The corresponding
direct-type GREEN exited 0 with all 32 cases passing and no skips in
`/tmp/swim-environment-query-green.log`. This adds evidence for centre-query scope/lifetime/world
guards, the named support/coverage/framed value invariants, two finite foreign-thread refusals,
disposed validation, nested acquisition and local reference equality/hashing. It does not prove
Span forwarding, support selection, frame rebinding or volumetric geometry coverage.

Buffer RED compiled with 30 missing EnumerateSupport/TraceWater failures. The corresponding
direct-call GREEN exited 0 with all 30 cases passing and no skips in
`/tmp/swim-environment-buffer-green.log`. It proves bounded cleared scratch, preservation of
nonzero caller sentinels through refusals and throws, count/identity/value/scope/error validation,
complete ordered span coverage checks and validation of both output buffers before either copy.
The slot was explicitly released. These are consumer-contract proofs, not native producer geometry,
support-selection or frame-rebinding evidence.

Pure frame RED compiled with 12 missing-helper failures. The same direct assertions passed in
`/tmp/swim-frame-rebind-green.log`, exit 0, 12 passed and no skips. This proves recorded-origin XYZ
conversion, fractional precision under large origins, source/target envelope checks, unchanged
state on refusal and selection invalidation on changed frame/epoch. It does not establish canonical
membership. The CS1 [cold-selection amendment](SWIM-COLD-SELECTION-SCOPE-AMENDMENT-2026-10-07.md)
separately governs null-hint acquisition and reconstruction readiness.

Support-selection RED compiled with 14 missing MovementSupportResolver runtime-type failures,
zero passes and zero skips, exit 1 in `/tmp/swim-support-selection-red.log`. All failures reached
the capability assertion after scene/context setup. The same direct-type assertions subsequently
passed in `/tmp/swim-support-selection-green.log`, exit 0, 14 passed with no failures or skips.
Sources remained unchanged during that granted run and compute was explicitly released afterward.
This proves finite real-Bepu clearance before highest-support selection, canonical alias versus
independent-owner ties, legal-link provenance, separate sloped capsule placement and refusal on
missing or uncertifiable physical support. It is not native geometry, swept traversal or Grimhollow
bridge-route evidence. The remaining volumetric proof is tracked separately below.

The first volumetric validation compiled and exited 1 with 24 passes, one failure and zero skips
in `/tmp/swim-environment-volume-first.log`. FinalEndpointTangencyIsNotLostByHalfOpenPathSpans
expected one contact at the final zero-length span and received none. This was an unexpected
test-producer defect, not a missing-capability RED. The secondary compute slot was released.

The closed-form fixture clipped the quadratic root before checking its closed endpoint. For the
exact supplied float radius 0.25099998712539673, origin -0.5019999742507935 and displacement
0.25099998712539673, scalar arithmetic gives a lower root of 1.0000000000000002, outside the
path ending at 1. Direct endpoint squared distance and squared radius are both
0.06300099353694932. The fixture now evaluates every closed piece boundary directly in addition
to the quadratic interior. It adds no tolerance and preserves the original endpoint assertion.
The separately granted second run passed all 25 unchanged assertions, zero failures and skips,
exit 0 in `/tmp/swim-environment-volume-second.log`. Both runs compiled Release with `-m:1` through
the coordinator's secondary wrapper. That slot was explicitly released after each run.
This validates the declared finite box/rectangular-union subset and the closed endpoint correction.
No production movement or native geometry change follows from this fixture failure. Variable
sloped floor/ceiling clipping, arbitrary nonrectangular unions and full mover proofs remain open.
