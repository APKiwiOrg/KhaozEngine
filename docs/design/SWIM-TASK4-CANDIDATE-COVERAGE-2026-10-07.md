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

Pending-stack certification must follow the pinned traversal order (A first, B pending when both
intersect), or prove an order-independent bound that dominates it. Another DFS order with a smaller
observed stack is insufficient. Use actual node/leaf counts, not backing buffer capacity.

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

## Aperture fixture checkpoint

Eight pure aperture facts compiled and failed at the absent helper type after independent endpoint
rounding controls passed. Zero passes/skips, exit 1 in `/tmp/swim-aperture-red.log`. No tree preflight
or bound-metadata production was present. These cases address query enclosure only.

The aperture helper passed all eight unchanged direct assertions, zero failures/skips, exit 0 in
`/tmp/swim-aperture-green.log`. It uses the unchanged path/interval primitives and publishes both
bounds only after every axis succeeds. Tree traversal and registration evidence remain unimplemented.

## Preflight value and fixture proposal

The internal `CapsuleTreePreflight.TryValidate(in Tree tree, Vector3 min, Vector3 max,
int maximumNodes, int maximumLeaves, int maximumPending, out int nodes, out int leaves,
out int pending)` reports counts only on complete success. All outputs are zero on refusal.
Maximum accepted policy values are 8192 visited internal nodes, 4096 visited leaves and 128 pending
B branches per tree. These are query-work limits, not tree/world-size limits. Pruned subtrees do
not consume their descendant visits. Two trees therefore have at most twice the per-tree work cap,
with an independent later aggregate candidate cap. All caps enter the eventual backend policy identity.

The pending cap stays below the pinned enumerator's fixed stack capacity. Both traversals use its
A-first/B-pending order. Validate live NodeCount/LeafCount before buffer access and child indices
against those counts, never merely against allocated capacity. Refuse invalid query bounds, visited
bounds, cycles exhausting work/stack or unsupported policy sizes without exposing partial counts.
The preflight is not a canonical topology, geometry or registration validator.

Twelve prepared facts use owned finite tree buffers. Known A-heavy and reversed three-leaf trees
have different pending peaks (2 versus 1). Other cases cover empty/single-leaf closed contact against
the real pinned enumerator, node/leaf/stack exhaustion, live-count versus capacity indices, malformed
bounds, a bounded cycle, invalid inputs and subtree pruning. Malformed trees are never passed to the
raw backend enumerator. These are traversal-preparation fixtures, not a native or selected-world proof.

## Mutation inventory refinement, source only

Current physical mutation entry points are below. Static registration hooks alone are insufficient.
Even a selected statics query can encounter sleeping-body leaves in the static tree, and a wake can
transfer them. Unsupported finite bound geometry and structurally invalid stored bounds must remain
distinct. Excluding an invalid leaf does not prove that its ancestors still bound other selected leaves.

| Entry point | Evidence affected |
| --- | --- |
| AddStatic / RemoveStatic | Shape lifetime, handles, static leaves and ancestor bounds |
| AddDynamic / RemoveDynamic | Active/sleeping leaves, shape lifetime, constraints removed with a body |
| AddConstraint / RemoveConstraint | Shapeless anchor lifetime and body wake/removal effects |
| SetConstraintTarget | Wake and active/static tree membership effects |
| SetDynamicVelocity | Wake and active/static tree membership effects |
| Step | Pose integration, bounds, refit/refinement and sleeping/waking |
| Rebase | Every pose, source origin/frame and all updated bounds |
| Dispose | Permanently unavailable source |
| QueryView.Dispose | Receiver becomes unavailable, with existing changesGeometry=false semantics |

One possible fail-closed bookkeeping design needs no QueryLease signature or EnterMutation change.
After entering a physical mutation, capture evidence only if its stamp matches the exact previous
GeometryGeneration, then invalidate it before any write. An explicit successful completion can carry
it forward only when that operation's full effect was audited, its original stamp was valid and no
nested/unexpected generation intervened. A missed hook leaves a generation gap. A later unrelated
successful hook cannot bridge that gap. Partial failure leaves evidence invalid. No catch/finally may
unconditionally advance the stamp.

This is a proposed implementation pattern, not permission to treat every successful operation as
preserving geometry. Each row still needs its exact disposition, including known pre-write refusal,
unsupported geometry, invalid stored bounds and any allowed recovery. No mutation hooks or lifecycle
changes have been implemented. Any required EnterMutation/QueryLease delta returns to the shared-seam
review before editing, as agreed with the feature owner.

The first preflight invocation stopped at fixture compilation, CS0246 for `IBreakableForEach<>`
at line 188. The required BepuUtilities root import was omitted. No tests executed and no capability
RED was established. Exit 1 in `/tmp/swim-tree-preflight-red.log`, with production still absent.

Restoring the single import produced valid compilation and twelve expected missing-helper failures,
zero passes/skips, exit 1 in `/tmp/swim-tree-preflight-red2.log`. Leading raw-enumerator/capacity controls
passed where reached. Assertions after a missing-helper call remain unexecuted until GREEN.

The preflight implementation passed all twelve unchanged direct assertions, zero failures/skips,
exit 0 in `/tmp/swim-tree-preflight-green.log`. The A-first pending peaks, raw valid-tree controls,
live-count refusals and zero outputs on incomplete traversal now have finite behavior evidence.
Topology, installed geometry bounds, mutation tracking and selected-world completeness remain separate
premises. No public sweep forwarding or registration hook was added by this slice.

## Evidence bookkeeping fixture proposal

The next isolated helper is a lifecycle guard, not a second lease, registry or geometry proof.
`CapsuleSweepEvidence(object source)` starts only from the caller premise of a newly constructed,
known-empty source at generation zero. Future backend integration must create it once in the owner,
never recreate it to repair an already mutated source. `IsCurrent(object source, long generation)`
requires reference identity and the accepted generation. It neither acquires a gate nor samples data.

`BeginMutation(long advancedGeneration)` immediately invalidates readiness and returns an instance-
bound token. It may carry evidence only from exactly advancedGeneration-1. `CompleteMutation(token,
long currentGeneration, bool recordsComplete)` accepts only that exact pending token, owner,
generation and complete-record assertion. Otherwise it clears pending/readiness. Abandoned, missing,
replayed, foreign, out-of-order and incomplete completions cannot be healed by an unrelated success.
No automatic recovery or generation reset is included.

Twelve prepared facts pin those lifecycle transitions, including distinct source objects equal by
value. They do not prove actual registration facts or mutation-hook coverage. Real backend hooks,
known pre-write failures and the complete mutation inventory still require separate integration proof.

The twelve lifecycle facts compiled and failed at the absent `CapsuleSweepEvidence` type, zero
passes/skips, exit 1 in `/tmp/swim-evidence-lifecycle-red.log`. No lifecycle behavior or actual
registration proof is inferred from this RED result.
