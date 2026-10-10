# Contact controller phase 2b: support neighborhood

Date: 2026-10-10. Detailed spec for phase 2b of [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438). The
program, body model, support rule and invariants are in
[CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md](CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md). Phase 1 and
[phase 2](CONTACT-CONTROLLER-PHASE-2-GROUND-CORE-2026-10-10.md) are on main at `b51f4fdf9`. Status: approved by
the owner 2026-10-10, implemented, staged for 20.30.1. No consumer or movement change. Plan in
`docs/superpowers/plans/2026-10-10-contact-controller-phase2b.md`. This document describes what shipped,
including the rulings made during implementation.

## Purpose

Phase 1 certifies support conservatively and refuses what it cannot certify. Phase 2b replaces every one of those
refusals with a certified result, so no game adopts a controller that holds a body in place on ordinary
geometry. The owner ruled that phase 2b lands before any adoption, and that curved props are standable.

It closes:

| Issue | Gap before phase 2b |
|---|---|
| [#1329](https://github.com/APKiwiOrg/KhaozEngine/issues/1329) | A concave crease refuses. The axis on a valley line returns `Refused`. |
| [#1330](https://github.com/APKiwiOrg/KhaozEngine/issues/1330) | A vertex refuses. A box top corner and larger mesh fans return `Refused`. |
| [#1331](https://github.com/APKiwiOrg/KhaozEngine/issues/1331) | Sphere, capsule and cylinder statics report `Unsupported` and refuse. |
| [#1332](https://github.com/APKiwiOrg/KhaozEngine/issues/1332) | The back face of a one-sided mesh stops the probe, and the floor beneath is never proposed. |
| [#1333](https://github.com/APKiwiOrg/KhaozEngine/issues/1333) | A ridge split across two statics floats support up to `FootRadius tan(theta)` above the axis-side plane. |
| [#1340](https://github.com/APKiwiOrg/KhaozEngine/issues/1340) | Each probe certifies only the static its sweep reports, so a plateau beside a separate steep face reads `Steep`. |
| [#1342](https://github.com/APKiwiOrg/KhaozEngine/issues/1342) | The feature query returns `Unresolved` within about 2e-7 m of a face and edge boundary, which stalls stair runs. |

It also closes the phase 1 known limit where a lower surface under a higher one inside the disc is not proposed.

## The root cause

Five of the seven gaps share one cause. `FootSupport` asked the backend for the unique closest feature of the one
static each probe sweep reports. Uniqueness forces a refusal on every tie, at a face and edge boundary (#1342) and
between two valley faces (#1329). Vertex fans need a cone proof the backend lacks (#1330). One static per sweep
loses the other statics at the same contact (#1340, #1333) and makes support depend on how a scene is split into
statics.

The support rule does not need a unique feature. It takes the highest certified contribution over every surface
the footprint touches. A real surface at the contact that is included in addition can only be a correct
contribution. A surface that is missing is the defect. Phase 2b therefore certifies the complete set of surfaces at
the probe's contact, and completeness replaces uniqueness as the property the backend proves.

## Support neighborhood query

A new optional capability `IPhysicsSupportNeighborhood`, beside `IPhysicsCapsuleFeatures` in the same
optional-interface pattern. `IPhysicsCapsuleFeatures` keeps its released members, so the addition is additive and
an outside implementer of the feature query is unaffected. Bepu owners and their restricted views implement both.

```csharp
namespace KhaozEngine.Physics;

public enum SupportElementKind : byte { Polygon, Tangent }

public readonly record struct SupportElement(StaticHandle Static, SupportElementKind Kind, int ElementId,
    Vector3 Normal, float NormalError, Vector3 Witness, float PositionErrorMetres,
    double SeparationLower, double SeparationUpper);

public readonly struct SupportNeighborhoodResult
{
    public const int MaximumElements = 256;
    public CapsuleFeatureStatus Status { get; }
    public int Elements { get; }
    public int RequiredElements { get; }
    public int JoinWordsPerRow { get; }
    public static int JoinWordsFor(int elements);
    // Lease metadata as on CapsuleFeatureResult.
}

public interface IPhysicsSupportNeighborhood
{
    SupportNeighborhoodResult QuerySupportNeighborhood(IPhysicsQueryLease lease, CapsuleShape probe, Pose pose,
        float bandMetres, Span<SupportElement> elements, Span<ulong> joins, QueryFilter filter = default);

    void AssertNeighborhoodCurrent(in SupportNeighborhoodResult result, IPhysicsQueryLease lease);
}
```

The contract:

- **Membership.** Every front-facing surface element of every selected static whose separation from the probe can
  be at most `bandMetres`. Membership is decided by the lower bound of the separation interval, so an element is
  included whenever it may lie within the band. A tie between features is never a reason to refuse. Dynamic bodies
  and statics a selected query view excludes are never members.
- **Polygon elements.** A planar face of a box or convex hull, or one triangle of a mesh: its front normal, its
  witness (the closest point of the face to the probe segment) and the error bounds of that witness and normal.
  Coplanar mesh triangles are not merged. Each triangle is its own element.
- **Tangent elements.** A point on a sphere, a capsule, a cylinder side or a cylinder cap: the witness and the
  surface normal there, with bounded error from closed-form arithmetic. A cylinder cap is planar and is published
  as a tangent element with the cap normal. The errors enclose a rigid solid built from the installed leaf
  position, not Bepu's per-pair float path, which differs from it by about 1e-7 m.
- **Element ids.** Stable per static: a polyhedron face is `leaf * 256 + face`, a mesh triangle is its triangle
  index, and a tangent element is `leaf * 256 + part`, where part 0 is the sphere, capsule or cylinder side and
  parts 1 and 2 are the cylinder's top and bottom caps.
- **Joins.** The backend decides joins, because the join test needs bounded polygon geometry that lives in
  `KhaozEngine.Physics.Bepu`. The result therefore carries no vertex span. Joins form a symmetric bit matrix over
  the published elements: row `i` starts at word `i * JoinWordsPerRow`, and bit `j` of that row (word `j / 64`,
  bit `j % 64`) is set when elements `i` and `j` are joined. `JoinWordsPerRow` equals
  `JoinWordsFor(Elements)`, so callers index rows with the published stride and never with a stride derived from
  their buffer. `joins` must hold `elements.Length * JoinWordsFor(elements.Length)` words. A convex fan of n faces
  has n(n-1)/2 joins, so a pair list with its own cap would refuse a UV-sphere pole. The matrix is sized from the
  element count and never refuses on its own.
- **Sidedness.** An element whose front faces away from the probe is not a member. Boxes, hulls and curved
  primitives are closed solids. Mesh triangles use the winding `Cross(C - A, B - A)` the feature backend already
  defines.
- **Meshes.** Candidate triangles come from the mesh's own bounding tree over the probe's band-inflated bounds,
  so cost grows with the triangles near the probe, not the mesh size. Membership, front facing and joins are
  decided per triangle, so mesh topology is never validated. Non-manifold edges, duplicate triangles and
  inconsistent winding certify triangle by triangle. A mis-wound triangle reads as a back face and becomes a hole,
  which matches Bepu's one-sided contacts. A triangle whose cross product is exactly zero, or whose normal cannot
  be bounded away from zero, has no certifiable surface and is not a member. Its edges belong to its neighbours.
  Under a rotated pose that happens when the sine of a triangle's smallest angle is below about 1e-7.
- **Order.** Elements are ordered by static handle, then element id, so certification is deterministic.
- **Capacity.** 256 elements per query, matching the backend's existing 256 incident-face cap, so a dense mesh fan
  never meets a capacity refusal the feature query would not.
- **Refusals.** `CapacityExceeded` when the member count exceeds the element span or 256, atomically, with
  `RequiredElements`. `Unsupported` for a pose, probe size or band outside the proven domain, a
  shape outside the captured domain such as a scaled mesh, or bounded arithmetic that cannot be resolved.
  `Ambiguous` remains only for a convex hull whose captured faces the backend cannot prove convex and manifold, as
  the feature query refuses it. Nothing else refuses. A refusal or exception leaves both spans untouched.
- **Arguments.** A layer filter, a non-finite pose, a zero rotation, an invalid probe and a join span shorter than
  `elements.Length * JoinWordsFor(elements.Length)` throw `ArgumentException`, as `QueryCapsuleFeature` does for its
  own arguments. Argument misuse is an exception in this API, not a refusal.
- **Lease.** The result is bound to the lease and receiver like `CapsuleFeatureResult`. The distinct method
  `AssertNeighborhoodCurrent` checks it, so `AssertFeatureCurrent` keeps its single signature.
- **Compounds.** Each convex leaf contributes its own elements. A curved leaf never refuses the polyhedral leaves
  beside it.
- **Install.** A `TriangleMeshShape` with a non-finite vertex is rejected at `AddStatic` with
  `ArgumentException`. A NaN vertex would poison the mesh tree's bounds and silently hide valid triangles from
  every tree query, and Bepu cannot simulate it either.

The broadphase collector behind it is backend-internal. Swimming's `IPhysicsCapsuleContacts` enumerates contacts
over the same candidate set. When phase 4 integrates `feature/swimming-contract`, both queries share the one
collector, so there is one membership definition and two projections of it. `QueryCapsuleFeature` stays as it is
for its existing callers.

## Certifying support from the neighborhood

The support rule is unchanged. `SupportCertification.CertifyNeighborhood` certifies a whole neighborhood and writes
one contribution per member, in element order. Every contribution is an interval with the existing outward-rounded
arithmetic.

- **No fixed thresholds.** Each member's normal and position errors propagate into its contribution interval, so
  `HeightError` reflects them. Phase 1's fixed normal bound would refuse honest long thin triangles on rotated
  meshes, which is ordinary content. Only a non-finite contribution refuses the neighborhood.
- **Walkable or steep.** A member is walkable when its normal's lower Y bound reaches `cos(MaxSlopeRadians)`, so a
  normal straddling the slope limit is steep. A member whose upward Y bound reaches zero may face sideways or
  down, has no bounded plane at the axis and contributes nothing, like a vertical face.
- **Walkable polygon.** Contributes `min(its plane at the axis, its witness height, the plane at the axis of every
  walkable polygon joined to it)`. On one static this reproduces the phase 1 convex crease rule, so a ridge
  supports its axis side. It gives the same answer when the ridge is split across statics.
- **Join.** Two polygons are joined when they may meet within the contact band and every vertex of each certainly
  lies at most the band above the other's plane. The test is geometric for every pair, inside one static and
  across statics, so a scene's partition into statics cannot change support. Polygons that cross each other's
  planes, such as a ramp sunk into a floor, are not joined, and the higher surface wins as it physically does.
- **Concave creases.** A concave pair fails the join test and adds no cap. Each face contributes its own plane, and
  the maximum gives the valley line its crease height.
- **Vertices.** No vertex classification is needed. A vertex's polygons and their pairwise joins carry it, so a
  convex apex gives the minimum of its planes, a box corner gives its top, and a saddle gives each face's own
  plane.
- **Tangent element.** Contributes `min(its tangent plane at the axis, its witness height)`, walkable or steep by its
  normal. Tangent elements are never joined. On a boulder the axis probe certifies the top directly, and the leg
  probe's contact on the side is lower and never wins.
- **Steep polygon.** A steep polygon joined to any walkable polygon contributes nothing. It is the steep side of a
  walkable crease, as a phase 1 feature with a walkable face ignored its steep faces, and the walkable side carries
  the crease. Any other steep polygon follows the phase 1 steep rule over its joined set: the minimum with the
  plane at the axis of every joined steep polygon. A narrow bevel between a walkable slope and a steep face
  therefore never lifts support to the steep face.
- **Selection.** Contributions from both probes and terrain qualify by the inclusive band, the highest midpoint
  wins, and the phase 1 tie order holds. A member whose contribution lies wholly above the band is not a
  candidate. A probe whose contact (its lowest point) lies above the band top, a refused neighborhood, or a
  neighborhood with no member that supports at or below the band top is a refused proposal at the probe's contact,
  because the surface that stopped the probe can hide support under the rest of the disc. A probe that brushes the
  next stair nosing beside an in-band tread therefore still reads the tread.

A join decided at exactly the band tolerance can switch a cap on or off. That is a discontinuity of at most
`FootRadius tan(theta)` for geometry whose faces meet within 0.1 mm. A gap wider than the band is a crack, and the
disc bridges it on its rims as the program table already says.

## Proposals

`FootSupport` keeps its two probes, the axis probe and the leg probe, each one sweep. A probe hands its contact
pose to the neighborhood query instead of naming a single static.

- **Back faces.** `QueryFilter` gains the init-only property `CullBackFaces`, outside its positional parameters so
  its constructor and `Deconstruct` keep their released signatures. With it, `SweepCapsule` skips a static mesh triangle that
  does not face against the sweep, one whose front normal has a dot product with the sweep direction of at least
  zero, through the backend's per-child sweep filter. Vertical triangles are culled too. A vertical triangle can
  never support, and its upper edge belongs to an upward neighbour in a closed mesh. Compound children are never
  culled, and raycasts are already one-sided. Bepu's contact generation treats mesh triangles as one-sided, so the
  support probe now agrees with the simulation. It passes a down-facing or vertical one-sided triangle and reaches
  the floor beneath. Shell sweeps do not set the flag, and a back face stays a wall to the shell.
- **Hidden lower surface.** Selection takes the highest contribution, so a lower surface under a higher one only
  mattered when the higher one was excluded. Back faces were the only exclusion, and the probe now passes them.
  The phase 1 known limit closes with no third probe.

## Refusals and held bodies

Support refuses only on a neighborhood refusal (capacity or an unsupported domain) or a probe stopped by a surface
it cannot use, as Selection describes. These are content or configuration defects, or geometry above the reach
band. `GroundCore` keeps `Held` for a refused start and blocks a refused target. Its rows move from the sphere
fixture, which now certifies, to an over-capacity fan: one flat mesh of 300 triangles meeting at one vertex. A
probe touching the apex meets every triangle, over the 256 element capacity.

`FootSupport` keeps one thread-static scratch per thread with 256 elements, the join matrix for 256 elements and
256 contributions, reused by both probes of every `Find`. A neighborhood larger than that is a capacity refusal,
never a truncation.

## Cost

Each probe still makes one sweep and one certification query. The neighborhood query replaces the feature query,
so `GroundCore`'s asserted query counts do not change. A neighborhood query does more work than a closest-feature
query. Phase 2b measured time and allocation per `FootSupport.Find` and records them in
`docs/verification/2026-10-10-p2b-support-cost.json` for [#1334](https://github.com/APKiwiOrg/KhaozEngine/issues/1334).
The figures are one local arm64 run under the parallel suite. Before is the first neighborhood implementation,
after is the allocation-free pass that kept every result bit-identical over a 3,013-row recorded fixture on arm64.
Its 1,213 `FootSupport` rows passed through Bepu sweeps and differed in the last bits on hosted x64, so they left
the committed fixture, as the Suite table describes.

| Scene | Bytes before | Bytes after | Microseconds before | Microseconds after |
|---|---|---|---|---|
| Flat box | 470,752 | 0 | 480 | 165 |
| Flat mesh | 246,328 | 0 | 227 | 93 |
| Stairs, boxes | 485,832 | 0 | 452 | 129 |
| Stairs, one mesh | 275,824 | 0 | 241 | 105 |
| Sphere | 85,592 | 0 | 114 | 55 |
| Grid of 20,000 triangles | 411,096 | 0 | 598 | 542 |
| Fan of 96 triangles | 278,734,632 | 0 | 267,077 | 102,298 |

Stack per `Find` fell from 43,264 bytes to at most 4,352. A warm `Find` allocates nothing on any scene, and the
allocation rows assert walkable support and zero bytes on every scene but the fan, which is recorded. No scene meets
its time target of 50 microseconds, or 5 ms for the fan. The fan's 9,120 exact join tests per `Find` cost about
10 microseconds each. The program spec's phases table makes closing that gap a gate before any game adopts.

## Invariant this phase adds

To the program list, as invariant 9: support is independent of how static geometry is partitioned into statics,
for surfaces that meet within the contact band.

## Known limits

- A one-sided fin tilted very slightly upward within 1 cm of the axis is still hit by the probe, contributes below
  the band and hides the floor beneath, giving `None` as phase 1 did. An exactly vertical fin is culled and the
  floor is read.
- A join decided at exactly the band tolerance switches a cap on or off, as above.

## Suite

Every expectation is derived from geometry, not from a run, and every case runs on box and mesh variants where
the geometry allows. Tests live in `KhaozEngine.Game.Tests` under `KhaozEngine.Tests.Locomotion.Contacts` and
`KhaozEngine.Tests.Physics`, and certification rows in `KhaozEngine.Movement.Tests`.

| Group | Cases |
|---|---|
| Former refusals | Valley line gives the crease height. Box top corner gives the top. Sphere, capsule, upright cylinder and a lying log give tangent support, walkable or steep by slope. A down-facing one-sided triangle over a floor gives the floor. |
| Neighborhood backend | Completeness against a brute-force distance oracle over random probe poses near meshes, boxes, hulls and curved statics, sampled from `-band` to `3 * band` so a halved or doubled band fails every oracle. Membership at the #1342 poses includes both the tread face and the nosing faces. Capacity refusal is atomic. Back faces are excluded. Compound leaves contribute independently. A non-manifold edge and a duplicate triangle certify per triangle. Degenerate and unbounded sliver triangles are not members, and a far sliver never refuses. Non-finite mesh vertices are rejected at install. |
| Joins | Ridges of 10, 30 and 45 degrees on one static and on two statics give the axis-side plane. A valley gives the crease height. A convex mesh apex gives the minimum plane, a saddle gives each face's plane. A ramp sunk into a floor is not joined. A narrow bevel does not lift to its steep face. |
| Separate statics | A plateau beside a separate 70 degree face, axis 0.01 past the edge, gives the walkable plateau in both variants. |
| Partition independence | The same terrain built as one mesh and split into several statics gives the same support within `HeightError` over a grid of axes, including ridges, valleys, steps and fans. |
| Probe filter | `CullBackFaces` passes a down-facing and a vertical triangle and still hits an up-facing one. Shell sweeps still hit back faces. |
| Band | A nosing beside an in-band tread is ignored. A contact above the band refuses, and so does a step just above it. |
| Ground | The #1342 escapes in `GroundScenarioTests` are removed. All 32 stair rows and the cost rows pass their full expectations. `Held` and refused-target rows run on the over-capacity fan. A curved prop seats the body on its top. |
| Cost | Time and allocation per `Find` recorded in `docs/verification/`. Zero allocation asserted on ordinary scenes, after asserting walkable support. |
| Equivalence | A recorded fixture pins direct neighborhood queries, their certification at two slope limits and closest-feature queries bit for bit, at analytic poses over random boxes, hulls, meshes, curved solids and exact square-rim fans of 96 and 300 triangles. It is platform stable because no row passes through a Bepu sweep, whose contact distance differs in the last bits between x64 and arm64, or through MathF trigonometry. `FootSupport` results, which depend on sweep contacts, are covered by the behaviour rows above and not by digests. |

Every round runs on arm64 locally and on hosted x64 CI. No assertion pins a contact position finer than half the
contact skin, and no test depends on a micrometre sweep result.

## Files

- `KhaozEngine.Physics/SupportNeighborhoodQuery.cs`: the element, the result and `IPhysicsSupportNeighborhood`.
- `KhaozEngine.Physics/QueryFilter.cs`: the `CullBackFaces` property.
- `KhaozEngine.Physics.Bepu/`: the collector, polygon publication for boxes, hulls and meshes, tangent publication
  for spheres, capsules and cylinders, the join test, the per-child back-face filter in the sweep handler, the
  install guard for non-finite mesh vertices, and the reusable capture scratch and expansion arithmetic that make a
  warm query allocation-free.
- `KhaozEngine.Locomotion/Contacts/SupportCertification.cs` and `FootSupport.cs`: neighborhood certification and the
  switch from the feature query. `GroundPlacement.cs` rounds paced feet down so a paid climb never exceeds its
  budget, a phase 2 defect the honest neighborhood errors exposed.
- The program spec's support primitive section, known limits, invariant list and phases table, the phase 2 spec's
  refusal rows, and `docs/INDEX.md`.

## Boundaries

No `MoveTuning`, wire, navigation or consumer change. The legacy stepper stays byte-unchanged. `QueryCapsuleFeature`
and its proofs stay as they are. Phase 2b rides 20.30.1 under the release ritual.

## Risks

- **Backend completeness.** The proof obligation moves from "this is the unique closest feature" to "nothing within
  the band is missing". The brute-force oracle rows carry that proof.
- **Join tolerance.** The join test decides creases between statics at the contact band. Authored geometry that
  misses by more than 0.1 mm behaves as a crack. The partition rows pin the boundary.
- **Curved arithmetic.** Closed forms lose precision near tangency and where a normal's Y vanishes. There the
  element's error widens rather than refusing, up to about 5 mm of witness error on contacts parallel to the probe.
  A normal whose Y bound reaches zero contributes nothing, like a vertical face, and a steep normal never
  contributes walkable support.
- **Query cost.** Larger neighborhoods cost more per call. The capacity bound caps it. Time per `Find` misses its
  target and is the gate [#1334](https://github.com/APKiwiOrg/KhaozEngine/issues/1334) closes before adoption.
