# Scoping — MMO 3B: Cross-cell ghosting (border overlap) over ICellLink

**Status:** TO-DO. **Depends on:** 3A (cell grid + `ShardHost`). **Fresh-chat kickoff:**
*"Execute `docs/superpowers/scoping/mmo-3b-cross-cell-ghosting.md`."*

## Read first
Phase 3 design spec (`docs/superpowers/specs/2026-06-25-mmo-phase3-seamless-shard-design.md`, "Border ghosting"
+ "Inter-cell messaging"), `CLAUDE.md`, and `MMO-EXECUTION-ORDER.md` (release policy B).

## Goal
Make entities near a cell boundary visible to the adjacent cell as read-only **ghosts**, so collision /
visibility / targeting work across borders. Introduce the `ICellLink` seam for cell↔cell messaging with an
in-process, deterministic implementation.

## Deliverable (in `KhaozEngine.Sharding`)
- `ICellLink` — seam for sending messages between cells (ghost sync payloads keyed by source/target
  `CellCoord`). In-process impl delivers in-memory, applied at tick boundaries (deterministic). Network impl is
  infra (out of scope here; just keep the seam clean).
- Border detection: a cell flags its owned entities within an **overlap margin** of each edge (reuse
  `InterestGrid` / cell math) and mirrors them to the neighboring cell.
- Ghost mirroring: serialize border entities via the existing `KhaozEngine.Replication` snapshot/delta codecs;
  the neighbor applies them into its `World` as **read-only ghosts** (tag ghosts so the cell's own sim does not
  treat them as authoritative — e.g., a `Ghost` component or a separate ghost view). A cell's world = owned +
  ghosts.

## Acceptance (headless)
- An entity placed within the overlap margin of the A/B boundary appears as a ghost in cell B after a tick.
- An entity well inside cell A (beyond the margin) does NOT appear in B.
- Ghost component values match the owner's (mirrored via the codecs); moving the owner updates the ghost next sync.
- The owner cell remains the only one that simulates it (ghosts are read-only).

## Conventions
Worktree `feature/mmo-3b-ghosting`. TDD headless. Hold per policy B (no publish). Doc sweep if public API
(`ICellLink`, ghost tagging) is added. Delete this doc when merged.
