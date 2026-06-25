# Scoping — MMO 3A: Cell grid + CellSim + ShardHost

**Status:** TO-DO (first Phase 3 sub-project; unblocks 3B–3E). **Depends on:** Phases 0–2 (shipped `7.36.0`).
**Fresh-chat kickoff:** *"Execute `docs/superpowers/scoping/mmo-3a-cell-grid-shardhost.md`."*

## Read first
- `docs/superpowers/specs/2026-06-25-mmo-phase3-seamless-shard-design.md` (Phase 3 design — the core model).
- `docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md` (program map, Layer 5).
- `CLAUDE.md` (worktree, TDD, release ritual, **one bump per batch**, doc sweep) + the **release policy B** note
  in `MMO-EXECUTION-ORDER.md` (hold/batch, confirm before publish).

## Goal
Partition the world into a uniform grid of authoritative **cells** and run them in one process. No cross-cell
crossing or ghosting yet — that's 3B/3C. This is the container the rest of Phase 3 builds on.

## Deliverable (new package `KhaozEngine.World`)
- `CellCoord` — integer cell coordinate from a world position + cell size (mirror `InterestGrid` cell math).
- `CellSim` — owns one cell's `KhaozEngine.Ecs.World` + a `KhaozEngine.Simulation.FixedTickHost` + a
  `KhaozEngine.Replication.ServerReplicator` + an `InterestGrid`. `Tick(dt)` steps the cell's sim.
- `ShardHost` — owns the cell map (`CellCoord → CellSim`), creates cells on demand, exposes
  `CellFor(worldPos)`, and `Tick(dt)` ticks all cells at one fixed rate. Entity→cell assignment by position.
- Depends on `Ecs`, `Simulation`, `Replication`. Register in `KhaozEngine.slnx`, `KhaozEngine.Tests.csproj`,
  and the `KhaozEngine.Server` umbrella.

## Acceptance (headless)
- A `ShardHost` over a grid creates the right `CellSim` for a given world position (`CellFor`).
- `ShardHost.Tick` advances every cell's `FixedTickHost` deterministically (tick counts match a known
  elapsed-time sequence).
- Spawning entities at positions in different cells routes them to the correct `CellSim` worlds.

## Conventions
Worktree `feature/mmo-3a-cell-grid`. TDD, headless. **Do not bump the version or publish** — hold per policy B
(this is the start of the Phase 3 batch). Full doc sweep for the new `KhaozEngine.World` package. When merged,
delete this scoping doc and tick the status table.
