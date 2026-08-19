# KhaozEngine.Sharding

World topology for the authoritative-multiplayer stack: a uniform grid of authoritative **cells**, run in one
process.

- **`CellCoord`** - an integer cell coordinate. `CellCoord.FromWorld(x, y, cellSize)` floors a world position
  into a cell (mirrors `KhaozEngine.Replication.InterestGrid`'s cell math). Value type, usable as a dictionary
  key.
- **`CellSim`** - one authoritative cell, and one SIMULATION ISLAND: its own ECS `World`, its own optional
  `IPhysicsWorld` (`Physics`), its own island `Frame`, a `FixedTickHost`, a `ServerReplicator`, and an
  `InterestGrid`. `Tick(elapsedSeconds)` advances the fixed-tick accumulator and steps the cell's ECS systems
  once per fixed tick.
- **`ShardHost`** - owns the `CellCoord -> CellSim` map, creates cells on demand, exposes `CellFor(x, y)` /
  `CoordFor(x, y)`, routes spawns to the cell containing a position (`SpawnAt`), and `Tick(elapsedSeconds)` ticks
  every live cell at one shared fixed rate. `EnsureCell(coord)` gets or creates a cell by coordinate, and the
  `CellCreated` event fires each time a coordinate is instantiated (from `CellFor`, `SpawnAt`, a handoff
  destination, or `EnsureCell`), including a recreate after `RemoveCell` - the load hook a per-cell persistence
  layer subscribes to.

**Island frames (since the floating-origin major).** A frame is a property of a SPACE, and a physics world IS a
space, so a cell owns both or neither. Built with `frameAnchoring: true`, `ShardHost` gives each cell the frame
nearest its CENTRE (`FrameFor(coord)`, pure, so a physics factory can call it), fixed for the cell's life - which
is why a shard host performs no runtime rebase at all. Built with a `physicsFactory`, it calls that factory once
per cell at creation, before the cell can tick or receive an entity, and disposes the returned world with the cell.
The factory's world must hold every static within `cellSize / 2 + overlapMargin` of the cell centre, expressed
against an `Origin` of `FrameFor(coord).Anchor`, and must belong to that one cell.

- **`IslandFrame`** is the singleton component carrying a world's frame, on a reserved entity with no `NetId` (so
  replication, persistence, interest rebuilds, ghosting and handoff all skip it). Read it with
  `world.GetIslandFrame()`, publish it with `world.SetIslandFrame(frame)`. It is how anything handed only a
  `World` - a pickup spawn callback, a consumer's per-tick brain, a cross-border ghost reader - reaches the frame
  without a new parameter on four separate seams. Absent means `WorldFrame.Origin`, which is correct for every
  unframed cell and every plain test world.
- **`ICellFrameAdapter`** is the seam that re-expresses an entity ARRIVING in a cell into that cell's frame, at
  every door: `AdoptFromMigrate` (a handoff), `ApplyGhostSnapshot` (a border mirror), `TryRestoreOwned` (a
  persistence restore). This package owns the topology and knows nothing about the position component, so the
  layer that does supplies the conversion (`KhaozEngine.NetWorld` wires one on every cell). Null means no
  conversion, which is what a plain unframed cell wants.

**Cells unload as well as load.** `ShardHost.RemoveCell(coord)` takes a cell out of the grid: it stops ticking,
stops being ghosted into and out of, and every entity it owned ceases to exist. `CanRemoveCell(coord)` is the same
gate as a query, and refuses a cell that is mid-handoff (an entity is `Migrating` out of it), one with undrained
inter-cell traffic on the `ICellLink`, and one owning the player entity of a client bound through `BindClient`.
Removal drops every trace the cell would otherwise leave behind: its entries in the netId -> cell ownership index,
the ghosts each neighbour mirrored from it (and the neighbour's now-unrefreshable view of it, via
`CellSim.RemoveGhostView`), the cached per-world serve index, and its link inbox. `CellRemoved` is the mirror of
`CellCreated` for anything a subscriber keyed per cell, and `CellCreated` fires again when the coordinate is next
instantiated, with a genuinely fresh `CellSim`.

