# Per-cell world-state persistence — design

Persist the authoritative, non-player state a `ShardHost` cell owns (mobs, resource nodes, dropped items,
doors — any non-player replicated entity) into an `IWorldStore`, keyed per cell, so a sharded world survives a
server restart. Mirrors the shipped player-keyed `WorldPersistence`, but keyed by cell coordinate instead of
account id.

Roadmap item: "Overworld / world content → Per-cell world-state snapshot persistence: persist cell/world
state, not just player records (pairs with sharding)."

## Context (what exists today)

- `KhaozEngine.Sharding.ShardHost` owns a `CellCoord -> CellSim` grid. Each `CellSim` wraps its own ECS
  `World` of entities carrying a `NetId` plus registered replicated components. A cell *owns* (authoritatively
  simulates) the entities whose position falls inside it; it also holds read-only `Ghost` mirrors from
  neighbours and `Migrating` entities mid-handoff.
- `KhaozEngine.Replication` already serializes a cell's world: `SnapshotWriter.WriteFiltered(world, registry,
  netIds)` writes `[count]{ netId, {typeId,data}*, 0 }*`, and `ClientReplicationView.Apply(world, snapshot)`
  reconstructs those entities into a world. This is exactly the codec cells use for ghosting + migrate. `CellSim.
  AdoptFromMigrate` already restores entities into a cell's world as *owned* via a throwaway `ClientReplicationView`.
- `KhaozEngine.NetWorld.WorldPersistence` is the reference wiring pattern: it drives an `IWorldStore` off an
  `IWorldPersistenceHost` (load-on-join, save-on-leave, periodic dirty snapshot, `FlushAsync`), applying async
  loads on the server thread inside `Update`.
- `KhaozEngine.NetWorld.ShardedWorldServer` is the multi-cell server. It allocates `NetId`s from an in-memory
  `nextNetId` counter starting at 1 (**not persisted**), tracks player NetIds in `netIdBySlot`, and today spawns
  **only players** — there are no NPC/mob entities in the netcode stack yet.

**Consequence:** this is infrastructure landing ahead of the content that fills it. The deliverable is the seam
that persists cell-owned world entities, proven with a small non-player fixture entity and wired into
`MmoServerSample`. Players are out of scope (already persisted player-keyed; a player belongs to whoever logs
in, not to a cell).

## Goals

1. Persist each cell's owned, non-player, non-ghost, non-migrating entities to `IWorldStore` under a stable
   per-cell key, and restore them when that cell is (re)created.
2. Reuse the Replication snapshot codec — no new serialization format. Any registered component persists
   automatically.
3. Survive a full server restart: restored entities keep their `NetId`s, and the allocator resumes above the
   highest persisted id so a freshly spawned player can never collide with a restored entity.
4. Same operational shape as `WorldPersistence`: periodic dirty save, shutdown flush, server-thread application
   of async loads, backend-agnostic (`InMemory` / `Sqlite` / `SqlServer`).
5. Headless-testable end to end, and demonstrably real in `MmoServerSample`.

## Non-goals

- Persisting players (already `WorldPersistence`), ghosts, or migrating entities.
- Idle-cell eviction / memory reclaim (a separate feature; this design leaves a clean hook for it).
- Cross-cell entity references or a relational schema. `IWorldStore` stays a keyed blob KV.
- Persisting transient per-tick state (change sets, event buffers) — only the durable component snapshot.

## Design

### Layering

Two packages, matching how `WorldPersistence` splits from the sharding core:

- **`KhaozEngine.Sharding`** gains the snapshot/restore *primitives* (it already deps `Replication`). No
  `IWorldStore` dependency — the sharding core stays storage-agnostic.
- **`KhaozEngine.NetWorld`** gains `CellPersistence` — the `IWorldStore` wiring, dirty-tracking, timing, and the
  NetId high-water record. `NetWorld` already deps both `Sharding` and `WorldStore`.

### Sharding primitives

On `CellSim`:

- `byte[] SnapshotOwned(IReadOnlySet<int> excludedNetIds)` — Replication snapshot of this cell's entities that
  are owned (not `Ghost`, not `Migrating`) and whose `NetId` is not in `excludedNetIds`. Empties to the 4-byte
  zero-count snapshot when nothing qualifies. Implemented by collecting the qualifying NetIds and calling
  `SnapshotWriter.WriteFiltered`.
- `IReadOnlyList<int> RestoreOwned(byte[] snapshot)` — adopts the snapshot's entities into this cell's world as
  freshly owned entities (throwaway `ClientReplicationView`, exactly like `AdoptFromMigrate`), returning the
  restored NetId values. Skips a NetId already present/owned (idempotent re-load safety).
- `int MaxOwnedNetId()` — the largest owned `NetId` in the cell (0 if none), for the high-water computation.

On `ShardHost`:

- `event Action<CellSim>? CellCreated` — fired inside `GetOrCreateCell` the first time a coordinate's cell is
  instantiated (lazily, on demand — a player entering, a ghost target, a handoff destination). This is the load
  hook. Firing once per cell means restore happens exactly once.

### NetWorld: `CellPersistence`

Constructed as `CellPersistence(ICellPersistenceHost host, IWorldStore store, CellPersistenceConfig? config)`,
mirroring `WorldPersistence`. Never touches `ShardHost` directly — it drives the small host seam, so it is
unit-testable with a fake host (no real shard grid).

```
public interface ICellPersistenceHost
{
    // Fired when a cell is first instantiated: CellPersistence enqueues its load here.
    event Action<CellCoord>? CellCreated;

