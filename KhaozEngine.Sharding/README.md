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

**NetId is 64-bit (since 10.0.0).** Every `netId` here is a `long` (was a 32-bit `int`): the owned index, the
`ShardHost` netId -> cell map, the AoI interest sets, and the handoff/ghost path all carry it, and the inter-cell
`MigrateAck` payload grew from a 4-byte to an 8-byte netId. See `KhaozEngine.Replication.NetIdAllocator` for the
node-prefix allocation scheme.

**Ownership lookup is O(1) (since 9.31.0).** `ShardHost.TryGetOwner(netId, out cell, out entity)` and
`CellSim.TryGetOwned(netId, out entity)` resolve the cell/entity that authoritatively owns a NetId in O(1) off a
maintained netId -> (cell, entity) index, not a linear scan across every cell - so calling them per player and per
NPC per tick stays flat as the world grows. Spawn owned entities through `ShardHost.SpawnOwned(x, y, netId, out cell)`
(spawn + assign `NetId` + register in one step) so they are eagerly indexed. `CellSim.RegisterOwned(netId, entity)`
and `UnregisterOwned(netId)` register/drop an owned entity managed through the raw `World` directly. The index is
self-healing: a lookup miss falls back to a scan behind the index (so the raw `SpawnAt` + `World.Set(new NetId(..))`
idiom without a register still resolves) and a stale entry (out-of-band despawn, or the entity became a ghost /
started migrating) is reaped on lookup. `OwnerCount(netId)` is deliberately an independent from-scratch scan, the
exactly-once handoff oracle that can observe a duplicate (2) or loss (0) the single-valued index never could.

Per-cell persistence primitives on `CellSim`, storage-agnostic (no new dependency): `SnapshotOwned(excludedNetIds)`
returns a durable Replication snapshot of the cell's owned (not `Ghost`, not `Migrating`) entities whose NetId is
not in the excluded set, so a caller can persist non-player state while player entities persist separately.
`RestoreOwned(snapshot)` adopts a snapshot's entities back into the cell as freshly owned, keeping their NetIds,
and returns the restored NetId list. `TryRestoreOwned(snapshot)` (since 9.33.0) is the non-throwing form returning
a `CellRestoreResult`: a blob that fails to decode is rolled back (the partial apply is despawned, so the cell is
left empty and the caller can quarantine the bytes) rather than throwing, and an extension frame whose id this
cell's registry does not know is retained per-netId and re-emitted verbatim by `SnapshotOwned` (retain-and-rewrite),
so a registry downgrade cannot strip data at rest. `MaxOwnedNetId()` reads the highest owned NetId (0 if none),
useful for resuming an id allocator. See `KhaozEngine.NetWorld.CellPersistence` for the `IWorldStore` wiring
(migration chain, quarantine, diagnostics) built on these.

**Per-channel components (since 9.28.0).** The three cross-cell/persistence consumers each serve one
`ReplicationChannels` channel, so a component only reaches the paths it declared (default `Replicate | Persist |
Migrate` = the pre-9.28.0 all-paths behaviour): `SnapshotOwned` captures the **Persist** channel, `ShardHost.ProcessHandoffs`
captures the **Migrate** channel, and `ShardHost.SyncGhosts` mirrors the **Replicate** channel with no owner (so a
mob's `Persist|Migrate`-only server state and a player's `OwnerOnly` private state are never ghosted - a ghost is a
read-only mirror served to OTHER cells' clients). `ShardHost.SnapshotForClient` serves the **Replicate** channel
owner-scoped to the client's own player. See `KhaozEngine.Replication.ReplicationChannels`.

**Serve-epoch interest sharing (perf).** `ShardHost.HomeInterest`/`SnapshotForClient` take an optional trailing
`serveEpoch`. Passed a value, the home cell's `InterestGrid` rebuild is shared across every call at that epoch
(one rebuild per served cell per tick instead of once per client). Omitted (the default, `null`), each call
rebuilds unconditionally, the contract a direct caller or test relies on right after a world mutation.
`ShardedWorldServer.Tick` bumps a fresh epoch once per tick and passes it to both the delta and snapshot serve
paths.

**Indexed snapshots (perf).** The filtered `SnapshotWriter` calls on the hot cross-cell and serve passes
(`SyncGhosts` ghost mirroring, `SnapshotForClient` non-delta fallback, `ProcessHandoffs` crossing capture) resolve
their net-id sets off a per-tick, per-world `WorldSnapshotIndex` (shared across a cell's target neighbours and the
clients homed in it, keyed to the serve epoch) instead of scanning the whole cell world per call, and encode through
one reused `SnapshotScratch` stream. The wire is byte-identical to the full-scan path; only the per-call
`O(worldPop)` scan and stream allocation are removed.

Phase 3A of the seamless-shard topology: the in-process container. No cross-cell crossing or ghosting yet
(that's 3B/3C). Deterministic and headless - no sockets, no window, no GPU. Depends on `KhaozEngine.Ecs`,
`KhaozEngine.Simulation`, and `KhaozEngine.Replication`.
