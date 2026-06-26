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
| 1 | Parallel cell ticks (`ShardHost` across cores) | **DONE** (`7.41.0`) → `IJobScheduler` seam in `Simulation` + opt-in `ShardHost.Scheduler` |
| 2 | Parallel `ForEach` + R/W access declarations | **DONE** (`7.42.0`) → `World.ParallelForEach` (arity 1-8 pure + 1-4 buffered) + `AccessSet`/`Access` + the `ParallelHazardChecks` debug guard, all in `KhaozEngine.Ecs` |
| 3 | System scheduler (conditional, measured) | **EVALUATED → DE-SCOPED** (gate not met; no code shipped) → the `--gate` benchmark verdict below. Re-open only if a real game's per-cell profile changes the picture. |

**The program is complete at layer 2.** Layers 0-2 shipped; layer 3 was measurement-gated and the measurement said no
(below). Nothing is wasted: the `AccessSet` model layer 3 would need is already in `KhaozEngine.Ecs`, so it can be
built later if a game's profile ever justifies it.

**Baseline numbers (jobs-0, single-threaded).** At equal N=65,536 entities, S=4 systems, 30 Hz, on a 12-core box:
many-small-cells (C=1024) ≈ 3.0 ms/tick (~21M entities/sec); one-hot-cell (C=1) ≈ 0.45 ms/tick (~144M
entities/sec); mid (C=64) ≈ 0.48 ms/tick (~135M entities/sec). The ~6× gap at equal N is pure per-cell overhead.

**jobs-1 (parallel cell ticks, `7.41.0`).** Same box/config, cells fanned across 12 cores: many-small-cells
**~10.5× speedup** (near-linear in P, since C=1024 >> cores), mid (C=64) **~4×**, one-hot-cell (C=1) **~1×** (a
single cell can't be split by the cell axis - that is layer 2, parallel `ForEach`). Re-measure with
`dotnet run --project KhaozEngine.Benchmarks -c Release` (now prints inline vs parallel + speedup).

**jobs-2 (parallel `ForEach`, the entities axis, `7.42.0`).** `World.ParallelForEach` fans one hot archetype's rows
across cores - the win the cell axis can't give a single hot cell. The benchmark's new entities-axis section sweeps
per-row work on one hot World (E=65,536) on the same 12-core box, because the result is a **crossover, not a single
number**: a fork/join has a fixed cost, so trivial per-row work is overhead-bound (work=1 ≈ **0.3×**, a loss) while a
realistic hot system amortizes it and scales toward ~P× (work=32 ≈ **5×**, work=128 ≈ **6.7×**, work=512 ≈ **7.5×**).
Correctness (parallel result == single-threaded, the hazard guard catches violations, buffered structural changes
replay deterministically) is headless-tested over the inline scheduler in `KhaozEngine.Tests`.

**jobs-3 GATE (system scheduler) — EVALUATED, gate NOT met, de-scoped.** The scoping doc said build the system
scheduler only if a single cell's tick is bottlenecked by the *sum of distinct systems* such that overlapping the
non-conflicting ones would beat what layers 1+2 already give. The `--gate` benchmark
(`dotnet run --project KhaozEngine.Benchmarks -c Release -- --gate`) models a realistic 7-system cell (a 4-system
Pos/Vel conflict cluster + 3 independent bookkeeping systems) and compares, per entity count: `T_seq` (all systems
single-threaded), `T_l2` (each system via `ParallelForEach`, what we ship), and `T_l3ceil` (the most optimistic
overlap of the single-threaded systems, list-scheduled over the `AccessSet` conflict graph on all cores). Verdict on a
12-core box, stable across runs:
- Layer 3's cross-system overlap is **capped at ~2.1×** (the conflict-graph width), exactly the "small, DAG-width-
  capped multiplier" the spec's big-O table predicts. It does **not** grow with entity count.
- Layer 2 parallelizes *within* each system, uncapped by system count, and **scales to ~5-6.4×**. For any cell heavy
  enough to be a bottleneck (≥16k entities) **layer 2 already beats the best-possible layer 3 by 2.4-3×** (e.g. at
  65,536: `T_l2`≈3.8 ms vs `T_l3ceil`≈9.2 ms). They compete for the same cores, and layer 2 ~reaches the total-work/P
  floor, so layer 3 adds nothing on top.
- The only entity counts where layer 3 "wins" are E=256 (a ~0.2 ms cell — the cell axis's domain, fanned in bulk by
  layer 1) and E=4096 (noise in layer 2's fork/join crossover; flips run-to-run). Neither is a real per-cell bottleneck.

So the systems axis is the wrong tool here: the bottleneck a hot cell has is *entities*, which layer 2 already splits
across all cores. Re-run `--gate` if a future game's per-cell system mix changes (many tiny independent systems over
few entities is the regime that would flip it); the `AccessSet` foundation is in place to build the scheduler then.

## Execution order (done)

**Measurement first, then biggest/cheapest/safest axis first:** `0 → 1 → 2 → (3 gated)`. Layers 0-2 shipped. Layer 3
(the system scheduler) was the conditional capstone, gated on the benchmark showing the per-cell critical path of
distinct systems as the remaining bottleneck. The `--gate` measurement (above) showed it is not — layer 2 already wins
that ground — so layer 3 was de-scoped, per the spec's "stop and record why".

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
