# Scoping — Jobs 3: System scheduler (conditional capstone)

**Status:** TO-DO, **CONDITIONAL** — build only if the layer-0 benchmark shows the **per-cell critical path** (not cell
count, not a single hot system) is the remaining bottleneck. **Depends on:** 2 (the access-declaration model). **Fresh-chat
kickoff:** *"Execute `docs/superpowers/scoping/jobs-3-system-scheduler.md`."*

## Read first
- `docs/superpowers/specs/2026-06-26-ecs-parallel-job-system-design.md` (big-O: the systems axis yields only a small,
  DAG-width-capped multiplier at the highest cost — this is the *last* layer, justified by measurement, not the foundation).
- The layer-2 access-declaration model + `KhaozEngine.Ecs/World.Systems.cs` (`ISystem`/groups/`Update`), `CLAUDE.md`.

## Gate (check before building)
Run the layer-0 benchmark with layers 1 + 2 in place. Only proceed if a single cell's tick is dominated by the **sum of
several distinct systems** (a long critical path of dependent systems), not by one hot system (layer 2 handles that) and
not by having few cells (layer 1 handles that). If the benchmark doesn't show this, **stop and record why** — the
foundation (0–2) is already exactly what this would have needed, so nothing is wasted.

## Goal
Run a world's **non-conflicting systems concurrently**: each `ISystem` declares its read/write component access (the
layer-2 model); a per-world dependency graph orders conflicting systems and overlaps independent ones on the worker pool,
deterministically.

## Deliverable (in `KhaozEngine.Ecs`)
- `ISystem` (or an adjacent declaration) exposes a read/write `AccessSet`.
- A scheduler that, per system group, builds a dependency graph (write-write and read-write on the same component type =
  an edge) and executes systems across the `IJobScheduler` honouring it, with a deterministic tie-break so the result is
  reproducible. Default remains the existing sequential `Update` (opt-in scheduling).
- Interplay with the command buffer: structural changes still flush at the existing safe points; document the ordering.

## Acceptance (headless)
- A scheduled group produces a world state **identical** to running the same systems sequentially.
- Two systems with disjoint access **demonstrably overlap** (e.g. observable via the inline-vs-parallel timing or a
  test scheduler that records concurrency); two systems that conflict are **serialized** in a deterministic order.
- A mis-declared access is caught by the layer-2 debug hazard check.

## Conventions
Worktree `feature/jobs-3-scheduler`. TDD headless (scheduled == sequential). Opt-in: sequential `Update` stays the
default. Highest blast radius on the ECS core — keep it additive and gated behind the measurement above. Full doc sweep.
SemVer minor. Delete this doc when merged (and the others, if this is the last); tick / retire the status table.
