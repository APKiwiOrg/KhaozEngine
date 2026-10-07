# Capsule feature polyhedra proof argument

Status: reviewed initial implementation with two verified applicability failures. The first
compiled invocation executed the two translated-domain regressions, both failing after ordinary
contact controls passed. The preserved 14-case RED remains the original geometry baseline. Whole
feature GREEN, format and corrected-source review remain parent-owned gates. This document does not
authorize a runtime consumer, a mesh backend, a release or a geometry tolerance change.

## Installed authority and read interval

The owner authenticates its own live read lease before acquiring the query monitor. Its existing
target lookup, mobility filter and exact selected-view exclusions run before geometry capture.
The private core receives the actual owner or selected receiver. No public lease/view signature
changes. A successful value records that receiver, the original lease instance and its owner,
origin and generation.

Capture reads `Statics.GetDescription`, its current `TypedIndex` and the installed registry under
that same interval. A box uses the installed half dimensions. A hull uses every actual face's
`HullVertexIndex` and `GetPoint`, never bundled point-buffer padding or a source descriptor. The
source index remains the vertex identity. Distinct source indices with equal represented positions
refuse rather than silently merging an alias.

The pinned hull implementation explicitly reverses its source indices in `ConvexHullTriangleSource`
to obtain externally counterclockwise triangles. Capture applies that one documented reversal to
every polygon. It then independently checks every outward supporting plane and every polygon's
winding. Per-face repair of inconsistent winding is not performed.

A flattened `Compound` uses its installed child indices and `LocalPose`, including hull centroid
offsets already folded into those poses. `Compound.GetWorldPose` supplies the exact backend's
represented composition. The captured leaf retains its shape index, installed local pose and
composed world pose. The composed represented pose is the authority, not an ideal composition of
caller descriptors. Root, child and composed poses must each pass the rigid numerical gate.

The implementation is intentionally uncached. All managed snapshots and work arrays are bounded
and local to this call. Removal, shape-slot reuse and disposal cannot leave an adjacency cache.
Rebase is observed by reading the current static pose again. Capture does not mutate generation or
the registry. Existing mutation fencing remains unchanged.

Pinned primary source inspected for this implementation:

- [ConvexHull 2.4.0](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/ConvexHull.cs)
- [Compound 2.4.0](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Compound.cs)
- [Box 2.4.0](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Box.cs)
- [Matrix3x3 2.4.0](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuUtilities/Matrix3x3.cs)

## Admitted numerical subset

This slice implements a conservative subset of the approved target domain. It does not claim the
whole near-unit quaternion band.

The query capsule is upright under the existing exact X/Z quaternion check and shared squared-unit
band check. Radius remains 0.01 to 2 m, cylinder length 0 to 8 m and separation band 0 to 0.01 m.
Its vertical endpoints must be exactly reconstructible as binary32 coordinates within 2048 m.
Yaw leaves this axis unchanged. Endpoint reconstruction that loses represented information refuses.

Installed rigid poses additionally require a proved exact squared quaternion norm of one, exact
orthonormal matrix rows and positive determinant one. All local vertices lie within 64 m on each
axis. All transformed world vertices must equal their real represented-matrix affine expression
exactly as binary32 values, within 2048 m. A finite transform outside this admitted subset refuses.
No pose is renormalized.

The stronger quaternion gate also certifies matrix conversion without a second quaternion kernel.
Every binary32 component is dyadic. Reduce an exactly unit quaternion to integer numerators over
a common minimal denominator `2^k`. For `k >= 1`, the sum of four squares is `4^k`. Modulo four,
minimality requires all four numerators to be odd. For `k >= 2`, their squared sum is four modulo
eight, contradicting `4^k`. Thus `k` is zero or one. The components are a single signed unit axis,
or four signed halves. The pinned conversion polynomial is exact binary32 arithmetic for these
values. This is an operation-domain gate, not box/hull or fixture-coordinate recognition.

The matrix is obtained from Bepu. Bepu also proposes each transformed point. A dot/translation
equality certificate verifies that proposal against the represented affine map. The shared
`RepresentedGeometryTransforms.PosePoint` still checks the approved domain and supplies its
conversion/rounding enclosure. No independent interval, BigInteger or quaternion transform kernel
is introduced. The admitted exact affine equality removes uncertainty for topology and exact
contact comparisons. It does not generalize that equality to rounded transforms that fail the gate.

