# MMO program — scoping & execution order

This directory holds **live, to-do handoff docs** for the MMO netcode program — one self-contained scoping doc
per remaining sub-project, each a kickoff for a **fresh chat**. Only current work lives here; completed work is
NOT duplicated (it's in `CHANGELOG.md` + `docs/superpowers/specs|plans/`). If a doc here is done, delete it.

**Program design (read for full context):** `docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md`
(the 8-layer map) and `docs/superpowers/specs/2026-06-25-mmo-phase3-seamless-shard-design.md` (Phase 3).

## Status

| Phase | What | State |
|---|---|---|
| 0 | Transport seam + `FixedTickHost` | **shipped `7.35.0`** |
| 1 | Session lifecycle + entity replication (full+delta+interp) | **shipped `7.36.0`** |
| 2 | Interest management (AoI) + world-store seam | **shipped `7.36.0`** |
| 3A | Cell grid + `CellSim` + `ShardHost` (new package `KhaozEngine.Sharding`) | **BUILT, held unpublished** (policy B - batches with 3B–3E) |
| 3B | Cross-cell ghosting over `ICellLink` | TO-DO (needs 3A) → `mmo-3b-cross-cell-ghosting.md` |
| 3C | Authority handoff (seamless crossing) | TO-DO (needs 3B) → `mmo-3c-authority-handoff.md` |
| 3D | Client home-cell serving + re-bind | TO-DO (needs 3C) → `mmo-3d-client-serving-rebind.md` |
| 3E | Dedicated-server template + `ICellLink` net seam | TO-DO (needs 3D) → `mmo-3e-server-template-celllink.md` |
| R | Refinements (see bottom) | TO-DO, independent / later |

## Execution order (go)

**Sequential — each depends on the previous:** `3A → 3B → 3C → 3D → 3E`. 3A unblocks everything; do it first.
The **refinements** (below) are independent and can run in parallel with Phase 3 in their own chats at any time.

## How to run one (each doc = one fresh chat)

1. Open a fresh chat in `~/KhaozEngine` and paste: *"Execute the scoping doc `docs/superpowers/scoping/<file>` —
   read it plus the docs it points to, then proceed."*
2. The chat follows the engine `CLAUDE.md`: **work in a worktree** (`feature/<name>`), **TDD headless** (every
   behaviour ships a `KhaozEngine.Tests` test), build green.
3. **Release policy = B (standing):** do NOT publish per sub-project. Hold the work locally (one version bump per
   *batch*, not per item) and **confirm before any push/publish**. Batch the Phase 3 publish (likely all of 3A–3E)
   into one release when a natural boundary is reached.
4. On any package add / API change: **full doc sweep** (README catalog + layout, `CLAUDE.md` package map,
   `docs/CONSUMERS.md` umbrella, `docs/USING-KHAOZENGINE.md`) per `CLAUDE.md`.
5. When a sub-project is done and merged, **delete its scoping doc here** and tick the table above, so this dir
   only ever shows what's left.

## Refinements (independent; schedule as needed, own chats)

- **ECS job scheduling** — read/write component access decls + parallel `ForEach`, so a cell tick (or N cells)
  uses multiple cores. The single biggest server-scale ceiling. Touches `KhaozEngine.Ecs` core.
- **Delta + AoI unification** — currently AoI uses interest-filtered full snapshots; fuse with per-client delta
  baselines so interest-filtered deltas are sent (less bandwidth still).
- **Delta bit-packing / quantization** — replace the `BinaryWriter`-level component encoding with bit-packing +
  position/rotation quantizers (`UnitAxisQuantizer` exists) for wire size.
- **SQLite `IWorldStore`** — a real DB-backed `IWorldStore` impl (the in-memory one is the reference). Adds a
  `Microsoft.Data.Sqlite` dependency; consider a separate `KhaozEngine.WorldStore.Sqlite` package to keep the
  seam package dep-free.
