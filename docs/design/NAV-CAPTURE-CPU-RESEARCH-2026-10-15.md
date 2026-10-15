# Ground navigation capture CPU research

Research for [#1417](https://github.com/APKiwiOrg/KhaozEngine/issues/1417).
One representative caller-selected ground window was captured on engine commit
`cbeff834a55f1e982dbaaeea21a154ff49e9ea31`, in Release, on ARM64 with .NET 10.0.13.
Production behavior and bake formats were not changed.

## Result and decision boundary

The 41 by 51 metre window, at 0.25 metre cells, contained 164 by 204 columns
(33,456 cells). Its single cold capture used 0.541884 process CPU seconds and
2.3564423 wall seconds, with 4,417,632 managed bytes allocated. That is about
132 bytes per cell. Capture used about 0.23 CPU seconds per wall second.

This observation does not reproduce minutes spent inside `PhysicsNavBake.Capture`.
It also does not explain the complete slow tests. They include setup, profile
derivation and other work. No downstream profile or region graph was built in this
experiment. Their CPU costs remain unknown. Do not select a production capture
optimization as the fix for those test durations from this observation alone.

## Method and stage evidence

An opt-in test utility invokes caller setup once, captures once and writes the
captured columns separately. No warm-up capture, repeated trial, benchmark loop
or full engine suite ran. The only other capture was a four-column correctness
smoke using an engine fixture.

| Stage | Wall seconds | Process CPU seconds | Managed bytes | Current-thread bytes | GC 0 / 1 / 2 |
| --- | ---: | ---: | ---: | ---: | --- |
| Setup | 9.3844449 | 1.944774 | 163,729,144 | 163,717,592 | 19 / 8 / 6 |
| Capture | 2.3564423 | 0.541884 | 4,417,632 | 4,417,440 | 1 / 1 / 0 |
| Diagnostic snapshot | 0.1248672 | 0.021075 | 5,760 | 5,760 | 0 / 0 / 0 |

Setup includes input loading, collider construction and registration, context and
classifier preparation, and local input-identity recording. Snapshot includes
writing the columns and hashing their bytes. Configuration and measurement JSON
I/O between stages is outside the timed actions. Process CPU counter intervals
include a small amount of instrumentation surrounding each action.

The machine reported 12 processors. Its load averages were approximately
125 / 156 / 150 before the process and 121 / 154 / 149 afterwards. Other build and
test lanes were active. This was a heavily contended local observation, not an
idle-machine wall-time baseline. CPU time is process-wide, including runtime and
profiling work. No unprofiled comparison was run, so profiler overhead is unknown.
Managed counters exclude native allocation.

The installed `dotnet-trace` launched the built probe with runtime suspension.
Tracing was active before setup and capture. Profiles were
`dotnet-sampled-thread-time,gc-verbose`, with the
`KhaozEngine-NavCaptureResearch` stage-marker provider. The capture and conversion
both exited 0. Raw trace, exact input identity, invocation, captured bytes and hash
remain outside the public repository. Input and selected wiring hashes were
unchanged across the run.

## What the samples establish

The trace contains the real call chain
`PhysicsNavBake.Capture` to `PhysicsColumnProbe.Sample` to
`BepuPhysicsWorld.RaycastCore` and Bepu tree traversal. Origin reads and basis
validation also repeatedly enter the physics query gate.

Speedscope reconstruction yielded 2,354.100 milliseconds of sampled intervals
containing `PhysicsNavBake.Capture`. After removing the synthetic `CPU_TIME` and
`UNMANAGED_CODE_TIME` leaves, the last resolved frames had these weights.

| Last resolved frame | Capture interval weight |
| --- | ---: |
| `System.Threading.Monitor.Enter_Slowpath` | 51.37% |
| `PhysicsNavBake.Capture` | 15.66% |
| Bepu `Tree.RayCast` overloads combined | 24.27% |
| `BepuPhysicsWorld.AcquireQueryReadLease` | 3.16% |

These are sampled thread-time weights, not exclusive CPU percentages. Scheduling
delays, stack reconstruction, inlining, JIT and runtime/native frames affect them.
The private physics owner had no intentional concurrent query or mutation work.
The monitor frame alone therefore does not prove competing physics readers or a
query-gate contention defect. No CPU-exclusive hot-method attribution or causal
speedup has been established. The strongest sampled investigation target is query
admission and repeated ray traversal, rather than a demonstrated multi-gigabyte
capture allocation problem.

The code explains the relevant paths without proving their individual costs.

- `KhaozEngine.Physics/PhysicsColumnProbe.cs` re-casts below each hit, advancing
  through convex interiors in small steps. Headroom and lower surfaces depend on
  this traversal. Increasing its epsilon or skipping a whole body can change bytes.
- `KhaozEngine.Movement/PhysicsNavCaptureBasis.cs` acquires a disposable read lease
  when validating each classified surface. The Bepu implementation allocates that
  lease in `BepuPhysicsWorld.QueryLease.cs`.
- `BepuPhysicsWorld.Rebase.cs` origin reads, ray queries and lease disposal enter
  `QueryGate.cs`. Most ray queries do not need rented query scratch.
- `PhysicsNavBake.cs` allocates maximum surface storage, then
  `PhysicsNavColumns.cs` copies the retained arrays. Allocation stacks were not
  separately decoded, so the relative allocation contributions remain unmeasured.

## Alternatives for the orchestrator

Scores are 1 to 10, with higher better. They rank the next action for this measured
window, not general engine architecture. Equal weighting is used.

| Direction | Evidence fit | Compatibility | Simplicity | Scaling value | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| Isolate downstream profile CPU next | 9 | 10 | 9 | 8 | 36 |
| Remove measured capture allocations | 3 | 9 | 8 | 5 | 25 |
| Share one compatible capture across profiles | 2 | 9 | 7 | 7 | 25 |
| Parallelize cells inside capture | 2 | 5 | 3 | 7 | 17 |
| Tiled windows | 1 | 3 | 1 | 10 | 15 |

Allocation removal is relatively contained and can preserve bytes. However, the
whole capture allocates only 4.42 MB here, and there is no allocation-stack cost
breakdown. [#1283](https://github.com/APKiwiOrg/KhaozEngine/issues/1283) measured an
older whole-bake path, not this isolated current capture. Its per-cell number is
not a current capture baseline. One directly visible ownership opportunity would
avoid copying the cell-prefix array, about 134 KB here. That is a bounded candidate,
not evidence of a useful overall speedup.

Capture sharing already exists through repeated `BuildProfile` calls and
`GroundNavigationBake.Create`. It can avoid duplicate captures, but not the profile
proofs. It requires identical bounds, slope, water sampling, classifier semantics
and unchanged provider state. Different profile windows cannot automatically share
one capture. The measured CPU saved per avoided equivalent capture is only about
0.54 seconds in this observation.

Parallel cells could reduce elapsed time on a machine with spare cores. They do
not reduce the serial CPU work measured here. They also require provider and
classifier concurrency contracts, deterministic surface order, bounded per-worker
storage and serial-equivalent failure selection.
[#1401](https://github.com/APKiwiOrg/KhaozEngine/issues/1401) parallelized contact
profile proofs, not this capture. Its comments explicitly separate shared-machine
wall slowdown from CPU work.

Tiling addresses residency, storage budgets and incremental rebakes, with separate
seam and identity obligations. It is valuable at world scale but is a larger
program and does not remove this window's per-cell work.
[#1301](https://github.com/APKiwiOrg/KhaozEngine/issues/1301) already owns that
direction and records its split with world authoring.

Recommendation is to isolate the long downstream stage before choosing a fix for
#1417. No smallest proper production fix is supported by this experiment. In
particular, do not change ray descent semantics, add capture workers or redesign
the physics lease contract on the strength of these samples alone.

## Implementation constraints and parity gate

If a later task establishes a capture hot spot, its exact investigation surfaces
are `PhysicsNavBake.cs`, `PhysicsNavColumns.cs`, `PhysicsNavCaptureBasis.cs`,
`PhysicsColumnProbe.cs`, `BepuPhysicsWorld.QueryLease.cs`,
`BepuPhysicsWorld.Queries.cs` and `QueryGate.cs`. No interface change is selected.

`IPhysicsConcurrentReadSource` is an opt-in promise of the exact receiver. It does
not automatically cover forwarding wrappers or caller delegates. Leases are
thread-affine and cannot nest. Classifiers can be stateful. A capture's current
mutation checks and exception ordering are covered by `NavBakeCaptureBasisTests`.
A long-held lease would change callback-mutation behavior and requires deliberate
contract review, even if successful captures keep the same bytes.

Before changing implementation, pin baseline diagnostic bytes using engine
fixtures for flat mesh and convex ground, stacked decks, separated compound
children, slopes, missing columns, fractional bounds, rebased coordinates, water,
area tags and surface-cap overflow. Compare every height and headroom bit, column
order and area/water entry byte for byte. Also compare complete KENB output for
affected profiles and preserve existing mutation-refusal tests. Preserve the
single recorded input for one separately authorized after comparison.

If any captured or shipped bake byte changes, stop and return the decision to the
orchestrator for owner approval. This research introduces no bake semantic change.

## Reusable evidence

The test-only helper and its smoke live in
`KhaozEngine.Movement.Tests/Research/`. Their README documents the counters,
profiling provider and diagnostic snapshot layout. Release verification passed
one focused smoke, with zero failed or skipped tests. The helper's initial red
run failed with `NotImplementedException`, before any fixture capture occurred.
