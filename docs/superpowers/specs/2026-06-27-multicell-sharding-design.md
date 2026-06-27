# Multi-cell server sharding design (sharded `WorldServer` over `ShardHost`)

Date: 2026-06-27
Status: approved design, ready for implementation plan
Program: MMO overworld render-scale track, sub-project 6b (finishes "6" with 6a streaming)

## Context

The engine is at `7.49.1`. The overworld track shipped terrain → walkable → forest → networked →
streaming (6a) → and the world now persists. The networked `NetWorld.WorldServer` is **single-`World`**:
one authoritative sim covering the whole walkable area, fine for a modest player count. This
sub-project gives it **scale**: run the authoritative world across a grid of cells with seamless
cross-cell ghosting and exactly-once handoff, so the world holds many players / a huge area without
one giant `World`. Together with 6a this completes the "big seamless world" (6).

Prior specs: terrain / walkable / prop-scatter / networked-overworld / world-streaming /
persistent-worldstore (all 2026-06-27). Program reference repo:
`https://github.com/levy-street/world-of-claudecraft`.

### This is integration, not new netcode — the sharding stack is built

- **`ShardHost`** (in-process host of the cell grid): owns `CellCoord -> CellSim`, `Tick`s every live
  cell at one shared rate (fanned across an opt-in `Scheduler`, default single-threaded), `SyncGhosts`
  mirrors border-overlap entities into neighbors as read-only `Ghost`s, `ProcessHandoffs` does
  exactly-once authority handoff (`Migrating` + `Migrate`/`MigrateAck`), `SpawnAt(x,y, out CellSim)`,
  `TryGetOwner(netId, out CellSim, out Entity)`. "The EVE / seamless-MMO topology run as a single
  process; a multi-process version is future."
- **`CellSim`** = one authoritative cell: its own `World` + `FixedTickHost` + `ServerReplicator` +
  `InterestGrid`, owning entities inside it plus read-only ghosts via `ApplyGhostSnapshot`.
- **`ICellLink`/`InProcessCellLink`** (GhostSync / Migrate / MigrateAck), **`Ghost`/`Migrating`** comps.
- **`MmoServerSample`** already runs this whole pattern end to end — but with a toy 2D `Position` and a
  bespoke protocol, NOT the overworld movement stack.
- The overworld uses `NetWorld.WorldServer` (single-`World`, `PlayerMoveSimulator`/`CharacterMovement`,
  the `MoveProtocol` the `WorldClient` already speaks).

So 6b reconciles them: a **sharded `WorldServer`** that runs the player-movement sim across `ShardHost`
cells, so the *existing* `WorldClient` walks across boundaries seamlessly and sees neighbor players.

### Locked decisions (from brainstorming)

1. **Server-transparent cells.** The server owns all cell logic; each client's **home cell serves it one
   unified AoI snapshot** (owned + ghosts), the shipped home-cell-serving model. The `WorldClient` is
   unchanged beyond tolerating its avatar's owning-cell changing on handoff (the `NetId` is stable
   across the migrate, so its replication/prediction continues).
2. **Single-process `ShardHost`** (`InProcessCellLink`). Multi-process / cross-machine cell distribution
   is future/out-of-scope.
3. **Persistence reuses `WorldPersistence`, player-keyed** (`player:{accountId}` works across cells:
   load-on-join spawns at the saved position in whatever cell contains it, save-on-leave from the
   owner). Per-*cell* world-state snapshots are deferred.

## Components

### Sharded server — `KhaozEngine.NetWorld` (beside `WorldServer`)

A `ShardedWorldServer` (or a sharded mode of `WorldServer`) that wires the overworld movement stack onto
`ShardHost` (cf. `MmoServerSample`, but with `PlayerMoveSimulator`/`CharacterMovement` and the
`MoveProtocol`):

- Construct a `ShardHost(cellSize, tickSeconds, registry, interestCellSize, overlapMargin,
  positionAccessor)` whose `positionAccessor` reads the player entity's XZ.
- On connect: `ShardHost.SpawnAt(spawn.x, spawn.z, out cell)`, assign the `NetId`, seed
  `PlayerMoveState` (from persistence load-on-join or default).
- Each tick: route each client's `MoveCommand` to the **owning** cell's `RemoteCommandQueue`
  (`TryGetOwner`), `ShardHost.Tick` steps every cell's `PlayerMoveSimulator` (ground-clamp via
  `TerrainCollision`; terrain is global analytic so any cell samples it), `SyncGhosts` refreshes border
  ghosts, `ProcessHandoffs` migrates players that crossed a boundary (exactly-once). Then serve each
  client the AoI snapshot from its **home cell** (owned + ghosts) framed in the existing `MoveProtocol`.
- Persistence: hook `WorldPersistence` at the server level (load-on-join / save-on-leave / periodic
  dirty), keyed `player:{accountId}`, backend-agnostic — unchanged across cells.

