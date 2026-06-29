# KhaozEngine.NetWorld

Render-free networked-world layer that wires the [KhaozEngine.Locomotion](../KhaozEngine.Locomotion)
movement core to the authoritative netcode stack ([Netcode](../KhaozEngine.Netcode) +
[Replication](../KhaozEngine.Replication)).

- **`PlayerMoveSimulator`** (`ITickSimulator`) runs `CharacterMovement.Step` both server-authoritatively
  and inside client prediction, so the two stay in lockstep.
- **`WorldServer`** is a single-`World` authoritative movement server: a `NetServer` session layer spawns
  one player entity per connection, drains that client's queued `MoveCommand` each tick, runs the ground-
  clamped sim, and serves each client a per-area-of-interest snapshot (`SnapshotWriter.WriteFiltered` over
  an `InterestGrid`) prefixed with that client's net id + last-acked move seq.
- **`ShardedWorldServer`** (+ `ShardedWorldServerConfig`) runs that same movement stack across a
  [`KhaozEngine.Sharding`](../KhaozEngine.Sharding) `ShardHost` grid of cells, so the world scales past a single
  `World`: each tick routes every client's `MoveCommand` to the cell that owns its player, steps each cell's
  `PlayerMovementSystem` via `ShardHost.Tick` (scheduler-fanned, deterministic), transfers authority for boundary
  crossers exactly-once (`ProcessHandoffs`, `NetId` stable), refreshes border ghosts (`SyncGhosts`), then serves
  each client its single home-cell area-of-interest snapshot (owned + ghosts) framed identically. The
  `WorldClient` and `MoveProtocol` are unchanged - a client cannot tell it is talking to a sharded server.
- **`WorldClient`** wraps `NetClient` + `ClientReplicationView` + `ClientPrediction` and exposes
  `EntityRenderState[]` (local player predicted + reconciled, remotes from replicated positions - smoothly
  interpolated between snapshots by default, so a remote glides instead of teleporting one ~tick-rate snapshot-step
  per ingest; `AdvancePresentation(dt)` drives it, opt out with `WorldClientConfig.InterpolateRemotes = false`).
  Optional `WorldBounds`/`IPhysicsWorld?` ctor params (mirroring `WorldServer`, since 8.0.0) make the client predict
  against the same play-area bound + static physics bodies the server is authoritative over, so a
  solid-prop world predicts straight instead of rubber-banding (null = terrain only).
  `WorldClient.NetStats` (a `KhaozEngine.Diagnostics.ClientNetStats`) surfaces connection health for a telemetry
  overlay: RTT / loss / byte rates from the transport, the AoI snapshot ingest rate, and the
  prediction-reconciliation correction magnitude (last + rolling average); `Connected` tracks `Joined`. Rates
  refresh once per ~1s window as `AdvancePresentation(dt)` is pumped. Reading it never mutates state.
- **`WorldPersistence`** (+ `WorldPersistenceConfig`, `PlayerRecord`) wires an
  [`IWorldStore`](../KhaozEngine.WorldStore) into the server lifecycle through **`IWorldPersistenceHost`** (the
  surface `WorldServer` and `ShardedWorldServer` both implement) so the world survives a restart: load-on-join
  (spawn at the saved position, default if absent), save-on-leave, and a periodic snapshot of players dirty since
  their last save. Keyed `player:{accountId}`; backend-agnostic and cell-agnostic (a loaded player spawns at its
  saved position in whatever cell contains it). Pick a backend: `KhaozEngine.WorldStore.Sqlite` (dev/test) or
  `KhaozEngine.WorldStore.SqlServer` (prod / Azure SQL).

No render, window, or GPU dependency: the servers are headless and the client glue is render-free (a sample
renders a capsule per `EntityRenderState`). `WorldServer` is the single-`World` slice; `ShardedWorldServer` is
the multi-cell variant (overworld sub-project 6b).

## Server administration (since 8.4.2)

Both `WorldServer` and `ShardedWorldServer` implement **`IAdminControllable`**: `ListOnline()` returns the
connected players as a snapshot (published once per tick); `Teleport(PlayerRef, Vector3)`, `Kick(PlayerRef, reason)`,
and `Broadcast(text)` are queued and applied on the host thread between ticks, safe to call from another thread.
Target a player by `PlayerRef.Slot(n)` or `PlayerRef.Account("...")`.

**`IBanStore`** is consulted at connect: a banned account is rejected before it spawns. `InMemoryBanStore` is
the in-memory default; `WorldStoreBanStore` persists over any `IWorldStore` keyspace (`ban:{accountId}`) with a
synchronous in-memory cache (call `LoadAsync()` once at startup). Pass either as the trailing `banStore:` ctor
arg on `WorldServer` or `ShardedWorldServer`. Bans key on the verified account id; guests are not bannable.

The **`ServerAdmin`** facade composes an `IAdminControllable` server, an optional `IBanStore`, and an optional
`IEnumerableWorldStore`: `BanAsync` persists and kicks if the account is online; `ListAccountsAsync(prefix)`
materializes the account enumeration. Unwired capabilities throw `NotSupportedException` (feature-detect via
`BansSupported` / `AccountsSupported`).

For the opt-in Kestrel HTTPS endpoint that exposes `ServerAdmin` as a REST API, see
[`KhaozEngine.Server.Admin`](../KhaozEngine.Server.Admin).
