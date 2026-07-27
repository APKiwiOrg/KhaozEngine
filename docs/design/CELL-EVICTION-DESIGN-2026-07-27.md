# Cell eviction: persist-then-evict

Rationale for the shard-cell eviction path ([#336](https://github.com/APKiwiOrg/KhaozEngine/issues/336)), engine
side of the Ruinborne 100 km world program. Shipped API and usage live in `CHANGELOG.md`,
`docs/USING-KHAOZENGINE.md` and the `KhaozEngine.Sharding` / `KhaozEngine.NetWorld` READMEs. What stays here is the
why.

## The problem

`ShardHost.GetOrCreateCell` inserts a `CellSim` into the cell map and the creation-ordered list, and nothing ever
removed from either. Harmless on a 2x2 grid, an unbounded leak on a large world where players roam: every visited
cell keeps its `World`, `ServerReplicator` and `InterestGrid` alive for the life of the process. The persist half
already existed in the right shape (`CellPersistence.SaveDirtyPass` snapshots dirty cells and batches the store
write), so the missing piece was removal, plus everything that has to be true around it.

## Layering: where each half lives

`KhaozEngine.Sharding` does not reference `KhaozEngine.NetWorld`, and the store lives in `NetWorld`. So the split is
forced and clean:

- **Sharding owns the mechanical removal and the policy seam.** `ShardHost.RemoveCell` / `CanRemoveCell` /
  `CellRemoved`, plus `ICellEvictionPolicy` over a `CellEvictionSignals` struct. Sharding stays storage-agnostic and
  never sees a byte.
- **NetWorld owns the orchestration.** `CellEvictor` snapshots through `CellPersistence`, waits for the write, and
  only then calls the host.

The policy interface deliberately went to Sharding rather than NetWorld even though only NetWorld drives it. "Which
cells are disposable" is a statement about the cell grid, expressible entirely in Sharding vocabulary, and a
consumer running its own `ShardHost`-based server should be able to write a policy without taking a NetWorld
dependency.

## Decisions

### 1. The persist gate is a real gate, not an ordering convention

Nothing is removed until the write for exactly those bytes has completed. The eviction is a two-phase operation
spanning frames: `RequestEvict` snapshots and dispatches, a later `Update` finalizes. Considered and rejected:
snapshot-and-remove in one call with a fire-and-forget save. It reads simpler and is wrong on any store that can
fail, which is every real one. The current shape means a store outage degrades to "cells stay loaded", which is the
old behaviour, rather than to data loss.

The cell keeps simulating while the write is in flight, so finalize re-verifies before removing:

- the owned entity count still matches, which catches a joined player's entity crossing into or out of the cell:
  `SnapshotCell` excludes player NetIds from the bytes (player state persists on its own record), so an arrival or
  departure of one moves the owned count without moving a single byte of the snapshot,
- the fresh snapshot is byte-identical to what was written,
- the host still permits removal.

Any mismatch abandons the eviction and leaves the cell alone. A later scan retries. Freezing the cell for the
duration of the write was the alternative, and it buys a marginally higher eviction hit rate for a new
"suspended cell" state that every other pass (tick, handoff, ghost sync, serving) would have to understand. Not
worth it.

### 2. Recreation restores synchronously, out of an in-memory cache

The hard requirement is that an evicted coordinate never comes back blank, and the case that forces it is a handoff:
`ProcessHandoffs` calls `GetOrCreateCell(dest)` and then adopts the migrating entity into it in the same pass. The
existing restore path is asynchronous (a store load applied on a later `Update`), so a coordinate recreated that way
would tick, serve clients, and adopt a migrant while empty, and the resident entities would pop back in some frames
later.

`ShardHost` raises `CellCreated` synchronously inside the create call, before the new cell can tick or receive a
migrate. That is the only hook with the right timing, so `CellEvictor` subscribes to it and restores there, from
bytes it kept in memory when it evicted the cell. The bytes are already durable, so the cache is purely about
immediacy and can be dropped at any time.

It is bounded (`MaxCachedSnapshots`, default 1024 cells) because an unbounded cache would re-introduce the very leak
this feature exists to fix, just smaller. Past the bound the coordinate falls back to the ordinary asynchronous
load, which is exactly what a cold cell does after a restart. The fallback is a real regression in seamlessness for
that one coordinate and it is the honest trade: the alternative is either an unbounded cache or a synchronous
blocking store read on the server thread, and blocking the tick on SQL is worse than a few frames of empty cell in a
region nobody has visited in the last thousand evictions.

**Exactly one restore path is armed per evicted coordinate.** `CellPersistence` loads a cell at most once per
coordinate (a `loadRequested` set). Caching an evicted snapshot deliberately LEAVES that marker in place, which is
what stops the driver from also restoring the same cell from the store and duplicating every entity. Dropping a
coordinate from the cache is therefore always paired with `CellPersistence.ForgetCell`, which clears the marker and
re-arms the store-backed path. This invariant is the single most breakable thing in the design and is stated in both
implementations.

### 3. What eviction persists is what a restart persists

The cell snapshot captures the `ReplicationChannels.Persist` channel. A component that did not declare `Persist`
does not survive an unload, exactly as it does not survive a shutdown. Widening eviction to capture more than a
restart would have meant a second blob format and a second restore path, and it would still be lossy across the
restart that eviction is modelled on. Consumers already reason about "what survives a restart", so eviction reuses
that answer rather than minting a second one.

The corollary is that a joined player's entity is never evictable: player state persists on its own record through
`WorldPersistence` and is explicitly excluded from cell snapshots, so unloading a cell holding one would destroy it.
That is enforced twice, deliberately. `ShardHost.CanRemoveCell` refuses a cell owning a bound client's player (the
Sharding-level truth, so a custom host cannot lose a player by forgetting to check), and `ShardedWorldServer` pins
the same cells off its own joined-slot table (the server-level truth). Both are cheap and they fail closed.

### 4. Refusal is a first-class outcome

`RemoveCell` also refuses a cell with an entity `Migrating` out of it and a cell with undrained inter-cell traffic.
Both would strand an entity mid-handshake. The second needed a new `ICellLink` member, `HasPending`, which
**defaults to `true`**. A default of `false` reads more natural and is the wrong way round: a custom link that has
not implemented it would silently permit lossy evictions. Defaulting to true means such a link simply never evicts,
which is visible, diagnosable, and not destructive. `Forget` is the paired hygiene call and defaults to a no-op,
because failing to drop a queue entry costs a dictionary slot rather than an entity.

### 5. Idle time is tracked by the driver, not the host

`CellEvictionSignals` carries `IdleSeconds`, but `ICellEvictionHost.TryReadEvictionSignals` leaves it at zero and the
driver fills it in via `WithIdleSeconds`. The host knows instantaneous facts (who is where, what a cell owns), while
idle time is a history the driver already has to keep for its scan cadence. Asking every host implementation to
track it too would duplicate state that can silently disagree.

Idle time advances in scan-sized steps rather than per tick, so the scan is `O(live cells x online players)` every
`ScanIntervalSeconds` (default 10) instead of every frame. A threshold finer than the scan interval therefore rounds
up to it, which is stated in the config docs.

### 6. The tick buffer bug the feature exposed

`ShardHost.Tick` reuses a fan-out buffer and refreshed it only when the cell COUNT changed, which was sound while the
cell list was append-only. With eviction, one cell can be unloaded and another created between two ticks, leaving the
count identical and the contents different: the evicted cell would keep ticking and the new one would never start.
Replaced with a version counter bumped on every create and every remove. This is a latent-correctness fix that only
eviction could surface, and it has its own test.

## Not done here

- **Issue [#135](https://github.com/APKiwiOrg/KhaozEngine/issues/135)** (an out-of-band despawn leaks an `ownerCell`
  entry) is adjacent and untouched. Eviction does not make it worse: it sweeps the evicted cell's owned index, which
  contains any stale entry for that cell, so a leaked entry belonging to an evicted cell is cleaned up as a side
  effect.
- **The `MmoServerSample` is not wired to a `CellEvictor`.** The sample's world is 2x2 and would never evict, so it
  would document the wiring without exercising it. `docs/USING-KHAOZENGINE.md` carries the wiring instead.
- **Cross-process eviction.** A networked `ICellLink` deployment has to decide which node owns a coordinate before it
  can decide whether to unload it. Out of scope, and the `HasPending` default keeps such a link safe meanwhile.
