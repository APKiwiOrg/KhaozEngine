# Parallel contact proof slowdown record

## Scope and question

Investigation of [#1409](https://github.com/APKiwiOrg/KhaozEngine/issues/1409) at `376971dda` (staged 20.40.0) on arm64
macOS 27.0.1, SDK 10.0.401, .NET 10.0.12, 12 cores (8 performance and 4 efficiency). The
[parallel contact bake cost record](2026-10-14-parallel-contact-bake-cost.md) found that an edge proof took 2.3 times
(warm) to 6.4 times (cold) as long on 12 workers as serially, so degree 12 gave only 1.67 times (cold) to 4.59 times
(warm) speedup. That record measured wall time only and could not separate code contention, garbage collection, cold
JIT, efficiency cores and machine load. This record separates them.

No game repository or Hollowmere assets were opened. This is local diagnosis, not the hosted cost gate.

## Static read of the per-query paths

Every shared write or lock a worker takes per query or per proof, with counts from the measured fixture. A pass makes
576 `Admit` calls and 3,348 edge proofs and acquires 36,488 movement leases, about 10.4 per edge proof and 2.6 per
admission.

| Site | Kind | Shared across workers | Per pass |
| --- | --- | --- | ---: |
| `QueryGate.EnterRead` and `ExitRead` (outer interval of each lease) | one monitor each on `_sync`, writes `_activeReaders` | yes | about 73,000 |
| `QueryScratchPool.Rent` and `Return` (scratch attach at first use in a lease) | one monitor each on `_sync`, LIFO stack push and pop | yes | up to about 73,000 |
| Nested reads inside a lease (`EnterQuery`, `StaticGeometryGeneration`, pin `AssertCurrent`) | thread-local `Holder` counter only | no | many |
| `MovementEnvironmentContext.TryAcquire` | `Interlocked.CompareExchange` on `_inUse` | no, one context per worker | 36,488 |
| `OrdinaryMovementEnvironment` `CreateScope` and `TryPinPrepared` | monitor on `_cacheGate`, three or four entries per lease | no, one environment per worker binding | about 110,000 to 146,000 |
| `InstalledSupportGeometryCache.GetOrCapture` on a hit | `ConcurrentDictionary.TryGetValue` and `Volatile.Read` | read only | per support read |
| `InstalledSupportGeometryCache` on a miss | `GetOrAdd`, slot lock, `Interlocked` counters | yes, once per key | a handful per process |
| `SupportQueryDiagnostics`, `ContactAllocationDiagnostics`, `WorkerDiagnostics` | `[ThreadStatic]` state, merged after the join | no | none shared |
| `ContactNavigationSchedule.TryClaim` | `Interlocked.Increment` on one counter | yes | one per band |
| `ContactNavigationCutoff.Admits` | `Volatile.Read` | read only | one per item |

The only shared monitors on the hot path are the gate and the scratch pool, up to four entries per lease and about 42
per edge proof. They guard a few instructions each, against roughly 0.6 ms of CPU per lease. The scratch pool is a
LIFO stack, so a scratch can move between cores from one lease to the next, which makes it a false-sharing candidate.
Managed allocation is about 3.5 KB per proof serially and 4.5 KB at degree 12. Nothing in the read predicts a 2 to 6
times per-proof slowdown, so the runs below test it.

## Method

The archived Task 4 harness was reapplied as a temporary local probe, then removed. Production files were restored
from git byte for byte before this record was written. The probe kept the cost record's proof, phase and band timers
and added:

- `Monitor.LockContentionCount` deltas over each pass
- `GC.GetTotalPauseDuration()` and `GC.CollectionCount(0/1/2)` deltas
- managed allocation per pass (`GC.GetTotalAllocatedBytes(precise: true)`)
- `Process.TotalProcessorTime` against pass wall time
- per-worker thread CPU time (`clock_gettime(CLOCK_THREAD_CPUTIME_ID)`), per-worker proof count, proof time and lease
  count, and the caller thread's CPU time and leases for the serial path
- the system load average before and after each pass
- in run 5 only, process voluntary and involuntary context switches from `getrusage`

Each run captured the cost record's fixture once (576 columns, 48 rows, low-lip bank, radius 0.3 m, DryOnly, 160-step
edge budget) and built it twice, cold then warm, in its own test process through
`build-slot --heavy -- dotnet test KhaozEngine.Movement.Tests ... --filter Category=ParallelContactBakeCost`. The five
runs ran once each, in sequence, with no repeats and no loops. Every pass of every run produced the cost record's
graph digest `723d8200e8b8fc6e3b15ffcfeb04c3586a48ed6d6e85938a9eeb8e7723ede694`. Every run exited 0.

