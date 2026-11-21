# Water boundary certificate

Runtime Contact movement answers a water boundary proof from capture facts when the answer cannot depend on the
water geometry. The full coverage trace stays the fallback for every other case and the only evidence a navigation
bake accepts. The code is `MovementWaterBoundaryCertificate` and `MovementWaterBoundary.Find` in Locomotion, with
the tile producer in `TileMediumSampler.BoundaryReach.cs`.

## Why capture facts are enough

`MovementWaterBoundary.Find` asks whether a capsule path meets water its policy forbids. It reads a complete ordered
coverage trace and derives a status, a blocked flag, an initial overlap flag and candidate normals. What can
constrain a body depends on its policy.

- A SurfaceSwimmer is constrained only by closed water, or by two different free surface levels in one span.
- A WadeOnly body is constrained by a free surface only where its path centre reaches its swim-enter line.
- A DryOnly body is constrained by any water.

A tile water producer builds every contact with a free surface at its own column's top, and every region-clipped
water body has one level. So for a path whose reach holds no column, or holds columns of one level only, the trace's
spans, contacts and 1 mm certificate cannot change the answer for a SurfaceSwimmer. They can only make the proof
refuse. The same holds for a WadeOnly body whose whole path stays above its enter line at that level.

A clean grounded step that moves makes seven boundary proofs: the placement proof and three that repeat it, then
the lift, the traverse and the seat. On a bank each of the four distinct proofs needs a fresh prism solve under the full path,
because its operands differ on every step and no result cache can serve it. Answering the question from capture
facts removes the solves instead of making them cheaper.

## The certificate

`IMovementWaterBoundaryReachProof` is an optional producer interface beside `IMovementDryTracePointProof`. A
producer that answers anything but a decline guarantees four facts for the query: every contact its trace can
report is free surface, every such contact carries its own column's top as its nominal surface, every such contact
belongs to a column the certificate counted, and every domain of the capture has one level. A producer that cannot
guarantee all four must not implement the interface.

The tile sampler collects the captured columns that meet the directed lateral bounds of the whole capsule path,
through the same index and the same bounds the prism kernel selects its candidates from. That set also covers the
flat kernel's selection. It answers an empty reach when there is no column, and one free surface when every
collected column carries identical surface bits. It declines on two levels, on more than 256 columns, on a query
its trace would refuse as invalid, and on a capture whose facts it did not verify. The capture verifies when it is
built that every column of one domain has the same surface bits and that it has at most 256 domains.

The lease entry repeats the trace's own pre-checks in the same order and declines on any failure, so the full trace
that follows reproduces the trace's own refusal for it. The pin converts the certified surface with the conversion
every trace contact's nominal surface gets, so a Known trace's levels equal it bit for bit.

`Find` then answers Known with no normals and a default hit in three cases.

- An empty reach, for every policy.
- One free surface, for a SurfaceSwimmer.
- One free surface, for a WadeOnly body whose lowest path centre lies strictly above the skip threshold at that
  level with the 0.001 m error a Known trace admits at most. The proof and the certificate compute that threshold
  with one shared helper. Every rounding and flush mode keeps both bounds monotone, and the certificate takes the
  single counted level, so it needs no monotonicity across levels.

Every comparison is exact. No tolerance is added and no proximity is a key.

## What changes

Wherever the full solve returns Known, every output bit of the step is unchanged. The parity suites compare the
certified answer with the full solve question by question, and whole movement and replay runs through a field that
forwards the certificate against one that hides it.

Where the full solve would refuse, a runtime step continues past that proof and its later checks decide the
outcome. There are four kinds: coverage error above 0.001 m, an evaluation that does not resolve or is not finite,
a kernel capacity limit, and a result the pin or lease rejects as malformed, which includes an exception raised
inside the producer. Such a refusal reports that the solver could not finish. It guards no movement rule. Point
samples, support, shell sweeps, solid contacts, route boundary proofs, frame checks and all authentication run
outside the trace and are unaffected.

## Navigation bakes keep the full solve

A navigation bake proves holds, edges, Stair links, Drops and terrain Drops by running the controller. A refusal
there has a published consequence: it rejects a Drop link, refuses a terrain Drop or refuses the whole profile
bake. A certificate answer in a bake could therefore publish a link or a graph that the full solve refuses. So the
bake creates its movement binding under `MovementWaterBoundaryScope.NavigationBake`, every proof and fall binding
derived from it inherits that scope, and its leases never consult the certificate. The navigation proof
constructors refuse a runtime binding, so a bake cannot forget the scope. Runtime movement, runtime Drop following
included, uses the runtime scope and may continue where a bake refused. A baked link replays identically at
runtime, because a link exists only where the full solve finished.

## Limits

- A producer reached through a wrapper that does not forward the interface keeps the full trace.
- A weir, a water body split by a region border at two heights, and a reach of more than 256 columns decline.
- A DryOnly body gets only the empty reach. A WadeOnly body at or below its enter line declines.
- The analytic level field does not implement the interface.
- A sampler built from borrowed test geometry is unverified and always declines.

## Tests that pin it

`WaterBoundaryCertificateParityTests` compares each certified answer with the full solve and lists the allowed
refusal differences. `WaterBoundaryCertificateMovementParityTests` compares movement and replay bit for bit.
`WaterBoundaryCertificateRefusalTests` pins the step that continues past a refused proof.
`WaterBoundaryCertificateWorkTests` pins seven coverage traces a clean grounded moving step without the certificate
and none with it, on the steep bank in both directions, the flat shelf and dry ground. `WaterBoundaryScopeTests`
pins that serial, parallel, Drop and terrain Drop bakes never consult the certificate and that runtime steps do.
