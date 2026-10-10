# Capsule feature correspondence

Status: written design approved by the owner on 2026-10-07 at `44acfe9b9`. Execute through the
[implementation plan](https://github.com/APKiwiOrg/KhaozEngine/blob/5eceb2c16/docs/superpowers/plans/2026-10-07-capsule-feature-correspondence.md). Runtime low-lip support
and navigation anchors remain gated on the proof below.

## Intent and boundaries

Provide an optional Physics query that can prove which finite geometric feature a capsule relates to
on a selected static body. The immediate consumer is the legacy low-lip repair in
[#1270](https://github.com/APKiwiOrg/KhaozEngine/issues/1270), supporting
[Grimhollow #416](https://github.com/APKiwiOrg/Grimhollow/issues/416). It must distinguish an identified
top-and-wall corner from unrelated or ambiguous nearby geometry. It must not turn a contact normal,
same-body ray hit, extended plane or rounded contact point into a geometric-face certificate.

The existing 24-case observations are preserved in `74e65de31` and
[their measurement file](../../design/LOW-LIP-FACE-OBSERVATIONS-2026-10-07.json). All six origin mesh
corner rays missed. Their contact points lay about 0.03 to 5.08 micrometres outside the known edge.
Interior controls passed. This is evidence against the proposed contact-point ray shortcut, not a
backend-wide numerical bound.

Physics supplies geometry. Locomotion retains support eligibility, slope, rise, clearance and
footprint policy. Navigation retains raw surface/layer identity and its 1 mm arrival requirement.
This work does not introduce native map identities, a surface index API, a bake format, a swept-path
certificate, a game pin, a world edit or a release. It does not widen collision or arrival tolerances.

## Source baseline and ownership

The design is grounded in engine main `e34cd884`, low-lip checkpoint `866105c09`, and the swimming
source assessment `acd452762`. `RayHitHandler` discards child identity and competing hits.
`SweepHitHandler` has no face identity and clears point/normal for initial overlap. A static can own
many triangles or compound children. `ShapeFactory` builds unit-scale meshes and flattens compounds
into convex leaves, including recentered hulls. Querying the original mutable shape descriptor would
not prove the geometry actually installed in Bepu.

The existing `IPhysicsQueryLeaseSource` and `IPhysicsQueryLease` in swimming `acd452762` provide the
required owner/origin/generation read interval. Their signatures remain unchanged. They are not on
the current main baseline. Before implementation, coordinate a separately verified prerequisite
integration with their owner. Do not copy a second lease mechanism or silently import the swimming
branch. Without that prerequisite, only public contract tests and mathematical kernel work may be
planned, and no live backend implementation can be declared ready.

Pivot owns this query design and the legacy consumer. Swimming owns its certified-sweep work and
future explicit locomotion. Both touch the private Bepu `QueryView` implementation, so edits there
must be serialized and reconciled. Migration retains native geometry/support/space producers.

## Chosen approach

Derive a closest capsule-axis/finite-geometry relation from the actual installed backend shape.
Do not repair or inset an old sweep point. The old sweep may choose the current static and propose a
capsule pose, but the new query independently validates that pose against current selected geometry.

| Criterion | Optional finite-feature query | Expose raw backend geometry to callers | More contact-point rays |
| --- | ---: | ---: | ---: |
| Establishes the required relation | 9 | 8 | 2 |
| Keeps backend and policy ownership clear | 9 | 3 | 6 |
| Bounded implementation simplicity | 5 | 6 | 9 |
| Total | 23 | 17 | 17 |

The optional query keeps geometry and numerical proof in the backend. Returning raw shapes would
duplicate geometry algorithms in consumers. More rays cannot recover discarded incidence or prove
that a nearby face corresponds to the capsule.

## Proposed public contract

One cohesive `KhaozEngine.Physics/CapsuleFeatureQuery.cs` contains the interface, result, face and
status types. No Bepu or MapDoc type crosses the dependency-free seam. The intended operation is:

```csharp
CapsuleFeatureResult QueryCapsuleFeature(
    IPhysicsQueryLease lease,
    StaticHandle target,
    CapsuleShape capsule,
    Pose pose,
    float maximumSeparationMetres,
    Span<CapsuleIncidentFace> faces,
    QueryFilter filter = default);

void AssertFeatureCurrent(in CapsuleFeatureResult result, IPhysicsQueryLease lease);
```

This belongs to optional `IPhysicsCapsuleFeatures`, implemented by the Bepu owner and its restricted
query view. Legacy `IPhysicsWorld`, ray, sweep, contact and lease signatures remain unchanged.
The target handle is scoped to `lease.SourceWorld`. It is never a portable identity. Callers capture
it from the same selected world within that read interval. The backend validates the actual live
lease and target mapping, not merely a user implementation whose `AssertCurrent` happens to return.
An equal-valued handle originating in another world is indistinguishable from a local integer. The
same-source capture requirement is a caller obligation, not a capability this API claims to detect.
No separate portable static token is introduced.

Inputs are validated before writing output. A null lease/capsule, malformed pose, non-finite or
negative separation, unsupported filter value, wrong-source lease, expired lease or wrong thread is
an argument/lifecycle error. Supported finite inputs beyond the advertised numerical domain return
`Unsupported`. Disposal follows the existing owner/view/lease exceptions. A valid query never falls
back from a restricted view to its unrestricted source.

Authenticate the owner-issued lease and run its thread/current checks before entering the query
monitor. A foreign thread must fail before trying to acquire a monitor held by the issuing thread.
The lease authenticates its owner, not a restricted view. Independently validate the exact live query
receiver and retain its exclusions throughout this operation.

`CapsuleFeatureStatus` has default `Unresolved`, plus `Complete`, `NoFeature`, `Unavailable`,
`Unsupported`, `Ambiguous` and `CapacityExceeded`. Meanings are distinct:

- `Complete` supplies one proved closest finite feature, or an exactly connected coplanar patch,
  and every incident face required to classify it. It does not mean usable support or world clearance.
- `NoFeature` proves no qualifying feature within the requested separation band on this target.
- `Unavailable` means the target is missing, removed or excluded by the selected view/filter.
- `Unsupported` covers a shape, transform or numeric domain outside the implemented proof.
- `Ambiguous` means fully observed competing closest features or non-manifold topology prevents one
  feature relation. `Unresolved` means arithmetic or incomplete geometric proof cannot decide.
- `CapacityExceeded` means an internal work cap or caller face buffer prevents a complete answer.

All non-Complete results report zero written faces and expose no usable feature or support data.
No partial prefix is consumable. Capacity diagnostics may report a required count only when known.
Every refusal and exception leaves the caller face span untouched. Compute into private bounded
scratch, validate the complete answer and destination capacity, then copy it atomically at the API
boundary. The zero written-count contract prevents old span contents from becoming a new answer.

The immutable result carries the original lease instance, exact query receiver, source owner, origin
and generation captured by the lease, target handle, backend-local leaf/feature identity, feature kind,
witness points on the
capsule axis and geometry, separation interval, position error bound, normal error bound and face
count. Faces carry backend-local face identity, oriented geometric unit normal with its error bound,
and incidence kind. IDs are process-local diagnostics, not wire or bake identities. They cannot be
passed back as authority after the lease expires.

Witnesses use query-frame `Vector3` values with explicit outward error bounds. Separation is the
axis-to-finite-geometry distance minus capsule radius, enclosed by double lower/upper bounds. The
normal is the geometric capsule/feature separation direction, not a smoothed contact-manifold normal.
The result refers to the exact supplied pose. `AssertFeatureCurrent` on the original capability first
requires reference equality with the original lease and receiver, then checks the authentic lease's
current/thread state and live receiver. Call it before geometry consumption. Metadata equality is insufficient because a
new lease without intervening mutation can have the same owner, origin and generation. A later lease
must never revive old output. After expiry, fields may be logged as historical diagnostics only.

## Supported geometry and completeness

The first backend supports upright capsules against boxes, convex hulls, unit-scale triangle meshes
and flattened compounds containing only supported polyhedral leaves. Spheres, cylinders, curved
capsules, unknown shape types and unsupported compound leaves refuse this optional operation.
Their existing collision and low-prop behavior remains unchanged.

Upright means the query quaternion has exactly zero X and Z components, with the norm check below.
Yaw does not change the capsule geometry. Other query-axis orientations return `Unsupported` in the
first implementation. This restriction does not limit the orientation of the target static geometry.

Read actual `TypedIndex` geometry and installed poses under the lease. Hull centroid wrappers and
compound child poses must be included exactly as installed. Do not retain caller-owned arrays as the
geometry authority. An immutable adjacency index may be cached from installed local geometry and
discarded when its shape is removed. Rebase changes the captured pose/origin, not local adjacency.
Lazy derived-cache construction may write under the exclusive query gate without changing physical
geometry generation. Actual shape, pose, rebase and lifetime mutations retain the existing fencing.

For a convex solid, enumerate finite face polygons and their exact vertex/edge adjacency. For a mesh,
enumerate actual finite triangles and complete incidence at the witness feature. Only exact matching
represented vertices/edges may join, with signed zero canonicalized. Never epsilon-weld cracks or
join disconnected patches merely because their planes agree. Duplicate, non-manifold or inconsistent
winding neighborhoods refuse unless a separately proved exact alias rule covers them.

All candidates whose certified distance intervals can attain the minimum participate. A provably
separated farther feature may be discarded. An unresolved ordering returns `Unresolved`. Distinct
disconnected minima return `Ambiguous`. Coplanar adjacent triangles may form one finite patch only
when their union and orientation are proved. Its outer edges remain real boundaries.

The initial implementation accepts an isolated closest axis/geometry witness. A continuum of closest
points, such as an axis parallel to a wall face, may return `Ambiguous`. This is adequate for the
lower-endpoint/top-edge relation under investigation and avoids inventing an arbitrary representative.
The capsule axis inside a solid or an unresolved deep overlap returns `Unresolved`.

Mesh front/back membership follows the actual one-sided Bepu surface. Feature output distinguishes
face interior, open boundary, convex crease, concave crease and vertex. A front-side boundary may be
geometrically complete without a closed volume. Uncertain side or dihedral classification refuses.
The query never substitutes a triangle's infinite plane for its finite domain.

The initial work caps are 64 convex leaves, 256 faces per convex leaf, 65,536 source mesh triangles,
128 local candidate features, 256 incident faces and 4,096 bits per exact predicate operand. These
are refusal limits, not evidence that the game bridge fits. Index construction has the same bounded
source limits. Every broadphase/locality exclusion must be conservative under the numeric enclosure.
Overflow or incomplete adjacency returns no usable result. The actual bridge must pass these limits
before the repair is declared applicable. Raising a cap requires a new reviewed finite proof.

## Numerical proof domain

Do not derive an error budget from the largest error observed in 24 tests. Treat represented input
floats and installed backend geometry as the data, then enclose every transform, distance and normal
operation. Use outward-rounded double intervals for arithmetic and square roots. Exact dyadic
predicates are the bounded fallback for topology and sign decisions. A fixed epsilon is not a
topology predicate. Exceeding the predicate budget returns `Unresolved`.

The initial target domain is query-frame coordinates within 2,048 m on each axis, local polyhedral
extents at most 64 m, capsule radius 0.01 to 2 m, cylindrical length 0 to 8 m, and separation band
0 to 0.01 m. Only finite rigid-pose transforms are supported. The quaternion squared-norm enclosure
must lie within `1 +/- 2^-20`, and the installed backend's represented transform is authoritative.
Do not silently normalize the pose into different geometry. The implementation must enclose the
actual quaternion/matrix conversion and its departure from orthogonality. Unsupported normalization,
non-unit mesh scale, degenerate faces or ill-conditioned transforms refuse.

The acceptance ceilings are position error at most 0.25 mm, separation interval width at most
0.1 mm, and unit-normal vector error at most 0.00001. These are proof targets, not granted numerical
facts or offsets applied to geometry. Output float rounding is included. A result exceeding any
ceiling is `Unresolved`, even if the nominal point looks correct. If proof cannot meet these ceilings,
stop and review the domain or algorithm. Do not widen them until fixtures turn green.

Independent tests use exact dyadic/rational predicates and high-precision distance enclosures on
finite cases. They must not call the production classifier to construct expectations. Include both
ends of the domain, one representable value either side of boundaries, rigid transforms, reversed
winding, skinny/degenerate faces, disjoint coplanar surfaces and mesh/compound aliases. Cross-platform
behavior must retain conservative status and bounds. No local stress loop is authorized.

A written operation-level enclosure argument is required in addition to those finite tests. Include
transform conversion, dot/cross products, division, square roots, witness reconstruction and output
rounding. Square-root endpoint validity must be checked by squared bounds, not assumed from one
platform's observed `Math.Sqrt` result. An operation that cannot prove its enclosure refuses the query.

## Legacy support eligibility

The existing near-flat sweep path stays intact. For its walkable but non-flat corner case, the caller
may propose the swept resting capsule pose to this query in the same read interval. A point or body
from another frame is not accepted. A complete answer must enclose near-contact at that actual pose.
It cannot certify a path from the starting pose or clearance against another body.

For this caller, the whole separation interval must lie in [-0.1 mm, +0.1 mm]. This is a narrow
corroboration condition, not a correction applied to either pose or geometry. It does not replace
the existing clearance check or widen the 1 mm arrival requirement.

The caller accepts only a lower-endpoint feature whose entire proved separation-direction interval
points upward and remains inside the unchanged walkable slope band. Exactly one upward near-flat
incident plane or connected coplanar patch must qualify under the existing flatness threshold.
All intervals must lie wholly on the accepted side of thresholds, otherwise refuse.

A top/wall convex crease is eligible when the finite witness belongs to that proved crease, its
normal cone contains the capsule separation direction, the top is the sole qualifying supporting
patch, and every incident finite face has a proved nonnegative local separation derivative in the
upward direction at this pose. A vertical side may have zero derivative. This is local eligibility,
not a certificate for a finite upward path. A front-side open top boundary may qualify under
the equivalent finite-patch rule. Concave creases, vertices with competing tops, uncertain topology,
roof/underside features and disconnected support alternatives refuse.

Do not select the flattest nearby face. Do not infer a top from an edge contact normal. At the proposed
resting pose, the contact-distance enclosure and finite membership must independently corroborate the
sweep candidate. The query does not supply a new height or snap the capsule to its face plane.

Existing rise, footprint, selected-view clearance and raw-layer ownership protections still apply.
Other bodies, ceilings and terrain remain part of those checks. The legacy bake-local anchor design
in [the repair proposal](https://github.com/APKiwiOrg/KhaozEngine/blob/5eceb2c16/docs/design/LOW-LIP-RESTING-PROOFS-2026-10-07.md) is unchanged. Explicit
swimming profiles must not inherit legacy Hold anchors implicitly. A failed feature query leaves the
old behavior in place, not a permissive substitute.

## Files and phased proof gates

The implementation plan will keep concerns in cohesive files:

- Physics `CapsuleFeatureQuery.cs` for the optional immutable contract.
- Bepu `BepuPhysicsWorld.CapsuleFeatures.cs`, `CapsuleFeatureGeometry.cs` and
  `CapsuleFeaturePredicates.cs` for selected access, finite geometry and numeric proof. Private
  `QueryView` gets forwarding only, coordinated with swimming. Shape-cache lifecycle edits stay at
  existing add/remove/dispose ownership points.
- Area tests for value invariants, lifetime/selection, polyhedral correspondence and independent
  numeric/ambiguity controls. Existing 24 observations remain a baseline, not the acceptance oracle.
- Only after those gates pass, `CharacterMovement.LowProp.cs` and a cohesive legacy resting-anchor
  helper used by `PhysicsNavBake.Profiles.cs`. Existing traversal-probe tolerance remains unchanged.

Gate 1 is contract RED/GREEN and exact prerequisite integration. Gate 2 is independent geometric and
numeric proof, including actual top/wall incidence and same-body unrelated-face refusal. Gate 3 is
low-lip runtime RED/GREEN with roof, wrong-layer, dome, wall, slope and seven-stair prior-art controls.
Gate 4 is actual profile candidates, bidirectional traversal, strict raw-point refusal and anchors,
then the unchanged consumer bridge Complete assertion and 600-step traversal bound on a separately
authorized released adoption. An engine-only test never claims that final game proof.

Gate 1 specifically includes foreign-thread refusal before monitor acquisition, wrong owner,
different selected receiver, disposed receiver, removed/excluded target, expired lease and consecutive
same-generation leases. Verify untouched output on every refusal/exception and deterministic atomic
output on success. Exercise lazy cache construction under the read lease without a generation change,
then prove actual geometry mutations remain fenced. A bounded synchronization test must release its
gate in `finally` and must not become a stress or timing loop.

Final engine verification includes fresh-main reconciliation, full required Release build/tests,
format and guards, review, integration and coordinated post-push packaging. No tag, release, game
pin or world mutation is authorized by approving this spec. If any stage needs broader collision
behavior, a bake format or native map producer, return for a separate decision.