## Expression and enclosure operations

`FeatureNumber` wraps the existing `GeometryInterval`. Uncertified additions, products and quotients
retain the shared outward intervals. An unresolved operand stays unresolved, including negation.
No midpoint becomes geometric data.

An exact flag is strengthened only from already exact operands. Addition proposes a represented
sum `s`, then uses the shared exact squared-distance comparator to prove `(s-a)^2 == b^2`.
The order of represented `s` and `a` must also match the sign of `b`. These two facts imply
`s-a == b`, including cancellation, signed zero and subnormals. A failed or over-budget comparison
retains only the interval. There is no second exact-add kernel or assumed zero floating residual.
The first independent review found no unsafe acceptance in this adapter within its stated subset.

Multiplication calls the shared bounded exact product comparator to prove that `a*b` equals the
proposed represented result. Division first uses the zero-free shared interval division, then
proves `quotient*denominator == numerator` with that same comparator. A failed or over-budget
comparison cannot set the exact flag. Its interval can still support a strict sign decision.
Only the shared kernel performs integer predicates, retaining its 4096-bit operand limit.

Dot and cross expressions compose these operations. Reconstructed points retain each coordinate's
complete enclosure, including division and multiply/add reconstruction. A nonbinary quotient is
an interval, never a rounded exact point. Finite membership and ordering must either have strict
outward sign bounds or a valid exact certificate. Uncertainty refuses.

## Full polyhedral topology

Each admitted leaf has at least four vertices and four faces. Source-coordinate duplicates refuse.
Every face contains distinct vertex IDs, has a nonzero projected orientation and is exactly planar
under the shared bounded `Orient3D`. All vertices outside that face must lie strictly behind its
outward supporting plane. Coplanar aliases and inconsistent winding refuse.

For every directed polygon edge, every other polygon vertex must have the same strict projected
orientation. The projection is injective on its already proved plane. This proves a simple strict
convex polygon, excludes collinear redundant vertices and checks its entire finite domain.

Every undirected edge must have exactly two oppositely directed incident faces. The complete
face graph is connected. Each vertex has at least three incident faces and a connected degree-two
face link. Euler characteristic must be two. Together with the strict supporting-plane checks,
these closed convex boundary conditions admit the complete solid, rather than an open collection
of convenient planes. No epsilon welding, local missing-face repair or approximate alias rule runs.

Face normals are cross products of that outward polygon's finite vertices. Strict convexity and
the two-face edge neighborhood prove a convex crease. A vertex reports its complete checked face
link. Face interior reports its own face. Per-face incidence equals the shared classified stratum.

## Exhaustive closest pairs

The axis is a closed segment. The finite solid boundary is partitioned into strict face interiors,
strict edge interiors and vertices. Every generated point is feasible in its stated stratum.
Cartesian feature bounds may exclude a feature only when an outward lower distance bound is
strictly greater than the upper contact-band distance. They are supersets of the finite geometry.
No infinite-plane exclusion substitutes for finite membership.

For each vertex, project to the axis and certify clamping to its closed parameter range. For each
edge, project both axis endpoints to the strict edge interior. Also solve the unconstrained
two-parameter stationary pair when the Gram determinant is strictly positive. Accept it only when
both parameters are strictly interior. Edge parameter boundaries are handled by the vertex cases.
Axis parameter boundaries are handled by endpoint cases.

If segment and edge directions are parallel, the stationary determinant is exactly zero. A
minimum line in the closed parameter rectangle reaches its boundary. The endpoint and vertex
cases already enumerate those boundary minima. A nontrivial continuum supplies distinct equal
witnesses and is ambiguous. A determinant whose sign cannot be certified refuses.

For each face, project both axis endpoints onto its plane and test every oriented finite polygon
edge at the reconstructed point. Strict interior is admitted. Decided boundary membership is left
to the separately enumerated edges and vertices. If the axis is not parallel to the plane, also
solve its interior crossing parameter. A crossing in strict face interior refuses as zero distance.
Plane boundaries are covered by the edge/vertex cases. Parallel face continua either reach axis
endpoints or finite polygon edges, which are already enumerated.

These are all active-constraint cases of the segment/convex-polygon quadratic minimum. Thus the
finite candidate set contains a true minimum whenever the arithmetic and membership checks finish.
Nonstationary endpoint candidates remain feasible and cannot lower the true minimum spuriously.
Any endpoint inside or on the solid refuses immediately. A segment entering from outside has a
boundary crossing and hence a zero-distance boundary minimum, which also refuses.

