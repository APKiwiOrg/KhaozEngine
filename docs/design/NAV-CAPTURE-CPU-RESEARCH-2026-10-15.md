# Ground navigation capture CPU research

Research for [#1417](https://github.com/APKiwiOrg/KhaozEngine/issues/1417).
One representative caller-selected ground window was captured on engine commit
`cbeff834a55f1e982dbaaeea21a154ff49e9ea31`, in Release, on ARM64 with .NET 10.0.13.
Production behavior and bake formats were not changed.

## Final disposition

End-to-end consumer-window and complete consumer-suite savings remain unmeasured for [#1417](https://github.com/APKiwiOrg/KhaozEngine/issues/1417). The original capture remains the valid isolated capture result at 0.541884 process CPU seconds, 2.3564423 wall seconds and 4,417,632 managed bytes. The separately authorized hosted pair below gives one unprofiled ordered A/B result for the stable-read candidate on a public fixture, but it does not establish statistical significance, a native Mac gain or Grimhollow full-suite savings. The research and the reviewed backend fix below do not change the bake format or public API.

### Experimental stable-read candidate

The local comparison remained inconclusive. The baseline profile used 15.199555 process CPU seconds and 15.855458 wall seconds. The candidate used 16.399351 process CPU seconds and 88.2522843 wall seconds under different conditions. Complete KENB output was equal at 588425 bytes, and the complete snapshot was equal at 535326 bytes. The candidate at [commit 7f51f266](https://github.com/APKiwiOrg/KhaozEngine/commit/7f51f266b91452fd82391df82b3ce21c7e438f98) is functionally reviewed and remains experimental and off main. The local data alone did not demonstrate a whole-navigation CPU win. The later separately authorized hosted ordered pair is recorded below. No further timing run is authorized.

### Offline limits

Offline analysis found 39.58 seconds of suspension-coordination intervals in the after trace. That is not 39.58 seconds of CPU and not disjoint application-paused time, so it cannot be subtracted from process CPU. Sampling safe points and off-CPU inclusion prevent claiming that `Monitor` was the dominant CPU cost. The trace supports research leads only.

### Accepted narrow backend work reduction

The [`CollectPenetration` lazy-batcher fix](https://github.com/APKiwiOrg/KhaozEngine/commit/ffa70bad4ce0e4403ec606a922a76d8a1a5bbbe4) constructs the collision batcher only after the first eligible static. On excluded-only and dynamic-only cold queries, retained native scratch capacity changed from 131,072 B to 0 B. Existing empty-query controls remained at 0, and an eligible query remained positive. This is cold retained native capacity, not per-call cleared bytes, warm rental count or CPU gain. Original pair order, penetration math and `Flush` finally ownership are retained.

The Release build passed with zero warnings or errors. The focused 62 physics and 3 movement tests passed. The full suite passed with 33,109 passed, 0 failed and 1,386 existing opt-in skips across 37 project runs. All five whole-tree guards passed. The normalized nine-cell, 516-byte canonical test deliberately substitutes only the engine identity. Its matching hash proves unchanged normalized header and payload, not cross-version load compatibility:

```text
8dc879897a4cdfed8602306ff7fc9d7c6d316988270a01de71ee5c55a523bc17
```

The lazy-batcher fix preserves bake output for a fixed engine version and introduces no bake format or API change. The existing loader checks the Movement assembly's normalized engine version, so consumers upgrading from an older engine version to 21.1.0 must regenerate bakes to avoid `EngineChanged`, even though the format and payload algorithm are unchanged. Existing 21.1.0 bakes gain no additional invalidation from this same-version backend fix. The staged 21.1.0 version remains unchanged. No new version is claimed.

### Original approved hosted outcome

The approved unprofiled hosted run was [run 38802161651](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38802161651) at `fc9a6a7d2` on GitHub-hosted Ubuntu 26.04 x64. A false completed one profile, then graph validation failed. B never started. The final process exit status was 1 with one failed test and no retries. Whole-child elapsed time was 13.0587502 seconds. It is not profile wall time. Profile metrics, counts and KENB output were not retained, so no performance or parity comparison exists. The later harness repair and hosted run below supersede its unknown predicate, but not its lost metrics.

### Later hosted outcome and harness repair

The separately approved [hosted run 38807384252](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38807384252) at [`24c83b0e4`](https://github.com/APKiwiOrg/KhaozEngine/commit/24c83b0e49f60be6e51fe3ba3b2bd98dc8b4f35a) identified only `CentralObstacleRejected=false`. A ran, B did not run, and the failure exit status was 1. Baseline A retained 7.3916306 wall seconds, 7.391034 process CPU seconds and 7.390059832 calling-thread CPU seconds. There is no A/B comparison from this run. This supersedes the first failed run's unknown predicate, not its lost metrics.

### Final authorized hosted comparison

The separately owner-approved [hosted run 38813435238](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38813435238) at [`1883c7b03`](https://github.com/APKiwiOrg/KhaozEngine/commit/1883c7b031a28728dace1540537c2204e3263818) ran one unprofiled ordered pair on GitHub-hosted Ubuntu 26.04 x64 with `iterations=1`, `load=0`, `Release` and `support-cost=true`. Both children succeeded with finalized process exit status 0. The approval is consumed. No further timing run is authorized.

| Metric | Baseline | Stable read scope | Reduction |
| --- | ---: | ---: | ---: |
| BuildProfile wall seconds | 12.7162112 | 7.7351701 | 39.1708% |
| Process CPU seconds | 12.716042 | 7.735654 | 39.1662% |
| Main-thread CPU seconds | 12.714819959 | 7.734685588 | 39.1680% |

The complete baseline and stable-read KENB files were each 602,366 bytes and equal, with matching recorded SHA-256. All eight acceptance predicates passed. The graph contained 32,582 accepted nodes and 258,106 directed edges. Canonical roundtrip and scope-release checks passed. Recorded binary-set hashes matched, but executable binaries were not retained for independent hashing. Managed allocation increased by 128 bytes, GC counts remained at one per generation and recorded Monitor contention was zero. Setup and capture were outside profile timing. First-use JIT and measurement boundaries remained included.

This is a coherent public-fixture CPU reduction that supports reconsidering the measured opt-in. It is one ordered pair, not evidence of statistical significance, a native Mac gain or Grimhollow full-suite savings. It is not a timing measurement of the lazy-batcher fix.

### Ownership boundary

The measured stable legacy read-scope option is separately owned by the integration work on `feature/nav-stable-read-1417`, based on current `main`. The current `fix/nav-query-cost-1417` branch contains only the lazy penetration-batcher fix and this research documentation. It does not contain the stable-read option or hosted harness. This documentation update is not option integration or consumer adoption.

The [fixture repair](https://github.com/APKiwiOrg/KhaozEngine/commit/8d91a7c575a57920fa2f8ed6977df98c6f9a51a1) proves with a 144-cell correctness test that the center is accepted as a Y=2 roof. Its replacement four-sided check tests adjacent ground obstruction using real accepted sources, permits the roof and keeps the other thresholds. Independent review passed, and parent evidence reports 26 small and pure tests passed. The repaired harness remains on the proof branch, not main. The later approved hosted run above exercised this corrected acceptance.

### Issue status

Issue #1417 remains open pending stable-read option integration and consumer adoption. The hosted pair establishes a measured public-fixture result, while whole-navigation, native Mac and Grimhollow full-suite savings remain unmeasured. The collision-batcher lead has a bounded native work reduction with no timing claim. Source-hold reuse and point-footprint observations remain unproven leads. This branch carries the research record plus the bounded backend fix. No release.

## Historical capture result and decision boundary

This section records the original capture-only result. Its recommendation to isolate downstream profile CPU was historical and is superseded by the E2 disposition above.

The 41 by 51 metre window, at 0.25 metre cells, contained 164 by 204 columns
(33,456 cells). Its single cold capture used 0.541884 process CPU seconds and
2.3564423 wall seconds, with 4,417,632 managed bytes allocated. That is about
132 bytes per cell. Capture used about 0.23 CPU seconds per wall second.

This observation does not reproduce minutes spent inside `PhysicsNavBake.Capture`.
It also does not explain the complete slow tests. They include setup, profile
derivation and other work. No downstream profile or region graph was built in the original
capture experiment. The continuation below measures those stages separately. Do not select a production capture
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

## Historical alternatives for the orchestrator

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

Historical recommendation. Isolate the long downstream stage before choosing a fix for
#1417. No production fix was supported by the capture-only experiment. In
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

## Continuation using the saved columns

A second bounded investigation restored the original PCAP1 columns and derived
exactly one representative profile. It ran the actual caller tuning, area filter,
ground and normal delegates, bounds clamp, medium and selected movement query
view, serially. It did not call Capture again for this input or run the complete
consumer test. No optimization, production API or bake format was changed.

Before admitting this as the same input, the private adapter verified every
preserved input and source digest, collider hash and count, option field and float
bit, dimensions and origin bits. A newly captured query basis described the
rebuilt static owner. Diagnostic serialization of the reconstructed columns was
byte-identical to the original, including every height, headroom, area and water
entry. The rebuilt classifier matched every frozen surface area. All 79 checked
identities were unchanged after the run. Raw identities and bytes stay private.

The test-only `NavCaptureSnapshot` utility reconstructs columns and invokes the
private builder constructor through reflection. It adds no shipped restore API.
Two new fixture tests check exact diagnostic bits without callbacks and complete
KENB equality against a fresh tiny engine fixture. Together with the affected
capture smoke, Release verification passed 3 tests with no failures or skips.
The initial restore red test failed at its unimplemented restoration entry point.

### Measured downstream stages

The measurement used the same production baseline as the original capture, with
local stage instrumentation inserted without altering algorithm decisions. That
instrumentation was archived privately and restored before committing research.
The installed tracer launched the built Release process suspended and collected
`dotnet-sampled-thread-time,gc-verbose`, plus outer and inner stage markers. This
was one cold application observation with no warm-up, repeated trial or full suite.

| Stage | Wall seconds | Process CPU seconds | Managed bytes | GC 0 / 1 / 2 |
| --- | ---: | ---: | ---: | --- |
| Context rebuild and setup | 1.7981888 | 1.785892 | 173,184,920 | 21 / 10 / 7 |
| Snapshot reconstruction | 0.0094449 | 0.009516 | 2,116,296 | 0 / 0 / 0 |
| Restoration validation | 0.0243743 | 0.022923 | 1,179,592 | 0 / 0 / 0 |
| BuildProfile, including graph construction | 15.8554580 | 15.199555 | 3,186,760 | 0 / 0 / 0 |
| Bake identity and assembly | 0.0049077 | 0.005084 | 23,800 | 0 / 0 / 0 |
| Canonical serialization | 0.0179106 | 0.018377 | 2,692,688 | 0 / 0 / 0 |
| Load and canonical rewrite validation | 0.0375760 | 0.037254 | 6,266,104 | 0 / 0 / 0 |

Current-thread allocation equaled process allocation in every downstream stage.
Setup's current-thread count was 173,170,920 bytes. The original capture and this
profile ran in separate processes under different contention, so their wall
numbers are not a before/after comparison. Profile CPU was approximately 28 times
the original isolated capture CPU. This establishes a downstream cost for this
window, without reproducing every minute of the consumer's full-test duration.

`BuildProfile` already builds the traversal graph. Its internal subdivisions were
measured separately rather than attributing all its work to columns or graph
allocation.

| BuildProfile subdivision | Wall seconds | Process CPU seconds | Managed bytes |
| --- | ---: | ---: | ---: |
| Argument and profile checks | 0.0017823 | 0.001948 | 1,424 |
| Candidate holds and layered topology | 0.6351675 | 0.873681 | 2,962,232 |
| Node arrays | 0.0129846 | 0.013429 | 67,272 |
| Directed neighbor proofs | 15.1933942 | 14.301217 | 67,368 |
| Stair links | 0.0004122 | 0.000584 | 312 |
| Graph and planner finalization | 0.0043347 | 0.004468 | 1,400 |

Directed neighbor proofs used 94.1% of measured profile CPU and 95.8% of its wall
time. Every subdivision had zero GC collections. The stage counters are nested
inside the outer measurement. Marker, counter and JSON work between subdivisions
is included in the outer measurement, so the rows do not sum exactly to it.

The result had one layer, 29,401 accepted nodes, 232,388 accepted directed edges
and no cross-layer links. The full shipped-format baseline occupied 588,425 bytes.
It loaded against its expected identity and rewrote byte for byte. The private
adapter reused the single already-built profile to assemble this bake through
reflection, reproducing Create's identity and candidate-link checks. Calling
`GroundNavigationBake.Create` here would have derived another profile.

### Attribution and limits

The exact trace markers cover both the outer profile and its subdivisions. Trace
conversion reported no lost events. The directed-edge interval contained 3,246
main-thread sample events, of which 2,312 had `Monitor.Enter_Slowpath` as the leaf.
The containing paths repeatedly pass through `GroundTraversalProbe.TryEdge`,
`GroundMoveContext.EnsureOrigin`, `BepuPhysicsWorld.EnterOperation` and movement
steps. Origin reads themselves enter the backend query gate. Each un-nested query
admits and releases an interval through the monitor, even on one worker.

Speedscope reconstruction covered 15,856.556 milliseconds containing BuildProfile.
After removing its synthetic timing leaves, 71.04% of that interval weight ended
at `Monitor.Enter_Slowpath`. Another 16.46% ended at an unresolved native address,
4.67% at zero-memory work and 4.47% at runtime GC polling. The conversion reported
two unresolved addresses. These are sampled thread-time weights, not exclusive
CPU percentages or a measured savings estimate. Inlining and native-stack
resolution make the apparent parent and leaf attribution incomplete.

The decoder also read 1,406 allocation ticks with associated stacks. The profile's
sampled allocations were concentrated in layered-topology arrays. The directed
edge stage allocated only 67 KB by precise counters and had no allocation ticks
assigned inside its exact marker interval. Managed allocation and GC are not the
large observed edge-proof cost. Allocation ticks are sampled byte estimates and
do not replace the precise counter totals.

Machine load averages were approximately 29 / 36 / 61 before this run and
27 / 35 / 60 afterwards, with other heavy lanes active. BuildProfile used 0.96
process CPU seconds per wall second, rather than the original capture's 0.23.
There was no idle-machine claim, affinity adjustment or unprofiled comparison.
CPU includes runtime and profiler activity. Profiling, cold JIT and nested
instrumentation overhead remain unquantified. Native allocation is not counted.

This was a single serial reader over a privately rebuilt owner, with no
intentional competing query or mutation thread. The selected providers do not
establish lock wait or kernel scheduling time. Monitor samples therefore nominate
query admission work, but do not prove contention or a broken gate. The closed
[#1409](https://github.com/APKiwiOrg/KhaozEngine/issues/1409) investigation found
preemption rather than contention in a different parallel contact workload. It
must not be reinterpreted as an unresolved contention defect from this trace.

### Historical ranked implementation recommendation

Scores are 1 to 10, higher better, with equal weighting. They rank a next
implementation trial for this measured serial legacy profile.

| Direction | Evidence fit | Compatibility | Simplicity | Scaling value | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| Reuse query admission within a legacy traversal proof | 9 | 7 | 7 | 8 | 31 |
| Reuse already certified node holds across outgoing edges | 7 | 6 | 7 | 8 | 28 |
| Share compatible captures between profiles | 3 | 9 | 7 | 7 | 26 |
| Remove capture allocations | 2 | 9 | 8 | 4 | 23 |
| Parallelize capture cells | 1 | 5 | 3 | 7 | 16 |
| Tiled windows | 2 | 3 | 1 | 10 | 16 |

The historical recommendation was a focused trial reducing repeated backend read admission inside one
serial legacy `GroundTraversalProbe.TryEdge`, with its movement steps and origin
checks sharing an authenticated stable read interval. Keep every directed proof,
step budget, footprint check, movement operation and floating-point decision.
The target is the measured 14.30 CPU-second edge stage. For scale, a 10% reduction
of that stage would save about 1.43 CPU seconds, or 9.4% of the whole profile.
That is arithmetic on the baseline, not a predicted or measured improvement.

The exact implementation surfaces are `GroundTraversalProbe.cs`,
`PhysicsNavBake.Profiles.cs`, `GroundMoveContext.cs`,
`GroundMoveContext.Coordinates.cs`, `BepuPhysicsWorld.QueryLease.cs`,
`BepuPhysicsWorld.Rebase.cs` and `QueryGate.cs`. Start from the existing
`IPhysicsQueryLeaseSource` and query-gate nesting contracts. Do not expose raw
physics or remove origin checks. No interface or production implementation has
been selected in this research.

The advantage is that admission reuse addresses the actual expensive stage and
retains the same proof algorithm. Its cost is a real contract review. Automatically
acquiring a lease can break an existing outer lease, forbid mutation from a
callback earlier than today, allocate per edge and keep scratch alive longer.
A build-wide lease would widen those effects further. Preserve existing callers
through an explicit stable-provider contract or a query-scope implementation that
honors their current nesting and failure behavior. A mutable or non-capable owner
must retain its current path. Authenticate the exact complete owner behind the
movement view, preserve thread affinity and never share a live context between
workers. A blind outer lease is not an approved implementation.

Hold reuse is a secondary direction. Every edge begins by settling the same source
node again, but caching that result requires stable providers and bit-identical
state, failure and call-order semantics. The trace did not measure the number or
cost of those redundant holds, so its benefit is less established. Increasing
step size, changing arrival tolerance or skipping reverse edges changes semantics
and is outside this recommendation.

Capture sharing only avoids the original 0.54 CPU-second capture per equivalent
window. Allocation removal cannot eliminate the measured allocation-free edge
work. Capture workers target the wrong stage here, while contact proof workers
from #1401 do not accelerate this legacy profile. Tiling remains a separate
world-scale program. None is supported as the first fix for this observation.

Before accepting an implementation, compare complete engine-fixture KENB bytes
for flat and sloped ground, wide and narrow capsules, thin walls and doorways,
stacked surfaces and Stair links, rebased origins, missing and padded columns,
area filters, water and aquatic fallback. Preserve origin and geometry mutation
refusals, disposed and wrong-thread leases, existing outer leases, callback
failures and serial exception order. Fixtures without lease capability must keep
their old behavior. The existing snapshot correctness test proves restoration,
not the safety or speed of a future admission optimization.

Keep the original capture, restored bytes, exact options and complete profile
baseline for one separately authorized after comparison. No such comparison or
production change ran here. Any changed captured or shipped byte returns to the
orchestrator for owner approval before adoption.
