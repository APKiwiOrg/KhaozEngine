# Scoping — MMO 3C: Authority handoff (seamless cell crossing)

**Status:** TO-DO. **Depends on:** 3B (ghosting + `ICellLink`). **Fresh-chat kickoff:**
*"Execute `docs/superpowers/scoping/mmo-3c-authority-handoff.md`."*

## Read first
Phase 3 design spec ("Authority handoff"), `CLAUDE.md`, `MMO-EXECUTION-ORDER.md` (policy B). This is the
**hardest, correctness-critical** sub-project — handoff dup/loss bugs are the classic seamless-MMO failure.

## Goal
When an owned entity crosses a cell boundary, transfer authority to the new cell with **exactly-once** semantics:
never two owners (duplication), never zero (loss).

## Deliverable (in `KhaozEngine.World`)
- Crossing detection: each tick, an owned entity whose position left the owner cell triggers a handoff to the
  destination `CellCoord`.
- Migrate handshake over `ICellLink`: source serializes the entity's full authoritative component set (reuse the
  `KhaozEngine.Replication` codecs / a full per-entity capture), marks it **Migrating** (frozen, not simulated),
  sends to destination; destination deserializes, takes ownership, **acks**; source then **Releases** (drops it,
  or converts to a ghost if still within the destination's border overlap). Apply at tick boundaries.
- Stable identity across the move: the entity keeps its `NetId` (so clients/ghosts track it through the handoff).

## Acceptance (headless)
- An entity moved stepwise across the A→B boundary is owned by A before, by B after, and by **exactly one** cell
  at every tick in between (assert no tick has 0 or 2 owners).
- Its component state is intact after the handoff (values preserved through serialize→transfer→deserialize).
- Its `NetId` is unchanged across the move.
- A rapid back-and-forth across the boundary does not duplicate or drop it.

## Conventions
Worktree `feature/mmo-3c-handoff`. TDD headless — write the dup/loss invariant tests first. Hold per policy B.
Doc sweep if public API added. Delete this doc when merged.
