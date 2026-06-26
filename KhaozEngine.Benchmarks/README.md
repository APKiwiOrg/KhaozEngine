# KhaozEngine.Benchmarks - server-tick benchmark (jobs-0/1)

A headless, repeatable benchmark of **one server tick** across a matrix of (cells `C`, entities/cell `E`,
systems `S`), of the parallel-job-system program (`docs/superpowers/specs/2026-06-26-ecs-parallel-job-system-design.md`).
jobs-0 established the **single-threaded baseline** every later layer must move; jobs-1 added parallel cell ticks, so
each regime is now run **inline and across cores** with the speedup reported. Without the benchmark we'd optimize
blind, so it landed first.

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
**One hot cell** (C=1) can't be split by the cell axis at all (~1x) - that is the next layer's job (parallel
`ForEach`, jobs-2). **Mid** lands in between.

## Parameterising

`BenchmarkConfig` exposes `GridWidth`/`GridHeight` (-> `C`), `EntitiesPerCell` (`E`), `Systems` (`S`), `WarmupTicks`,
`TimedTicks`, `Seed`, `CellSize`, `TickSeconds`. `BenchmarkMatrix.Default()` is the standard matrix; construct your own
`BenchmarkConfig` and call `ServerTickBenchmark.Run(config, scheduler)` (pass a `ThreadPoolJobScheduler` for the
parallel path, or omit for inline) to compare a single point - a later layer does exactly this to show its win.
