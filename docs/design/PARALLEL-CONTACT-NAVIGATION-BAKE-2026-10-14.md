# Parallel contact navigation bake

Design for [#1401](https://github.com/APKiwiOrg/KhaozEngine/issues/1401). Source baseline: `c45768b73` on `main`,
released as 20.39.0 with [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438) phase 5b. The owner chose option A
(parallel bake split by region) on 2026-10-14. Every choice below is a recorded ruling for the owner's review.
Revision 3 answers review rounds 1 and 2.

Status: implemented in staged 20.40.0. The provider audit endorses the TileWorld ground samplers, `TileMediumSampler`
and the TileWorld registration, named by method in the Movement package README. The
[local cost record](../verification/2026-10-14-parallel-contact-bake-cost.md) holds the PB-5 metrics.

Erratum: the first parameter of the new `GroundNavigationBake.Create` overload is named `capture`, not `bake`, to
match the existing three argument overload.

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

**Gate.** `BepuPhysicsWorld` replaces its monitor with a writer-preferring reader and writer gate that tracks read and
write depth per thread.

- The outermost read on a thread (a lease or a single query) admits one reader interval. Every nested read on that
  thread (view-to-core delegation, footprint-to-sweep delegation, registration authentication, ground delegates,
  diagnostic callbacks) reuses the admitted interval and never waits, even when a writer is queued.
- A second explicit lease on the same world and thread still throws and leaves the first lease intact.
- A mutation or disposal on a thread that holds a read interval throws before any upgrade attempt and before the
  geometry generation advances, as the public seam promises today.
- Writers are exclusive and run in arrival order. Once a writer queues, new outer readers wait, and the writer waits for
  active outer readers to leave. A nested mutation on the writer's own thread runs inside its interval, as the
  reentrant monitor allows today. Reads on the writer's own thread also run inside it, with scratch attached lazily
  for the writer's interval and released at its outer exit. An explicit lease requested inside a writer interval
  throws, so no lease can outlive the writer.
- Wrong-thread lease use and disposal are rejected before waiting on any gate.
- **Supported dependencies.** Same-thread reentrancy is supported. A read callback must not synchronously wait on
  another thread entering this world or on a writer completing, exactly as the current exclusive monitor already
  forbids. Writers make progress once every admitted reader interval ends. A continuous writer queue can delay new
  readers, which is accepted because no writer runs during a bake.

**Scratch.** Gate nesting and scratch loans are separate concepts.

- Each outer interval borrows one `QueryScratch` from a world-owned pool. It owns a private Bepu `BufferPool`, distinct
  from the simulation pool. Every read-side allocation and return goes through it, including `Simulation.Sweep`,
  penetration and contact batchers, mesh contact reduction and smoothing.
- Collectors and buffer sets inside a `QueryScratch` are loaned per operation with the existing take-and-restore
  pattern (`BepuPhysicsWorld.SupportFootprint.cs`, `SupportNeighborhood.cs`). A nested operation of the same kind,
  such as a footprint query issued from a diagnostic callback, gets an inactive set or a fresh one, never the active
  one.
- Returning scratch resets it logically and keeps its allocator capacity. Pools are not cleared per interval.
- World disposal is exclusive. After every interval has left, it drains the scratch pool and disposes every
  `QueryScratch` exactly once, clearing each private `BufferPool`. A partially constructed scratch is cleaned where it
  failed. A borrowed view's disposal never disposes world-owned scratch.

**Installed support geometry.** The cache publishes immutable installed topology through a thread-safe map keyed by
installation identity and static geometry version. Concurrent misses for one key construct it once. With a diagnostic allocation observer attached, a miss captures with no cache lock held, so concurrent or nested misses may each capture and only the first publication is kept and counted (`Constructions` counts published captures, `Captures` counts every capture). Mutations
invalidate entries inside the writer interval. Materialization writes only into the borrowed scratch. No lock is held
across a footprint proof.

**Capability.** A new optional interface, `IPhysicsConcurrentReadSource`, declares that a receiver supports simultaneous
reader intervals and simultaneous query calls under the rules above. `IPhysicsQueryLeaseSource` does not change.
`BepuPhysicsWorld` and its restricted `QueryView` declare the capability. A forwarding wrapper declares it only after
auditing its own state.

**Audit.** The implementation records a call-graph audit of the pinned BepuPhysics 2.4.0 read paths (ray, sweep,
broad-phase overlap, mesh, compound and hull branches) showing no shared write outside the borrowed pool, and pins it
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
- **Serial order.** The build defines the complete serial order the current code runs in, and phases are barriers.
  - Setup comes first: snapshot, worker construction and first pins.
  - Admission follows, in captured Z, then X, then raw-surface order. Within a surface it runs anchor, placement and
    Hold in sequence, with its anchor insertion at that position.
  - Layering and link generation follow.
  - Edges then run in layer, Z, X and `ProfileNeighbors` direction order.
  - Stair links run last, in original candidate-array index order, skipped candidates included.
- **Failure selection.** Each work item carries its serial ordinal. Every item ends in success, refusal or failure, and
  failed items are never retried. When an item fails, the build atomically lowers a failure cutoff to that ordinal,
  stops scheduling items above it, and lets every unfinished item below it reach a terminal state. The failure with
  the lowest ordinal wins, which is the exception the serial API throws first, including the controller's existing
  exception translations. A cross-worker duplicate anchor carries the later insertion's admission ordinal. Setup,
  merge, extraction and generation failures keep their logical positions. A later phase starts only after its
  prerequisite phase joins successfully. Under fixed, terminating providers and the existing finite proof budgets, this
  drains a finite prefix and terminates. The selected exception is rethrown with its original type and stack.
- **Shutdown.** Stop checks happen between proofs, never inside a leased call. Every worker is joined, each worker
  disposes its own leases on its own thread, and only then are bindings and the context-owned view released.
  Caller-owned physics and borrowed views stay usable. If cleanup also fails, the primary exception is rethrown and
  the cleanup failures are attached to it as an `AggregateException` under `Exception.Data["CleanupFailures"]`.

### PB-4 Execution setting

- `NavBakeExecution` is a public record with `MaxDegreeOfParallelism`, which must be at least 1.
- New overloads, keeping every existing signature:
  - `PhysicsNavBake.BuildProfile(in MoveTuning tuning, NavAreaFilter areas, GroundProfileOptions? options,
    NavBakeExecution? execution)`
  - `GroundNavigationBake.Create(PhysicsNavBake bake, NavBakeSources sources, IReadOnlyList<NavBakeProfile> profiles,
    NavBakeExecution? execution)`

  The existing two and three argument `BuildProfile` forms and the three argument `Create` form forward with a null
  execution. Existing `null` and `default` option calls stay unambiguous. Null execution means degree 1.
- Validation order:
  1. The execution argument is checked first, with `ArgumentOutOfRangeException` for a degree below 1.
  2. Every existing check runs next, in today's order, including profile resolution and sorting in `Create`.
  3. Only then, for a resolved contact profile at a degree above 1, the build requires `IPhysicsConcurrentReadSource`
     on both the logical physics source and the exact selected query view, before any worker is constructed. If
     either lacks it, the build throws `NotSupportedException`.

  Legacy profiles always build serially and never require the capability, including in an all-legacy build above
  degree 1.
- At degree 1 the build runs exactly the current serial path, with no new snapshot check.
- `GroundNavigationBake.Create` builds profiles one after another and passes the execution to each contact profile.
- The setting is not part of `PhysicsNavBakeOptions` or the bake identity, so no bytes change.
- Endorsed providers are named in the documentation by method after the audit. That covers the TileWorld ground
  samplers, `TileMediumSampler` as a water field and the TileWorld registration. MapDoc is not endorsed: it has no
  ordinary contact factory, and `MapWaterDomain` triangulation and `MapScopedSurfaces.Patch` hold unaudited state.

### PB-5 Diagnostics and cost

- **Isolation.** Workers copy the caller's diagnostic enablement, not its observer delegates. Each worker records into
  its own recorder through a scoped helper that captures and restores every prior thread-local counter, observation
  and callback, including on setup failure. Records merge into the caller's after the join. A caller observer reaches
  workers only through an explicit per-worker observer factory.
- The cost record measures:
  - whole-build wall time on the caller, including setup and disposal,
  - summed per-proof time, separately,
  - managed allocations across all threads (`GC.GetTotalAllocatedBytes(precise: true)` deltas) and native scratch
    capacity retained by reader pools,
  - worker utilization and per-phase time, including binding construction, envelope validation, merge and disposal.
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
- Cold installed support geometry for one owner, requested by two readers at once, is constructed once. With a diagnostic allocation
  observer attached, concurrent or nested misses may each capture and only the first publication is kept and counted
  (`Constructions` counts published captures, `Captures` counts every capture). A following
  mutation, removal and handle reuse invalidate it.
- Event-coordinated gate tests: a queued writer with a nested query inside an active reader completes without deadlock,
  a mutation from a ground or diagnostic callback throws before the generation changes, wrong-thread disposal is
  rejected without waiting, and a refused acquisition releases everything it took.
- A footprint query triggered from a diagnostic observer during an outer footprint query, with different requests and
  output arrays, leaves both results correct. Writer-interval reads release their scratch at writer exit, and an
  explicit lease inside a writer interval throws.
- Scratch accounting: every constructed `QueryScratch` is disposed exactly once with its native pool cleared, after
  success, a query exception, a failed acquisition and owner disposal.
- Capability: an unchanged old `IPhysicsQueryLeaseSource` implementer still compiles and works serially. The Bepu world
  and its view declare concurrent reads. A borrowed wrapper without the capability over a Bepu owner refuses degree
  above 1.
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
- Failure: two-failure fixtures across layers, raw surfaces and candidate order, where the later one fails first in
  wall-clock time, throw the serial API's exception. Also covered: a provider exception, a held sibling lease, a setup
  failure after partial worker construction, and a cleanup failure attached to the primary. Each publishes nothing,
  releases every gate and leaves the caller's world and view usable. Fault providers are schedule-independent.
- Diagnostics: a diagnostic-enabled build at degree 8 matches the degree 1 bytes, with balanced independent recorder
  stacks, correct merged counts and the caller's prior thread-local state restored.
- Execution: degree below 1 (also combined with an invalid existing argument), null, the existing `null` and `default`
  option call forms, an all-legacy build above degree 1 on an undeclared backend (serial, no refusal), and a mixed build
  above degree 1 each behave as stated.
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

Round 2 (`review-2.md`, Not ready, 0 Critical, 6 Important, 1 Minor):

- G1: supported dependencies stated, with writer progress conditional and reader delay accepted (PB-1).
- G2: scratch loans per operation separate from gate nesting, writer-interval scratch, and leases refused inside
  writers (PB-1).
- G3: the complete serial order with phase barriers, terminal states, the failure cutoff and termination (PB-3).
- G4: a separate `IPhysicsConcurrentReadSource` checked on the logical source and the exact selected view (PB-1,
  PB-4).
- G5: per-worker recorders without shared observer delegates, and a per-worker observer factory (PB-5).
- G6: logical reset on return and exclusive drain and disposal of reader pools (PB-1).
- G7: exact overload signatures, validation placement, legacy exemption, and degree 1 without the new check (PB-4).