Distinct candidate distances use strict interval ordering or the shared exact squared-distance
comparator on certified exact point coordinates. Equal minima on different leaves or different
features are ambiguous. Even one feature with distinct equal witness pairs is ambiguous. Only an
identical exact pair on the same leaf and feature can alias. No compound patch union is inferred.

All candidates within the upper distance band are retained until global selection. Candidates
strictly beyond it cannot be its minimum. The lower distance-band gate runs on the selected minimum.
The entire closed band is proved. At zero band, the represented exact distance comparator must
prove distance equal to radius, or equivalent fully decisive arithmetic must prove both sides.
An interval merely overlapping contact does not qualify. Deeper overlap remains unresolved.

## Publication, bounds and caps

Verified squared-distance roots and outward radius subtraction enclose separation. Exact proved
contact may use the singleton zero interval. `GeometryVectorOperations.Normalize` handles both
the separation vector and every oriented finite face normal. Its verified roots and divisions
enclose their true unit vectors. The existing publication helper includes binary32 output rounding
in a Euclidean error bound. Axis and geometry use the maximum of their independently proved errors.

Ceilings are checked as exact product inequalities against `1/4000`, `1/10000` and `1/100000`.
Rounded float spellings of these constants cannot widen acceptance. Nonpositive distance/norm,
unresolved roots, invalid output, over-wide separation or excess position/normal error refuse.

The source caps are 64 leaves and 256 faces per leaf. For a closed strict convex polyhedron, Euler
and minimum vertex degree three give at most 508 vertices, 762 edges and 1524 face entries at that
face cap. Extraction uses these derived bounds before allocating from hull topology. Enumeration
retains at most 128 local candidates and publication at most 256 incident faces. No subset is
silently truncated. Internal cap refusal has no usable prefix. Destination refusal reports the
exact selected face requirement.

All incident normals, errors, capacity and the immutable result are validated in private scratch.
The result constructor runs before copying faces, so even a lifecycle/structural exception leaves
the destination untouched. Copying the already validated prefix is the only caller-span write.

## Remaining gates and missing extensions

The prototype compiled, but the two added translated-domain regressions failed. The parent must
resolve those applicability gaps and verify the original 14 public-world cases, both new domain
cases and the 12 lifetime facts. Proposed finite GREEN after correction, once:

```bash
dotnet test /Users/antonio/KhaozEngine/.worktrees/low-lip-resting-proof/KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~CapsuleFeaturePolyhedronTests|FullyQualifiedName~CapsuleFeatureLifetimeTests'
```

Expected count is 28 facts with no skips. Build/format and broader existing numerical regressions
remain separate parent-owned commands. No successful result is claimed by this source handoff.

Meshes, one-sided triangle incidence and connected coplanar patch unions remain Task 5. Curves,
nested/nonconvex installed children and unknown shape types return Unsupported. Duplicate or
non-manifold polyhedral neighborhoods return Ambiguous. An over-budget or undecidable predicate,
nonrepresentable world-vertex equality, unresolved candidate ordering or membership, and deep
overlap return Unresolved. Nonexact installed unit rotations return Unsupported in this slice.

To expand exact closed-band acceptance to nonbinary reconstructed witnesses, the missing shared
operation is a bounded rational expression value with exact represented-input construction,
add/subtract/product/zero-free quotient, sign and squared-distance comparison, plus verified
binary64 enclosure conversion. Every numerator, denominator, intermediate product and alignment
must obey the existing 4096-bit cap before allocation. Its proof must preserve algebraic witness
membership and compare rational squared distances to represented radius squares without replacing
the rational witness with rounded coordinates. This slice does not copy such a kernel.

General near-unit represented rotations additionally need a shared installed-affine certificate
covering matrix conversion, departure from orthogonality, transformed finite incidence and normal
reconstruction. The exact admitted subset here cannot stand in for that proof. The first parent review found the exact subset internally consistent but inadequate for ordinary
translated legacy capsules and decimal extents. Those two restrictions are now executable RED
cases, without widened publication error ceilings. Cache performance, actual bridge fit, runtime support, navigation and consumer
arrival proofs remain gated. No 1 mm arrival or legacy movement threshold changes.
