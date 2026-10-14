# Bepu concurrent read audit

Ruling PB-1 of [the parallel contact bake design](../design/PARALLEL-CONTACT-NAVIGATION-BAKE-2026-10-14.md) lets
several threads read one `BepuPhysicsWorld` at once. This audit records every read entry point, the pinned
BepuPhysics 2.4.0 calls it makes (`Directory.Packages.props`), and where each allocation and shared write goes. The
library is not vendored. Upstream citations point at tag `v2.4.0` (commit `9beac0122935`) and use the prefix
`BP` for `https://github.com/bepu/bepuphysics2/blob/v2.4.0/`.

## Result

No read path writes shared state outside the reading interval's own storage.

- Every read-side Bepu allocation goes to the interval's `QueryScratch.Pool`, a private `BufferPool` distinct from
  the simulation pool. The simulation pool (`BepuPhysicsWorld._pool`) is now used only by writers.
- Engine-side managed scratch (candidate lists, contact collectors, footprint and neighborhood collectors) is
  loaned to one operation at a time. Nested operations of the same kind receive an inactive or fresh set.
- The installed support geometry cache publishes immutable topology through a concurrent map, and the first
  publication for a key wins. Writers invalidate it. A reader only compare-and-removes a slot it found refused or
  no longer matching the live installation, which never removes a newer slot.
- Bepu reads touch the simulation through read-only accessors, `readonly` tree traversals with stack-allocated
  traversal stacks, and per-call dispatcher structs. Upstream already shares the collision and sweep task
  registries and `Shapes` across its own narrow phase workers.

The pins are `ConcurrentQueryParityTests` (rays, mesh, compound and hull sweeps, penetration, mesh contact
smoothing, certified sweeps, footprint and neighborhood queries through the world and a restricted view, compared
with serial answers under three simultaneous leases), `ConcurrentReadScratchTests` and `ConcurrentReadGateTests`.

## Why the allocator must be private