    // Coords of all live cells (for the periodic dirty pass + flush).
    IReadOnlyCollection<CellCoord> LiveCellCoords { get; }

    // Snapshot a cell's persistable (owned, non-player, non-ghost, non-migrating) entities. Null if the cell is gone.
    byte[]? SnapshotCell(CellCoord coord);

    // Restore entities into a cell (applied on the server thread). Returns restored NetIds.
    IReadOnlyList<int> RestoreCell(CellCoord coord, byte[] snapshot);

    // NetId high-water: read the current allocator value; raise it so it never dips below `atLeast`.
    int NextNetId { get; }
    void EnsureNextNetIdAtLeast(int atLeast);
}
```

`ShardedWorldServer` implements `ICellPersistenceHost` (alongside `IWorldPersistenceHost`):

- `CellCreated` re-exposes `ShardHost.CellCreated` (mapping `CellSim -> Coord`).
- `SnapshotCell` looks up the `CellSim` and calls `SnapshotOwned(playerNetIds)`, where `playerNetIds` is the set
  from `netIdBySlot.Values` (players excluded).
- `RestoreCell` looks up/creates the cell and calls `RestoreOwned`.
- `NextNetId` returns the counter; `EnsureNextNetIdAtLeast` bumps `nextNetId = Math.Max(nextNetId, atLeast)`.

### Persisted blob format (version guard)

A cell record is a small header followed by the raw Replication snapshot:

```
[int32 magic 'KCP1'][int32 schemaVersion][Replication snapshot bytes]
```

- `schemaVersion` is game-supplied (`CellPersistenceConfig.SchemaVersion`, default 1). The Replication snapshot
  is self-describing per component (`ushort` typeId prefixes), so adding a *new* component type is
  backward-safe. Changing an *existing* component's binary layout under the same typeId is not — the game bumps
  `SchemaVersion` when it does.
- On load, a mismatched `magic` or `schemaVersion` is treated as "no usable save": the record is skipped and
  logged via the injected diagnostics sink (the cell comes up empty rather than mis-decoding). A future
  migration hook can slot in here; not built now.

The NetId high-water is a separate record under `world:meta` (a tiny JSON `{ nextNetId }`), loaded once at
startup before any cell load applies, so restored entities resume the counter.

### Lifecycle

- **Load (lazy, per cell):** `CellCreated(coord)` -> async `store.LoadAsync("cell:{x}:{y}")` -> on success,
  enqueue a `(coord, snapshot)` restore. `Update(dt)` drains the queue on the **server thread** and calls
  `host.RestoreCell`, then `host.EnsureNextNetIdAtLeast(maxRestoredNetId + 1)`. Never restores from a background
  continuation (same discipline as `WorldPersistence`).
- **Startup preload (optional):** `PreloadAsync()` — if the store `is IEnumerableWorldStore`, enumerate
  `cell:*` keys and touch each coord so its cell instantiates (firing `CellCreated` -> load). Lets NPCs exist
  before any player enters a region. No-op on a non-enumerable store.
- **Periodic dirty save:** every `SaveIntervalSeconds`, for each live cell compare the current `SnapshotCell`
  bytes to the last-saved bytes (per-cell dirty comparison, like `WorldPersistence`'s per-player one) and
  `SaveAsync` the changed ones. Also persists the `world:meta` high-water when `NextNetId` advanced.
- **Shutdown:** `FlushAsync()` awaits in-flight loads/saves, applies any queued restores, then does a final
  dirty pass + high-water save, reaching a quiescent fully-persisted point.

### `CellPersistenceConfig`

- `SaveIntervalSeconds` (default 30) — max data loss on a crash.
- `CellKeyPrefix` (default `"cell:"`) — stored key is `{prefix}{x}:{y}`.
- `MetaKey` (default `"world:meta"`) — NetId high-water record key.
- `SchemaVersion` (default 1) — blob header version; bump on a breaking component-layout change.

### Diagnostics

A skipped/mismatched load and each save pass log through the existing engine diagnostics seam (injected, no
hard dependency), so a bad save or a schema mismatch is visible rather than silent.

## Isolation / testability

- `CellSim` primitives are pure ECS + Replication (headless, deterministic). Unit-tested directly:
  snapshot-excludes-players, snapshot-excludes-ghosts/migrating, restore-round-trips, MaxOwnedNetId.
- `CellPersistence` is tested against a **fake `ICellPersistenceHost`** + `InMemoryWorldStore` — no real
  `ShardHost` — proving load-on-create, periodic dirty save, high-water restore, and `FlushAsync` quiescence.
- One integration test over a real `ShardHost` + `ShardedWorldServer`: spawn a fixture non-player entity in a
  cell, save, drop the host, rebuild from the same store, assert the entity is back with its NetId and a new
  player spawns above it (no collision).

## Test plan (headless)

1. `SnapshotOwned` excludes player NetIds, ghosts, and migrating entities; includes owned non-player entities.
2. `RestoreOwned` round-trips a fixture component (position + a custom field) exactly; is idempotent on re-load.
3. `MaxOwnedNetId` returns the max, 0 for an empty cell.
4. `ShardHost.CellCreated` fires exactly once per coord, on first instantiation, for all creation paths
   (`CellFor`, `SpawnAt`, handoff destination).
5. `CellPersistence` load-on-create applies restored entities on the server thread (via `Update`), not before.
6. Periodic dirty save writes only changed cells; unchanged cells are not re-saved.
7. NetId high-water: after restore, `NextNetId > maxRestoredNetId`; a fresh spawn gets a non-colliding id.
8. Blob header: a wrong magic / schemaVersion is skipped (cell comes up empty, logged), not mis-decoded.
9. `FlushAsync` reaches quiescence: all saves durable, queue drained.
10. Integration: fixture entity survives a full host rebuild from a shared `InMemoryWorldStore`.

## `MmoServerSample` wiring

Register a small non-player fixture entity type (e.g. a `ResourceNode { Kind, Amount }` replicated component) in
the sample's registry, spawn a few into cells at startup, construct `CellPersistence` over the sample's world
store, call `PreloadAsync` at boot and `Update` each tick, `FlushAsync` on shutdown. Demonstrates restart
survival with a concrete entity.

## Docs to update on ship (full-sweep)

- `README.md` package table: note `Sharding` gains cell snapshot/restore primitives; `NetWorld` gains
  `CellPersistence`.
- `KhaozEngine.Sharding/README.md` and `KhaozEngine.NetWorld/README.md`: new public API.
- `docs/USING-KHAOZENGINE.md`: a "Per-cell world persistence" section next to the player-persistence one.
- `docs/DEPENDENCY-SEAMS.md`: the `CellPersistence -> IWorldStore` + `ICellPersistenceHost` seam.
- `docs/ROADMAP.md`: delete the shipped "Per-cell world-state snapshot persistence" bullet.
- `CHANGELOG.md` + `<KhaozEngineVersion>` bump (additive minor) + the three guard-checked version declarations.
- `docs/CONSUMERS.md`: engine-version line.
</content>
</invoke>
