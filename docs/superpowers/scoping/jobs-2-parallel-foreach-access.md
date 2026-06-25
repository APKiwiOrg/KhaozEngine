# Scoping — Jobs 2: Parallel ForEach + read/write access declarations

**Status:** TO-DO. **Depends on:** 0 (benchmark); the worker-pool seam from 1 (independent of 1's `ShardHost` wiring).
**Fresh-chat kickoff:** *"Execute `docs/superpowers/scoping/jobs-2-parallel-foreach-access.md`."*

## Read first
- `docs/superpowers/specs/2026-06-26-ecs-parallel-job-system-design.md` (big-O: the entities axis breaks the single-hot-cell
  ceiling; the access-declaration vocabulary is the prerequisite the scheduler later reuses).
- `KhaozEngine.Ecs/Query.cs` + `World.Query.cs` (the per-archetype contiguous SoA `ForEach` to parallelize),
  `Archetype.cs`/`Column.cs` (row layout), `EntityCommandBuffer.cs` (NOT thread-safe — see hazards), `CLAUDE.md`.

## Goal
Opt-in **data-parallel `ForEach`** in `KhaozEngine.Ecs`: partition a matched archetype's row range `[0,Count)` across the
worker pool so a single hot system over many entities uses all cores. Rows are independent memory (`Column<T>.Data[r]`),
so per-row-pure actions are race-free and order-independent (deterministic). Introduce the read/write component-access
declaration model that makes this safe to reason about — and that the system scheduler (layer 3) will reuse.

## Deliverable (in `KhaozEngine.Ecs`)
- `World.ParallelForEach<...>` overloads mirroring `ForEach<...>`, fanning archetype row ranges across an `IJobScheduler`
  (default inline = identical to `ForEach`). Per-row-pure contract: the action touches only its row's components.
- A **read/write access declaration** model (e.g. a `SystemAccess`/`AccessSet` describing which component types a unit
  reads vs writes), and a **debug-mode hazard check** that rejects unsafe parallel actions (inline structural changes,
  declared write-write / write-read conflicts). Off in release for speed; on in tests.
- A **thread-safe deferred-structural-change path**: per-worker `EntityCommandBuffer`s merged in a deterministic order at
  the join (because the shared `World.Commands` ECB is explicitly not thread-safe). Inline structural changes inside a
  parallel action stay forbidden (hazard check catches them).

## Acceptance (headless)
- `ParallelForEach` over a per-row-pure action produces a world state **bit-identical** to the same `ForEach`
  (inline-vs-threadpool scheduler equality), for single- and multi-archetype matches.
- The debug hazard check **rejects** a deliberately unsafe action (e.g. one that writes a component it didn't declare,
  or mutates another entity) — assert it throws/flags.
- Deferred structural changes recorded from parallel workers replay deterministically (same result every run).
- On the layer-0 benchmark, a hot single-cell system scales with cores.

## Conventions
Worktree `feature/jobs-2-parallel-foreach`. TDD headless (parallel == single-threaded; hazard check catches a violation).
Opt-in: the existing `ForEach` path is byte-unchanged. Touches the ECS core — keep the access model small and the parallel
overloads generated/consistent with the existing `ForEach` arity. Full doc sweep (USING section for `ParallelForEach` +
the access model). SemVer minor. Delete this doc when merged; tick the status table.
