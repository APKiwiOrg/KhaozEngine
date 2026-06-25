# MMO Phase 3 — Seamless sharded grid — design

**Date:** 2026-06-25
**Status:** Design (approved). Sub-projects 3A–3E each get their own scoping doc (`docs/superpowers/scoping/`) → plan → build.
**Builds on:** Phases 0–2, published `7.36.0` (transport seam + `FixedTickHost`; session layer; `KhaozEngine.Replication` full-state+delta+interpolation+`InterestGrid`; `KhaozEngine.WorldStore`).
**Parent program:** `docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md` (Layer 5).

## Goal

A seamless open world partitioned into a uniform **grid of cells**, each an authoritative sim node, with
entities crossing cell boundaries **seamlessly** (no load screen) and interacting across borders. Shipped as an
**in-process** `ShardHost` (one process runs N cells) with the protocol behind seams so a multi-process
deployment can implement it. This is the EVE / seamless-MMO topology.

## Engine vs infra boundary

- **`[engine]`** — the cell-grid model, per-cell `CellSim` (a `World` + `FixedTickHost` + replication + AoI),
  `ShardHost` running N cells in one process with real seamless crossing + border ghosting + authority handoff,
  the `ICellLink` inter-cell messaging seam (in-process impl), and a dedicated-server template. Deterministic,
  headless-testable.
- **`[infra]`** — running cells across processes/machines (a network `ICellLink`), cross-node clock sync,
  cell discovery/orchestration, load-based cell splitting/merging. The engine proves the protocol in-process;
  production distribution implements the seam.

## Core model

- The world is a uniform grid of **cells** (`InterestGrid`-style cell coords). Each cell is a `CellSim`: its own
  ECS `World`, `FixedTickHost`, `ServerReplicator`, and `InterestGrid`. `ShardHost` owns the cell map and ticks
  all cells at the same fixed rate.
- An entity is **owned** (authoritatively simulated) by exactly one cell — the cell containing its position.
- Cells exchange messages only through **`ICellLink`** (in-process impl shipped; network = infra).

## The four mechanics

1. **Border ghosting.** Each cell mirrors its border entities (those within an **overlap margin** of a cell
   edge) into the neighboring cell as read-only **ghosts**, using the existing `Replication` snapshot/delta
   codecs over `ICellLink`. A cell's `World` = owned entities + neighbor ghosts. Ghosts are simulated by their
   owner; the neighbor reads them for collision/visibility/targeting only.
2. **Authority handoff.** When an owned entity crosses cell A→B: A serializes its authoritative component set,
   sends it over `ICellLink`, B deserializes + takes ownership + acks, A drops it (or retains it as a ghost if
   still within B's border overlap). A migrate handshake (Migrating → Owned-ack → Released) guarantees **no
   duplication** (never two owners) and **no loss** (never zero). Deterministic; applied at tick boundaries.
3. **Client home-cell serving.** Invariant: **overlap margin ≥ max client interest radius.** Then the cell that
   owns a player already holds ghosts of everything within that player's interest, so the player's **home cell**
   serves that client's entire AoI alone — no client-side multi-cell aggregation. On a player crossing, the
   client **re-binds** to the new home cell seamlessly (the new cell already had the player's surroundings as
   ghosts / takes ownership via handoff).
4. **Inter-cell messaging (`ICellLink`).** A seam for cell↔cell messages (ghost sync, handoff). In-process impl
   = direct in-memory delivery applied at tick boundaries (deterministic). Network impl across nodes = infra.

## Reuse

Phase 3 is mostly composition: `FixedTickHost` per cell, `Replication` codecs for ghost mirroring + handoff
serialization, `InterestGrid` for both AoI and border-overlap detection, `WorldStore` for persistence,
`NetServer`/sessions for clients. The genuinely new code is the cell grid, the ghost protocol, and the handoff
protocol.

## Decomposition (staged; each its own scoping doc → plan → build)

- **3A** Cell grid + `CellSim` + `ShardHost` (in-process; ticks all cells; entity→cell by position; no crossing
  yet). New package `KhaozEngine.World`.
- **3B** Cross-cell ghosting over `ICellLink` (`ICellLink` seam + in-process impl; border-overlap mirrors via
  Replication; a cell's World holds owned + ghost entities).
- **3C** Authority handoff (migrate handshake on boundary crossing; no dup/loss; deterministic).
- **3D** Client home-cell serving + seamless re-bind on crossing (overlap ≥ interest radius invariant).
- **3E** Dedicated-server template wiring it all + the `ICellLink` network seam (in-process shipped; network =
  infra stub/interface).

## Key decisions (settled)

- **Home-cell-serves** clients (overlap ≥ interest radius), not client-side multi-cell aggregation.
- **Reuse delta replication** for ghost mirroring (cells delta-mirror borders to neighbors).
- **In-process `ICellLink`** first; network impl deferred to infra.
- Cells tick in lockstep at one fixed rate in-process; cross-cell messages apply at tick boundaries.

## Acceptance (phase-level)

In a single-process `ShardHost` with a multi-cell grid, headless: an entity moving across a cell boundary keeps
a single authoritative owner throughout (no dup/loss); an entity near a border is visible as a ghost in the
neighbor; a client served by its home cell sees the correct interest set across the border; a player crossing a
boundary re-binds without losing its surroundings. All deterministic, no sockets (in-process `ICellLink`).

## Open / deferred (infra or later)

Multi-process/distributed cells, network `ICellLink`, cross-node clock sync, cell discovery/orchestration,
load-based split/merge. Plus program-wide refinements: ECS job scheduling (per-cell parallel ticks), delta+AoI
unification, delta bit-packing/quantization, a SQLite `IWorldStore`.
