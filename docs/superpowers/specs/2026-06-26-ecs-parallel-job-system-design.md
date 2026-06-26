# ECS parallel job system — program design

**Date:** 2026-06-26
**Status:** Program spec (the map). **Outcome: complete at layer 2.** Layers 0 (benchmark), 1 (parallel cell ticks,
`7.41.0`), 2 (parallel `ForEach` + access declarations, `7.42.0`) shipped; layer 3 (system scheduler) was
measurement-gated and the gate said no (see "System scheduler — gate verdict" below), so it was de-scoped without
shipping code. Each sub-project got its own scoping doc (`docs/superpowers/scoping/`) → plan → build.
**Builds on:** the single-threaded archetype ECS (`KhaozEngine.Ecs`), `KhaozEngine.Simulation` (`FixedTickHost`), and
`KhaozEngine.Sharding` (`ShardHost`/`CellSim`, the MMO server topology, shipped `7.38.0`).
**Motivation:** the MMO netcode program (`docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md`) flagged the
strictly single-threaded ECS as the single biggest server-scale ceiling. This program builds the parallelism, in
dependency order, measurement-first.

## The problem, in big-O

A server tick is **O(S·N)** work, where **C** = active cells, **E** = entities per cell, **S** = systems per world,
**N = C·E** total entities, **P** = cores. Today that work runs on one core. Parallelism does not reduce the work; it
reduces wall-clock by splitting one of three independent axes:

| Axis | Strategy | Wall-clock | Speedup ceiling | Cost / hazard |
|---|---|---|---|---|
| **Cells (C)** | parallel cell ticks | `O(S·N / P)` when C≥P | **~P× (linear)** | none — cells are independent `World`s; one fork/join per tick |
| **Entities (E)** | parallel `ForEach` | `O(E/P)` per hot system | **~P×** for a hot cell | per-row-pure only; fork/join per `ForEach`; needs access decls + thread-safe structural changes |
| **Systems (S)** | system scheduler | `O(S·E / w)`, w = DAG width | **~w× (small, capped)** | every system declares accurate R/W access; dependency graph; highest blast radius |

The dominant, cheapest, hazard-free axis for the MMO shape is **cells**: many independent cells tick in parallel with
one fork/join and near-linear speedup. The **entities** axis breaks the degenerate "one hot cell" case (C<P, or one
cell holding most entities), and introduces the read/write access-declaration vocabulary the scheduler later reuses.
The **systems** axis (auto-parallel non-conflicting systems within a world) yields only a small, DAG-width-capped
multiplier at the highest complexity, and it does **not** split a hot system's entities — so it is the *last* layer,
not the foundation. (This matches Bevy / Unity DOTS / Flecs: parallel queries + a job system first; automatic system
scheduling rides on top of the access declarations parallel queries already require.)

## Determinism boundary

- **The authoritative server relaxes determinism** (per the MMO design: no cross-machine bit-identical FP; the heavy
  `DeterministicFpScope` machinery is not required server-side). `DeterministicRng` stays valuable for seeded spawns.
- A **per-row-pure** parallel `ForEach` (each invocation reads/writes only its own entity's components, no cross-entity
  reads, no shared mutable state, no inline structural changes) is **order-independent** — the result is identical
  regardless of how rows are partitioned across threads, so it is deterministic even bit-for-bit within a run.
