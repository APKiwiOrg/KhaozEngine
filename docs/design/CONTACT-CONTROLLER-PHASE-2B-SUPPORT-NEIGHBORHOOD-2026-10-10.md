# Contact controller phase 2b: support neighborhood

Date: 2026-10-10. Detailed spec for phase 2b of [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438). The
program, body model, support rule and invariants are in
[CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md](CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md). Phase 1 and
[phase 2](CONTACT-CONTROLLER-PHASE-2-GROUND-CORE-2026-10-10.md) are on main at `b51f4fdf9`. Status: written for
owner review.

## Purpose

Phase 1 certifies support conservatively and refuses what it cannot certify. Phase 2b replaces every one of those
refusals with a certified result, so no game adopts a controller that holds a body in place on ordinary
geometry. The owner ruled that phase 2b lands before any adoption, and that curved props are standable.

It closes:

| Issue | Gap today |
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

Five of the seven gaps share one cause. `FootSupport` asks the backend for the unique closest feature of the one
static each probe sweep reports. Uniqueness forces a refusal on every tie, at a face and edge boundary (#1342) and
between two valley faces (#1329). Vertex fans need a cone proof the backend lacks (#1330). One static per sweep
loses the other statics at the same contact (#1340, #1333) and makes support depend on how a scene is split into
statics.

The support rule does not need a unique feature. It takes the highest certified contribution over every surface
the footprint touches. A real surface at the contact that is included in addition can only be a correct
contribution. A surface that is missing is the defect. Phase 2b therefore certifies the complete set of surfaces at
the probe's contact, and completeness replaces uniqueness as the property the backend proves.

## Support neighborhood query

A new method on the existing optional capability `IPhysicsCapsuleFeatures`:

```csharp
namespace KhaozEngine.Physics;

public enum SupportElementKind : byte { Polygon, Tangent }

public readonly record struct SupportElement(StaticHandle Static, SupportElementKind Kind, int ElementId,
    Vector3 Normal, float NormalError, Vector3 Witness, float PositionErrorMetres,
    double SeparationLower, double SeparationUpper, int VertexStart, int VertexCount);

public readonly record struct SupportNeighborhoodResult(CapsuleFeatureStatus Status, int Elements, int Vertices,
    int RequiredElements, int RequiredVertices);

SupportNeighborhoodResult QuerySupportNeighborhood(IPhysicsQueryLease lease, CapsuleShape probe, Pose pose,
    float bandMetres, Span<SupportElement> elements, Span<Vector3> vertices, QueryFilter filter = default);
```

The implementation plan pins the final field names. The contract is fixed here:

- **Membership.** Every front-facing surface element of every selected static whose separation from the probe can
  be at most `bandMetres`. Membership is decided by the lower bound of the separation interval, so an element is
  included whenever it may lie within the band. A tie between features is never a reason to refuse.
- **Polygon elements.** A planar face of a box, convex hull or mesh: its front normal, its vertices in the vertex
  span, its witness (the closest point of the face to the probe) and the error bounds the feature query already
  publishes. Coplanar triangles of one mesh may be published as one polygon or as separate triangles. Both give
  the same certified result.
- **Tangent elements.** A point on a sphere, a capsule, a cylinder side or a cylinder cap: the witness and the
  surface normal there, with bounded error from closed-form arithmetic. A cylinder cap is planar and is published
  as a tangent element with the cap normal. Tangent elements have no vertices.
- **Sidedness.** An element whose front faces away from the probe is not a member. Boxes, hulls and curved
  primitives are closed solids. Mesh triangles use the winding `Cross(C - A, B - A)` the feature backend already
  defines.
- **Order.** Elements are ordered by static handle, then element id, so certification is deterministic.
- **Refusals.** `CapacityExceeded` when either span is too small, atomically, with the required sizes. `Ambiguous`
  for a non-manifold mesh, meaning three triangles on one edge, inconsistent traversal or duplicate triangles.
  `Unsupported` for a pose outside the proven domain or a layer filter, as the feature query does today. Nothing
  else refuses.
- **Lease.** The result is bound to the lease and receiver like `CapsuleFeatureResult`, and
  `AssertFeatureCurrent` gains an overload for it.
- **Compounds.** Each convex leaf contributes its own elements. A compound no longer refuses because one leaf is
  curved.

The broadphase collector behind it is backend-internal. Swimming's `IPhysicsCapsuleContacts` enumerates contacts
over the same candidate set. When phase 4 integrates `feature/swimming-contract`, both queries share the one
collector, so there is one membership definition and two projections of it. `QueryCapsuleFeature` stays as it is
for its existing callers.

## Certifying support from the neighborhood

The support rule is unchanged. `SupportCertification` gains one entry point that certifies a whole neighborhood,
and every contribution is an interval with the existing outward-rounded arithmetic.

- **Walkable polygon.** Contributes `min(its plane at the axis, its witness height, the plane at the axis of every
  walkable polygon joined to it)`. On one static this reproduces the phase 1 convex crease rule, so a ridge
  supports its axis side. It gives the same answer when the ridge is split across statics.
- **Join.** Two walkable polygons are joined when they meet within the contact band and each lies on or below the
  other's plane within the contact band. The test is geometric for every pair, inside one static and across
  statics, so a scene's partition into statics cannot change support. Polygons that cross each other's planes,
  such as a ramp sunk into a floor, are not joined, and the higher surface wins as it physically does.
- **Concave creases.** A concave pair fails the join test and adds no cap. Each face contributes its own plane, and
  the maximum gives the valley line its crease height.
- **Vertices.** No vertex classification is needed. A vertex's polygons and their pairwise joins carry it, so a
  convex apex gives the minimum of its planes, a box corner gives its top, and a saddle gives each face's own
  plane.
- **Tangent element.** Contributes `min(its tangent plane at the axis, its witness height)`, walkable or steep by its
  normal. Tangent elements are never joined. On a boulder the axis probe certifies the top directly, and the leg
  probe's contact on the side is lower and never wins.
- **Steep.** A polygon or tangent element whose normal fails the slope limit contributes as steep support by the
  phase 1 steep rule. Vertical faces contribute nothing, as today.
- **Selection.** Unchanged. Contributions from both probes and terrain qualify by the inclusive band, the highest
  midpoint wins, and the phase 1 tie order holds.

A join decided at exactly the band tolerance can switch a cap on or off. That is a discontinuity of at most
`FootRadius tan(theta)` for geometry whose faces meet within 0.1 mm. A gap wider than the band is a crack, and the
disc bridges it on its rims as the program table already says.

## Proposals

`FootSupport` keeps its two probes, the axis probe and the leg probe, each one sweep. A probe hands its contact
pose to the neighborhood query instead of naming a single static.

- **Back faces.** `QueryFilter` gains `CullBackFaces`. With it, `SweepCapsule` skips mesh triangles whose front
  faces away from the sweep's origin, through the backend's per-child sweep filter. Vertical triangles are kept.
  Bepu's contact generation already treats mesh triangles as one-sided, so the support probe now agrees with the
  simulation. It passes a down-facing one-sided triangle and reaches the floor beneath. Shell sweeps do not set the
  flag, and a back face stays a wall to the shell.
- **Hidden lower surface.** Selection takes the highest contribution, so a lower surface under a higher one only
  mattered when the higher one was excluded. Back faces were the only exclusion, and the probe now passes them.
  The phase 1 known limit closes with no third probe.

## Refusals and held bodies

Support refuses only on a neighborhood refusal: capacity, a non-manifold mesh or an unsupported pose. These are
content or configuration defects, and the refusal names them. `GroundCore` keeps `Held` for a refused start and
blocks a refused target. Its rows move from the sphere fixture to a non-manifold mesh fixture.

`FootSupport` sizes its spans at 64 elements and 512 vertices per probe. A neighborhood larger than that is a
capacity refusal, never a truncation.

## Cost

Each probe still makes one sweep and one certification query. The neighborhood query replaces the feature query,
so `GroundCore`'s asserted query counts do not change. A neighborhood query does more work than a closest-feature
query. Phase 2b measures time and allocation per `FootSupport.Find` on flat ground, stairs, a mesh fan and a
curved prop, and records them for [#1334](https://github.com/APKiwiOrg/KhaozEngine/issues/1334).

## Invariant this phase adds

To the program list, as invariant 9: support is independent of how static geometry is partitioned into statics,
for surfaces that meet within the contact band.

## Suite

Every expectation is derived from geometry, not from a run, and every case runs on box and mesh variants where
the geometry allows. Tests live in `KhaozEngine.Game.Tests` under `KhaozEngine.Tests.Locomotion.Contacts` and
`KhaozEngine.Tests.Physics`.

| Group | Cases |
|---|---|
| Former refusals | Valley line gives the crease height. Box top corner gives the top. Sphere, capsule, upright cylinder and a lying log give tangent support, walkable or steep by slope. A down-facing one-sided triangle over a floor gives the floor. |
| Neighborhood backend | Completeness against a brute-force distance oracle over random probe poses near meshes, boxes, hulls and curved statics. Membership at the #1342 poses includes both the tread face and the nosing faces. Capacity refusal is atomic. Back faces are excluded. Compound leaves contribute independently. |
| Joins | Ridges of 10, 30 and 45 degrees on one static and on two statics give the axis-side plane. A valley gives the crease height. A convex mesh apex gives the minimum plane, a saddle gives each face's plane. A ramp sunk into a floor is not joined. |
| Separate statics | A plateau beside a separate 70 degree face, axis 0.01 past the edge, gives the walkable plateau in both variants. |
| Partition independence | The same terrain built as one mesh and split into several statics gives the same support within `HeightError` over a grid of axes, including ridges, valleys, steps and fans. |
| Probe filter | `CullBackFaces` passes a down-facing triangle and still hits an up-facing and a vertical one. Shell sweeps still hit back faces. |
| Ground | The #1342 escapes in `GroundScenarioTests` are removed. All 32 stair rows and the cost rows pass their full expectations. `Held` and refused-target rows run on the non-manifold fixture. |
| Cost | Time and allocation per `Find` recorded in `docs/verification/`. |

Every round runs on arm64 locally and on hosted x64 CI. No assertion pins a contact position finer than half the
contact skin, and no test depends on a micrometre sweep result.

## Files

- `KhaozEngine.Physics/CapsuleFeatureQuery.cs`: the element, result and method on `IPhysicsCapsuleFeatures`.
- `KhaozEngine.Physics/QueryFilter.cs`: `CullBackFaces`.
- `KhaozEngine.Physics.Bepu/`: the collector, polygon element publication for boxes, hulls and meshes, tangent
  element publication for spheres, capsules and cylinders, and the per-child back-face filter in the sweep
  handler. New files follow the existing `CapsuleFeature*` naming.
- `KhaozEngine.Locomotion/Contacts/SupportCertification.cs` and `FootSupport.cs`: neighborhood certification and the
  switch from the feature query.
- The program spec's support primitive section, known limits and invariant list, the phase 2 spec's refusal rows,
  and `docs/INDEX.md`.

## Boundaries

No `MoveTuning`, wire, navigation or consumer change. The legacy stepper stays byte-unchanged. `QueryCapsuleFeature`
and its proofs stay as they are. Phase 2b rides the next engine version under the release ritual.

## Risks

- **Backend completeness.** The proof obligation moves from "this is the unique closest feature" to "nothing within
  the band is missing". The brute-force oracle rows carry that proof, and the plan gives them their own task.
- **Join tolerance.** The join test decides creases between statics at the contact band. Authored geometry that
  misses by more than 0.1 mm behaves as a crack. The partition rows pin the boundary.
- **Curved arithmetic.** Closed forms lose precision near tangency and where a normal's Y vanishes. There the
  element's error widens rather than refusing. A normal whose Y bound reaches zero contributes nothing, like a
  vertical face, and a steep normal never contributes walkable support. The plan proves the bounds over the
  backend's supported radius and pose domain.
- **Query cost.** Larger neighborhoods cost more per call. The capacity bound caps it, and the cost rows measure it
  before phase 3 builds on it.
