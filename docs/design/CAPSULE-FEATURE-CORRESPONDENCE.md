# Capsule feature correspondence

Status: implemented optional Physics query. This reference describes finite geometry correspondence,
not locomotion support or a swept-path certificate.

`IPhysicsCapsuleFeatures` in `KhaozEngine.Physics` exposes `QueryCapsuleFeature` and
`AssertFeatureCurrent`. The Bepu world and its restricted query view implement it. The dependency-free
contract exposes no backend shape and leaves existing ray, sweep and contact APIs unchanged.

## Ownership and atomic output

The target handle belongs to the source world of the supplied owner-issued `IPhysicsQueryLease`.
Capture it from the same selected world during that interval. An integer handle from another world
cannot be distinguished from an equal local integer, so handle provenance remains a caller obligation.

The backend authenticates its concrete lease and checks thread/current state before entering the
query gate. A foreign-thread query therefore fails before waiting on a gate held by the issuing thread.
The selected receiver retains its own exclusions. It never falls back to the unrestricted source.

Results bind the original lease instance, exact receiver, source, origin, generation and target.
`AssertFeatureCurrent` requires that original receiver and lease by reference. Equal metadata on a
later lease does not revive an old result. Backend leaf and feature IDs are local diagnostics, not
portable world, network or navigation identities.

All validation, finite-feature classification, numerical proof and capacity checks finish before the
caller span is written. Every refusal or exception leaves it untouched. A non-Complete result has
zero written faces and no usable witness. Old contents of the destination are never a result prefix.

| Status | Meaning |
| --- | --- |
| `Complete` | One proved closest finite feature and its complete incident-face set. No support or clearance promise. |
| `NoFeature` | No qualifying feature in the requested band on the selected target. |
| `Unavailable` | Missing, removed, excluded or filter-ineligible static. |
| `Unsupported` | Shape, transform or numerical domain outside the implemented proof. |
| `Ambiguous` | Competing minima or topology prevent one feature relation. |
| `Unresolved` | Arithmetic or geometry cannot establish the answer. This is the default. |
| `CapacityExceeded` | Work or destination capacity cannot hold a complete answer. A known required count is diagnostic. |

Malformed poses, capsules, separation values and unsupported filters are argument errors. Lease and
receiver expiry remain lifecycle errors, rather than negative geometric answers.

## Installed finite geometry

The geometry authority is the live installed shape and pose read under the lease. It includes hull
centroid compensation and actual compound child composition. Caller-owned shape descriptors and
rounded sweep/contact points cannot establish that geometry. This query captures private, uncached
managed snapshots. It does not retain an adjacency cache across calls.

The first query supports upright capsules against boxes, convex hulls, unit-scale meshes and flattened
compounds of supported convex leaves. Query quaternion X and Z must be exactly zero. Yaw does not
change the upright capsule. Curved leaves and unsupported compound leaves refuse the whole operation.
This optional query's limits do not restrict ordinary collision against those shapes.

Finite face, edge and vertex incidence matters. Mesh adjacency joins exact represented vertices and
edges, canonicalizing signed zero. It does not epsilon-weld cracks or connect disjoint coplanar
surfaces. One-sided mesh eligibility follows installed winding. A triangle's infinite plane is never
a substitute for its finite triangle. Connected coplanar patches retain their outer boundaries.
Disconnected equal minima are ambiguous. Unproved ordering and deep overlap refuse instead of
choosing the flattest nearby face or an arbitrary contact representative.

| Bound | Value |
| --- | --- |
| Convex leaves | 64 |
| Faces per convex leaf | 256 |
| Convex vertices and face entries | 508 and 1524 |
| Source mesh triangles | 65,536 |
| Local closest candidates | 128 |
| Incident faces | 256 |
| Exact predicate operand | 4096 bits |
| Local installed vertex offset | At most 64 m on each axis |
| Query-frame coordinate envelope | 2048 m on each axis |
| Capsule radius and cylindrical length | 0.01 to 2 m, and 0 to 8 m |
| Maximum separation band | 0 to 0.01 m |

These are refusal bounds, not evidence that any particular world fits. Unsupported, unresolved and
capacity statuses remain distinct. For example, an out-of-envelope query axis can return Unresolved.

## Numerical meaning

Witnesses describe the exact supplied pose in query-frame coordinates. Separation is distance from
the capsule axis to the finite geometry minus the radius, enclosed by double lower/upper bounds.
Normals are geometric directions, not smoothed manifold normals. Represented transforms are
authoritative. Silently normalizing a pose would change the geometry being certified.

Outward interval arithmetic encloses transforms, reconstruction, division, square roots and output
rounding. Bounded exact dyadic predicates decide topology and signs. A fixed epsilon cannot establish
incidence. An operand beyond the predicate budget refuses. Square-root endpoints are checked by
squared bounds. Quaternion admission uses the represented transform and the `1 +/- 2^-20` norm domain.

Publication requires position error at most 0.25 mm, separation interval width at most 0.1 mm and
unit-normal error at most 0.00001, including output float rounding. These ceilings are not offsets
applied to geometry. Failure to establish them gives Unresolved.

The operation-level arguments live with the [arithmetic proof](CAPSULE-FEATURE-NUMERICAL-PROOF-2026-10-07.md),
[polyhedral proof](CAPSULE-FEATURE-POLYHEDRA-PROOF-2026-10-07.md),
[mesh proof](CAPSULE-FEATURE-MESH-PROOF-2026-10-07.md) and
[installed-pose proof](CAPSULE-FEATURE-INSTALLED-POSE-PROOF-2026-10-08.md).
Independent geometry fixtures complement those arguments. Observed contact-point error is not a
backend-wide numerical bound.

## Consumer boundary

Physics proves geometry. Locomotion still owns slope, rise, footprint and clearance policy.
Navigation owns raw layer identity and its 1 mm arrival requirement. A Complete feature does not
certify a path from another pose, clearance against another body or a valid support height.
The legacy low-prop path continues to use its own sweep and footprint rules. The contact controller
uses its support-footprint capability. Neither implicitly acquires a support certificate from this API.
