# Swimming Task 4 candidate coverage

Status: source proposal for the next generic backend slice. No candidate implementation, public
sweep forwarding, native adapter or complete-world certificate exists from this document.
The single identity-box sweep is preserved at `04e38bf73`.

## Problem

A complete proof for each returned leaf is insufficient if broad-phase selection can omit a relevant
leaf. The current single-box helpers establish neither selected-world coverage nor numerical bounds
for other shape families. An unsupported collider cannot be treated as harmless merely because an
unproved bounding box did not overlap the query. Whole-world enumeration on every movement tick would
also contradict the larger-map direction.

Pinned source observations:

- [Box.ComputeBounds](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Box.cs#L50)
  reduces to the stored half sizes for an exact identity orientation.
- [ConvexShapeBatch.ComputeBounds](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Shapes.cs#L224)
  adds the installed pose translation in binary32.
- [Statics.Add and UpdateBounds](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Statics.cs#L324)
  install/update the computed bounds in the broad phase.
- [BroadPhase.GetOverlaps](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/BroadPhase_Queries.cs#L125)
  visits active and static trees. A callback refusal in the first tree does not prevent starting the
  second tree, so an exhausted collector must stay exhausted across both.
- [Tree volume query](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Trees/Tree_VolumeQuery.cs)
  delegates to [BoundingBox.Intersects](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuUtilities/BoundingBox.cs#L63),
  which uses inclusive comparisons. The pinned tree query uses a fixed traversal stack with a
  Debug-only depth assertion. A leaf callback budget alone therefore does not bound its traversal.
  Parent-bound maintenance and dynamic update ordering still need their own pinned audit.

## Recommendation

Keep bound-certification evidence with the backend's existing registration/lifecycle bookkeeping.
Use the existing spatial tree only when the exact selected view's relevant shape/pose families have
proved bounds. This evidence is local, derived and invalidated by physical mutation. It is not a new
native sampler, world directory, portable identity or replacement spatial index.

| Option | Complete evidence | Large-map fit | Cohesion | Simplicity | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| Registration-backed bounds evidence plus existing tree | 9 | 9 | 9 | 6 | 33 |
| Bounded complete world scan for every query | 9 | 2 | 6 | 8 | 25 |
| Trust the current tree without numerical evidence | 1 | 9 | 7 | 10 | 27 |

Completeness is mandatory, so the third option is disqualified despite its convenience. The first
option avoids a world-size limit for certified spatially indexed content. Its cost is explicit
mutation bookkeeping and proof for each admitted shape family. The second may be useful only as a
finite test oracle, not as the production movement path.

## Initial bound argument and remaining proof

For an identity box, each true face coordinate is a sum or difference of two represented binary32
values. Its stored bound is that expression rounded to binary32. Monotonic rounding means that a
query bound rounded outward cannot miss a touching face because of that final rounding alone. This
argument needs the actual installed shape, exact identity pose, finite extents and finite stored
bounds. It does not apply to rotated shapes, compounds, meshes or dynamic bounding-box prediction.

A swept query aperture must enclose the complete original capsule path in all three axes, including
its axial/radial extent and closed endpoints. It must be encoded outward before tree traversal.
A selected leaf with unsupported narrow-phase geometry still refuses the whole result. A shape whose
bounds themselves are unproved must cause refusal even if its raw tree bound would omit it, unless
that shape is explicitly outside the selected view/filter. No assumption about native partitioning
makes this condition true.

The first usable bound domain may be identity-box statics only. That is an explicit capability limit,
not the completion of Task 4 or a sufficient native domain. Other shape families and dynamics remain
unresolved until their bounds and swept geometry are proved. General installed-pose arithmetic stays
owned by the feature-query lane. Sweep candidate selection and aggregation stay owned by swimming.

## Bounded tree preflight proposal

Before calling the existing overlap enumerator, traverse the same public Tree node buffers under the
same physical read interval with checked, bounded scratch storage. Certify only the exact query
aperture. Bound node visits, pending stack depth and visited leaves, including leaves later excluded
by selection. Validate node/leaf indices and refuse malformed or over-budget traversal. This avoids
claiming that callback limits protect work or stack depth before the first callback.

Only after both active/static preflights succeed may the existing backend enumerator resolve its
internal leaf references. Both passes use the identical immutable trees and aperture. This retains
the backend's leaf-to-collidable mapping without reflection, a copied spatial index or a whole-world
scan. The second traversal is deliberate bounded overhead. Candidate collection still keeps a sticky
refusal and validates its final counts. A tree mutation between these passes is forbidden by the read
gate. The exact finite caps and degenerate-tree controls remain to be pinned before implementation.

## Proposed file and lifecycle scope

- New `BepuPhysicsWorld.SweepCandidates.cs` for local evidence and bounded selected collection.
- New cohesive aperture helper using the existing interval primitives, with no arithmetic fork.
- Narrow registration/removal hooks in `BepuPhysicsWorld.cs` and rebase handling in
  `BepuPhysicsWorld.Rebase.cs`, only after the complete mutation inventory and failure paths are pinned.
- Later optional sweep forwarding in QueryView. Feature-query methods and lease signatures stay owned
  by their existing lane and unchanged.

The inventory must cover successful and partially failed add/remove/rebase operations, disposal,
actual source counts, handle reuse and any dynamic operation admitted by the eventual domain.
An exception cannot leave a stale valid certificate. A later unrelated successful mutation cannot
silently revalidate it. GeometryGeneration remains the existing local read-lifetime identity, not a
portable navigation hash. Local evidence updates under the physical mutation gate, never by escaping
the selected view during an active read lease.

Bound collection work explicitly, including excluded/unsupported entries examined before filtering.
Do not cap only appended candidates while scanning unlimited excluded geometry. Exhaustion retains
no usable prefix, and refusal stays sticky across active/static tree traversal. Actual finite limits
and their policy-identity fields will be pinned with the implementation fixtures.

## Finite proof inventory before public forwarding

1. Outward aperture at origin, translated/negative coordinates, exact contact and one-float neighbors.
2. Actual tree inclusion at closed start/end and a thin collider between clear endpoints.
3. Parent tree refit/insertion/removal and rebased coordinates, with reversed insertion order.
4. Exact selected-view exclusions and mobility filters, including excluded unsupported geometry.
5. Unsupported bound family outside the raw aperture still refuses without a supporting certificate.
6. Exhaustion counts examined entries, stays sticky across both trees and exposes no destination prefix.
7. Partial mutation failure invalidates evidence, and stale/reused handles cannot revalidate it.
8. Aggregate earliest bracket from every relevant leaf, tied contacts and no geometry-stage inference
   from an eventual composed refusal.

This proposal does not authorize an assumption about native meshes or residency. Migration still
owns the native producers and G1b proof. The existing scope, frame, identity and lease checks remain
required around any eventual public backend result.
