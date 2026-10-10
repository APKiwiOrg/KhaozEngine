# Capsule feature finite mesh proof argument

Status: independently reviewed bounded mesh slice. Focused verification passed 16 mesh cases and
28 existing polyhedron/lifetime cases, zero failures/skips. Exact-file format ran both analyzer
stages for backend and test projects and changed no files. Evidence is recorded in
`docs/verification/2026-10-08-feature-mesh-green.json`. This slice does not complete Task 5's remaining
corner/support-consumer proof or authorize movement, navigation, a release, a game adoption or a
bridge-repair claim.

Validated expanded-admission slice: the [installed-pose argument](CAPSULE-FEATURE-INSTALLED-POSE-PROOF-2026-10-08.md)
records the closed-band common affine certificate and operational correspondence limits. Independent
source/math review, 124 arithmetic/geometry cases, 39 movement correspondence/policy cases and scoped
format passed. Earlier exact-rigid admission restrictions below describe their historical subset.
Current admission is governed by the linked certificate. APIs, caps and output ceilings are unchanged.


## Installed source and lifetime

The existing feature owner authenticates the exact owner-issued lease and its thread/current state
before acquiring the query monitor. Target lookup, mobility and selected receiver exclusions precede
capture. The mesh branch then reads that static's current description and the actual installed
`Shapes.GetShape<Mesh>` entry. It copies every `Triangles` entry and requires `Scale == Vector3.One`.
No descriptor array, ray, sweep, contact manifold or reflection supplies geometry authority.

The Bepu 2.4 source distinguishes the stored triangle buffer from its scale and tree. Its constructor
copies no separate geometric authority into the tree. The feature query therefore enumerates the
actual buffer, without relying on a tree traversal to prove complete source coverage.

Capture is uncached and read-only. Removing/readding a shape or rebasing the world is observed by
the next current lease's description/registry read. There is no cache keyed by a recycled slot and
no generation-changing lazy write. Existing mutation fencing and selected-view validation remain
unchanged. The successful result records the original receiver, lease, source owner, origin,
generation and target through the existing publication boundary. Later equal metadata cannot revive
the result under another lease instance.

Pinned primary source inspected:

- [Mesh 2.4.0](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Mesh.cs)
- [Triangle 2.4.0](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Triangle.cs)
- [Matrix3x3 2.4.0](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuUtilities/Matrix3x3.cs)

`Triangle.RayTest` computes the front normal as `Cross(C-A, B-A)`. Mesh capture uses that exact
orientation, including reversed triangles. Geometric front-side predicates use its signed plane,
without borrowing a collision contact's tolerance, smoothing or rejection threshold.

## Transform and arithmetic responsibility

Mesh capture reuses the existing installed-pose `ProveRigidPose` and affine vertex conversion from
the [polyhedron argument](CAPSULE-FEATURE-POLYHEDRA-PROOF-2026-10-07.md). Extraction into the shared
private `TransformVertices` changes no expression or gate. The installed quaternion must have
certified exact squared norm one. The actual pinned matrix must have exact orthonormal rows and
positive determinant one. That admitted dyadic quaternion subset also proves the pinned conversion
polynomial is exact. General near-unit rotations remain unsupported. A supplied pose enclosure
alone is not an installed-affine certificate.

Each local coordinate is finite and within 64 m. Every real represented-matrix dot product plus
installed translation remains a `FeaturePoint`, with an exact binary64 certificate where proved
and otherwise an outward enclosure. World vertices and capsule axis endpoints remain within
2048 m. The axis is exactly the upright supplied center plus/minus represented length/2, with the
existing radius, length and separation-band domain gates. No float proposal is used as geometry.

All additions, subtractions, products, quotients, dot/cross operations and square roots reuse the
existing arithmetic and vector owners, described in the
[numerical argument](CAPSULE-FEATURE-NUMERICAL-PROOF-2026-10-07.md). `FeatureNumber` strengthens an
exact flag only through the existing bounded product/distance predicates. An interval sign must be
strict unless an exact zero is proved. The shared exact operand ceiling stays 4096 bits. There is
no second rational, transform or vector kernel and no midpoint certificate.

## Complete exact source incidence

The source cap is 65536 triangles. It bounds source vertex IDs, source edge IDs and triangle-vertex
incidence entries by three times that count. Managed storage is constructed only after checking the
source cap. Dictionary insertion examines each of the three represented vertices and edges once.
Vertex equality canonicalizes signed zero and otherwise requires exact coordinate equality.
One float of separation remains a crack. There is no epsilon weld or plane-based vertex identity.

