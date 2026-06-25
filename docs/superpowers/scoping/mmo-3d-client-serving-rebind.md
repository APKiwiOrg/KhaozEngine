# Scoping — MMO 3D: Client home-cell serving + seamless re-bind

**Status:** TO-DO. **Depends on:** 3C (handoff). **Fresh-chat kickoff:**
*"Execute `docs/superpowers/scoping/mmo-3d-client-serving-rebind.md`."*

## Read first
Phase 3 design spec ("Client home-cell serving"), `CLAUDE.md`, `MMO-EXECUTION-ORDER.md` (policy B). Builds on
the Phase 1 session layer (`NetServer`) + replication.

## Goal
Serve each client its full area-of-interest from a single **home cell** (the cell owning the player), relying on
the invariant **overlap margin ≥ max client interest radius** (so the home cell already holds, as ghosts,
everything within the player's interest). When the player crosses a boundary, **re-bind** the client to the new
home cell seamlessly — no gap, no missing surroundings.

## Deliverable (in `KhaozEngine.Sharding`)
- Per-client home-cell tracking: a client (session slot from `NetServer`) is bound to the cell owning its player
  entity. Replication to that client = the home cell's `ServerReplicator` filtered by the client's interest
  (`SnapshotWriter.WriteFiltered` / delta) over owned + ghost entities.
- Enforce/validate the invariant: overlap margin ≥ interest radius (assert/throw on misconfig so the home-cell
  guarantee holds).
- Seamless re-bind: when the player hands off (3C) to a new cell, switch the client's serving cell. The new home
  cell already has the player's surroundings (it had them as ghosts pre-crossing), so the client sees no gap.

## Acceptance (headless)
- A client near a border receives entities from across the border (ghosts in its home cell) in its interest set.
- After the player crosses A→B, the client is served by B, and the entities around the player are continuous
  across the crossing (nothing in-interest disappears then reappears).
- Misconfiguring overlap < interest radius is rejected (the home-cell guarantee can't silently break).

## Conventions
Worktree `feature/mmo-3d-client-serving`. TDD headless. Hold per policy B. Doc sweep if public API added.
Delete this doc when merged.
