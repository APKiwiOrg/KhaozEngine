# KhaozEngine.Benchmarks - server-tick benchmark (jobs-0/1/2 + the jobs-3 gate)

A headless, repeatable benchmark of **one server tick** across a matrix of (cells `C`, entities/cell `E`,
systems `S`), of the parallel-job-system program.
jobs-0 established the **single-threaded baseline** every later layer must move; jobs-1 added parallel cell ticks (the
**cell axis**), so each regime is run **inline and with cells fanned across cores**; jobs-2 added the **entities axis**,
shown in a dedicated section where one hot cell's system fans its entity rows across cores via `World.ParallelForEach`.
The `--gate` run is jobs-3's **decision tool**: it measured whether a system scheduler (the *systems axis*) was worth
building, and the answer was no (see "jobs-3 gate" below). The program is complete at layer 2.
Without the benchmark we'd optimize blind, so it landed first.

Not a shipped package (`IsPackable=false`) and not on the engine version line. The timing loop only runs via
`dotnet run`; CI's `dotnet test` never invokes it, so it stays out of CI's timed path. The harness's deterministic
and structural behaviour *is* covered by `KhaozEngine.Server.Tests/Benchmarks/ServerTickBenchmarkTests.cs`, which run in CI.

## What it measures

The real owned-tick path: `ShardHost.Tick` -> every `CellSim.Tick` -> `FixedTickHost.Advance` -> `World.Update`,
running `S` trivial per-row-pure systems (`IntegratePositionSystem`: `pos += vel * dt`) over `E` entities in each of
`C` cells. One server tick is `O(S·N)` work, `N = C·E`. Ghosting / handoff / snapshotting are never invoked - this
is the pure owned simulation cost.

Population is seeded (`DeterministicRng`), built in a fixed order, so re-running a config is bit-identical and its
timings are stable. Warmup ticks are excluded; per-tick wall-clock divides elapsed time by the ticks **actually**
produced, so float accumulator drift can't skew it.

## Run it

```bash
# full matrix (N = 65,536 per regime; a few seconds in Release)
dotnet run --project KhaozEngine.Benchmarks -c Release

# fast smoke (smaller N, fewer ticks; sub-second)
dotnet run --project KhaozEngine.Benchmarks -c Release -- --quick

# jobs-3 gate: should we build a system scheduler? (runs alone, prints a verdict)
dotnet run --project KhaozEngine.Benchmarks -c Release -- --gate

# replication-hotpath jobs-1 matrix only (runs alone, no cell/entities/ownership-lookup sections)
dotnet run --project KhaozEngine.Benchmarks -c Release -- --replication
```

Always run in `-c Release`. Debug numbers are not representative.

## Read it

Each regime is run twice - inline (`SingleThreadedJobScheduler`) and across cores (`ThreadPoolJobScheduler`):

```
regime                   C         E           N   S   inline ms      par ms  speedup   par entities/sec
--------------------------------------------------------------------------------------------------------
many small cells      1024        64       65536   4       2.970       0.284   10.46x        230,866,242
one hot cell             1     65536       65536   4       0.442       0.446    0.99x        147,031,538
mid (8x8 cells)         64      1024       65536   4       0.486       0.119    4.07x        548,808,776
```

(Representative 12-core run; absolute numbers vary by machine.)

- **inline ms** - mean wall-clock for one single-threaded server tick. The jobs-0 baseline.
- **par ms** - same tick with cells fanned across cores (`ShardHost.Scheduler = new ThreadPoolJobScheduler()`).
- **speedup** - `inline ms / par ms`. Scales ~linearly with cores **up to the cell count**.
- **par entities/sec** - parallel throughput (`N` per tick / per-tick seconds).

The three regimes are the shapes the program's big-O table distinguishes, held at **equal N**. **Many small cells**
(C >> cores) is the dominant MMO shape and the one parallel cell ticks speeds up most (near-linear in cores).
**One hot cell** (C=1) can't be split by the cell axis at all (~1x) - that is the **entities axis** (jobs-2), in its
own section below. **Mid** lands in between.

### Entities axis (jobs-2)

After the cell-axis table the benchmark prints a second section (`EntitiesAxisBenchmark`): one hot `World` of `E`
entities - the single-cell case the cell axis **can't** split - with its dominant pass run single-threaded
(`World.ForEach`) versus fanned across cores (`World.ParallelForEach`). It **sweeps the per-row work** because that is
the whole story: a fork/join has a fixed cost, so over trivial per-row work the dispatch overhead dominates and the
parallel pass loses; as the per-row compute grows toward what a real hot system does, it amortizes the fork/join and
scales toward ~P×.

