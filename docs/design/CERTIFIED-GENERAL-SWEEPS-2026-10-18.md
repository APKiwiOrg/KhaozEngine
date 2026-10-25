# Certified general capsule sweeps

Design for [#1355](https://github.com/APKiwiOrg/KhaozEngine/issues/1355), including the
explicit-tick reproduction recorded in closed duplicate #1459. Baseline `dcb0599e3`.
Status: implemented with headless family and acquired movement acceptance. Identity and rotated
boxes, installed convex hulls, finite one-sided mesh triangles, spheres, capsules, cylinders and
supported flat compound descendants certify within GS-3 and the refusal boundaries below.
Current consumer use lives in the
[backend README](../../KhaozEngine.Physics.Bepu/README.md).

The original investigation found that the first refused operation was the upward shell lift.
The complete zero-margin contact query and acquisition succeeded, and borrowing the composed lease
did not change the refusal. `SelectedSweepBoundsProved` rejected the selected hulls, meshes
and tilted crate before aperture construction. Behind it, `CapsuleBoxFractionSweep` also
required an identity box. Both sweep proof gaps are now closed for the admitted families.
The separate #1494 complete-footprint refusal at exact quarter-turn tread tangency is resolved
by [lane C](CONTACT-CONTROLLER-FOLLOWUPS-C-2026-10-19.md), which also resolves #1487 curved work.
The GB-R13 results below remain historical evidence from the general-sweep baseline.

This extends [the original sweep design](SWIM-TASK4-CERTIFIED-SWEEP-2026-10-07.md) and
[candidate coverage](SWIM-TASK4-CANDIDATE-COVERAGE-2026-10-07.md). It preserves
[SG-1, SG-6 to SG-8, SG-22 and SG-23](CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md)
and the [phase 5 selector contract](CONTACT-CONTROLLER-PHASE-5-SELECTOR-AND-NAVIGATION-2026-10-13.md).

## Rulings

### GS-1. Land complete families in dependency order

1. Rotated boxes, retaining the existing identity-box fast path. This removes the hidden
   crate's selected-bound poison and certifies its real tilted leaf.
2. Convex hulls, including the single-child centroid wrapper installed by `ShapeFactory`.
   This includes the authored stairs. A bare Bepu hull test alone cannot complete this family.
3. One-sided triangle meshes with their installed scale, winding and query-filter behavior.
4. Compound descendants made from the certified convex families, including nested seam
   compounds flattened by the existing installer.
5. Acquired explicit-tick acceptance for all four cost scenes, then captured authored stairs.

Each family needs installed-bound evidence, leaf collision proof, public owner/view forwarding,
lifecycle tests and zero warm query allocation before its task is accepted. Later unsupported
families still refuse. These are serial family gates, not permission for a bounds-only rollout.
Cost if wrong: a nominally supported shape still holds authoritative ticks or tunnels.

### GS-2. Preserve movement and support contracts

preserve the 1 mm arrival rule, roof and footprint protections and raw layer identity.
No tolerance, skin, band or limit may be widened.
A proper fix beats a workaround even when larger.
A hull is never treated as its AABB, a rotated box is never certified by inflated planes, a mesh keeps one-sided triangle semantics, and a ray, a sampled path or a FootSupport witness never establishes swept completeness.
Partial work never yields a Clear prefix.
No change to ShellMotion, ShellGeometry, LeasedContactQueryContext, FootSupport or MovementQueryLease behaviour.
No timing or load run is part of any task.
No version or CHANGELOG change except in the final task, which adds a CHANGELOG bullet riding the staged version in Directory.Build.props.

In particular, retain the 0.001 m explicit placement offset, 0.0001 m join/contact band,
256 raw support-member cap, roof/nosing guards, above-band refusals, support-height pacing,
canonical binding and raw Static/ElementId. This expands backend evidence, not legal footing.
Cost if wrong: certification changes ground semantics, roofs or arrival while appearing to fix sweeps.

### GS-3. State the numerical and work domain explicitly

The query capsule remains upright with exact identity orientation, positive finite radius,
nonnegative finite cylinder length and finite original centre/displacement. Unsupported layer
masks remain refused (`Layers != 0`). Retain mobility selection and both culling flag values.
Ruling GB-R16. General-family queries additionally require radius at most 1000 m,
half-cylinder length at most 1000 m, and every original start-position and displacement
component within the closed interval [-1000000, 1000000] m. These constants live in
`CapsuleGeneralSweepPolicy`. This is a certification domain, never a collision tolerance.
Before any tree traversal, an out-of-domain query refuses atomically if its mobility and
exclusions select any non-identity-box installation. Mutation-maintained owner and view
counts decide this without scanning registrations or exclusions at query time. View creation
initializes its count from the immutable excluded handles. Add, remove, pose refresh and
sleep/wake refresh maintain it under the owner mutation gate. Excluded general statics and
unselected mobility do not restrict an identity-only selection.
Identity boxes retain their current domain and original aperture. New installed transforms use
`InstalledPoseOperator` and `RepresentedGeometryTransforms`: local coordinates and pose positions
within 2048 m, the existing squared quaternion norm band `1 +/- 2^-20`, positive determinant,
and successful operator/operation enclosures. Never normalize a stored quaternion to pass admission.
Mesh scale must be finite and strictly positive on every axis. Zero, reflected or unproved
scale stays unsupported. Stationary bodies are queried at their lease pose, including dynamics.

Retain these ceilings, with smaller injected budgets allowed in internal tests only:

| Work or error | Ceiling |
| --- | --- |
| Encoded sweep error | 0.001 m, including the entire published Hit bracket |
| Stored broad-phase coordinate magnitude | 1,000,000 m |
| Broad-phase preflight per active/static tree | 8192 nodes, 4096 leaves, 128 pending branches |
| Aggregate callbacks, including excluded entries | 4096 |
| Unproved registration examinations | 4096 |
| Fraction cells per primitive and subdivision depth | 256 and 48 |
| Relevant primitive descendants per whole query | 4096, including mesh triangles |
| Convex feature-pair regimes per primitive | 4096 |
| Compound children per installed root | 64, matching the existing geometry reader |
| Hull faces, vertices and face-index entries | 256, 508 and 1524 |
| Exact predicate storage | 4096 bits per integer, with checked intermediate growth |

The original identity traversal keeps these counts exactly. GS-4 defines the separate
general-only traversal and its selected-leaf accounting.
The new descendant and feature budgets bound previously unsupported work. They do not increase
any existing cap. Sparse large meshes use certified pruning, not a total-world triangle cap.
Policy versions and all these constants are recorded together in an internal
`CapsuleGeneralSweepPolicy`, initially version 1. Existing box/tree policies remain version 1.
No public movement profile, frozen fingerprint, wire generation 14 or navigation format changes.
Native G1b must carry this backend domain into its own policy identity before adoption.
Cost if wrong: a nominal certificate has hidden unbounded work or a scale-dependent numerical hole.

### GS-4. Prove broad-phase coverage independently of leaf collision

At mutation completion derive conservative required bounds from actual installed data. The
eight box corners, all hull vertices, scaled mesh triangles and composed compound descendants
must be enclosed. A local AABB may help bound geometry but never replaces its distance predicate.
Include quaternion polynomial, scalar/SIMD evaluation, translation, scale and child-pose rounding.
Compare actual stored leaf bounds with that enclosure. If the normal backend bound is inward,
expand it through Bepu's owning broad-phase update path and refit ancestors under the mutation
gate, then verify the installed result. Never write only a leaf while leaving its ancestors stale.
Failure leaves evidence invalid. An ordinary rounded `ComputeBounds` result is not the proof.

The original unpadded original-path aperture and both checked tree preflights precede enumeration
under one read interval. Inner mesh traversal has the same closed-bound and checked-stack obligations.
Every pruned subtree needs conservative installed bounds. Budget counts include examined entries
before filtering, and exhaustion stays sticky across trees and descendants.

A far supported shape with proved bounds must no longer poison an unrelated Clear request.
An unproved selected bound still refuses even outside the apparent aperture. Exclusion or mobility
may remove unsupported geometry, but cannot hide structurally invalid tree bounds or evidence.
No per-query whole-world scan or replacement spatial index is introduced.
Cost if wrong: correct leaf proofs certify Clear after the tree silently omitted a collision.

Rulings GB-R14, GB-R16 and GB-R17. Stored Bepu bounds contain only the forward affine
geometry and its rounded pose graph, using Task 1's owning repair and ancestor refit. No
operational pad or inverse preimage is written into Bepu's collision tree. This matters to
ordinary simulation pair generation as well as the certified sweep's finite candidate budget.

Let A be the actual represented transpose used by the pinned local map, and K the installed
local solid. Mutation evidence separately retains the union of the forward enclosure and
`leafPosition + inverse(A) K`. Every admitted operator proves inverse infinity norm at most
2 and inverse Euclidean row norm at most `1 + 2^-15`. Curved leaves enclose their full
radial/axial sets. Mesh evidence retains both raw/scaled and recentered preimages, with
bottom-up ancestor unions in its private posed bounds. Bepu's raw mesh tree is unchanged.
Let Q be an outward maximum coordinate excess of this private enclosure beyond the forward
bounds. For compounds take the maximum child excess, since union preserves containment.
For meshes compare the two complete root enclosures after ancestor unions.

`CapsuleSweepPruningBounds` publishes maxima of root and composed leaf position magnitudes,
rounded rotated child magnitude H, root/child composition error c, local contact extent L,
transpose absolute row sum T, largest two-term absolute row sum V, inverse absolute row sum I,
and transformed unit-axis component magnitude a. The actual rounded child offset is checked
by the existing scalar/SIMD composition proof. Its composition error is enclosed from
`rootPosition + roundedChild - leafPosition`, retaining every original operand and exponent.
In particular c is bounded by the rounding of the root-plus-child sum, not by a cancellation
of the query path. These are mutation-time scans of installed data, never query-time rebuilds.
Owner and live view selection maintain these maxima along with the GS-3 counts. Exclusions,
pose changes, mobility, removals and sleep/wake refreshes cannot leave a smaller stale bound.
New entries extend maxima directly. A transaction with pose changes or removals rebuilds each
dirty owner/view maximum once before evidence publication, avoiding a scan per changed body.

The earlier B/J full-spacing derivation remains valid for leaf certification. Substituting
4096 m operands in that loose bound does not give a centimetre-scale query aperture. Pruning
instead uses the following tighter forward-error proof of the same operational graph.
All arithmetic, norm sums, extrema and endpoint conversions are outward. Write
`u = 2^-24`, `eta = 2^-150`, `R(x) = u*x + eta`, and `S(x) = R(x)/(1-u)`.
For one binary32 round-to-nearest operation with exact magnitude at most x, R(x) bounds
its absolute rounding error, including subnormals. If the rounded output has magnitude at
most x, solving that same inequality backwards bounds its error by S(x).

Let C and D be the maximum absolute original query components, p0 and pL the selected
root and leaf maxima, r the actual radius and h the actual half-cylinder length. Then

```text
m0 = C + p0
m = C + D + pL
e0 = R(m0) + R(D)
e = e0 + R(m0 + D + e0)
e = e + c + R(m + e + c)              when a child offset is present
v = m + e
b = u*T*v + 3*eta
f = R(V*v + b)
z = h*a
j = R(z)
k = S(L + r)
l = S(L + r + z + j + k)
E = T*e + b + f + l + j + k
P = Q + I*E + r*2^-15
```

The first three error terms cover the original root-relative subtraction, displacement
product and path sum. A child additionally contributes its captured composed-pose error
and the later rounded child addition. The exact pre-round child sum equals the leaf-relative
path plus these errors, so its magnitude is bounded by m plus the accumulated error. Neither
small endpoint coordinates nor final-position cancellation reduces C or D.

The transpose has three products with total rounding error b and a first sum with error f.
Every supported sum order has its two coefficients bounded by V. For a possible operational
contact the final rounded axis point is within r of the installed local solid, hence its
component magnitude is at most L+r. Backwards rounding therefore bounds the final axis add
by k. Subtracting the rounded axis product bounds the preceding rounded transpose output
by `L+r+z+j+k`, so its final sum error is at most l. This implication is used only for
pruning: an operational contact must obey it. It asserts no contact witness and does not
replace any finite feature predicate. The complete capsule axis is retained for all s in
[-h,h], including axis-product error j. T propagates the input error and I propagates E
back to world coordinates. The inverse Euclidean row bound propagates the skin. The original
aperture already contains r, leaving only `r*2^-15` as additional skin departure.
Thus an operational contact intersects the original aperture expanded by P against the
forward stored bound. A forward contact is already enclosed without P. Failure of any
required account refuses instead of authorizing a prune.

For an explicit Grimhollow-scale small-prop check, C and D are each 4096 m, r is binary32
0.3, h is 0.75, p0 and pL are 2048, H is at most 16384, c is at most 1/8192, L is 2,
T and I are at most 1.75, V is at most 1.5, a is at most `1+2^-15`, and Q is at most
1/4096. The outward pad is below 0.009706754 m. This is a checked fixture envelope, not
a new domain limit. Larger local geometry or larger proved preimage excess uses its actual
mutation-published bound and can require a larger query-only pad.

Identity enumeration retains its original aperture, original checked preflight and original
callback ceiling. It admits only identity leaves to the identity kernel. When and only when
the selected set contains a general installation, a separate general-only pass uses P.
Its checked A-first structural preflight retains the 8192-node and 128-pending ceilings.
The matching count-only callback preflight and subsequent enumeration each count only selected
non-identity leaves against their own 4096-leaf/callback ceiling. Identity leaves in the padded
fringe consume none of that count. Both passes run in the same owner read interval. No
replacement spatial index or private Bepu leaf-buffer access is introduced. All leaf work,
including the shared descendant ceiling, still aggregates atomically.

After enumeration a general candidate can use its own mutation-published private geometry
union and actual-query error pad to reject a proved separated candidate before its leaf
kernel. That union already includes Q, so it needs only `I*E+r*2^-15`. Mesh inner traversal
uses the same query-time error pad over its private raw/recentered bounds. No stored tree
pad, warm allocation, geometric tolerance, primitive predicate, publication ceiling or work
limit changes. Installation identity and the published pose are checked before this evidence
is used. Original identity bounds and kernels remain byte-identical to merge base fdce0905c.

### GS-5. Bind immutable geometry to installation and mutation lifetime

Capture immutable topology at installation, keyed by the existing owner, registration lifetime,
typed shape index and local child identity. Read actual Bepu hull points/face loops, mesh triangles
and scale, and installed compound children. Never reconstruct them from caller descriptors or
reuse an entry solely because a recycled handle/index matches. Capture allocations belong to
installation. Pose refreshes reuse topology and publish value evidence at mutation completion.

Extend the existing `BeginSweepMutation`/`CompleteSweepMutation` transaction. Audit Add/RemoveStatic,
Add/RemoveDynamic, Step, velocity changes, constraints/targets, wake/sleep transfers, rebase including
no-op, and disposal. Shapeless constraint anchors remain noncolliders. Rebase refreshes all posed
bounds. Removal destroys the installation binding. Partial failure, missed generation or failed
expansion never publishes current evidence, and a later unrelated success cannot heal it.
Keep QueryLease, EnterMutation, source-count and token semantics unchanged.
Cost if wrong: a stale shape, pose or tree transfer is certified using another installation's data.

### GS-6. Prove closed capsule distance over the original whole path

For `C(t) = C0 + t*D`, `t` in closed `[0,1]`, the capsule axis is
`C(t) + s*UnitY`, `s` in closed `[-halfCylinderLength, halfCylinderLength]`.
A convex solid event is `distance(axis(t), solid)^2 <= radius^2`, including axis/solid
intersection. Enumerate the segment endpoint/interior regimes against finite face interiors,
edges and vertices. Include solid-interior occupancy for stationary or fully enclosed axes.
Every feature candidate carries its membership and closest-point region inequalities.
Parallel and zero-length regimes reduce to their valid lower-dimensional strata, not an epsilon.

For a fixed admitted feature regime, eliminating closest-point parameters gives a quadratic in
the original fraction and linear parameter/membership constraints. Use outward interval bounds
first. Strict positive distance lower bounds over an entire closed cell prove separation.
An endpoint's sampled separation never proves the intervening cell. Preserve original C0 and D
through subdivision, without rounding a new trajectory at each split.

Use bounded exact signs for feature membership, determinant/denominator signs, quadratic extrema,
discriminants and root ordering when intervals straddle a boundary. Clear requires all admissible
regimes separated over the whole cell. A collision needs a proved nonpositive witness or an
isolated exact root with proved feature eligibility. Exact root isolation is required for a
double root such as `(3*t - 1)^2 = 0`, which dyadic point sampling cannot witness. Tangency is Hit,
even without a negative-distance interval. Root enclosures are not permission to commit a pose.

Reuse the shared interval and exact arithmetic home. Any higher-degree intermediate sign needed
by elimination is evaluated from the original represented expression, never from an interval
midpoint or rounded polynomial coefficient asserted exact. Check growth before computing it.
Arbitrary non-rigid or ill-conditioned operational disagreements remain Unresolved. An audit must
show how the installed forward geometry and pinned backend local-map/contact semantics agree
within the existing error ceiling. Transform uncertainty cannot be erased by exact scalar fallback.

Exactly rigid maps also require the operational proof. Identity and cyclic rotations do not make
the source's rounded root-offset subtraction and later child-offset addition exact. The child
order enclosure applies to hull centroid wrappers as well as recentered triangles. For a proved
signed permutation, each stationary local coordinate retains its actual rounded source value
throughout the whole path, including any disagreement with the forward model. Moving coordinates
retain the full operational enclosure.

A rigid source sample can supply a genuine contact witness through the same finite feature
predicate. The forward bracket's upper endpoint, midpoint and entering binary32 neighbor are
bounded proposals. A feature quadratic minimum can also propose an isolated-tangency sample.
Neither a proposal nor a sampled miss proves anything. The pinned root-relative path, child add,
conjugate transform and full capsule axis must produce an actual eligible closed contact. Every
operational feature still proves its complete earlier interval and the published bracket still
contains the union of both models within 0.001 m. These attempts consume the unchanged primitive
feature, cell and exact-storage account. No rigid-map shortcut remains in the general convex route.

Left-first bounded refinement covers every required closed fraction cell. Refuse atomically on
cell, depth, feature, exact-storage or arithmetic exhaustion. Zero displacement distinguishes
complete stationary separation from contact/overlap. Initial overlap, start tangency and tangential
motion along a touching surface return Hit at `[0,0]`. Strictly separated tangential motion is Clear
only with full-path proof. An unencodable irrational-length endpoint remains Unresolved.
Cost if wrong: thin crossings, corners and isolated tangencies disappear between sampled poses.

### GS-7. Certify rotated boxes as finite convex geometry

Keep `TryReadIdentityBox`, `CapsuleBoxFractionSweep` and `CapsuleBoxSweep` unchanged for their
proved domain. Add a separate rotated-box route that presents the installed box's eight vertices,
six finite faces and twelve edges to GS-6. Rotating the capsule into local coordinates must carry
its entire axis and transform error. It cannot remain a fictitious upright local capsule.
Independent plane inflation creates square corners instead of capsule distance and is prohibited.
Certify the hidden crate at position `(0.28660256, 0.2, 0)` and quaternion
`(0, 0, 0.5, 0.8660254)` without adjusting the input or its rounding enclosure.
Cost if wrong: the first family passes flat faces but still clips or holds at tilted corners.

### GS-8. Certify hull topology and its centering wrapper

The hull route consumes installed face loops, edges and vertices, with closed solid interior.
It shares GS-6's convex distance kernel with rotated boxes. Preserve Bepu's computed centroid
and the installed child offset exactly. Public `ConvexHullShape` installs as a one-hull Compound
today, so this exact wrapper is certified in the hull task. General mixed compounds wait for GS-10.
Reject malformed/ambiguous hull topology and unproved feature regimes. A tetrahedron's AABB has
empty corners, which must remain traversable. No original descriptor or box-like appearance can
replace installed hull identity, including the eight box-shaped authored stair hulls.
Cost if wrong: authored stairs move relative to the visual mesh or solid AABB corners block free space.

### GS-9. Preserve finite, sided mesh semantics

Certify each relevant installed triangle's face, three edges and vertices. The mesh is an open
surface, never a convex solid, filled AABB or closed hull. Preserve raw triangle identity and
boundary/incident-face semantics at seams, convex corners and concave corners. Include scale and
pose error in outer bounds, inner-tree bounds, feature distance and side predicates.

Audit pinned Bepu 2.4.0 `Mesh`, `Triangle`, capsule/triangle sweep and contact testers and the
engine's sweep filter. Front normal uses `Cross(C - A, B - A)`. Preserve the distinction recorded
by `SweepBackFaceCullingTests`: with `CullBackFaces=true`, a triangle not facing against motion
is culled, including a vertical face on a vertical sweep. Default raw sweeps also block the
back-facing row in `WithoutCullingBackFacesStillBlock`. Do not replace these rules with a blanket
direction test or turn the installed one-sided contact surface into a two-sided volume.
Stationary and start-contact eligibility must be source-derived separately from a direction test.
If certified distance and the retained raw/contact route cannot agree, refuse that case.

Enumerate every relevant triangle under GS-4's proved pruning and GS-3's whole-query budget.
An unresolved side, degenerate relevant triangle, missing member or inner-tree overflow refuses
the whole result. A thin interior triangle cannot disappear after a near triangle is found.
Cost if wrong: a mesh allows passage through a front face, blocks its open back incorrectly or loses seams.

#### Installed mesh implementation and pinned side policy

`InstalledMeshSweepGeometry` captures the actual scaled `Mesh.GetLocalChild` triangle and the
separate rounded `Mesh.GetPosedLocalChild` continuation, retaining each raw triangle index. The
latter subtracts a binary32 centroid and is composed through the existing scalar/SIMD pose proof.
The operational enclosure also covers the different order of the original root-relative path and
rotated centroid addition, also used by hull wrappers. It bounds intermediate root offset M, rotated centroid H and root-position
magnitude P independently of the final leaf offset, retaining cancellation. The extra offset error is
`U(M) + U(2M) + U(4M) + U(4M+H) + U(P+H)`, propagated through the actual absolute transpose row sums
with outward accumulation. Here U is the full neighboring binary32 spacing. It is numerical evidence
inside the existing bracket ceiling, not a collision margin.
These representations must both complete the shared finite feature kernel. A different event status
or a bracket union above 0.001 m refuses. Recentered boundary disagreements are not repaired by
changing the radius. Seven triangle features (one face, three edges and three vertices) reuse the
convex feature elimination and exact polynomial workspace. No interior occupancy feature is added. Triangle endpoint regimes also retain the closed necessary
axis-distance derivative sign, nonnegative at the lower endpoint and nonpositive at the upper.
The interior-axis regime covers the remaining minima. Removing these redundant endpoint candidates
preserves completeness while keeping both source representations inside the existing cell budget.
Box and hull eligibility and result calculations are unchanged.

Pinned Bepu 2.4.0 source symbols are `DefaultTypes.CreateDefaultSweepTaskRegistry`,
`ConvexHomogeneousCompoundSweepTask.PreorderedTypeSweep`, `Mesh.GetLocalChild`,
`Mesh.GetPosedLocalChild`, `GJKDistanceTester`, `TriangleSupportFinder`,
`CapsuleTriangleTester.Test`, `TriangleWide.ComputeNondegenerateTriangleMask` and the engine's
`SweepHitHandler.IsMeshBackFace`. The default capsule/triangle sweep is registered with GJK support.
It does not use the one-sided contact tester. The contact tester selects face or edge normals,
requires `collidingWithSolidSide` and a nondegenerate triangle, and separately gates face contacts
with `capsuleOffsetAlongNormal >= 0`. Its backface-normal threshold is `-1e-2f` and degeneracy scale
is `1e-6f`. Certification does not repurpose either as a distance tolerance. A relevant triangle
outside the proved nondegenerate domain refuses. Initial contact admission proves the complete axis
on the front with `InstalledMeshSides.Agree`, including its pinned contact-centering operation graph.
An unproved back, crossing axis or operational side relationship refuses rather than becoming Clear.
A directionally culled triangle proves the complete stationary initial query in both representations
before returning Clear. This preserves initial front contact without requiring an excluded later
boundary event to complete a distance proof.

| Closed path situation | `CullBackFaces=false` | `CullBackFaces=true` |
| --- | --- | --- |
| Stationary, complete finite separation | Clear(0) | Clear(0) |
| Admitted initial front contact, tangency or overlap | Hit(0) | Hit(0) |
| That initial contact with tangential or departing motion | Hit(0) | Hit(0) |
| Initial geometric contact without proved front/contact agreement | Unresolved | Unresolved |
| Initially separated, front normal opposes motion | Complete raw finite-triangle result | Same result |
| Initially separated, front normal follows or is perpendicular to motion | Complete raw finite-triangle result, including a back-face Hit | Clear after complete initial exclusion and directional agreement |
| Unknown side, transform, continuation or work proof | Unresolved | Unresolved |

The initial event check precedes direction-based culling. A bracket that merely starts at zero is
not a stationary witness. It cannot authorize a culled Clear. Closed endpoints remain events.
The raw public sweep and contact implementations are unchanged. Source references are the pinned
[Mesh child getters](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Mesh.cs),
[capsule/triangle contact tester](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/CollisionTasks/CapsuleTriangleTester.cs),
[triangle domain and support](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Triangle.cs),
[default registrations](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/DefaultTypes.cs) and
[homogeneous compound sweep](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/SweepTasks/ConvexHomogeneousCompoundSweepTask.cs).

Mutation proof validates the inner tree's unique nodes and leaves, exact raw triangle identities,
leaf back-links, metanode parents, child counts and complete root reachability. Finite inward leaf
bounds are expanded through `Tree.UpdateBounds` and ancestors are verified. Nonfinite or structural
failure invalidates owner evidence even for an excluded mesh. Each posed inner bound encloses its
positive-scaled raw box and the actual recentered continuation, then includes all descendant bounds
bottom-up. The rounded continuation's additional envelope stays in these posed certificates, so
valid raw Bepu triangle bounds and pruning remain unchanged. Exact identity rotation uses direct
translation enclosures and reuses proved rotation data. An unchanged posed certificate is reused
only after raw geometry, structure and node snapshots are revalidated. The outer broad-phase bound
encloses the root union. Cached snapshots bind this proof
to the current installation and mutation pose.

A query checks each examined node against that snapshot, uses closed world-bound overlap and visits
A first with B pending. Every pruned branch therefore retains a proved descendant enclosure. Node,
leaf and pending ceilings remain 8192, 4096 and 128. Relevant triangles consume the shared 4096
primitive account even after Hit(0). All required triangles must complete before publication.
Installation and mutation may scan the mesh. Warm queries traverse its existing tree and allocate
nothing. There is no total-mesh triangle cap or replacement spatial index.

### GS-10a. Certify sphere, capsule and cylinder leaves

Added 2026-10-18 (ruling GB-R3). Every leased tick over a lying log or pipe threw in `SweepShell` because curved leaves were unsupported, so an authoritative tick held a body still over any round prop. Sphere and capsule leaves use exact closed distance between the swept capsule segment and the leaf point or segment. Cylinder leaves add side, cap-disc and rim-circle features, and a rim is never approximated by a polygon. Every curved family goes through the same operational correspondence proof as the polyhedral families (GS-6, ruling GB-R6), the same work ceilings and atomic refusal. This task runs before compounds so GS-10 can aggregate curved children.
Cost if wrong: a false Clear past a round prop, or round props that keep holding authoritative ticks.

### GS-10. Aggregate every supported compound descendant

Use the installer's actual flattened leaves and each stored child pose, including hull centering.
Compose with the actual parent pose through the common installed-transform proof, not an assumed
exact inverse. Certify each required convex descendant, including the GS-10a curved families, and union required bounds before publishing
the parent's evidence. Preserve stable raw child/feature identity without coalescing coincident faces.
Reverse child order must preserve the aggregate sweep result and canonical support binding.

Nested seam compounds are tested through the real flattening installer. Current Bepu compound
children must be convex. Mesh-in-compound installation remains rejected by `ShapeFactory`.
Do not add nested installed compounds, BigCompound or a new seam shape merely to enlarge this task.
Unsupported descendants retain atomic refusal, even after Hit(0). The implementation checks every
flat child without child pruning, within 64 children per root and the shared 4096-descendant account.
Root captures authenticate the registration id and installation generation independently of recycled
native handles and typed indices. Removal destroys both bindings, while posed refreshes retain them.

Composition admission does not guarantee a complete non-rigid leaf certificate. The nested fixture
with parent position `(3,2,-4)`, cyclic parent rotation, child position `(0.25,0.5,-0.125)` and rounded
60-degree child rotation passes flattening, pose composition and bounds. Its box's tangent face has
enclosed coefficients whose distance straddles zero at depth 48, without a robust closed-contact
witness. It returns exact default Unresolved atomically, even though the independent forward oracle
encloses a Hit. The rigid cyclic-child sibling certifies Hit. No tolerance, work limit or leaf kernel
is changed to accept the ambiguous non-rigid case (GB-R9).
Cost if wrong: one proved child hides another collider or a wrapper applies centroid compensation twice.

### GS-11. Publish only complete world results

Replace unconditional `CapsuleBoxSweep.Sweep` dispatch in `CertifyCandidates` with family dispatch.
Every required primitive must complete before publication. Across complete Hit brackets take
`min(lower)` and `min(upper)`, retaining an error that contains the aggregate interval and is at
most 0.001 m. Existing outward `CapsuleSweepDistanceEncoding` handles fractions and request extent.
Clear represents the correctly rounded length of the entire original vector, with both endpoints
and all intermediate poses proved. Never clamp an impact to the request or shorten the trajectory.

Unsupported query orientation/filter, bad stored bounds, invalid source counts, stale evidence,
unknown geometry/scale, failed side proof and every exhausted budget return the exact default
Unresolved result with zero clear distance, null impact and no usable prefix. One earlier Hit,
including `[0,0]`, cannot bypass a later unresolved selected member. Failure to find a separator
does not establish Hit. An ordinary Bepu hit or miss remains a proposal, not a completeness oracle.
Cost if wrong: a partial answer becomes accepted motion through unknown geometry.

### GS-12. Keep SG-22 allocation and scratch ownership

Warm certified queries allocate zero managed bytes, including exact boundary fallback and refusal.
Both `CapsuleShape` and exact `CapsuleQueryDimensions` overloads use the same core. No per-query
shape creation, LINQ, closures, topology copies, `BigInteger` heap fallback or dictionary growth.
Use immutable installed topology and bounded retained `QueryScratch` storage with take/restore.
Exact expansion is reused inside its proved degree/exponent domain. Additional exact polynomial
work uses bounded retained integer storage under the same arithmetic owner, not allocating dyadics.
Cold provisioning is bounded and never hides per-query growth after warmup.

Each reader interval's general-sweep scratch retains at most four simultaneous workspaces, one
per nesting depth. The first loan at a depth provisions it once. Later loans at that warmed depth
allocate zero bytes, and restoration never discards an inner workspace. A fifth simultaneous loan
returns default Unresolved without allocation or a usable prefix. Four retained workspaces permit
nested queries while bounding each reader's exact-number storage. Refusing at the cap preserves
GS-12's bounded provisioning and zero-allocation contracts.

Acquired authoritative ticks allocate only the named one-use movement/release binding, physics
ReadLease and producer pin tokens, including acquisition and disposal. The existing arm64 inventory
is 192 + 64 + 56 = 312 bytes. Derive the ceiling from the live inventory on other runtimes, without
adding a token or pooling a disposed public token. Borrowed queries allocate zero. Allocation tests
use the checked shared allocation region and `AllocSensitive`, never stopwatch sampling.
Cost if wrong: new geometric success adds per-actor garbage or unsafe reused lease references.

### GS-13. Retain one selected read interval and frame

Owner and restricted views read through the owner's authenticated read interval and their own exclusions.
Leases are owner-scoped by contract (`IPhysicsQueryLeaseSource.SourceWorld`), and the parameterless sweep
APIs take no token, so two views over one owner share that owner's interval by design. A view applies its
own exclusions to every general family exactly as it does for identity boxes. Where an API receives a
token (the leased movement compositions), wrong receiver, source or thread, expired or disposed tokens,
generation changes and origin mismatch are rejected as today. Sweep geometry evidence is rejected when its
installation or mutation generation is stale. Validate before and after. No lease, gate, view or token
semantics change in this program (ruling GB-R2, 2026-10-18). Immutable topology may be shared, active scratch may not. Concurrent and
nested queries use the existing scratch-loan protocol. No unrestricted source-world escape, extra
lease, implicit scope expansion or new registration/acquisition scan is permitted.
Prove translated frames, nonzero Y origins, rebase, handle reuse and wake/sleep transfers for each
admitted family. SG-8 undersized scopes still refuse before any backend or producer query.
Cost if wrong: a correct kernel certifies geometry from a different interval, view or frame.

### GS-14. Require explicit acceptance on the actual first lift and four cost scenes

Keep original and enclosed dimension pairs `(radius, length)` equal to
`(0.4, 0.59999996)` and `(0.40000007, 0.6000001)`. At centres
`(0.525, 1.6010002, 0)` for stairs box/hull/mesh and `(0, 1.1010001, 0)` for hidden crate,
the upward displacement is exactly `(0, 0.4, 0)`. Both sweep inputs must be complete Clear
through 0.4 m. The original-dimension zero-margin contact query remains complete with zero contacts.

For each named cost row, override `Explicit=true` and `MustPass=true`. Exercise
`EnvironmentAcquisitionFixture.Acquire`, which calls the real `MovementEnvironmentContext.TryAcquire`,
then the real `ExplicitCharacterMovement.StepTowards`. Require Known acquisition, Advanced,
non-refusal, positive intended horizontal progress, nonzero ground substeps and independent final
footprint/roof agreement. Pacing may leave the existing support-height lag, never an unexplained lift.
For the three stairs rows require consumed look-ahead when the supplied stair-signal state triggers it.
Separately acquire and borrow a composed interval and compare the full resulting state. A dry
GroundTickBorrowed result cannot satisfy this gate. Do not use the cost sampler, stopwatch or hosted job.
Cost if wrong: kernel tests pass while the authoritative composition still holds before its first substep.

### GS-15. Preserve the authored stairs capture and prove real-scale movement

Commit a portable collision fixture derived from Grimhollow's `assets/models/kit/stairs.glb`,
source SHA-256 `f5ce2f2853608907e0bf6ae415969b1ef2fb70b8faf1adf146a60c4d67126af9`.
Each of eight identity-node meshes has 24 POSITION entries and exactly eight unique box corners.
Capture the vertices as hull points with provenance, not one staircase AABB. The part bounds are:

| Part | Minimum | Maximum |
| --- | --- | --- |
| 0 | (-0.5, 0, -1) | (0.5, 0.22, -0.5) |
| 1 | (-0.5, 0.22, -1) | (0.5, 0.25, -0.5) |
| 2 | (-0.5, 0, -0.5) | (0.5, 0.47, 0) |
| 3 | (-0.5, 0.47, -0.5) | (0.5, 0.5, 0) |
| 4 | (-0.5, 0, 0) | (0.5, 0.72, 0.5) |
| 5 | (-0.5, 0.72, 0) | (0.5, 0.75, 0.5) |
| 6 | (-0.5, 0, 0.5) | (0.5, 0.97, 1) |
| 7 | (-0.5, 0.97, 0.5) | (0.5, 1, 1) |

Use radius 0.3 m, half-height 0.75 m, step 0.4 m, foot radius 0.15 m and explicit offset
0.001 m. At feet `(0, 0.5, -0.25)`, the first shell centre is `(0, 1.4510001, -0.25)`,
radius 0.3 and cylinder length 0.5. Prove its upward lift and a grounded +Z acquired explicit tick.
Then bounded ascent/descent, yaw-rotated installation, split statics and compound representations
reach the final 3D target within a Euclidean 0.001 m ball, subject to GB-R13's retained support boundary.
No per-axis substitute.
Capture source float values without decimal retuning and independently read installed geometry
for support/roof expectations. Canonical owners follow captured provenance, raw IDs remain visible.

The fixture does not assert a production freeze. Grimhollow currently declares this archetype's
collision kind None. No game asset, world or catalog change belongs here.
Cost if wrong: synthetic stairs pass while the actual authored representation still refuses.

### GS-16. Update contracts honestly and keep adoption gates separate

Update these sources as each family becomes proved, retaining historical evidence as history:

- `SWIM-TASK4-CERTIFIED-SWEEP-2026-10-07.md`: installed family, closed-distance and numerical domain.
- `SWIM-TASK4-CANDIDATE-COVERAGE-2026-10-07.md`: bound derivation, inner traversal and lifetime coverage.
- `CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md`: link the successful explicit regression evidence,
  explicitly leaving SG-1, SG-6 to SG-8, SG-22 and SG-23 unchanged.
- `CONTACT-CONTROLLER-PHASE-5-SELECTOR-AND-NAVIGATION-2026-10-13.md`: replace the current #1355
  deferral only after final acceptance. Preserve P5A-2, P5A-9, P5A-14 and A2, A3, A5 obligations.
- `KhaozEngine.Physics.Bepu/README.md`, `docs/USING-KHAOZENGINE.md`, this spec's status and its index row:
  exact supported domain, filters, remaining refusals, proof links and allocation contract.
- Final task only, `CHANGELOG.md`: one bullet under the then-staged `KhaozEngineVersion`.

The implementation record includes commands, exit codes, actual discovered test counts and the
family-by-family remaining domain. Correctness/counters/allocation are this program's acceptance.
No timing, load, release, tag, consumer pin, native G1b, navigation or #1334 cost acceptance is implied.
The full Release solution build and full Game.Tests suite run once at each task's finish through
build-slot. A changed failure gets investigation, not retries of unchanged tests.
Cost if wrong: documentation advertises consumer readiness before its separate gates pass.

## Task 5 acquired explicit evidence

The mandatory four cost rows now run through `EnvironmentAcquisitionFixture.Acquire` and
`ExplicitCharacterMovement.StepTowards`, with `Explicit=true` and `MustPass=true` local to these
tests. `SupportCostExecution.LastAcquisitionStatus` observes the actual acquisition result.
The advisory cost rows, SG-12 artifacts and production movement owners are unchanged.

At dt `1f/30f`, stairs feet `(0.525,0.5,0)` and crate feet `(0,0,0)`, every acquisition is Known,
every tick is Advanced and non-refused, and every support read agrees with the independent oracle.
The final footprint and roof checks pass without changing the height or pacing allowances.

| Scene | Motion X | Motion Y | Ground substeps | Find reads | Raw final static / element | Final support height |
| --- | --- | --- | --- | --- | --- | --- |
| stairs box | 0.20000005 | 0.116666675 | 2 | 4 | 3 / 3 | 0.75 |
| stairs hull | 0.20000005 | 0.116666675 | 2 | 4 | 3 / 1 | 0.75 |
| stairs mesh | 0.20000005 | 0.116666675 | 2 | 4 | 3 / 1 | 0.75 |
| hidden crate | 0.20000002 | 0.116666615 | 2 | 7 | 2 / 1 | 0.33660255029224206 |

No roof binds in these four rows. Each stays within `MaxStepClimbSpeed * dt`. A separately acquired
composed lease, borrowed by `ExplicitTickBorrowed`, matches the full MoveState, outcome, motion,
raw support binding requests, support reads and query counters. Only acquisition-token counters
differ because the borrowed leg acquires before its tick capture.

GB-R11 preserves shipped SC-13 behavior. The original stairs inputs carry ClimbRate and
ClimbRateEwma 1, so their decided ascent consumes zero look-ahead reads. Three additional rows
clear only those two fields, retaining StepDeltaY 0.1 and the rest of the input. Each advances,
completes all three forward-axis look-ahead reads without refusal and has seven total Find reads.
Their ClimbRate, ClimbRateEwma and StepDeltaY equal the dry route exactly
(`2.4499989`, `2.4499989`, `0`). No AlwaysSample control or GroundSignals change is used.

Both first-lift dimension pairs `(0.4,0.59999996)` and `(0.40000007,0.6000001)` return complete
Clear through exactly 0.4 m, with no impact and certified error at most 0.001 m. The centres are
`(0.525,1.6010002,0)` for all three stairs families and `(0,1.1010001,0)` for the crate, with delta
`(0,0.4,0)`. Owner and actual empty-exclusion restricted views exercise both shape and value
overloads. The original pair's zero-margin contacts are complete with Written and RequiredCapacity 0.

The deferred FA-R9 plank proof has 16 acquired explicit traversal rows, using the existing
`ContactFollowupScenes.Plate` installations at pitches `0.05235988` and `0.10471976`, split and
compound geometry, both travel directions, and stop-on-plank or cross-plank targets. The default
tuning and existing 240-tick bound remain. All rows arrive within the 0.001 m Euclidean ball,
in 45 ticks for a stop and 90 ticks for a crossing. The independent oracle and final leased read
agree on height, raw static/element identity and canonical plate/floor ownership.

SG-22 allocation uses the checked shared `AllocationMeasurement` region in `AllocSensitive`.
One cold and one priming invocation precede one measured invocation per route, including fresh
acquisition and disposal on the acquired leg. Geometry and full cold/prime/measured output equality
are checked before the allocation verdict. On .NET 10.0.13 arm64, the independent live inventory is
192 bytes for movement/release binding, 64 for the physics ReadLease and 56 for the producer pin.
All four acquired scenes allocate exactly that 312-byte sum. Every borrowed leg allocates zero.

An additional stairs-hull input with direction `(0.25,0)` varies the query workload while retaining
successful geometry. Its Find/shell sweep/contact/body-sample/substep counts are `3/4/4/7/1`,
compared with `4/6/6/9/2` for the full-speed stairs and `7/6/6/9/2` for the crate. Acquired bytes
remain 312 and borrowed bytes remain zero. No timing sampler, stopwatch or load run is involved.

The new atomic guards preserve whole-tick hold behavior for an undersized scope, stale producer
pin and missing canonical binding. A wrong frame retains FrameMismatch. Scope, stale and frame
failures make zero backend, analytic or contact-binding calls. SG-6 physical refusals, SG-7 raw
capacity sentinels and SG-8 leased/raw binding rows retain their existing assertions.

The focused Release selection passed 268/268 tests, including the 36 new acceptance rows,
FootSupportNeighborhoodTests, FootSupportBoundaryTests, FootSupportCompletenessTests,
ContactLeasedQueryTests, ContactSupportBindingTests and the 96/256/257/300 raw-capacity rows.
The Release solution build passed with zero warnings and zero errors. The full Game.Tests suite
ran once, with 5,901 passed, one existing intentional measurement-only skip and zero failures.
Its results include both `CurvedRawCapacityUsesTwoSlots` rows at 256/257, every named SG-6 refusal
and the pushed-axis raw-support rebind. No existing expectation changed.
Final solution format verification passed, with zero of 8,366 files changed. Diagnostic project
coverage includes all 169 solution projects, with both target frameworks for the four multi-target
projects. Staged dash, prose and file-size guards and the doc-version check pass.

## Task 6 captured-content evidence and GB-R13

`CapturedStairExplicitSweepTests` consumes the committed portable collision JSON without another
checkout or the private GLB. Provenance asserts the source repository, path and SHA-256 above,
eight identity-node meshes, 24 source positions per mesh, eight corners and every bound's float bits.
The actual installer supplies both eight split hulls and the real eight-hull compound, with the
centroid-compensating local offsets retained. A common edge is formed from local source operands
before applying the affine map, retaining shared transform coefficients and translation cancellation.
No installed point, quarter turn, support reach, skin, tolerance or work ceiling is changed.

The exact player-scale first lift has complete zero-margin contacts with zero written/required,
a raw upward miss as a control and certified Clear through 0.4 m through owner/restricted views
and both shape/value sweep overloads. Real empty-exclusion acquired intervals drive each public
explicit tick, with canonical stairs and separately labelled pad owners bound only to registered
raw static/feature pairs. Independent installed-face footprint and roof oracles check accepted ticks.
Ascent and descent use the unchanged 240-tick bound and exact dyadic Euclidean 0.001 m arrival ball,
including closed-radius acceptance and rejection of the per-axis cube. The 0.001 m placement offset
is removed before comparing support feet with the absolute target.

At the general-sweep baseline, GB-R13 kept all six zero-origin quarter-turn exact-edge rows. Their actual upward shell lift and
both horizontal sweeps certify Clear, while the complete footprint returns Unsupported and the
controller returns Blocked. The initial-tick edge/disc discriminants expose opposite exact signs
for the real polynomial of the stored quaternion and the represented matrix, about 1e-9 apart.
The traversal refusals retain their independently checked near-tangent edges. The lower
raw face remains part of the proof. Lane C resolves this #1494 support-source decision using
represented support and retains these historical discriminants. All six unchanged rows now
complete and advance. The feature and sweep contract is unchanged.

Additional quarter-turn rows offset start and target along the walk axis by one whole millimetre.
Both representations, both physics origins and both traversal directions advance every tick,
retain supported final placement and arrive within the same 1 mm ball. For every actual footprint
query, an independent rational oracle reads installed source vertices and composed poses, checks
both transform readings, proves equal discriminant signs and more than 1e-7 m2 of squared-distance
separation from every upward edge's line. No exact-tangency row is replaced or silently moved.

Removal, reused handles, invalidated registration, expired pins, disposed leases and undersized
scopes hold atomically. Warm successful captured ticks retain only the named one-use tokens
(312 bytes on the recorded .NET 10 arm64 route), with zero borrowed-query bytes. Allocation
acceptance follows successful geometry and uses one cold, one prime and one measured invocation.
Over-cap topology, unsupported descendants, unresolved geometry/side/operational correspondence,
the GS-10 non-rigid nested seam and exhausted bounded work remain atomic refusals. Native G1b,
SG-12/#1334 timing and aggregate actor cost, navigation, release and consumer adoption remain
separate gates. SG-1, SG-6 to SG-8, SG-22, SG-23 and P5A-2/P5A-9/P5A-14 with A2/A3/A5 are unchanged.

The final focused regression selection passes 724 tests with no skips. The strengthened captured
selection passes all 54 cases with no skips. The Release solution build has zero warnings and
errors, and the once-only full Game.Tests suite passes 5,955 tests with one pre-existing skipped
perturbation recipe, 5,956 discovered in total. No timing/load acceptance follows from these runs.
Final solution format verification changes zero of 8,371 files and covers all 169 declared projects.
Staged dash, prose and file-size guards plus the doc-version check pass. The version remains 21.5.1.
