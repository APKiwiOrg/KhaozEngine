# Parallel contact navigation bake

Design for [#1401](https://github.com/APKiwiOrg/KhaozEngine/issues/1401). Source baseline: `c45768b73` on `main`,
staged 20.39.0 with [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438) phase 5b. The owner chose option A
(parallel bake split by region) on 2026-10-14. Every choice below is a recorded ruling for the owner's review.
Revision 2 answers review round 1 (F1 to F8).

## Problem

The phase 5b cost record measured about 65 to 109 ms of warm work per admitted contact surface, 95 to 97 percent of it
in edge proofs. At Hollowmere scale (about one million columns, one or two raw surfaces each) one contact profile
bakes in about 18 to 61 hours on one thread. Every world edit and engine pin rebakes navigation, so contact adoption
is not practical at that cost.

Facts that make the bake serial today, verified at `c45768b73`:

1. `BepuPhysicsWorld` has one reentrant query monitor (`BepuPhysicsWorld.QueryLease.cs`). Every query and read lease
   enters it, because reads share one Bepu `BufferPool` (`BepuPhysicsWorld.cs:40`), shared collectors and the
   world-wide `InstalledSupportGeometryCache`, whose dictionary is written from the read path. A movement lease holds
   the monitor for a whole step.
2. A `MovementEnvironmentContext` admits one lease at a time and owns one workspace. Proof bindings share their
   source's context.
3. `GroundMoveContext.NavigationFrame` writes the context's origin and frame on every read.
4. Diagnostics (`SupportQueryDiagnostics`, `ContactAllocationDiagnostics`) are thread-static.

## Scope

In scope: concurrent physics readers on `BepuPhysicsWorld`, a worker model for contact profile builds, a partitioned
schedule with serial-equivalent failures, an opt-in execution setting, worker diagnostics and a measured speedup.

Out of scope: legacy profile parallelism (legacy bakes take minutes), multi-process or multi-world bakes, proof reuse
or cheaper proofs (#1401 option B), native MapDoc movement, Grimhollow adoption, and any change to bake bytes.

## Rulings

### PB-1 Concurrent physics readers

**Gate.** `BepuPhysicsWorld` replaces its monitor with a writer-preferring reader and writer gate that tracks read
depth per thread.

- The outermost read on a thread (a lease or a single query) admits one reader interval. Every nested read on that
  thread (view-to-core delegation, footprint-to-sweep delegation, registration authentication, ground delegates,
  diagnostic callbacks) reuses the admitted interval and never waits, even when a writer is queued.
- A second explicit lease on the same world and thread still throws and leaves the first lease intact.
- A mutation or disposal on a thread that holds a read interval throws before any upgrade attempt and before the
  geometry generation advances, as the public seam promises today.
- Writers are exclusive. Once a writer queues, new outer readers wait. The writer waits for active outer readers to
  leave. Writers run in arrival order. Reads made by the writer's own thread during its mutation run inside the
  writer's interval, as the reentrant monitor allows today.
- Wrong-thread lease use and disposal are rejected before waiting on any gate.

**Scratch.** Each reader interval borrows one `QueryScratch` from a thread-safe owner pool and returns it at the end of
the interval. A `QueryScratch` owns its own Bepu `BufferPool`, distinct from the simulation pool, plus every reusable
collector, traversal stack and result buffer the read path uses. Every read-side allocation and return goes through
that pool, including `Simulation.Sweep`, penetration and contact batchers, mesh contact reduction and smoothing.
Scratch is reused across intervals and cleared on return. Partial acquisition releases what it took.

**Installed support geometry.** The cache publishes immutable installed topology through a thread-safe map keyed by
installation identity and static geometry version. Concurrent misses for one key construct it once. Mutations
invalidate entries inside the writer interval. Materialization writes only into the reader's scratch. No lock is held
across a footprint proof.

**Audit.** The implementation records a call-graph audit of the pinned BepuPhysics 2.4.0 read paths (ray, sweep,
broad-phase overlap, mesh, compound and hull branches) showing no shared write outside the reader's pool, and pins it
with simultaneous-reader result comparisons.

**Why:** edge proofs are almost the whole bake. Concurrent readers are also the engine capability a multi-threaded
server tick needs. A per-thread world copy would duplicate world construction and multiply memory for the same result.

### PB-2 Workers and one build snapshot

- **Provider contract.** Above degree one, every provider the caller supplied (ground height and normal delegates,
  medium delegate, water field, registration, area classifier) must answer as a deterministic function of its inputs
  over one fixed build snapshot, independent of call order and thread, and be safe for concurrent reads. The caller
  keeps every underlying datum fixed through capture and build, as it must today.
- **Snapshot.** Before any work, the build captures the physics basis and the environment producer identity (physics
  source, registration, boundary, water field and its generation, extent) once. It then constructs every worker's
  source and proof binding through `GroundMoveContext.CreateNavigationBinding` and authenticates each worker's first
  pin against the common snapshot. Only then does work start. After all workers join, the snapshot is checked again
  before anything is published. Any difference refuses the whole build. Worker-local lease basis checks stay.
- **Frame.** The navigation frame is captured once into the snapshot. `NavigationFrame` stops writing context state on
  read, and every caller reads the snapshot or a pure computation.
- **Sharing.** Workers share only immutable inputs: captured columns, the footprint, the snapshot and the options. The
  envelope is validated once, for the whole build.

### PB-3 Partitioned schedule and serial-equivalent failure

- **Admission.** Columns split into contiguous row bands. Each worker admits its band into its own anchor map. Maps
  merge into one, and a duplicate key throws as `Dictionary.Add` does today.
- **Layering.** Unchanged and serial, on the merged result in row-major order.
- **Edges.** Cells split into row bands. Each worker writes only its own cells' exit bytes.
- **Stair links.** Candidates are proved in parallel into a per-candidate result array, then emitted in the existing
  candidate order.
- **Bands.** The degree is a maximum. The build never creates more workers than bands and never schedules an empty
  band.
- **Determinism.** Every column, edge and link result is a pure function of the snapshot and the world, with no
  cross-cell accumulation, so graphs, resting heights and file bytes are identical for any degree.
- **Failure selection.** Each work item has its serial ordinal (phase, then row-major cell, then direction or candidate
  index). When a worker fails, the build stops scheduling items above that ordinal, lets running proofs finish, and
  completes every item below it, so the failure selected is the lowest ordinal, which is the one the serial build
  throws first. The original exception is rethrown with its type and stack.
- **Shutdown.** Stop checks happen between proofs, never inside a leased call. Every worker is joined, each worker
  disposes its own leases on its own thread, and only then are bindings and the context-owned view released.
  Caller-owned physics and borrowed views stay usable. If cleanup also fails, the primary exception is rethrown and
  the cleanup failures are attached to it as an `AggregateException` under `Exception.Data["CleanupFailures"]`.

### PB-4 Execution setting

- `NavBakeExecution` is a public record with `MaxDegreeOfParallelism`, which must be at least 1.
- New overloads of `PhysicsNavBake.BuildProfile` and `GroundNavigationBake.Create` take it. The existing signatures
  stay as forwarding overloads, so compiled consumers keep working. Null or omitted means degree 1.
- Validation order: the execution argument is checked first (`ArgumentOutOfRangeException` for a degree below 1), then
  everything checked today, in today's order.
- At degree 1 the build runs exactly the current serial path.
- `GroundNavigationBake.Create` builds profiles one after another and passes the execution to each contact profile.
  Legacy profiles always build serially.
- Above degree 1 on a physics world that does not declare concurrent readers,
  `IPhysicsQueryLeaseSource.SupportsConcurrentReaders`, the build throws `NotSupportedException`.
  `BepuPhysicsWorld` declares it.
- The setting is not part of `PhysicsNavBakeOptions` or the bake identity, so no bytes change.
- Endorsed providers are named in the documentation by method after the audit. That covers the TileWorld ground
  samplers, `TileMediumSampler` as a water field and the TileWorld registration. MapDoc is not endorsed: it has no
  ordinary contact factory, and `MapWaterDomain` triangulation and `MapScopedSurfaces.Patch` hold unaudited state.

### PB-5 Diagnostics and cost

- Each worker initializes the caller's enabled diagnostics on its own thread and collects them in a `finally`, restoring
  any prior thread-local state. Counts and allocation deltas are aggregated after the join.
- The cost record measures:
  - whole-build wall time on the caller,
  - summed per-proof time, separately,
  - allocations across all threads (`GC.GetTotalAllocatedBytes(precise: true)` deltas),
  - scratch pool size, worker utilization and per-phase time, including binding construction, envelope validation,
    merge and disposal.
- One cold and one warm local measurement at degree 1 and at the machine's core count, through `build-slot --heavy`.
  No loop, repeat or load run, and no no-GC assertion.

### PB-6 Version

The work is a minor version (new public API). It rides the newest untagged version on `main` at merge time, or cuts
the next minor if `main`'s version is tagged.

## Acceptance

Baselines first: before any production change, record normalized full-file baselines (engine identity replaced as
the legacy golden does) for the contact fixtures and a mixed legacy and contact fixture.

Physics:

- Two threads hold read leases on one world at once and both query successfully. Rays, mesh and compound sweeps,
  penetration and contact smoothing produce identical results to the serial gate under simultaneous readers.
- Cold installed support geometry for one owner, requested by two readers at once, is constructed once. A following
  mutation, removal and handle reuse invalidate it.
- Event-coordinated gate tests: a queued writer with a nested query inside an active reader completes without deadlock,
  a mutation from a ground or diagnostic callback throws before the generation changes, wrong-thread disposal is
  rejected without waiting, and a refused acquisition releases everything it took.
- A nested second lease on one thread still throws. Every existing physics, lease, support and movement test passes
  unchanged.

Bake:

- Every contact fixture (low-lip bank, deck, wade pool, rebased origin, mixed legacy and contact, the contact Stair
  fixture extended to both accepted and refused candidates) builds byte-identical to its baseline at degrees 1, 2, 3
  and 8. Raw and resting height bits, layer metadata, open and exit masks, candidate order and accepted link bits are
  compared.
- A fixture with multiple raw layers, edges and links crossing band boundaries, uneven bands, a degree above the row
  count, a non-binary cell size and a nonzero origin matches its baseline at every degree.
- Roof, footprint and exact-arrival refusals match at every degree.
- Snapshot: a water generation change between two workers' first acquisitions, and a change after one band completes,
  each refuse the whole build.
- Failure: a fixture where a later cell fails first in wall-clock time throws the earlier cell's exception, with the
  serial type and message, publishes nothing, releases every gate and leaves the caller's world and view usable. Fault
  providers used in these tests are schedule-independent.
- Execution: degree below 1, null, the forwarding overloads, and degree above 1 on a world without concurrent readers
  each behave as stated.
- Legacy bakes are byte-identical, and the all-legacy full-file golden still matches.

Cost: the PB-5 record with the measured speedup and an updated Hollowmere extrapolation.

## Tasks

1. Pre-change contact and mixed full-file baselines.
2. Concurrent physics readers: gate, reader scratch pools, installed support geometry publication and the Bepu audit
   (PB-1).
3. Contact bake workers: snapshot, frame, schedule, failure selection and shutdown, execution setting and worker
   diagnostics (PB-2 to PB-5).
4. Provider audit, documentation, cost record, changelog and full verification.

## Review response

Round 1 (`review-1.md`, Not ready, 0 Critical, 6 Important, 2 Minor):

- F1: installed support geometry publication (PB-1).
- F2: one Bepu `BufferPool` per reader scratch, all read allocations routed through it (PB-1).
- F3: per-thread read depth, nested reads never wait, writer preference, same-thread write refusal before upgrade,
  wrong-thread rejection before waiting (PB-1).
- F4: deterministic provider contract, one build snapshot with a start barrier and a post-join check (PB-2).
- F5: lowest serial ordinal failure, original exception, ordered shutdown and secondary-error policy (PB-3).
- F6: pre-change baselines and the expanded fixture matrix (Acceptance).
- F7: forwarding overloads, validation order, maximum degree semantics, backend declaration (PB-4).
- F8: worker diagnostics and all-thread cost accounting (PB-5).
- Unverified notes: the Bepu audit is an implementation deliverable (PB-1), MapDoc is excluded from endorsement (PB-4),
  and the speedup is measured rather than claimed (PB-5).