```
entities axis - one hot World (E=65536), ForEach vs ParallelForEach, sweeping per-row work (jobs-2)
    work/row    ForEach ms        Par ms    speedup   par entities/sec
-----------------------------------------------------------------------
           1         0.170         0.524      0.32x        125,057,167
           8         0.346         0.542      0.64x        120,804,429
          32         1.694         0.326      5.20x        201,269,399
         128        12.502         1.868      6.69x         35,090,525
         512        64.505         8.661      7.45x          7,566,443
```

(Representative 12-core run; absolute numbers vary by machine. The **speedup curve** is the result.)

- **work/row** - times the integrate inner-loop is repeated per entity, a proxy for how heavy the hot system is.
- **ForEach ms** / **Par ms** - mean wall-clock of one pass over all `E` entities, single-threaded vs across cores.
- **speedup** - `ForEach ms / Par ms`. Below ~1x for trivial work (fork/join-bound), crossing 1x and climbing toward
  ~P× as per-row work grows. This is the ceiling parallel cell ticks (`~1x` for one cell) cannot reach: the entities
  axis splits a single hot system's rows across cores, so even a degenerate single-cell load uses every core - **once
  the per-row work clears the fork/join floor**. That caveat is why the benchmark prints the whole curve, not one row.

### Ownership-lookup axis (gap 6)

After the entities axis the benchmark prints a third section (`OwnerLookupBenchmark`): `ShardHost.TryGetOwner` -
the per-player / per-NPC-per-tick owner lookup - timed as the O(1) `netId -> (cell, entity)` index it uses today
against a naive linear owner-scan over the same host (the pre-index behaviour: scan every cell and
`World.ForEach` for the netId). It sweeps the total entity count with the cell count held fixed, so it shows the
index cost stay flat while the naive scan grows linearly - the quadratic-population wall the index removed.

```
ownership-lookup axis - ShardHost.TryGetOwner: O(1) index vs pre-index linear scan, sweeping total entities (gap 6)
N (entities) index ns/lookup  scan ns/lookup   scan/index
----------------------------------------------------------
        4096           130.5         18026.6         138x
       16384           117.1         13293.1         113x
       65536            27.1         42502.0        1570x
      262144            25.2        147964.4        5865x
```

(Representative 12-core run. Absolute numbers vary by machine.)

- **N (entities)** - total owned entities across the grid for that row.
- **index ns/lookup** - mean nanoseconds per `TryGetOwner` call (the O(1) index path).
- **scan ns/lookup** - mean nanoseconds per naive linear owner-scan at the same `N` (sampled at a stride, not every
  netId, to keep the O(N^2) naive path tractable to time, the reported per-lookup cost is unaffected).
- **scan/index** - `scan ns/lookup / index ns/lookup`, how many times slower the naive scan is at that `N`.

**index ns/lookup stays ~constant as `N` grows** (a dictionary hit). **scan ns/lookup grows ~linearly with `N`**.
The per-tick cost is that lookup cost times (players + NPCs), so the index turns an `O(population x entities)`
quadratic into `O(population)`.

### Replication axis (replication-hotpath jobs-1)