- **Lockstep / single-player sims stay single-threaded.** Every parallel facility here is **opt-in**; the default
  `World.Update` / `ForEach` path is byte-unchanged. A lockstep game (e.g. SpaceGame's old model) simply does not use
  the parallel APIs, so the lockstep determinism guarantees are untouched.

## Engine vs game

`[engine]` ships the parallel facilities (a worker pool, parallel `ForEach`, access declarations, optional scheduler)
and the benchmark harness. A game opts in (mark a hot system parallel; enable parallel cell ticks on its `ShardHost`).
No game is forced to change; the single-threaded path remains the default.

## Architecture — layered, in dependency order

Each layer is independently shippable and independently valuable; stop after any layer once the benchmark says the
numbers are good enough. The scheduler (layer 3) is **conditional on measurement**.

| # | Sub-project | Package(s) | Gives you | Depends on |
|---|---|---|---|---|
| 0 | **Benchmark harness** | a benchmark project (no package) | a headless server-tick benchmark over (C, E, S) so every later layer has a target and we never optimize blind | Ecs, Simulation, Sharding |
| 1 | **Parallel cell ticks** | `KhaozEngine.Simulation` (+ `Sharding`) | `ShardHost` ticks independent cells across cores via a small worker-pool seam; near-linear in P for the MMO shape | Sharding, 0 |
| 2 | **Parallel `ForEach` + access decls** | `KhaozEngine.Ecs` | opt-in data-parallel `ForEach` (partition an archetype's rows across workers) with read/write component-access declarations + a debug hazard check; breaks the single-hot-cell ceiling | Ecs, 0 |
| 3 | **System scheduler (conditional)** | ~~`KhaozEngine.Ecs`~~ | auto-run non-conflicting systems within a world concurrently, built on layer 2's access declarations; only if the benchmark shows the per-cell critical path is the bottleneck | **DE-SCOPED — gate not met** (see verdict below) |

### Shared primitive: a worker pool seam

Layers 1–3 all need to fan work across cores. Ship one small seam (e.g. `IJobScheduler` / a default
`ThreadPoolJobScheduler` over the BCL thread pool, plus an inline/`SingleThreadedJobScheduler` for deterministic tests
and the single-thread default). Headless-testable: a deterministic inline scheduler runs jobs in order so a parallel
result can be asserted equal to the single-threaded result. (Exact shape is layer 1's to finalize; layers 2–3 reuse it.)

## Decomposition (each its own scoping doc → plan → build)

- **0 · Benchmark harness** — `jobs-0-benchmark-harness.md`. A headless, repeatable benchmark of one server tick across
  a matrix of (cells, entities/cell, systems), reporting wall-clock + entities/sec, single-threaded baseline. The
  measurement that justifies (or de-scopes) each later layer. No public API; a benchmark project + a `LiveSocket`-style
  excluded-from-CI trait so it does not gate CI.
- **1 · Parallel cell ticks** — `jobs-1-parallel-cell-ticks.md`. A worker-pool seam + `ShardHost.Tick` (and the handoff/
  ghost passes where safe) fanning independent cells across cores. Opt-in (`ShardHost` takes a scheduler; default = inline
  single-threaded). Acceptance: parallel tick == single-threaded tick (same world state) and scales ~P× with cell count
  in the benchmark.
- **2 · Parallel `ForEach` + access declarations** — `jobs-2-parallel-foreach-access.md`. `World.ParallelForEach<...>`
  partitioning archetype rows across the worker pool; a read/write access declaration model so a debug-mode hazard check
  rejects unsafe actions (cross-entity writes, inline structural changes); a thread-safe path for deferred structural
  changes (per-worker command buffers merged deterministically). Acceptance: parallel == single-threaded result; debug
  hazard check catches a deliberate violation; scales with E for a hot archetype.
- **3 · System scheduler (conditional)** — *was* `jobs-3-system-scheduler.md` (deleted on de-scope). `ISystem` access
  declarations → a per-world dependency graph → run non-conflicting systems concurrently on the worker pool,
  deterministically. Gated on the benchmark showing per-cell critical path (not cell count, not a hot system) is the
  bottleneck. **The gate was evaluated and not met (verdict below); no scheduler was built.**

### System scheduler — gate verdict (de-scoped)

The gate was measured with the `--gate` benchmark (`KhaozEngine.Benchmarks/SystemAxisGate.cs`), which models one cell
with a realistic mix of distinct systems (a 4-system Position/Velocity conflict cluster + 3 independent bookkeeping
systems, each declaring an `AccessSet`) and compares, per entity count: `T_seq` (all systems single-threaded), `T_l2`
(each system via `ParallelForEach`, the shipped layer 2), and `T_l3ceil` (the most optimistic overlap of the
single-threaded systems, list-scheduled over the conflict graph on all cores — the strongest case for layer 3). On a
12-core box, stable across runs:

- **Layer 3 is capped at ~2.1×** (the conflict-graph width) and does not grow with entity count — exactly the small,
  DAG-width-capped multiplier the big-O table predicts.
- **Layer 2 scales to ~5–6.4×** (it parallelizes *within* a system, uncapped by system count). For any cell heavy
  enough to be a bottleneck (≥16k entities) `T_l2` already beats the best-possible `T_l3ceil` by **2.4–3×** (at 65,536:
  ~3.8 ms vs ~9.2 ms). The two layers compete for the same cores and layer 2 ~reaches the total-work/P floor, so layer
  3 adds nothing on top.
- Layer 3 only "wins" at E=256 (a ~0.2 ms cell — the cell axis's domain) and E=4096 (noise in layer 2's fork/join
  crossover). Neither is a real per-cell bottleneck.

**Conclusion:** a hot cell's bottleneck is *entities*, which layer 2 already fans across every core; the *systems* axis
is the wrong tool and its ceiling is below layer 2's. The `AccessSet` foundation layer 3 would need is shipped (jobs-2),
so the scheduler can be built later if a real game's per-cell profile ever shows the gate regime (many tiny independent
systems over few entities). Re-run `--gate` to re-check.

## Key decisions (settled in brainstorming, 2026-06-26)

- **Measurement first.** Build the benchmark (0) before any parallelism; each later layer must show its win on it.
- **Cells → entities → systems**, in that order: biggest/cheapest/safest axis first; the scheduler is the conditional
  capstone, not the foundation.
- **Opt-in, default single-threaded.** The existing `Update`/`ForEach` path is byte-unchanged; lockstep sims are untouched.
- **One worker-pool seam** reused by all layers; a deterministic inline scheduler makes every layer headless-testable by
  asserting parallel == single-threaded.
- **Server-relaxed determinism**; per-row-pure parallel work is order-independent, so correctness is asserted as
  "parallel result equals single-threaded result".

## Acceptance (program-level)

On the layer-0 benchmark, headless: parallel cell ticks scale ~linearly with cores up to cell count (layer 1); a single
hot cell's dominant system scales with cores (layer 2); every parallel path produces a world state bit-identical to the
single-threaded path. The scheduler (layer 3) is built only if the benchmark identifies per-cell critical path as the
remaining bottleneck — **it did not (gate verdict above), so layer 3 was de-scoped and the program is complete at layer 2.**

## Open questions / risks

- **Worker-pool choice.** BCL `ThreadPool`/`Parallel` vs a bespoke pinned pool. Start with the BCL behind the seam;
  revisit only if the benchmark shows fork/join or scheduling overhead dominating.
- **Structural-change safety under parallel `ForEach`.** `EntityCommandBuffer` is explicitly not thread-safe; layer 2 must
  provide per-worker buffers merged in a deterministic order (or forbid inline structural changes in parallel actions).
- **Access-declaration accuracy.** Inaccurate R/W declarations cause silent data races; the debug hazard check (layer 2)
  is the mitigation, and it is the prerequisite the scheduler (layer 3) leans on entirely.
- **Whether layer 3 is ever worth it.** ~~Likely a modest, DAG-width-capped win; the benchmark decides.~~ **Answered: no
  (for the current shape).** The `--gate` benchmark showed layer 3's cross-system overlap caps at ~2.1× while layer 2
  already scales to ~5–6× on the same cores, so layer 3 loses for any cell heavy enough to matter. De-scoped; the
  foundation (0–2) is exactly what it would need if a future game's per-cell profile reopens the question.

## Cadence

Build layer by layer; each sub-project is one worktree + release on the shared `<KhaozEngine5xVersion>` line, headless
tests over the inline scheduler, full doc sweep. Like the MMO program, no code is written against this map directly —
each layer gets its own scoping doc (already drafted alongside this spec) → plan → build.
