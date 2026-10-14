# Parallel contact navigation bake

Design for [#1401](https://github.com/APKiwiOrg/KhaozEngine/issues/1401). Source baseline: `c45768b73` on `main`,
staged 20.39.0 with [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438) phase 5b. The owner chose option A
(parallel bake split by region) on 2026-10-14. Every choice below is a recorded ruling for the owner's review.

## Problem

The phase 5b cost record measured about 65 to 109 ms of warm work per admitted contact surface. At Hollowmere scale
(about one million columns, one or two raw surfaces each) one contact profile bakes in about 18 to 61 hours on one
thread. Every world edit and engine pin rebakes navigation, so contact adoption is not practical at that cost.

Three facts make the bake serial today, verified at `c45768b73`:

1. `BepuPhysicsWorld` has one query gate (`BepuPhysicsWorld.QueryLease.cs`). Every query and every read lease enters
   the same monitor, because queries reuse backend scratch storage. A movement lease holds that gate for a whole
   step, so other threads wait even for plain raycasts.
2. A `MovementEnvironmentContext` admits one lease at a time and owns one `ContactTickWorkspace`. Proof bindings made
   by `CreateProofBinding` share their source's context, so two threads cannot prove through them at once.
3. `GroundMoveContext.NavigationFrame` writes the context's origin and frame on every read, unsynchronized, and the
   proof reads it on every lease.

## Scope

In scope: concurrent physics read leases on `BepuPhysicsWorld`, a worker model for contact profile builds, a
partitioned admission, edge and Stair proof schedule, and an opt-in execution setting.

Out of scope: legacy profile parallelism (legacy bakes take minutes), multi-process or multi-world bakes, proof reuse
or cheaper proofs (option B in #1401), Grimhollow adoption, and any change to bake bytes.

## Rulings

### PB-1 Concurrent read leases

`BepuPhysicsWorld` replaces its single query monitor with a reader and writer gate.

- Read leases and single queries are readers. Any number may run at once.
- Mutations (add, remove, move or reshape statics and bodies, stepping, rebasing) are writers. A writer waits for every
  reader to finish and blocks new readers while it runs.
- Each read lease owns its query scratch (traversal stacks, buffer pool slices and result buffers), rented from a
  thread-safe pool and returned at dispose. A query outside a lease rents scratch for its own duration. No scratch is
  shared between two live readers.
- Lease rules are unchanged per thread. A thread still cannot nest leases, a lease is still bound to its thread, and
  `AssertCurrent`, `Identity` and `GeometryGeneration` keep their meaning.
- Query results depend only on world state and inputs, never on which scratch served them, so results are identical
  to the serial gate.

**Why:** reads are the whole bake. Concurrent readers are also the engine capability a multi-threaded server tick
needs. A per-thread copy of the world would duplicate world construction and multiply memory for the same result.

### PB-2 Worker model for contact profiles

A contact `BuildProfile` with a parallelism above one creates one worker per degree of parallelism.

- Each worker has its own source and proof binding from `GroundMoveContext.CreateNavigationBinding`, so each worker
  has its own environment context, workspace and lease.
- The navigation frame is captured once at the start of the build as an immutable snapshot, and every worker reads
  the snapshot. `NavigationFrame` stops writing context state on read.
- Workers share only immutable inputs: captured columns, the footprint, the capture basis, options and the snapshot.
- Each worker checks its lease basis against the recorded capture basis as today. Any refusal or exception on one
  worker stops the build and is rethrown. No partial graph is published.

### PB-3 Partitioned schedule

- **Admission.** Columns are partitioned into contiguous row bands. Each worker admits its band and records anchors
  in its own map. The maps merge into one, and a duplicate key throws as `Dictionary.Add` does today.
- **Layering.** Unchanged and serial, on the merged admission result in row-major order.
- **Edges.** Cells are partitioned into row bands. Each worker writes only its own cells' exit bytes.
- **Stair links.** Candidates are proved in parallel into a per-candidate result array. The kept links are then
  emitted in the existing candidate order.
- **Determinism.** Every column, edge and link result is a pure function of immutable inputs and the world, so the
  graph, the resting heights and the file bytes are identical for any degree of parallelism, including one.

### PB-4 Opt-in execution setting

A new `NavBakeExecution` record carries `MaxDegreeOfParallelism`, default 1, and is passed as an optional argument to
`PhysicsNavBake.BuildProfile` and `GroundNavigationBake.Create`.

- It is not part of `PhysicsNavBakeOptions` and not part of the bake identity, so legacy and existing contact bytes do
  not change.
- At 1 the build runs exactly the current serial path.
- Above 1 the caller asserts that every provider it supplied (ground height and normal delegates, medium delegate,
  water field, registration and area classifier) is safe for concurrent reads. The public documentation states this
  contract. The engine's own TileWorld and MapDoc providers are audited and documented for it.
- Legacy profiles ignore the setting and build serially.

### PB-5 Version

The work is a minor version (new public API). It rides the newest untagged version on `main` at merge time, or cuts
the next minor if `main`'s version is tagged.

## Acceptance

- Two threads hold read leases on one world at once and both query successfully. A mutation started during them waits
  until both dispose, and readers started during a mutation wait for it. A nested lease on one thread still throws.
- Every existing physics, lease, support and movement test passes unchanged.
- For every contact fixture in the phase 5b tests (low-lip bank, deck, wade pool, mixed legacy and contact, rebased
  origin), a build at parallelism 1, 2 and 8 produces byte-identical files and equal graphs.
- A refusal on one worker (unknown anchor, changed basis, out-of-scope envelope) throws from `BuildProfile` and
  publishes nothing, at parallelism above one.
- Legacy bakes are byte-identical, and the all-legacy full-file golden still matches.
- Cost record: one cold and one warm local measurement of the cost fixtures at parallelism 1 and at the machine's core
  count, with the speedup and an updated Hollowmere extrapolation. No load or repeat run.

## Tasks

1. Concurrent physics read leases (PB-1).
2. Contact bake workers, frame snapshot and partitioned schedule with the execution setting (PB-2 to PB-4).
3. Provider audit, documentation, cost record, changelog and full verification.
