# Swimming contact backend evidence

This is Task 2 implementation evidence for the [approved generic plan](../superpowers/plans/2026-10-06-swimming-generic-foundations.md).
It does not amend accepted F3 or open native G1b.

## Validation state

The initial 16-case RED run compiled and exited 1. Every case failed because the optional
`IPhysicsCapsuleContacts` capability was absent. The unsupported-mask case consequently failed
its expected `NotSupportedException` assertion with the missing-capability assertion instead.
That RED is not evidence of working refusal behavior. The direct-API GREEN must independently
prove supported contact queries and nonzero-mask refusal.

First GREEN compiled and exited 1 with 21 passed and 1 failed out of 22. Unsupported-mask refusal
passed independently. The flat mesh seam case returned a complete empty set at 0.001 m positive
separation, violating completeness. Named regressions were not run, and the slot was released.

Pinned `ConvexCompoundOverlapFinder.FindLocalOverlaps` selects mesh and compound children from
convex bounds expanded by velocity over `dt`, not by `SpeculativeMargin`. The short
`CollisionBatcher.AddDirectly` overload supplies zero velocities and zero maximum expansion,
and this query uses zero `dt`. The capsule's bottom is above the flat mesh, so the mesh child is
omitted before a triangle contact can be generated. Parent broad-phase expansion alone does
not fix that inner selection.

The correction enumerates mesh triangles and compound convex children using explicitly
margin-expanded capsule bounds, then submits each convex pair directly to the same Bepu
narrow phase. The mesh still uses Bepu seam smoothing. This is physics geometry selection,
not a native water/terrain sampler. The original seam assertion remains intact and an additional
positive-separation compound-child case covers the same defect for compounds.

The corrected Release run exited 0 with 23 passed, no failures and no skips. This includes the
unchanged mesh seam assertion, the positive-margin compound case, both thin-wall insertion orders,
analytic box/capsule/hull separation, vertical coordinates at +/-640 m, destination refusal,
precision refusal, dense-mesh refusal and unsupported-mask refusal.

The affected Release regression run used `--no-build` and exited 0 with 46 passed, no failures and
no skips. It covered PhysicsQueryLeaseTests, PhysicsQueryViewLifecycleTests,
PenetrationAllocationTests, ConvexHullAlignmentTests and TerrainMeshCollisionTests.

Logs are `/tmp/swim-contacts-green.log` (first 21/1), `/tmp/swim-contacts-green2.log` (23/0), and
`/tmp/swim-contacts-regressions.log` (46/0). Each command ran serially through the shared slot
under its explicit coordinator grant. The slot was released after each granted window.
This is focused Task 2 evidence, not whole-branch verification or native G1b acceptance.

## Gathering and deliberate refusals

The implementation submits source convex leaves directly so no final nonconvex contact-count
reduction can discard independent constraints. All contacts come from direct convex pair results.
Mesh contacts use the pinned backend's seam smoothing before publication, without applying its final contact-count reduction. All output is held privately
until the complete query succeeds and the caller's capacity is known to be sufficient.

The following are backend capability limits, not claims that the geometry is clear:

| Condition | Impact and result |
| --- | --- |
| More than 4096 selected broad-phase candidates | Any supported source shape can exceed the work bound. Incomplete, untouched destination |
| More than 4096 selected leaves, 4096 children in one compound, or 16384 collected contacts | Dense compounds or meshes can exceed the work bound. Incomplete, untouched destination |
| More than 1024 triangles in a mesh's expanded local capsule region | Bepu 2.4.0's public smoothing helper returns without smoothing above 1024. This query refuses the whole result, including valid terrain/building meshes |
| Mesh scale other than unit scale | Current ShapeFactory creates unit-scale meshes. A future backend representation with a different scale is refused pending proof |
| Smoothing retains a contact but changes its normal | Bepu can rotate a mutually infringing, penetrating triangle contact while retaining its old depth. The old depth is not a certified separation along the changed normal. The complete query is refused. This can affect a capsule wedged between adjacent triangles, including mesh start penetration |
| Numerical budget exceeds 0.001 m or an input/intermediate cannot be certified | Explicit incomplete result. No assumed-clear or legacy fallback |
| Nonzero layer mask | `NotSupportedException`. The backend has no per-body layer assignment. Legacy filter repair remains separate issue #1315 |
| Destination too small | Incomplete with exact required capacity after gathering. No usable prefix and no clearing of caller storage |

The finite analytic fixtures pass within the returned numerical budgets. They are not a proof
for all possible backend geometry or all native import inputs.
A passing ordinary mesh fixture does not prove all mesh start penetrations or all mesh densities.
Legacy deepest-penetration queries retain their existing behavior.

## Native handoff obligation

G1b must explicitly account for these limits using actual native solids and scope/seam proofs.
No assumption is made that native mesh partitioning keeps every query below the limits, or that
valid native placements never begin in a mutually infringing mesh contact. Native producers and
the generic resolver must handle an explicit unresolved/refused result without movement commit,
unknown-to-dry fallback, or treating it as proven unreachable for combat policy. If native
requirements need broader accepted coverage, that backend work needs a proved implementation
before adoption. This task does not change native content or prescribe its partitioning.

## Pinned source evidence

The relevant implementation is BepuPhysics 2.4.0, not current upstream main:

- [CollisionBatcher](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/CollisionBatcher.cs), child callbacks before continuation reduction.
- [ConvexCompoundOverlapFinder](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/CollisionTasks/ConvexCompoundOverlapFinder.cs), velocity-expanded child bounds without speculative-margin expansion.
- [NonconvexReduction](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/NonconvexReduction.cs), bounded parent contact selection.
- [MeshReduction](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/MeshReduction.cs), the 1024 early return and normal correction without depth recomputation.