The machine was shared. Build-slot was boosted to 4 heavy slots, and other heavy jobs, including a test flake hunt,
held slots during runs 1 to 4. None held one during run 5, but work outside build-slot kept the load average
near 60. The load average was 43 to 115 throughout.

## Runs

Edge columns cover the edge phase only. "Edge CPU per proof" is worker thread CPU time divided by edge proofs. For
degree 1 it is the caller thread's CPU over the pass, scaled by the edge section's share of wall time. "CPU share" is
CPU time over wall time per running proof thread. "Cores" is process CPU time over pass wall time.

| Run | Pass | Degree | Wall ms | Cores | Load | Edge wall ms per proof | Edge CPU ms per proof | CPU share | Lock contention | GC pause ms | Gen 0/1/2 | Allocated B |
| --- | --- | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| 1 | cold | 1 | 75,564.2 | 0.35 | 115 to 91 | 18.36 | 5.74 | 0.313 | 0 | 9.2 | 2/0/0 | 14,884,232 |
| 1 | warm | 1 | 42,576.6 | 0.47 | 91 to 66 | 12.45 | 5.79 | 0.465 | 0 | 10.2 | 2/1/1 | 13,638,016 |
| 2 | cold | 12 | 13,740.8 | 2.13 | 58 to 59 | 40.01 | 7.53 | 0.175 | 39 | 15.3 | 4/1/1 | 32,466,936 |
| 2 | warm | 12 | 10,313.0 | 2.17 | 59 to 61 | 34.34 | 6.42 | 0.177 | 19 | 30.9 | 2/1/0 | 17,759,176 |
| 3 | cold | 8 | 9,574.3 | 2.71 | 48 to 44 | 18.67 | 6.43 | 0.318 | 7 | 8.3 | 4/1/1 | 26,571,352 |
| 3 | warm | 8 | 6,951.2 | 3.09 | 44 to 43 | 14.62 | 6.19 | 0.383 | 17 | 3.7 | 2/1/0 | 16,391,312 |
| 4, server GC | cold | 12 | 14,697.7 | 1.90 | 59 to 74 | 43.02 | 7.12 | 0.157 | 56 | 0.0 | 0/0/0 | 32,384,144 |
| 4, server GC | warm | 12 | 10,006.5 | 2.14 | 74 to 78 | 33.37 | 6.08 | 0.175 | 28 | 0.0 | 0/0/0 | 17,816,840 |
| 5 | cold | 12 | 5,292.5 | 5.54 | 66 to 63 | 16.00 | 7.26 | 0.421 | 48 | 7.7 | 4/1/1 | 32,488,952 |
| 5 | warm | 12 | 4,494.6 | 5.19 | 63 to 60 | 14.59 | 6.66 | 0.430 | 15 | 8.7 | 2/1/0 | 17,747,512 |

Run 5 context switches: 25 voluntary and 125,688 involuntary cold, 24 voluntary and 51,277 involuntary warm.

Run 4 confirmed `GCSettings.IsServerGC` true inside the test host. The edge phase kept workers 90 to 96 percent busy in
every run.

Per worker in the run 2 warm edge phase, all 12 workers ran at a CPU share of 0.17 to 0.19 and spent 6.30 to 6.58 ms of
CPU per proof. In the run 3 warm edge phase all 8 ran at 0.41 to 0.43 and spent 5.99 to 6.46 ms.

## Root cause

The per-proof slowdown is CPU starvation from the shared machine, not code. Workers are runnable but preempted. Each
proof thread gets only the CPU share the macOS scheduler grants it, and at a load average near 60 on 12 cores that
share falls to about 0.18 per thread.

The numbers that show it:

- Wall time per proof factors into CPU per proof times the inverse of CPU share. Run 2 warm: CPU per edge proof rose
  1.11 times over serial (6.42 against 5.79 ms) while the share fell 2.63 times (0.177 against 0.465). The product,
  2.91, matches the measured 2.76 times wall slowdown per proof (34.34 against 12.45 ms).
- When a parallel run got a share close to the serial run's, the slowdown nearly vanished. Run 5 warm workers ran at
  0.430 against 0.465 serially, and per-proof wall time was 1.17 times serial (14.59 against 12.45 ms).