An unordered triple rejects duplicate triangles regardless of cyclic order or reversed winding.
Coincident triangle vertices and a nonpositive/unproved geometric normal norm refuse. Each directed
source edge maps to one undirected identity. A second triangle must traverse it in reverse. A third
triangle or equal directed traversal refuses Ambiguous, including backward neighbors that cannot
be discarded to manufacture an open edge.

Every vertex retains all raw triangle IDs. More than 256 incident faces refuses CapacityExceeded.
Its face link traverses only the two edges meeting that vertex in each incident triangle. The edge
checks give link degree at most two. A connected link with zero or two open ends is respectively a
cycle or path. Disconnected/pinched links and other open-end counts refuse Ambiguous. This traversal
visits each triangle at most once per source vertex, with total source incidence bounded by 3T.
It never compares every source triangle pair.

For two incident edge faces, bounded exact local `Orient3D` tests the second triangle's opposite
vertex against the first triangle's plane. The front convention reverses the usual cross product,
so a positive determinant places that opposite vertex behind the front plane and classifies a
convex crease. A negative determinant classifies a concave crease. An undecided determinant refuses.
The rigid affine map preserves this signed classification.

Zero determinant proves coplanarity. Opposite directed edge order and a positive dot of the actual
front normals prove equal orientation and opposite triangle-interior half-planes at the edge.
The finite triangles therefore meet only at the shared edge and form one connected finite patch
there. Its edge stratum is FaceInterior and retains both raw incident face IDs. A folded coplanar
pair cannot receive this classification. A single incident face is an exposed OpenBoundary.

A closed all-coplanar vertex link additionally proves a single planar revolution. Choose a nonzero
exact coordinate projection, whose restriction to the common plane is injective. Every incident
triangle sector must turn strictly with the same projected winding through less than half a turn.
Represented coordinate comparisons classify the two polar half-planes without rounded coordinate
subtraction. Counting directed transitions across the positive projection axis gives the link's
number of revolutions. Exactly one covers a neighborhood once, without overlapping sectors, and
permits FaceInterior. Multiple revolutions refuse Ambiguous. A two-triangle flat boundary is
OpenBoundary only when its two outer edge vectors are exactly collinear and oppositely directed.
Other one/two-triangle finite corners are Vertex. A selected larger vertex fan lacking that flat
cycle proof returns Unresolved, even after its complete source link is retained.

## Conservative locality and finite minima

Enumeration visits every source vertex, exact source edge and finite triangle, with no tree/query
callback truncation. A feature box takes the minima of all vertex enclosure lowers and maxima of
all vertex enclosure uppers in each coordinate. It contains the feature's entire convex hull.
The axis box similarly contains the full endpoint segment. For disjoint coordinate ranges,
outward subtraction encloses their nonnegative gap. Outward Square and Add give a lower bound on
every squared feature/axis distance. An exclusion requires that lower bound to be strictly greater
than the upper bound on `(radius + band)^2`. Unresolved boxes cannot exclude. No infinite plane or
rounded bounding point replaces this finite-domain proof.

The active-constraint closest-pair cases reuse the segment/polygon argument from the polyhedron
owner. Vertices project onto the axis with certified closed clamping. Edges project both axis
endpoints onto their strict interior and solve the two-parameter stationary pair only with a
positive Gram determinant and both parameters strictly interior. Boundary parameters belong to
the separately enumerated vertices/endpoints. A zero Gram determinant is a parallel minimum line
that reaches those closed parameter boundaries. Distinct equal witnesses remain Ambiguous.

Faces project each axis endpoint onto the real face plane using signed dot divided by positive
normal squared norm. The point is reconstructed through the same bounded operations. Three finite
oriented edge predicates, reversed consistently with the Bepu front normal, classify strict triangle
interior versus exact boundary or outside. The projection equation establishes coplanarity of its
real expression. A decided boundary is covered by the exact edge/vertex stratum. No projection on
an infinite plane qualifies without finite membership.

For a nearby face, both axis endpoint signed-plane values are inspected. Strictly negative values
at both endpoints prove the entire axis is behind it. Crossing a front half-space, or an undecided
endpoint side, refuses Unresolved. This deliberately avoids omitting a possible clipped-side
parameter boundary from one-sided minimization. For an otherwise admitted face, nonparallel axis
crossings of its strict finite interior refuse as zero distance. Finite boundary crossings are
covered by edge/vertex enumeration and also refuse when they lack a positive distance/normal.

