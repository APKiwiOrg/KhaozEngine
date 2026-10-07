# Capsule feature installed-pose candidate argument

Status: independently reviewed and finitely verified installed-pose slice. The focused Game.Tests
run passed 124 cases, including all 20 pose rows, and the affected Movement.Tests run passed 39.
Scoped format passed after only eight initializer line breaks. The proof below received independent
source/math approval. See `docs/verification/2026-10-08-installed-pose-green.json` for tested/final
source identities and the disclosed whitespace-only delta. This does not promise Complete for every
in-domain input, universal ray/manifold agreement, runtime support, navigation, release or game repair.

The approved [feature specification](../superpowers/specs/2026-10-07-capsule-feature-correspondence-design.md)
supplies the domain, ceilings and work caps. The existing [arithmetic](CAPSULE-FEATURE-NUMERICAL-PROOF-2026-10-07.md),
[polyhedron](CAPSULE-FEATURE-POLYHEDRA-PROOF-2026-10-07.md) and
[mesh](CAPSULE-FEATURE-MESH-PROOF-2026-10-07.md) records retain their earlier evidence. The expanded-path evidence is recorded separately above. The independent InstalledPose fixtures distinguish
the real polynomial, represented coefficients and rounded operation outputs. Their required public
witness and separation bounds concern the two forward affine interpretations described below.

## Frozen source identity

The tested candidate starts at `13b0bfa7783553cfd1790842ec0457a23a5ca850` on `fix/low-lip-resting-proof` in
`/Users/antonio/KhaozEngine/.worktrees/low-lip-resting-proof`. The following SHA-256 identities freeze
its ten production files before whitespace normalization. Final identities and token-equivalence evidence are in the verification record above.

All paths in this table are relative to `KhaozEngine.Physics.Bepu/`.

| File | SHA-256 |
| --- | --- |
| `BoundedGeometryArithmetic.cs` | `a6e76756830e94d584f1f5da8a015c671e5b0507081c820e04aa656586bc8e91` |
| `RepresentedGeometryTransforms.cs` | `41754c74fc8d49857ca6999f73f386a310895894dba98630e9af76e918d7684a` |
| `InstalledPoseOperator.cs` | `9baf0ee8069ea9fe8148a30b0d3b360cc2a9b4c77fd471f07ed7062f23aa905a` |
| `InstalledPoseOperations.cs` | `6c1aa87eddad6c7468e61bcd76c7809a7126927188dd210264808665b7666b99` |
| `InstalledMeshSides.cs` | `8b361f14886dfe69e29a0ce996ff83a92a37a3792437761bbe0884d6f8191678` |
| `CapsuleFeatureGeometry.cs` | `4f3819eb39570a2d3f289c8043ac47dbdd6f02264d9bcd9433f10dee73f40afe` |
| `CapsuleFeaturePolyhedra.cs` | `41229a31785ea36e610a8387852d2eaf6f743c8fa18299c7f8c73de0f984979b` |
| `CapsuleFeaturePolyhedraClosest.cs` | `ba61dd7d378069e1ea3cfe05b55e019a680fe64c3c4092ae63feb00dddd5c21b` |
| `CapsuleFeatureMesh.cs` | `de8df7f3b732b9f680071808818042791cdd45917fa94e079b7abb7c684f1819` |
| `CapsuleFeatureMeshClosest.cs` | `7f12739999aefa73ce6202eae22f9a82e0650c25f94682bc956c644d45e71e4f` |

Tests, public Physics APIs, query ownership/lifetime, selected-target identity, exclusions, atomic
publication and work caps are unchanged. `GeometryVectorOperations` retains its previously reviewed
exact normalized-singleton certificate. Its source is not part of this modification.

## Installed authority and the two forward maps

Capture consumes actual installed shapes and represented poses under the existing authenticated read
interval. Boxes supply their installed half dimensions. Hulls supply actual face indices and raw
centered vertices. Meshes supply their entire stored triangle buffer and must have unit scale.
Compounds supply actual stored child poses and the represented result of `Compound.GetWorldPose`.
Caller descriptors, SIMD padding, rays and contact manifolds do not supply expected finite geometry.