- Workers do not block. Run 5 recorded 24 or 25 voluntary context switches per pass, which is the joins plus a few
  lock waits, against 51,277 to 125,688 involuntary ones.
- Locks are not contended. Monitor contention was 7 to 56 events per pass against up to about 146,000 shared monitor
  entries, at most 0.04 percent.
- GC is not a factor. Pauses were 0 to 31 ms per pass, at most 0.3 percent of wall. Server GC removed every collection
  and left warm wall time and CPU per proof unchanged within noise (10,006.5 against 10,313.0 ms, 6.08 against 6.42
  ms).
- Cold JIT is a fixed cost per process, not per thread. Process CPU over a cold pass exceeded the warm pass by 6.7 s
  serially and 6.0 to 7.0 s at degree 12. It accounts for the cold rows' higher CPU per proof, 7.1 to 7.5 ms against
  6.1 to 6.7 ms warm.

The residual CPU-per-proof inflation at degree 8 and 12 is 1.05 to 1.15 times serial when warm. That fits time on the
efficiency cores, where a thread spends more CPU nanoseconds per instruction, plus the all-core frequency drop and
shared cache pressure. An in-process probe cannot split those further, and at 15 percent they are not the issue.

The cost record's own sample fits the same mechanism. Its degree 1 warm pass ran 62 percent slower than its cold pass
on identical work, which only a change in available CPU explains. That record captured no CPU time, so its exact
shares are unknown.

## Fix

None. The cause is outside the engine, so no gate, scratch, cache, diagnostics, worker, GC or thread setting change
is warranted. Removing the gate and pool monitors could save at most their uncontended cost, a few microseconds per
lease against about 0.6 ms of CPU, under 1 percent. Server GC changed nothing measurable. The bake's correctness
contracts stay as they are, with the seven pinned baseline hashes unchanged and no production file modified.

The open lever is the measurement, not the code. Wall-clock speedup on this shared Mac measures how much CPU the
scheduler lent the bake, so the 1.67 and 4.59 figures are not the bake's parallel efficiency.

## Speedup in CPU terms

Wall speedups against run 1 vary with each run's load and are not comparable. Degree 12 against degree 1 warm gave
4.13 times in run 2 and 9.47 times in run 5. Cold run 5 gave 14.3 times, beyond the core count, because run 1's
serial thread held only a 0.31 share. These are load artifacts.

CPU-normalized, degree 12 warm costs 50.9 to 55.5 ms of CPU per admitted surface (admission plus eight edge proofs)
against 47.2 ms serially, an efficiency of 0.85 to 0.93. With 94 to 96 percent worker utilization, a 12-core machine
owned by the bake projects to about 9.6 to 10.7 times serial if every thread gets a full performance core. With the 4
efficiency cores at about half a performance core, about 8 to 9 times. These are projections from CPU time, not a
measured idle-machine speedup. A bake-only hosted run is still the authority.

## Updated Hollowmere extrapolation

Same assumptions as the cost record: one contact profile, about one million columns with one or two raw surfaces
each, every surface admitting with eight directed edges, no Stair links, and no capture, serialization, extraction
growth or additional profiles. Rate per admitted surface is the non-edge pass time per surface reaching its Hold plus
eight times the edge time per edge proof.

| Source | ms per surface | One million surfaces | Two million surfaces |
| --- | ---: | ---: | ---: |
| Serial, run 1 warm, CPU time on one owned thread | 47.2 | 13.1 hours | 26.2 hours |
| Degree 12 warm, measured wall on this shared Mac (run 5 to run 2) | 10.7 to 24.6 | 3.0 to 6.8 hours | 6.0 to 13.7 hours |
| Degree 8 warm, measured wall on this shared Mac (run 3) | 16.6 | 4.6 hours | 9.2 hours |
| Degree 12 warm, projected on a 12-core Mac owned by the bake, all full cores | 4.4 to 4.9 | 1.2 to 1.4 hours | 2.5 to 2.7 hours |
| Same, with the 4 efficiency cores at half a performance core | 5.3 to 5.9 | 1.5 to 1.6 hours | 3.0 to 3.3 hours |
| Cost record, degree 12 warm and cold, shared Mac | 46.0 to 77.5 | 12.8 to 21.5 hours | 25.6 to 43.0 hours |

A contact profile for Hollowmere is a one to three hour bake on a 12-core Mac the bake has to itself, and several
hours to half a day when the Mac is shared with other heavy work. Bake it with the machine otherwise idle or on a
dedicated host. Proof reuse or cheaper proofs (option B of #1401) remain the levers beyond core count.
