# Task 3 scope witness and pin details

Status: accepted by migration and approved by the coordinator on 2026-10-07 under the owner's
delegated authority, with all six clarification points incorporated below. This fills the
scope-witness and pin-property signatures left descriptive in the approved generic plan.
It does not replace F3 or authorize native producers. The initial 27-case value RED is queued,
not granted. No heavy command starts without an explicit slot grant.

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

Capacity fixtures cover 257 entries without indexing them, one 1025-byte identifier, aggregate
65537 bytes, exact boundaries, duplicate/blank identifiers, malformed UTF-16, and immutable copying.
The all-known-nonempty fixture sets the attestation true without manufacturing an empty region.

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
