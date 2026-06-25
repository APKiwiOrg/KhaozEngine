# Scoping — Jobs 1: Parallel cell ticks

**Status:** TO-DO. **Depends on:** 0 (benchmark). **Fresh-chat kickoff:**
*"Execute `docs/superpowers/scoping/jobs-1-parallel-cell-ticks.md`."*

## Read first
- `docs/superpowers/specs/2026-06-26-ecs-parallel-job-system-design.md` (big-O: cells are the cheapest, hazard-free,
  ~linear-in-P axis — independent `World`s, one fork/join per tick).
- `KhaozEngine.Sharding/ShardHost.cs` + `CellSim.cs` (the tick loop to parallelize), `CLAUDE.md`, `JOBS-EXECUTION-ORDER.md`.

## Goal
Tick the `ShardHost`'s independent cells across cores. A cell is its own `World` + `FixedTickHost` with no shared state,
so the per-cell sim step is embarrassingly parallel. This is the biggest MMO-shape win for the least risk and does not
touch the ECS core.

## Deliverable
- A small **worker-pool seam** (the shared primitive layers 2–3 reuse): e.g. `IJobScheduler` with a default
  `ThreadPoolJobScheduler` (BCL thread pool / `Parallel.ForEach`) and an **inline `SingleThreadedJobScheduler`** (runs
  jobs in order) for deterministic tests + the single-threaded default. Likely in `KhaozEngine.Simulation`.
- `ShardHost` takes an optional `IJobScheduler` (default = inline); `Tick` fans the per-cell sim step across it. Keep the
  cross-cell passes (`SyncGhosts`, `ProcessHandoffs`) on their existing single-threaded boundary unless trivially safe —
  ghosting/handoff mutate neighbours via `ICellLink`, so they stay sequential for now (document why).
- Decide + document handling of cells created on demand mid-tick (snapshot the cell list before fanning).

## Acceptance (headless)
- Parallel tick produces **identical world state** to a single-threaded tick over the same inputs (assert via the inline
  scheduler vs the thread-pool scheduler — same per-cell `TickCount` + component values).
- No data races (cells touch only their own `World`); a stress test with many cells + entities passes repeatably.
- On the layer-0 benchmark, wall-clock scales ~linearly with cores up to cell count (report the numbers).

## Conventions
Worktree `feature/jobs-1-parallel-cells`. TDD headless (parallel == single-threaded). Opt-in: default scheduler is inline,
so existing behaviour is byte-unchanged. Full doc sweep if public API added (the scheduler seam + `ShardHost` ctor arg).
SemVer minor. Delete this doc when merged; tick the status table.
