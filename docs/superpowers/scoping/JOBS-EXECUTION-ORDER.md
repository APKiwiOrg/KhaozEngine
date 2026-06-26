# ECS parallel job system — scoping & execution order

Live, to-do handoff docs for the parallel-job-system program — one self-contained scoping doc per sub-project, each a
kickoff for a **fresh chat**. Only current work lives here; completed work is NOT duplicated (it's in `CHANGELOG.md` +
`docs/superpowers/specs|plans/`). If a doc here is done, delete it.

**Program design (read for full context + the big-O rationale):**
`docs/superpowers/specs/2026-06-26-ecs-parallel-job-system-design.md`.

## Status

| # | What | State |
|---|---|---|
| 0 | Benchmark harness (measure the server tick) | **DONE** → `KhaozEngine.Benchmarks` (project + README; no package, no version bump) |
| 1 | Parallel cell ticks (`ShardHost` across cores) | **TO-DO** (unblocked by 0) → `jobs-1-parallel-cell-ticks.md` |
| 2 | Parallel `ForEach` + R/W access declarations | TO-DO (unblocked by 0; independent of 1) → `jobs-2-parallel-foreach-access.md` |
| 3 | System scheduler (conditional, measured) | TO-DO (needs 2 + a benchmark verdict) → `jobs-3-system-scheduler.md` |

**Baseline numbers (jobs-0, single-threaded).** At equal N=65,536 entities, S=4 systems, 30 Hz, on a 12-core box:
many-small-cells (C=1024) ≈ 3.0 ms/tick (~21M entities/sec); one-hot-cell (C=1) ≈ 0.45 ms/tick (~144M
entities/sec); mid (C=64) ≈ 0.48 ms/tick (~135M entities/sec). The ~6× gap at equal N is pure per-cell overhead -
exactly what layer 1 (parallel cell ticks) moves. Re-measure with
`dotnet run --project KhaozEngine.Benchmarks -c Release`.

## Execution order (go)

**Measurement first, then biggest/cheapest/safest axis first:** `0 → 1 → 2 → (3 only if the benchmark says so)`.
Layer 1 (cells) and layer 2 (entities) are independent of each other and could run as concurrent worktrees once 0
lands, but 1 is the bigger MMO win for less risk, so do it first. Layer 3 (the system scheduler) is the conditional
capstone: build it only if the benchmark shows the **per-cell critical path** (not cell count, not a hot system) is the
remaining bottleneck — see the program spec's big-O table.

## How to run one (each doc = one fresh chat)

1. Open a fresh chat in `~/KhaozEngine` and paste: *"Execute the scoping doc `docs/superpowers/scoping/<file>` — read it
   plus the docs it points to, then proceed."*
2. The chat follows the engine `CLAUDE.md`: **work in a worktree**, **TDD headless** (every behaviour ships a
   `KhaozEngine.Tests` test; assert **parallel result == single-threaded result** via the inline scheduler), build green.
3. **Opt-in, default single-threaded:** the existing `Update`/`ForEach` path stays byte-unchanged; lockstep sims untouched.
4. On any package add / public API change: **full doc sweep** (README catalog + layout, `CLAUDE.md` package map,
   `docs/CONSUMERS.md`, `docs/USING-KHAOZENGINE.md`) per `CLAUDE.md`.
5. Release per the normal ritual (each layer is a minor bump on the shared line). No standing hold policy here unless the
   user sets one; confirm before push/publish per the global rule.
6. When a sub-project is done and merged, **delete its scoping doc here** and tick the table, so this dir only shows
   what's left.
