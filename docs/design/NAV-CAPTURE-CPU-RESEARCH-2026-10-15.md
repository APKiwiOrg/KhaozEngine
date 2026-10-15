# Ground navigation capture CPU research

Research for [#1417](https://github.com/APKiwiOrg/KhaozEngine/issues/1417).
One representative caller-selected ground window was captured on engine commit
`cbeff834a55f1e982dbaaeea21a154ff49e9ea31`, in Release, on ARM64 with .NET 10.0.13.
The original capture and baseline profile experiments changed neither production behavior nor bake formats.
The implementation and single after observation below add a default-off legacy read scope. They preserve bytes
but do not establish a performance improvement.

## Result and decision boundary

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

The original recommendation was to isolate the long downstream stage before choosing a fix for
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

### Ranked implementation recommendation

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

Recommend a focused trial reducing repeated backend read admission inside one
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

## Stable legacy scope implementation and single after observation

The approved implementation adds `NavBakeExecution.UseStableLegacyReadScope`, default false. It is a caller
assertion that every provider used for a synchronous legacy profile is deterministic, stable and read-only.
`BuildProfile` and `GroundNavigationBake.Create` reuse one thread-affine lease per selected legacy profile.
The exact complete source and selected movement view must declare `IPhysicsQueryLeaseSource` themselves.
The helper authenticates the live lease against the exact owner, view origin and captured geometry without
acquiring another lease inside it. Existing degree and profile argument checks keep their order.

This explicit contract preserves mutable callbacks and existing outer leases on the default path. True refuses
unsupported capability before proofs, refuses physics mutation and incompatible explicit nested leases in
callbacks, and receives the backend's existing non-nesting refusal under a caller lease. Scope cleanup runs on
success and failure. Ordinary contact profiles ignore the property. Legacy work stays serial. No proof, budget,
footprint, origin check, floating-point operation, graph order, capture format or bake identity changed.
The [Movement contract](../../KhaozEngine.Movement/README.md#stable-legacy-read-scope) owns API usage details.

The single authorized after profile restored the retained columns. It did not call `Capture` again. The exact
source, input, tuning, scene, collider, capture-option and snapshot identities were checked before and after.
The adapter, runtime, architecture, trace providers and stage-counter implementation matched the baseline.
Fresh Release assemblies included the new option. The same seven inner stage boundaries were reproduced,
with scope admission included in profile checks and its release included in the outer profile interval.
Temporary instrumentation was removed afterwards and every authored Movement source matched its saved
pre-instrumentation hash. Raw traces and private reproduction data stay outside the public repository.

| Outer stage | Baseline CPU s | After CPU s | Baseline wall s | After wall s | After managed B | After GC 0 / 1 / 2 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| context-rebuild | 1.785892 | 2.134660 | 1.7981888 | 10.9853080 | 173,177,848 | 20 / 9 / 6 |
| snapshot-reconstruction | 0.009516 | 0.013190 | 0.0094449 | 0.0286770 | 2,116,296 | 0 / 0 / 0 |
| restore-validation | 0.022923 | 0.093096 | 0.0243743 | 0.6198890 | 1,167,312 | 0 / 0 / 0 |
| build-profile | 15.199555 | 16.399351 | 15.8554580 | 88.2522843 | 3,186,856 | 0 / 0 / 0 |
| bake-assembly | 0.005084 | 0.006690 | 0.0049077 | 0.0122319 | 23,800 | 0 / 0 / 0 |
| serialization | 0.018377 | 0.039455 | 0.0179106 | 0.2249139 | 2,692,680 | 0 / 0 / 0 |
| load-validation | 0.037254 | 0.061391 | 0.0375760 | 0.2838634 | 6,266,096 | 0 / 0 / 0 |

Current-thread and process allocation counts were equal in every downstream after stage. Setup's current-thread
count was 173,115,800 bytes. BuildProfile allocated 96 bytes more than the baseline and collected no GC.

| After subdivision | Wall s | Process CPU s | Managed B | GC 0 / 1 / 2 |
| --- | ---: | ---: | ---: | --- |
| profile-checks | 0.0058455 | 0.005502 | 1,544 | 0 / 0 / 0 |
| layered-candidates | 3.9592816 | 1.169047 | 2,962,224 | 0 / 0 / 0 |
| node-arrays | 0.0235628 | 0.021091 | 67,264 | 0 / 0 / 0 |
| directed-edges | 84.2401606 | 15.185862 | 67,360 | 0 / 0 / 0 |
| stair-links | 0.0010351 | 0.001222 | 304 | 0 / 0 / 0 |
| graph-finalization | 0.0051887 | 0.005085 | 1,392 | 0 / 0 / 0 |

**The performance gate was not met.** Profile CPU rose from 15.199555 to 16.399351 seconds, an observed 7.9%
increase. Wall time rose from 15.8554580 to 88.2522843 seconds, 5.57 times the baseline. Directed-edge CPU
rose from 14.301217 to 15.185862 seconds. These observations establish no performance win and do not resolve
[#1417](https://github.com/APKiwiOrg/KhaozEngine/issues/1417). The default-off candidate is retained for review,
without a claim that it should be integrated as a performance fix.

The host was under different contention. After-run load averages were approximately 58 / 85 / 80 before and
42 / 71 / 76 afterwards, compared with the baseline profile's 29 / 36 / 61 and 27 / 35 / 60.
Profile process CPU per wall second fell from 0.96 to 0.19. No competing query or mutation thread was deliberately
introduced inside the measured owner. A single profiled cold observation cannot isolate the scope's cost from
host scheduling, runtime, profiler and JIT effects. Native allocation remains uncounted. There was no warm-up,
baseline rerun, second after trial or stress run. Further representative measurement needs separate authorization.

Offline decoding of the same after trace reported 71,901 events, no lost-event callback and one unresolved native
address. Its directed-edge interval contained 11,374 main-thread sample events with zero
`Monitor.Enter_Slowpath` leaves, compared with 2,312 of 3,246 baseline events. The trace also decoded 1,406
allocation ticks, 1,405 with stacks and one without. Removing the sampled monitor leaf did not produce a measured
CPU saving. Sample counts and thread-time weights remain unsuitable as exclusive CPU percentages or savings
estimates, especially across these different wall-time and scheduling conditions.

The complete 588,425-byte KENB was equal to the baseline, SHA-256
`7D07149A3DA1582016A9DE4872B56348AF6A945BD4050453DB6B845BFEA6876A`.
The complete 535,326-byte diagnostic snapshot was equal too, SHA-256
`E6D195C94B5A7F7E2746119DBDF39B837E6AAE1FDB87C8FDF949BDD45662FDB5`.
All 86 retained input and baseline hashes matched. Profile counts stayed at one layer, 29,401 accepted nodes,
232,388 directed edges and no cross-layer links. Canonical load and rewrite remained byte-identical.

The focused Release verification passed 282 tests, including 27 new scope and fixture parity cases. Complete
canonical KENB comparisons cover flat and sloped analytic ground over a complete physics capture, two capsule
sizes, restricted movement views, thin walls and doors, stacked surfaces and Stair links, rebase, missing and padded
columns, area footprints, water, aquatic float routing and dry fallback. Behavioral cases cover capability and live
owner/view/lease identity, captured geometry, callbacks, cleanup, disposed and wrong-thread tokens, preserved caller
leases and ignored contact execution. Existing capture, profile, lifetime, scheduling, format and traversal coverage
also ran. No existing test was weakened or skipped. This is focused verification, not a full engine suite or consumer
adoption proof. No consumer file or pin changed.