**The host has no persistence of its own, so a cell removed here is simply gone.** Persisting first is the caller's
job: `KhaozEngine.NetWorld.CellEvictor` is the driver that snapshots a cell, waits for the store write to land, and
only then calls `RemoveCell` - plus restores the coordinate on recreation. The policy side lives here and is the
game's to replace: `ICellEvictionPolicy.ShouldEvict(in CellEvictionSignals)` sees the cell's owned entity count, how
many clients are homed in it, the Chebyshev cell distance to the nearest one (`int.MaxValue` when nobody is online),
whether the host pins it, and how long it has gone unattended. The shipped `IdleCellEvictionPolicy` unloads a cell
with no client homed in it, none within `KeepRadius` cells (default 2, so the ghost-neighbour ring feeding a client's
area of interest is never pulled out from under it), after `IdleSeconds` (default 300).
`ShardHost.CollectBoundPlayerCells(list)` is the raw "where is everyone" signal behind that.

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
returns a durable Replication snapshot of the cell's owned (not `Ghost`, not `Migrating`, not `Transient`) entities
whose NetId is not in the excluded set, so a caller can persist non-player state while player entities persist
separately.
`RestoreOwned(snapshot)` adopts a snapshot's entities back into the cell as freshly owned, keeping their NetIds,
and returns the restored NetId list. `TryRestoreOwned(snapshot)` (since 9.33.0) is the non-throwing form returning
a `CellRestoreResult`: a blob that fails to decode is rolled back (the partial apply is despawned, so the cell is
left empty and the caller can quarantine the bytes) rather than throwing, and an extension frame whose id this
cell's registry does not know is retained per-netId and re-emitted verbatim by `SnapshotOwned` (retain-and-rewrite),
so a registry downgrade cannot strip data at rest. `MaxOwnedNetId()` reads the highest owned NetId (0 if none),
useful for resuming an id allocator. See `KhaozEngine.NetWorld.CellPersistence` for the `IWorldStore` wiring
(migration chain, quarantine, diagnostics) built on these.

**`Transient`: the per-entity persist opt-out (since 17.37.1).** A field-less tag, beside `Ghost` and `Migrating`,
meaning this entity is never saved. `SnapshotOwned` leaves it out of the blob entirely, so a server-owned thing meant
to outlive nothing (a world pickup, a timed spawn, a projectile) cannot be caught in an interval save and resurrected
on restart as a husk no subsystem is tracking. It excludes the ENTITY, which is the axis a `ReplicationChannels` flag
cannot reach: a channel gates one component TYPE on one channel, and dropping a component's bytes would still persist
the entity, just as a stripped husk. Deliberately in no `ReplicationRegistry`, since persistence is a server-local
decision no client needs to hear, so it spends no wire type id, adds no bytes to any snapshot, and moves no blob
layout. `ShardHost.ProcessHandoffs` carries the mark across a crossing (it rides beside the Migrate capture rather
than inside it), so a transient entity walking into the next cell does not become persistable there. What it cannot
do is edit a blob already written: husks in older saves still need a one-time boot sweep.

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
one reused `SnapshotScratch` stream. The wire is byte-identical to the full-scan path. Only the per-call
`O(worldPop)` scan and stream allocation are removed.

**The per-cell fan-out runs in the canonical FP environment.** `Tick` wraps each cell's step in a
`DeterministicFpScope`, because `DeterministicFp` pins the floating-point control register per THREAD and a
scheduler fanning cells across the thread pool runs them on arbitrary workers whose register is whatever the pool
last left it at. `ThreadPoolJobScheduler` installs the same scope around its own worker bodies, so a consumer's own
`For()` call site is covered too, and entering twice is harmless.

Phase 3A of the seamless-shard topology: the in-process container. No cross-cell crossing or ghosting yet
(that's 3B/3C). Deterministic and headless - no sockets, no window, no GPU. Depends on `KhaozEngine.Ecs`,
`KhaozEngine.Simulation`, `KhaozEngine.Physics` (the seam a cell's own world is typed as) and
`KhaozEngine.Replication`.
