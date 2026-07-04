# KhaozEngine.Sharding

World topology for the authoritative-multiplayer stack: a uniform grid of authoritative **cells**, run in one
process.

- **`CellCoord`** - an integer cell coordinate. `CellCoord.FromWorld(x, y, cellSize)` floors a world position
  into a cell (mirrors `KhaozEngine.Replication.InterestGrid`'s cell math). Value type, usable as a dictionary
  key.
- **`CellSim`** - one authoritative cell: its own ECS `World`, a `FixedTickHost`, a `ServerReplicator`, and an
  `InterestGrid`. `Tick(elapsedSeconds)` advances the fixed-tick accumulator and steps the cell's ECS systems
  once per fixed tick.
- **`ShardHost`** - owns the `CellCoord -> CellSim` map, creates cells on demand, exposes `CellFor(x, y)` /
  `CoordFor(x, y)`, routes spawns to the cell containing a position (`SpawnAt`), and `Tick(elapsedSeconds)` ticks
  every live cell at one shared fixed rate. `EnsureCell(coord)` gets or creates a cell by coordinate, and the
  `CellCreated` event fires once per cell the first time its coordinate is instantiated (from `CellFor`,
  `SpawnAt`, a handoff destination, or `EnsureCell`) - the load hook a per-cell persistence layer subscribes to.

Per-cell persistence primitives on `CellSim`, storage-agnostic (no new dependency): `SnapshotOwned(excludedNetIds)`
returns a durable Replication snapshot of the cell's owned (not `Ghost`, not `Migrating`) entities whose NetId is
not in the excluded set, so a caller can persist non-player state while player entities persist separately.
`RestoreOwned(snapshot)` adopts a snapshot's entities back into the cell as freshly owned, keeping their NetIds,
and returns the restored NetId list. `MaxOwnedNetId()` reads the highest owned NetId (0 if none), useful for
resuming an id allocator. See `KhaozEngine.NetWorld.CellPersistence` for the `IWorldStore` wiring built on these.

**Per-channel components (since 9.28.0).** The three cross-cell/persistence consumers each serve one
`ReplicationChannels` channel, so a component only reaches the paths it declared (default `Replicate | Persist |
Migrate` = the pre-9.28.0 all-paths behaviour): `SnapshotOwned` captures the **Persist** channel, `ShardHost.ProcessHandoffs`
captures the **Migrate** channel, and `ShardHost.SyncGhosts` mirrors the **Replicate** channel with no owner (so a
mob's `Persist|Migrate`-only server state and a player's `OwnerOnly` private state are never ghosted - a ghost is a
read-only mirror served to OTHER cells' clients). `ShardHost.SnapshotForClient` serves the **Replicate** channel
owner-scoped to the client's own player. See `KhaozEngine.Replication.ReplicationChannels`.

Phase 3A of the seamless-shard topology: the in-process container. No cross-cell crossing or ghosting yet
(that's 3B/3C). Deterministic and headless - no sockets, no window, no GPU. Depends on `KhaozEngine.Ecs`,
`KhaozEngine.Simulation`, and `KhaozEngine.Replication`.

See `docs/superpowers/specs/2026-06-25-mmo-phase3-seamless-shard-design.md`.