For stored quaternion `q=(x,y,z,w)`, let `A(q)` be the real, unnormalized quaternion polynomial. In
column-vector notation its columns are Bepu's row-basis vectors. For example, its first column is
`(1-2*y*y-2*z*z, 2*x*y+2*z*w, 2*x*z-2*y*w)`. The other columns follow the pinned conversion graph.
Let `AhatScalar` and `AhatWide` denote actual binary32 coefficient conversions. The required forward
interpretations are `A(q)*v+t` and the affine images from those represented coefficient maps.

The pinned [scalar conversion](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuUtilities/Matrix3x3.cs#L278)
and [SIMD conversion](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuUtilities/Matrix3x3Wide.cs#L219)
use matching coefficient expressions. [Box support](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Box.cs#L280)
sends directions through the adjoint and selected local points forward, as does
[hull support](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/ConvexHull.cs#L427).
[Mesh bounds](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Mesh.cs#L161)
also transform stored triangles forward. This supports a source-backed affine interpretation without
exact orthogonality. The transpose used by local ray/contact paths has a separate correspondence burden.

An actual binary32 point operation also rounds multiplication, addition and translation. Such
outputs need not form an exactly planar vertex set. `PosePoint` encloses those operations, while
`TransformVertices` constructs finite geometry from the common affine operator and stored translation.
The full `PosePoint` boxes do not replace vertices or inflate geometric separation by final point
translation rounding. This distinction matters at a 1024 m frame, where float spacing can otherwise
create a false separation-width failure. Float publication still contributes its own proved error.

No pose is normalized. With `s=x*x+y*y+z*z+w*w`, the real polynomial is
`A(q)=s*R(q/sqrt(s))+(1-s)*I`. It is neither the normalized rotation nor the unadjusted quaternion
sandwich for nonunit input. Ideal composition of normalized rotations is not installed authority.

## Closed squared-norm admission

`QuaternionNormWithinBand` is shared by `PosePoint` and the private installed operator. It first
forms an outward interval for the sum of four represented component squares. An interval wholly
outside the approved closed band refuses. Decisive interior endpoint comparisons need no exact
fallback. An undecided comparison uses `CompareSumOfFourSquares` against the exact binary64
constant `1-2^-20` or `1+2^-20`. Admission requires both comparisons to be decided and inclusive.

The new fixed four-component operation splits each finite input into its exact dyadic mantissa and
exponent, uses the existing `TryMultiply` for each square, and the existing `TryAdd` for each sum.
Multiplication checks operand bit lengths before allocating its product. Addition checks alignment
and reserves the possible carry before shifting or adding. Final comparison checks aligned operand
sizes against the same 4096-bit ceiling. Nonfinite or over-budget operands return Unresolved.
There is no refinement loop, new dyadic representation or extension to the one-to-three-coordinate
`CompareSquaredDistances` contract. An unresolved norm comparison cannot admit a pose.

The exact endpoint identities are:

```text
upper: q=(0,0,1,1/1024)
       squared norm = 1048577/1048576 = 1+2^-20
lower: q=(1023,43,14,1)/1024
       1023^2+43^2+14^2+1^2 = 1048575
       squared norm = 1048575/1048576 = 1-2^-20
```

Moving the positive W component one represented value changes its square strictly. The exact
fallback therefore distinguishes the adjacent inward and outward values without widening the
tolerance. Outward norm arithmetic cannot reject an endpoint merely because its enclosure crosses
the boundary. The finite endpoint tests remain a pending executable gate for this candidate.

## One common coefficient enclosure

`InstalledPoseOperator.TryCreate` evaluates `A(q)` through `FeatureNumber` using the source
polynomial and the existing outward binary64 operations. Exact binary64 coefficients are retained
only when the existing bounded equality predicates certify their construction. It also obtains the
rounded coefficient graph extracted from `PosePoint`, the actual scalar matrix and a broadcast SIMD
conversion. Every actual scalar coefficient and every SIMD lane must be finite and contained in
the corresponding rounded graph enclosure.

For each coefficient, `Certify` takes the hull of the real polynomial enclosure and all actual
represented coefficient values. If scalar and SIMD coefficients differ, the represented-operator
enclosure retains both. The common geometry enclosure additionally retains the real polynomial.
It does not choose whichever coefficient interpretation makes a predicate convenient.

An exact common coefficient is permitted only if the real expression already has an exact
`FeatureNumber` certificate and every actual scalar/SIMD conversion equals that certified value.
Finite `Matrix3x3` output, an interval midpoint or scalar/SIMD agreement alone is insufficient. A
represented-only singleton used for operational correspondence denotes inspected represented data,
not proof that the real polynomial equals it.

The nine coefficient intervals define an enclosing family of common affine operators. Every actual
forward map is in this family. Unrelated combinations may also be present, making a decision harder
but never excluding an intended map. A particular geometry uses one member consistently across its
vertices, edges and candidate expressions. This is an algebraic construction invariant. It is not
an assertion that independently selected points from the resulting vertex boxes form a planar face.

## Determinant, topology and normals

For common columns `X,Y,Z`, the candidate encloses `det(A)=Dot(X,Cross(Y,Z))` and requires its sign
to be strictly Positive. It also encloses the six independent entries of `A^T*A-I` and the columns
of `A^-T` from `Cross(Y,Z)/det(A)` and cyclic permutations. Every quotient uses the existing
zero-free denominator check. Unresolved Gram or inverse-transpose arithmetic refuses admission.
These operations do not assert exact Gram identity or grant a normal/position error allowance.

For the real polynomial, writing `a=x*x+y*y+z*z` gives `det(A)=1+4*a*(s-1)`. In the approved norm
band its lower bound is `1-4*(1+2^-20)*2^-20`, which is positive. That identity explains why a broad
orientation domain can be well conditioned. Admission still proves positivity over the actual common
coefficient enclosure, including represented conversion departure. The identity alone cannot admit
a represented operator, and conditioning alone cannot satisfy a publication ceiling.

An invertible positive-determinant affine map preserves local vertex identity, coplanarity, convex
support signs, finite polygon interiors, incidence and winding. Existing exact local validation
supplies those premises for each leaf. The same sign preservation carries crease classification.
Mesh flat-boundary collinearity and opposite direction are checked as local identities, then carried
through this map. World interval overlap never welds a crack or supplies an exact incidence.

For local points `a,b`, `EdgeDirection` constructs `A*(b-a)` using certified local differences. It
does not subtract independently enclosed translated world vertices. Hence translation cancels before
interval evaluation. For local edges `u,v`, the identity
`Cross(A*u,A*v)=det(A)*A^-T*Cross(u,v)` gives the forward face's geometric normal. Polyhedron faces
retain their outward cross convention. Mesh faces retain `Cross(ac,ab)`. These unnormalized normals
remain bounded expressions and receive separate norm, normalization and publication checks.

The exact-rigid fast path requires certified Gram identities and determinant one from these common
coefficients. It retains the earlier exact incident-wall behavior. Passing only the norm band does
not activate that path.

## Finite witnesses, separation and output errors

The existing complete vertex/edge/face enumeration remains the finite minimum mechanism. Every
world vertex, transformed edge, projection parameter and reconstructed witness retains its full
`FeatureNumber` enclosure. For an edge, `origin+u*EdgeDirection` is the image of the corresponding
local edge point. For a face, projection onto its cross-derived normal establishes the plane
relation algebraically, then oriented finite edge tests must certify membership. No rounded proposal
is promoted to an exact witness. Lost interval dependence can produce Unresolved.

Locality boxes enclose the true finite features for every common operator. An exclusion requires a
decisive lower distance bound. Parameter boundaries, candidate ordering, incidence and the closed
contact band retain their existing exact or decisive interval requirements. An unproved comparison
cannot become equality or absence. Distinct equal witnesses remain Ambiguous, and deep or unproved
overlap remains Unresolved. Nonbinary exact rational witness/contact decisions are not newly solved.

Squared separation is the bounded dot of the reconstructed axis/geometry difference. The publisher
requires positive resolved distance, obtains a verified square-root enclosure and subtracts the
represented radius outward. Exact contact can use singleton zero only when the existing exact
represented-point comparator proves it. Operator uncertainty therefore remains in the separation
interval unless an applicable exact certificate removes it.

Separation direction and every incident face normal pass through the existing vector normalization.
Positive squared-norm bounds, square-root endpoint checks by exact squared comparison, and zero-free
division enclose their unit vectors. The previously reviewed normalized-singleton path is unchanged.
Float representatives are proposals. Outward squared component errors, their sum and verified root
bound the Euclidean error for all enclosed true vectors or points. Float error publication rounds
up when needed.

The publisher checks exact product inequalities against the unchanged rational ceilings:
position error `1/4000` m, separation interval width `1/10000` m and unit-normal vector error
`1/100000`. No maximum observed test error grants these values. Unresolved arithmetic or an exceeded
ceiling returns no usable output. All gates, incident normals and destination capacity are checked
before the existing sole caller-span copy. This candidate changes no atomic/lifetime boundary.

## Mesh operational side agreement and its limits

The forward half-space function is affine along the upright capsule axis. For the expanded path,
`InstalledMeshSides.Agree` first requires strict, equal forward signs at both endpoints. Those signs
must hold over the common forward operator enclosure. Their equality rules out an unexamined forward
front-half-space crossing. Exactly grazing or undecided endpoints refuse in this expanded path.

The operational coefficient enclosure contains actual scalar and SIMD maps separately from the
real-polynomial geometry enclosure. `RoundedTranspose` evaluates the transpose used by the pinned
local paths. It is not named or treated as an inverse. Each product and addition retains the real
expression and binary32 rounding. Operational dot products retain all three pairings of three terms
to cover scalar reduction order as well as the explicit wide order. These wrappers use the existing
interval owner, not another exact arithmetic kernel.

The helper encloses the [raw local plane expressions](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Triangle.cs#L54)
from rounded raw edge differences, cross, frame subtraction, represented transpose, subtraction of
raw A and plane dot. Both endpoint signs must match the forward signs. This directly retains the
transpose-as-inverse discrepancy, including coefficient departure and operation rounding.

The [centered capsule/triangle expressions](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/CollisionTasks/CapsuleTriangleTester.cs#L108)
receive separate enclosures. The helper forms the triangle center from rounded `(A+B)+C` and the
represented binary32 `1/3`, forms `offsetB=targetPosition-capsuleCenter`, and encloses
`localOffsetA=-(Ahat^T*offsetB+triangleCenter)`. It transforms the upright unit axis through the
represented transpose and constructs both local endpoints with rounded half-length operations.
Rounded raw-edge cross, length, reciprocal and scale enclose the local face normal used by the dot.

Both centered endpoint signs must agree too. The helper additionally encloses the source
center/half-extent expressions `centerSide +/- halfLength*abs(axisSide)`, including their operation
rounding. Their minimum and maximum signs cover the local axis extent. Both must have the same
strict agreed sign. No contact rejection threshold supplies any of these geometric signs.

Nearby faces receive this check before back-side exclusion. Every retained near-unit edge/vertex
candidate receives it for all incident faces. A strictly back-sided neighborhood can be excluded
only after agreement. Uncertain, grazing, crossing or disagreeing neighborhoods return Unresolved.
The already certified exact-rigid path retains the earlier rules, including a positive incident top
with an exact zero wall sign.

These side gates accompany complete finite source incidence, conservative locality and candidate
enumeration. They do not replace finite membership with an infinite plane, omit backward neighbors
from incidence or return a ray witness. The final incidence pass still considers all raw triangles.
The argument concerns the admitted finite geometric subset and bounded local-side expressions. It
does not reproduce a contact manifold, prove which contact-solver axis is selected, or certify a
rounded backend closest point as the finite witness. Independent review must check the relevant
caller and expression-graph premises before operational completeness is accepted.

The discrepancy is not universally harmless. For `q=(0,0,1,2^-10)`, the XY forward block is
`[-1,-2^-9;2^-9,-1]`, with determinant `D=1+2^-18`. Its transpose-inverse image scales the forward
point by `1/D`. At local `(64,64,0)` the difference is about 0.345266 mm, exceeding the position
ceiling. The norm endpoint may be admitted, but it grants no transpose/inverse equivalence or
Complete result. Sensitive side disagreement must refuse. Forward affine witnesses and rounded
backend point-operation observations remain distinct proof targets.

## Represented compound composition

Capture retains the actual stored child/root poses and the actual scalar
[GetWorldPose result](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Compound.cs#L97).
`ProveComposition` validates all three poses with the same installed certificate. It encloses the
four concatenated quaternion components in the exact pinned expression order, using the existing
interval operations followed by binary32 rounding envelopes. Each captured quaternion component
must lie within its corresponding graph enclosure.

The child offset has its own finite 2048 m component check through child-pose admission. It does not
borrow the 64 m local-vertex bound. Its root-oriented transform and root translation are enclosed
with operation rounding, and the represented captured world position must be contained. The
quaternion-transform graph moves some doubled factors relative to the matrix graph. Doubling an
admitted binary32 quaternion component is exact and finite, including subnormals. The corresponding
product has the same exact real value and rounding, justifying this coefficient correspondence.
All interval operations still require finite endpoints. Composed positions and every transformed
vertex separately pass the existing world-domain gates.

The helper also obtains actual SIMD concatenation, offset rotation and translation. Every broadcast
lane must equal the captured scalar world pose. A differing continuation would need an additional
composed-pose certificate, so this candidate refuses rather than assuming equality. Actual stored
world quaternion and position remain the leaf data. Multiplying parent and child ideal matrices is
not substituted. The leaf's real-polynomial and represented-coefficient affine maps are evaluated
from that represented composed quaternion, with the represented composed position as translation.

Valid inputs can compose outside the band. Two pure-W inputs with `W=1+2^-22` produce represented
`W=1+2^-21`, whose squared norm is `1+2^-20+2^-42`, strictly above the closed upper endpoint.
The composed pose therefore refuses Unsupported even though its scalar matrix would be identity.
No normalization repairs the domain violation. The recentered-hull wrapper follows the same route
using its actual stored centroid offset and centered raw vertices.

## Remaining limits and verification gate

All original caps remain: 64 convex leaves, 256 faces per convex leaf, 508 vertices and 1524 face
entries derived from that convex topology, 65536 source mesh triangles, 128 local candidates,
256 incident faces and 4096 bits per exact predicate operand. Positions remain within 2048 m,
local vertices within 64 m, capsule radius 0.01 to 2 m, cylinder length 0 to 8 m and separation
band 0 to 0.01 m. Scale, finite source topology and upright-axis requirements are unchanged.

Curves, unknown shapes, nonunit mesh scale and nested/nonconvex compound leaves still refuse.
Duplicate or nonmanifold source neighborhoods, competing minima and unproved larger vertex fans
retain their existing refusal paths. Missing coefficient/composition correspondence, undecided
membership/order/contact, nonpositive or unresolved normal/determinant arithmetic and exceeded
publication ceilings cannot yield Complete. General orientation is no longer itself an exact-unit
gate, but neither the norm band nor finite arithmetic guarantees Complete for every input.

The source still requires parent-owned first compilation and the meaningful combined installed-pose
plus affected numerical/geometry verification, followed by the assigned corner/eligibility controls
and independent source/proof review. Prior 44, 24 and 15 evidence contracts are preserved, not
relabelled as validation of this candidate. Immutable tests, public APIs, caps and ceilings have not
been adjusted to obtain an acceptance result. Shared-main integration, runtime consumers, navigation,
engine adoption, releases and the actual bridge proof remain outside this source/document handoff.