Every retained candidate is a feasible finite point pair. Its squared distance must be proved
within the upper band. Strictly farther pairs are excluded only by exact distance-to-radius or
decisive outward ordering. Candidates strictly behind all incident faces cannot qualify. A
positive incident front side may coexist with exact zero on another incident wall. Mixed front/back,
uncertain or purely grazing neighborhoods refuse Unresolved. Thus the admitted subset has no
unexamined front-side clipping minimum. All feasible active-constraint minima on that subset are
enumerated. A resolved empty candidate set proves NoFeature within the requested band.

The local candidate cap is 128. Reaching it refuses the entire operation. It cannot select the
first 128 candidates or silently omit neighbors. Selection compares every retained candidate with
the current minimum, using exact represented-coordinate squared-distance comparison when certified
and otherwise decisive outward intervals. Uncertain ordering refuses. Only the same exact witness
pair on the same exact stratum can alias. Connected coplanar edge/vertex strata already encode their
proved patch. Disconnected equal minima and distinct witness continua are Ambiguous.

## Additional witness incidence

After selection, a linear pass over every raw source triangle proves no additional geometric
incidence was hidden by source-edge matching, side rejection or candidate generation. Known raw
incident faces follow from the selected construction and exact source link. Every other triangle
first receives a conservative witness/triangle box-disjoint test. A strict coordinate interval gap
proves absence. Otherwise a decided nonzero signed-plane value proves absence. A negative finite
edge predicate also proves absence, even if the plane sign is undecided.

If the remaining triangle contains the witness exactly, an additional unadmitted incidence returns
Ambiguous. If membership or coplanarity remains uncertain, it returns Unresolved. This catches
touching/overlapping triangles or T-junctions without exact source adjacency, including backward
triangles that still participate in geometric incidence. The pass scans T triangles once at the
one selected witness. It does not quadratic-scan source triangle pairs or silently turn an unproved
neighbor into a boundary. The result certifies this closest witness neighborhood, not a globally
embedded or watertight mesh.

## Closed band and atomic publication

The selected distance must also be at least `radius - band` by exact or decisive bounded comparison.
Zero band therefore needs proved equality, not interval overlap with contact. Deeper overlap refuses.
The shared private publisher then encloses sqrt(squared distance) minus radius, using squared-bound
verification of every proposed square-root endpoint. Exact represented-coordinate contact may use
the singleton zero separation interval. Normals use verified root/division normalization for the
actual geometric separation vector and every actual raw face normal.

Witness and normal float proposals are published only with outward Euclidean error bounds including
their float rounding. Exact product inequalities enforce the unchanged 0.25 mm position, 0.1 mm
separation-width and 0.00001 normal ceilings. Failure of any enclosure, root, norm or ceiling refuses.
The refactored private publisher applies the same gates to existing polyhedron output.

Raw face IDs remain the installed triangle buffer IDs within leaf zero. A seam with two incident
triangles reports two distinct IDs and shared FaceInterior incidence, including both oriented
normals. Private scratch contains every face before destination capacity is checked. Insufficient
destination capacity reports its exact selected requirement. The immutable result constructor runs
before the sole destination copy. All refusals, cap failures and exceptions therefore leave the
caller span untouched and carry no usable witness or written prefix.

## Remaining proof and consumer gates

The immutable sixteen-case test file and independent rational oracle passed on the implemented
backend, including the later assertions not reached during blanket-Unsupported RED. The shared
publication extraction also passed the existing 28 polyhedron/lifetime cases. Scoped format and
independent source/proof review passed. Shared numerical primitives were unchanged, so their prior
focused evidence is retained without a repeated run.

General represented rotations, non-unit mesh scale, nested/nonconvex compound leaves and curved
shapes remain unsupported. Unresolved rational witness membership/contact/order can still refuse.
Front-half-space crossings, purely grazing/mixed front neighborhoods and larger unproved vertex
fans deliberately return Unresolved. Source and incident caps apply to complete capture, including
remote neighborhoods. No cached/index-performance fit or actual bridge source-cap fit is claimed.

Task 5 still needs its six origin corners and translations, crease/concavity/vertex and work-cap
controls, actual top/wall eligibility and the movement-facing contract proof. Runtime support,
navigation anchors, strict raw-layer arrival and the unchanged consumer bridge proof remain gated.