### Client — `WorldClient` unchanged (verify, don't rebuild)

The `WorldClient` keeps consuming AoI snapshots + predicting/reconciling its local avatar. The only
requirement is that its authoritative entity's `NetId` is **stable across handoff** so the replication
view + prediction continue without a respawn. Add a minimal re-anchor only if a gap is found; do not
introduce a cell concept into the client.

### Demo — sharded `NetworkedWalkServer`

The demo server becomes a multi-cell `ShardHost` (e.g. 3x3 cells, `cellSize` aligned to the terrain /
streaming chunk grid) over `TerrainPresets.Clearing()`. `NetworkedWalkSample` (the client) is unchanged.
Walk across a cell boundary → handed off with no hitch; two clients in adjacent cells see each other via
ghosting.

## Data flow

```
client input → owning cell RemoteCommandQueue (TryGetOwner)
   → ShardHost.Tick: each cell's PlayerMoveSimulator (ground-clamp) → SyncGhosts (border ghosts)
   → ProcessHandoffs (boundary crossers, exactly-once: Migrating/Migrate/MigrateAck, NetId stable)
   → home-cell AoI snapshot (owned + ghosts) → MoveProtocol → WorldClient.EntityRenderState[] (unchanged)
```

## Testing (headless, `InProcessCellLink` + `LoopbackTransport`)

- **Handoff exactly-once**: a player crossing a boundary migrates to the neighbor exactly once; `NetId`
  stable; position/velocity continuous (no teleport, no duplicate, no drop).
- **Ghosting**: two players adjacent across a border each see the other (the other appears as a ghost in
  their home cell's AoI); a player far from any border does not pull distant ghosts.
- **AoI from home cell**: a client's snapshot = owned + ghost neighbors within interest range.
- **Movement continuity across handoff**: authoritative `PlayerMoveState` is continuous through the
  migrate; client prediction does not snap.
- **Persistence across cells**: load-on-join spawns at the saved position (correct cell); save-on-leave;
  the restart-survival property still holds with the sharded server.
- **Multi-cell determinism**: `ShardHost.Tick` (single-threaded scheduler) is deterministic; an optional
  `ThreadPoolJobScheduler` run produces the same authoritative result.

## Scope

### In scope

- Sharded `WorldServer` over `ShardHost` in `NetWorld` (per-cell `PlayerMoveSimulator`, ghosting,
  exactly-once handoff, home-cell AoI, the existing `MoveProtocol`).
- `WorldClient` unchanged (or a minimal handoff re-anchor only if a real gap is found).
- `WorldPersistence` player-keyed across cells.
- Sharded `NetworkedWalkServer` demo (NxN cells over the terrain); `NetworkedWalkSample` unchanged.
- Headless tests (InProcessCellLink + Loopback).
- Release: **minor** bump (additive API in existing packages — likely no new package). Update
  `Directory.Build.props`, `CHANGELOG.md` + `CHANGENOTES.md`, the 3 guard declarations,
  `docs/USING-KHAOZENGINE.md` (a sharded-server usage section), `docs/CONSUMERS.md` if the Server
  umbrella description shifts.

### Out of scope (named so they are not forgotten)

- **Multi-process / cross-machine cell distribution** — `InProcessCellLink` only; a networked `ICellLink`
  is future.
- **Per-cell world-state snapshot persistence** — player records only here.
- **Dynamic cell spawn/despawn / load-based cell scaling** — fixed grid.
- **NPCs / creatures / combat / chat**, animation.

## Engine-first placement

- Sharded server → `KhaozEngine.NetWorld` (beside `WorldServer`), reusing `Sharding` + `Replication` +
  `Locomotion` + `WorldStore`. `WorldClient` stays as-is. Server umbrella already bundles `Sharding` +
  `NetWorld`, so a sharded game server is one umbrella reference (plus its `WorldStore.*` backend).

## Open items to confirm during implementation

- A new `ShardedWorldServer` type vs a sharded mode on `WorldServer` (prefer whichever keeps the single-
  `World` path intact and the client protocol identical).
- Verify `NetId` stability across `ProcessHandoffs` (the client view must survive the migrate); add the
  smallest re-anchor only if needed.
- `cellSize` vs the terrain/streaming chunk grid (whole-number ratio) and `overlapMargin` vs the client
  interest radius (ghost band must cover what a client can see across a border).
- How the connect-token account id routes to the owning cell on join (spawn cell from the loaded
  position).
- Threaded cell ticks via `ThreadPoolJobScheduler` as an opt-in (determinism parity tested).

## The overworld program (for orientation)

1-5 ✅ + 6a streaming ✅ (`7.43`-`7.48`) + persistence ✅ (`7.49`). **6b sharding - this spec - finishes 6.**
Remaining: glTF animation-clip playback → animated characters, the procedural dungeon generator, and PBR
splat textures + water.