`BufferPool` is not synchronized. `PowerPool.Take` pops a slot from a `ManagedIdPool` and may resize its block array
([BP BepuUtilities/Memory/BufferPool.cs#L110](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuUtilities/Memory/BufferPool.cs#L110)),
`Return` pushes the slot back ([#L166](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuUtilities/Memory/BufferPool.cs#L166)),
and `Clear` frees native blocks ([#L191](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuUtilities/Memory/BufferPool.cs#L191),
[#L403](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuUtilities/Memory/BufferPool.cs#L403)). Two readers on one
pool would corrupt it. Each `QueryScratch` therefore owns one pool, used by one interval at a time. Returning scratch
keeps its capacity. World disposal clears every pool once. `GetCapacityForPower`
([#L249](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuUtilities/Memory/BufferPool.cs#L249)) measures the
retained native capacity in the scratch accounting test and in the support cost scratch payload. The payload never
walks a pool by reflection: `PowerPool.Blocks` is a `byte*[]` in 2.4.0, and its native blocks carry no managed
layout.

## Gate and scratch ownership

`QueryGate` (`KhaozEngine.Physics.Bepu/QueryGate.cs`) admits one reader interval per thread for the outermost read
and lets every nested read on that thread reuse it without waiting. Writers take FIFO tickets, wait for admitted
readers to leave, and block new outer readers once queued. A mutation on a thread holding a read interval throws
before any wait or generation change. A lease inside a writer interval throws. Scratch is rented lazily from
`QueryScratchPool` on first use in an interval and returned at the outer exit of the reader or writer interval.

## Read entry points

Lines refer to `KhaozEngine.Physics.Bepu` after this change.

| Entry point | Engine storage | Bepu 2.4.0 calls | Allocation and shared writes |
| --- | --- | --- | --- |
| `Raycast` (`BepuPhysicsWorld.Queries.cs:19`) | none | `Simulation.RayCast`, `BroadPhase.RayCast`, `Tree.RayCast`, `ShapeBatch.RayTest` | Per-call `RayHitDispatcher` and handler structs ([BP Simulation_Queries.cs#L163](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Simulation_Queries.cs#L163), [#L135](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Simulation_Queries.cs#L135)). `GetPoseAndShape` reads handles and poses ([#L117](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Simulation_Queries.cs#L117)). Broad phase traversal is per call ([BP BroadPhase_Queries.cs#L44](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/BroadPhase_Queries.cs#L44)), and `Tree.RayCast` is `readonly` with a `stackalloc` stack ([BP Tree_RayCast.cs#L92](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Trees/Tree_RayCast.cs#L92)). Mesh rays use a local leaf tester over the mesh tree ([BP Mesh.cs#L230](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Mesh.cs#L230)). Compound and big compound rays iterate children or their tree ([BP Compound.cs#L181](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Compound.cs#L181), [BP BigCompound.cs#L109](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/BigCompound.cs#L109)). No allocation, no shared write. |
| `SweepCapsule` (`Queries.cs:43`) | `QueryScratch.Pool` | `Simulation.Sweep`, `BroadPhase.Sweep`, `Tree.Sweep`, `SweepTaskRegistry.GetTask`, convex, convex compound and convex homogeneous compound sweep tasks | The dispatcher carries the caller's pool ([BP Simulation_Queries.cs#L174](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Simulation_Queries.cs#L174), task call at [#L221](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Simulation_Queries.cs#L221)). Mesh and compound targets collect child overlaps into that pool and return them ([BP ConvexHomogeneousCompoundSweepTask.cs#L26](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/SweepTasks/ConvexHomogeneousCompoundSweepTask.cs#L26), [BP ConvexCompoundSweepOverlapFinder.cs#L22](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/SweepTasks/ConvexCompoundSweepOverlapFinder.cs#L22), [BP Mesh.cs#L297](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Mesh.cs#L297), [BP Compound.cs#L262](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/Compound.cs#L262), [BP BigCompound.cs#L196](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Collidables/BigCompound.cs#L196), [BP CompoundPairOverlaps.cs#L30](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/CollisionTasks/CompoundPairOverlaps.cs#L30)). `Tree.Sweep` is `readonly` with a `stackalloc` stack ([BP Tree_Sweep.cs#L84](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Trees/Tree_Sweep.cs#L84)). The sweep task registry is shared upstream by per-worker CCD sweeps with per-worker pools ([BP NarrowPhase.cs#L494](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/NarrowPhase.cs#L494), [#L563](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/NarrowPhase.cs#L563)). Back-face culling reads `Statics.GetDirectReference` and the mesh (`HitHandlers.cs:118`). |
| `ComputePenetration` (`Queries.cs:80`) | `QueryScratch.Overlaps`, `QueryScratch.Pool` | `BroadPhase.GetOverlaps`, `Tree.GetOverlaps`, `Statics.GetDescription`, `CollisionBatcher` with `NarrowPhase.CollisionTaskRegistry` | Overlap traversal is per call with a `stackalloc` stack ([BP BroadPhase_Queries.cs#L125](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/BroadPhase_Queries.cs#L125), [BP Tree_VolumeQuery.cs#L61](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/Trees/Tree_VolumeQuery.cs#L61)). The batcher takes its batch table, cached shapes and pairs from its pool and returns them in `Flush` ([BP CollisionBatcher.cs#L57](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/CollisionBatcher.cs#L57), [#L298](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/CollisionBatcher.cs#L298)). It only reads the registry ([#L203](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/CollisionBatcher.cs#L203)), which Bepu's narrow phase shares across worker batchers ([BP NarrowPhase.cs#L342](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/NarrowPhase.cs#L342)). Mesh and compound reductions are batcher continuations. |
| `QueryCapsuleContacts` (both overloads, `CapsuleContacts.cs:21`, `:26`) | `QueryScratch.Contacts` (candidates and `CapsuleContactCollector`), `QueryScratch.Pool` | As penetration, plus `Mesh.Tree.GetOverlaps`, `Mesh.GetLocalChild`, `MeshReduction.ReduceManifolds` | `CapsuleMeshContacts.Smooth` takes and returns its triangle and child buffers from the interval pool (`CapsuleMeshContacts.cs:48`, `:84`). `ReduceManifolds` allocates its lists and dictionary from the pool it is given ([BP MeshReduction.cs#L297](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/MeshReduction.cs#L297), [#L411](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/MeshReduction.cs#L411), [#L505](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuPhysics/CollisionDetection/MeshReduction.cs#L505)). |
| `SweepCapsuleCertified` (both overloads, `CertifiedSweeps.cs:15`, `:19`) | `QueryScratch.SweepCandidates` | `Tree` node and leaf reads in `CapsuleTreePreflight`, `BroadPhase.GetOverlaps`, `Statics.GetDescription`, `Bodies.GetBodyReference` | Engine kernels (`CapsuleBoxSweep`) read shapes only. The sweep evidence and bound records are written by writers (`BepuPhysicsWorld.SweepMutations.cs`) and only read here. |
| `QuerySupportFootprint` (`SupportFootprint.cs:25`, view `QueryView.cs:101`) | `QueryScratch.Footprints`, `InstalledSupportGeometryCache` | Nested `SweepCapsuleCore` proposals, `BroadPhase.GetOverlaps`, `Statics.GetDescription`, `Shapes.GetShape`, `Compound.GetWorldPose`, mesh tree overlaps | Managed collector storage belongs to the loaned collector. Installed topology is published once per owner, installation and static version, then materialized into the collector's own capture scratch. Box faces come from a shared `static readonly` table that admission only reads (`CapsuleFeatureGeometry.cs:79`). |
| `QuerySupportNeighborhood` (`SupportNeighborhood.cs:15`, view `QueryView.cs:84`) | `QueryScratch.Neighborhoods` | `BroadPhase.GetOverlaps`, shape and pose reads | Collector-owned managed storage only. |
| `QueryCapsuleFeature`, `Assert*Current` (`CapsuleFeatures.cs:11`, `:17`, `SupportFootprint.cs:30`, `SupportNeighborhood.cs:20`) | Uncached managed captures per call | Shape and pose reads | Fresh managed arrays per call, no shared write. Assertions read lease state and the owner fields a live lease keeps writers from changing. |
| `GetDynamicPose`, `GetDynamicVelocity`, `IsAwake` (`BepuPhysicsWorld.cs:380` to `:410`) | none | `Bodies.GetBodyReference`, `BodyReference.Pose`, `Velocity`, `Awake` getters | Reads only. |
| `Origin`, `StaticGeometryGeneration`, `CreateQueryViewExcludingStatics`, `AcquireQueryReadLease` | Per-thread gate holder | none | The view snapshot is a new immutable exclusion set over the live reverse-handle map, which only writers change. The lease records itself on its thread's holder. |

Restricted views (`BepuPhysicsWorld.QueryView.cs`) forward each read to the same cores after entering a nested read.
Their own state is the immutable exclusion snapshot and a disposal flag changed only inside a writer interval, so
they declare `IPhysicsConcurrentReadSource` as well.

## Installed support geometry

`InstalledSupportGeometryCache` keys a `ConcurrentDictionary` by owner, installation and static geometry version.
Each key holds one slot that publishes an immutable capture outcome by compare-and-exchange, so the first
publication wins and a later capture never replaces it. No lock is held across a footprint proof.

- **Unobserved misses construct once.** Without an allocation observer on the thread, a miss captures under the
  slot's lock, and capture runs only engine code over read-only simulation state. A concurrent miss waits for that
  bounded capture and reuses its outcome.
- **Observed captures hold no lock.** Capture reaches `ContactAllocationPhase.SupportLocalValidation`, so with an
  observer installed it would run foreign code that may block on another reader or reenter the same key on its own
  thread. An observed miss therefore captures outside the lock and publishes only into an empty slot. A concurrent
  or nested observed miss may capture as well. A capture that loses publication is discarded, and its call uses
  the published outcome. `Constructions` counts
  published captures and `Captures` counts every capture run.
- **Read-path removal is compare-and-remove.** `GetOrCapture` removes a slot from the read path in two cases only:
  its published outcome is a refusal, which is never retained, or its published capture no longer matches the live
  compound. Both use `TryRemove(KeyValuePair)`, which removes only that exact slot, never a newer one.
- **Writers invalidate.** `StaticGeometryChanged` and `Rebase` clear or remove entries inside their writer interval.

Materialization copies topology into the borrowing collector's `CapsuleFeatureCaptureScratch`.

## Thread-local state outside the world

`SupportQueryDiagnostics` and `ContactAllocationDiagnostics` are thread-static, so diagnostics stay per thread.
A diagnostic observer runs inside the reading interval and may issue nested reads, which reuse the interval. It
must not synchronously wait on another thread entering the same world, as the PB-1 dependency rule states. The world
holds no lock of its own while an observer runs: the gate's internal lock is held only for admission bookkeeping, and the
installed geometry cache captures outside its slot lock whenever an observer is installed. So an observer may wait
on another reader's observer without stalling either reader.
