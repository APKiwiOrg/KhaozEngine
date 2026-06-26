# KhaozEngine.Benchmarks - server-tick benchmark (jobs-0)

A headless, repeatable benchmark of **one single-threaded server tick** across a matrix of
(cells `C`, entities/cell `E`, systems `S`). This is sub-project 0 of the parallel-job-system program
(`docs/superpowers/specs/2026-06-26-ecs-parallel-job-system-design.md`): the **single-threaded baseline** every
later layer (parallel cell ticks, parallel `ForEach`, the system scheduler) must move. Without it we'd optimize
blind, so this lands first.

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

```
regime                   C         E           N   S     ms/tick     entities/sec    comp-visits/sec
-------------------------------------------------------------------------------------------------------
many small cells      1024        64       65536   4       x.xxx      nnn,nnn,nnn        nnn,nnn,nnn
one hot cell             1     65536       65536   4       x.xxx      nnn,nnn,nnn        nnn,nnn,nnn
mid (8x8 cells)         64      1024       65536   4       x.xxx      nnn,nnn,nnn        nnn,nnn,nnn
```

- **ms/tick** - mean wall-clock for one server tick (every cell advanced once). The headline latency.
- **entities/sec** - `N` processed per tick / per-tick seconds. Headline throughput.
- **comp-visits/sec** - `S × entities/sec`: the real `O(S·N)` work rate (each of `S` systems passes over all `N`
  entities every tick). Lets rows with different `S` stay comparable.

The three regimes are the shapes the program's big-O table distinguishes, held at **equal N** so the numbers compare
directly. On the single-threaded baseline they should be close (the work is `O(S·N)` regardless of shape); the gap is
mostly per-cell overhead. They diverge under the later parallel layers - **many small cells** is what layer 1
(parallel cell ticks) speeds up; **one hot cell** is the degenerate case only layer 2 (parallel `ForEach`) can split.

## Parameterising

`BenchmarkConfig` exposes `GridWidth`/`GridHeight` (-> `C`), `EntitiesPerCell` (`E`), `Systems` (`S`), `WarmupTicks`,
`TimedTicks`, `Seed`, `CellSize`, `TickSeconds`. `BenchmarkMatrix.Default()` is the standard matrix; construct your own
`BenchmarkConfig` and call `ServerTickBenchmark.Run(config)` to compare a single point (a later layer does exactly
this to show its win).
