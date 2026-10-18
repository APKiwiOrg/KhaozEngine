# Certified general capsule sweeps

Design for [#1355](https://github.com/APKiwiOrg/KhaozEngine/issues/1355), including the
explicit-tick reproduction recorded in closed duplicate #1459. Baseline `dcb0599e3`.
Status: proposed implementation contract, no new shape family is certified by this document.
The [implementation plan](../superpowers/plans/2026-10-18-certified-general-sweeps.md) carries
the family gates and exact fixtures.

The investigation is retained at `.superpowers/sdd/contact-followups/research-a.md` in the
engine main checkout. The first refused operation is the upward shell lift. The complete
zero-margin contact query succeeds, acquisition succeeds, and borrowing the composed lease
does not change the refusal. `SelectedSweepBoundsProved` rejects the selected hulls, meshes
and tilted crate before aperture construction. Behind it, `CapsuleBoxFractionSweep` also
requires an identity box. Both proof gaps must close.

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
Identity boxes retain their current domain. New installed transforms use
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

The unchanged original-path aperture and both checked tree preflights precede enumeration under
one read interval. Inner mesh traversal has the same closed-bound and checked-stack obligations.
Every pruned subtree needs conservative installed bounds. Budget counts include examined entries
before filtering, and exhaustion stays sticky across trees and descendants.

A far supported shape with proved bounds must no longer poison an unrelated Clear request.
An unproved selected bound still refuses even outside the apparent aperture. Exclusion or mobility
may remove unsupported geometry, but cannot hide structurally invalid tree bounds or evidence.
No per-query whole-world scan or replacement spatial index is introduced.
Cost if wrong: correct leaf proofs certify Clear after the tree silently omitted a collision.

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

### GS-10. Aggregate every supported compound descendant

Use the installer's actual flattened leaves and each stored child pose, including hull centering.
Compose with the actual parent pose through the common installed-transform proof, not an assumed
exact inverse. Certify each relevant box/hull descendant and union required bounds before publishing
the parent's evidence. Preserve stable raw child/feature identity without coalescing coincident faces.
Reverse child order must preserve the aggregate sweep result and canonical support binding.

Nested seam compounds are tested through the real flattening installer. Current Bepu compound
children must be convex. Mesh-in-compound installation remains rejected by `ShapeFactory`.
Do not add nested installed compounds, BigCompound, sphere/capsule/cylinder leaves or a new seam
shape merely to enlarge this task. Unsupported descendants retain atomic refusal, even after Hit(0).
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
| 5 | (-0.5, 0.72, 0.5) | (0.5, 0.75, 0.5) |
| 6 | (-0.5, 0, 0.5) | (0.5, 0.97, 1) |
| 7 | (-0.5, 0.97, 0.5) | (0.5, 1, 1) |

Use radius 0.3 m, half-height 0.75 m, step 0.4 m, foot radius 0.15 m and explicit offset
0.001 m. At feet `(0, 0.5, -0.25)`, the first shell centre is `(0, 1.4510001, -0.25)`,
radius 0.3 and cylinder length 0.5. Prove its upward lift and a grounded +Z acquired explicit tick.
Then bounded ascent/descent, yaw-rotated installation, split statics and compound representations
must reach the final 3D target within a Euclidean 0.001 m ball. No per-axis substitute.
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