After the ownership-lookup axis the benchmark unconditionally runs a fourth section (`ReplicationTickBenchmark`,
matrix from `ReplicationBenchmarkMatrix`): the real `AoiDeltaReplicator` hot path against a populated
`ReplicationRegistry` - `NetId` entities carrying a few replicated components, `C` simulated clients each with an
area-of-interest, movement-heavy steady state (every entity moves every tick, each client acks the previous
tick's snapshot one tick later). The interest grid is rebuilt once per tick and shared across clients, matching
`ShardHost.HomeInterest`'s per-serve-pass cadence inside `ShardedWorldServer`, and the world is captured once per
tick into one consolidated buffer with `(offset, length)` segments (no per-component `byte[]`), so the win being
measured is the shared once-per-tick scan and capture, not a cheaper per-client walk - each client's own
`WriteFor` projection still walks the whole shared capture, filtering by its own interest set.

```
regime                  C       E  comp  per-tick ms    alloc B/tick   gen0/Kt   gen1/Kt   gen2/Kt     wire B/tick
------------------------------------------------------------------------------------------------------------------
C=8  E=4096  comp=1     8    4096     1        5.884       2,427,875     300.0     166.7      66.7          84,598
C=8  E=4096  comp=4     8    4096     4        7.138       2,866,008     333.3     166.7      66.7          83,937
C=8  E=16384 comp=1     8   16384     1       11.211       8,775,216    1100.0     500.0     166.7         342,206
C=64 E=4096  comp=1    64    4096     1        7.095       5,780,610     700.0     266.7     133.3         668,503
C=64 E=16384 comp=1    64   16384     1       34.641      22,161,168    3200.0    1533.3     666.7       2,708,327
C=64 E=16384 comp=4    64   16384     4       43.965      23,813,686    3400.0    1666.7     700.0       2,668,843
```

(Representative 12-core run. Absolute numbers vary by machine. `--replication` runs this matrix alone.)

- **C** / **E** / **comp** - client count, entity count, and replicated components per entity for that row.
- **per-tick ms** - movement + the one shared (interest-grid rebuild + world capture) + every client's own
  (Query + `WriteFor`), mean over the timed ticks.
- **alloc B/tick** - bytes allocated on the benchmark thread per tick (`GC.GetAllocatedBytesForCurrentThread` delta).
- **gen0/1/2 per Kt** - GC collections per 1000 ticks, by generation.
- **wire B/tick** - total bytes `WriteFor` returned, summed across all clients for that tick.

This measures the shared per-tick path only (one interest-grid rebuild plus one world capture per tick, however
many clients read from it), not a from-scratch per-client capture.

### jobs-3 gate (`--gate`): is a system scheduler worth building?

`--gate` (`SystemAxisGate.cs`) is the decision tool for the program's conditional layer 3. It models **one cell with
several distinct systems** - a 4-system Position/Velocity conflict cluster + 3 independent bookkeeping systems, each
declaring a jobs-2 `AccessSet` - and compares the per-cell tick three ways, swept over entity count:

```
 entities    T_seq ms     T_l2 ms      l2 x   T_l3ceil ms      l3 x       verdict
---------------------------------------------------------------------------------
      256       0.414       0.200     2.07x         0.187     2.22x       L3 WINS
     1024       1.611       0.500     3.22x         0.733     2.20x       L2 wins
     4096       3.052       1.724     1.77x         1.679     1.82x       L2/L3 ~tie (noise)
    16384       6.514       2.843     2.29x         3.445     1.89x       L2 wins
    65536      20.250       4.089     4.95x         9.871     2.05x       L2 wins
   262144      80.748      12.624     6.40x        38.961     2.07x       L2 wins
```

- **T_seq** - all systems single-threaded, sequential (today's per-cell tick).
- **T_l2** - each system via `ParallelForEach`, sequential between systems (jobs-2, **shipped**).
- **T_l3ceil** - the most optimistic overlap of the *single-threaded* systems: a list-schedule of each system's measured
  solo cost honouring the `AccessSet` conflict graph on all cores. The best a system scheduler could do alone.

**Verdict: gate not met, layer 3 de-scoped.** Layer 3's overlap is capped at **~2.1×** (the conflict-graph width) and
does not grow with `E`; layer 2 parallelizes *within* a system and scales to **~5-6×**, so for any cell heavy enough to
matter (≥16k) layer 2 already beats the best-possible layer 3 by 2.4-3×. They share the same cores, layer 2 ~reaches
the total-work/P floor, and the only "L3 WINS" rows are a 0.2 ms cell (E=256, the cell axis's job) and fork/join noise
(E=4096). A hot cell's bottleneck is *entities*, which layer 2 already splits across every core. Re-run `--gate` if a
game ever has the opposite shape (many tiny independent systems over few entities).

## Parameterising

`BenchmarkConfig` exposes `GridWidth`/`GridHeight` (-> `C`), `EntitiesPerCell` (`E`), `Systems` (`S`), `WarmupTicks`,
`TimedTicks`, `Seed`, `CellSize`, `TickSeconds`. `BenchmarkMatrix.Default()` is the standard matrix; construct your own
`BenchmarkConfig` and call `ServerTickBenchmark.Run(config, scheduler)` (pass a `ThreadPoolJobScheduler` for the
parallel cell-tick path, or omit for inline) to compare a single point - jobs-1 does exactly this to show its win. For
the entities axis, `EntitiesAxisBenchmark.Measure(entities, workPerRow, warmup, timed, scheduler)` times one hot
`World` both ways at a chosen per-row work.
