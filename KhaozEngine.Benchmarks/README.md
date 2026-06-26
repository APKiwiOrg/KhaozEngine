# KhaozEngine.Benchmarks - server-tick benchmark (jobs-0/1/2)

A headless, repeatable benchmark of **one server tick** across a matrix of (cells `C`, entities/cell `E`,
systems `S`), of the parallel-job-system program (`docs/superpowers/specs/2026-06-26-ecs-parallel-job-system-design.md`).
jobs-0 established the **single-threaded baseline** every later layer must move; jobs-1 added parallel cell ticks (the
**cell axis**), so each regime is run **inline and with cells fanned across cores**; jobs-2 added the **entities axis**,
shown in a dedicated section where one hot cell's system fans its entity rows across cores via `World.ParallelForEach`.
Without the benchmark we'd optimize blind, so it landed first.

Not a shipped package (`IsPackable=false`) and not on the engine version line. The timing loop only runs via
`dotnet run`; CI's `dotnet test` never invokes it, so it stays out of CI's timed path. The harness's deterministic
and structural behaviour *is* covered by `KhaozEngine.Tests/Benchmarks/ServerTickBenchmarkTests.cs`, which run in CI.

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

## Parameterising

`BenchmarkConfig` exposes `GridWidth`/`GridHeight` (-> `C`), `EntitiesPerCell` (`E`), `Systems` (`S`), `WarmupTicks`,
`TimedTicks`, `Seed`, `CellSize`, `TickSeconds`. `BenchmarkMatrix.Default()` is the standard matrix; construct your own
`BenchmarkConfig` and call `ServerTickBenchmark.Run(config, scheduler)` (pass a `ThreadPoolJobScheduler` for the
parallel cell-tick path, or omit for inline) to compare a single point - jobs-1 does exactly this to show its win. For
the entities axis, `EntitiesAxisBenchmark.Measure(entities, workPerRow, warmup, timed, scheduler)` times one hot
`World` both ways at a chosen per-row work.
