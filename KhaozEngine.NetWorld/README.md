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
  Read-only local-avatar shorthands: `LocalRenderState` / `LocalGrounded` / `LocalVerticalVelocity`, plus
  `LocalHorizontalSpeed` (since 8.7.0) - the predicted planar speed in m/s straight off
  `ClientPrediction.PredictedHorizontalSpeed`, computed per prediction tick and immune to reconciliation snaps,
  so it stays steady under lag (the clean source for a speed HUD / footstep audio / locomotion blend, vs
  differencing `LocalRenderState.Position`, which carries the decaying render offset and wobbles).
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

## Version-skew resilience (since 8.5.0)

Two opt-in backstops so a client on an older build than the server is rejected cleanly instead of hard-crashing
on a snapshot it cannot decode. Both are additive: the wire and existing ctors are unchanged when unused.

- **Connect-time version handshake.** Set `WorldClientConfig.ProtocolVersion` and `WorldClient` prepends its
  protocol/build version to the connect token (`ProtocolHandshake.WrapToken`). Wrap your authenticator in a
  **`VersionCheckingAuthenticator(serverVersion, isCompatible, inner?)`** and pass it as the existing
  `authenticator:` arg on `WorldServer` / `ShardedWorldServer`: it unwraps the version, runs the
  consumer-supplied `isCompatible` rule before the real auth check, and on mismatch rejects cleanly. The client
  surfaces it as `DisconnectReason.IncompatibleVersion` with the server's required version in
  `DisconnectReasonDetail`, and never proceeds to snapshots. A legacy/version-less client decodes as version
  `""`, so the rule can reject it; a compatible version delegates the inner token to `inner` unchanged
  (subject + display-name resolution identical).
- **Graceful decode (last resort).** `WorldClient.OnSnapshot` decodes via `ClientReplicationView.TryApply`, so an
  undecodable snapshot (an unregistered component type id from a newer protocol) becomes the same clean
  `DisconnectReason.IncompatibleVersion` disconnect plus a **`SnapshotDecodeFailed`** event - never an unhandled
  exception in the consumer's frame loop. Pair with the `KhaozEngine.Updates` startup gate
  (`UpdateService.EnsureUpToDateAsync`) so a client self-heals before it ever connects.

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

## Client self-rescue / unstuck (since 8.6.0)

A normal game client can ask the authoritative server to teleport **itself** to a server-decided safe position (a
"return to spawn" / "unstuck"). Because the server is authoritative, a client-side position overwrite reconciles
away within ~1 RTT, so an unstuck must move the player on the server.

- **Client:** `WorldClient.RequestSelfRescue()` - fire-and-forget over the reliable channel; returns false (and
  sends nothing) when not connected. The client never names a destination; it only asks (a client-supplied target
  would be a teleport-anywhere cheat).
- **Server:** set `WorldServerConfig.SelfRescueDestination` / `ShardedWorldServerConfig.SelfRescueDestination` (a
  `Func<PlayerRef, Vector3>?`; **null = the feature is OFF**) to the safe spot to send the requesting player to. A
  fixed point is just `_ => point`. The request reuses the admin `Teleport` apply path (position set, vertical
  velocity zeroed), so it reconciles to the client exactly like an admin teleport. `SelfRescueCooldownSeconds`
  (default 5s) rate-limits it per player. Both servers handle it identically; `ShardedWorldServer` teleports
  across cells.

Wire: a control frame distinct by length from a move (`MoveProtocol.ClientControlKind` /
`EncodeClientControl` / `TryDecodeClientControl`), so it shares the client->server Data channel without aliasing
and a server that predates the feature harmlessly ignores it (no protocol-version break).
