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
