# KhaozEngine.NetWorld

Render-free networked-world layer that wires the [KhaozEngine.Locomotion](../KhaozEngine.Locomotion)
movement core to the authoritative netcode stack ([Netcode](../KhaozEngine.Netcode) +
[Replication](../KhaozEngine.Replication)).

- **`PlayerMoveSimulator`** (`ITickSimulator`) runs `CharacterMovement.Step` both server-authoritatively
  and inside client prediction, so the two stay in lockstep.
- **`WorldServer`** is a single-`World` authoritative movement server: a `NetServer` session layer spawns
  one player entity per connection, drains that client's queued `MoveCommand` each tick, runs the ground-
  clamped sim, and serves each client its area of interest prefixed with that client's net id + last-acked move
  seq. By default (since 9.18.0) it serves per-client AoI **deltas** (only what changed since each client's
  acknowledged baseline; see the delta section below) and falls back to a full `SnapshotWriter.WriteFiltered`
  snapshot for a client that hasn't opted in.
- **`ShardedWorldServer`** (+ `ShardedWorldServerConfig`) runs that same movement stack across a
  [`KhaozEngine.Sharding`](../KhaozEngine.Sharding) `ShardHost` grid of cells, so the world scales past a single
  `World`: each tick routes every client's `MoveCommand` to the cell that owns its player, steps each cell's
  `PlayerMovementSystem` via `ShardHost.Tick` (scheduler-fanned, deterministic), transfers authority for boundary
  crossers exactly-once (`ProcessHandoffs`, `NetId` stable), refreshes border ghosts (`SyncGhosts`), then serves
  each client its single home-cell area of interest (owned + ghosts) framed identically - as an AoI delta by
  default (keyed by `NetId`, so a boundary crossing stays a component delta, never a despawn+respawn), or a full
  snapshot for a non-opted-in client. The `WorldClient` and `MoveProtocol` are unchanged - a client cannot tell it
  is talking to a sharded server.
- **`WorldClient`** wraps `NetClient` + `ClientReplicationView` + `ClientPrediction` and exposes
  `EntityRenderState[]` (local player predicted + reconciled, remotes from replicated positions - smoothly
  interpolated between snapshots by default, so a remote glides instead of teleporting one ~tick-rate snapshot-step
  per ingest; `AdvancePresentation(dt)` drives it, opt out with `WorldClientConfig.InterpolateRemotes = false`).
  Remote interpolation is a **fixed-delay snapshot buffer** (since 9.23.0): each render frame renders remotes at
  `latest - interpolationDelay` and lerps the two buffered snapshots bracketing that time by their true timestamps,
  so presentation is decoupled from both the tick cadence and the render fps - no hold frames, no catch-up snaps at
  a non-integer render:tick ratio (the pre-9.23.0 estimate-the-interval-and-ramp-alpha scheme drifted and stuttered).
  Tune the delay with **`WorldClientConfig.InterpolationDelayTicks`** (default 2 ticks); lower it for less latency,
  raise it for a rougher network. A **remote teleport is a hard cut for observers too** (since 10.67.0): when a
  remote's replicated `MovementState.TeleportEpoch` advances, its interpolation buffer is flushed to the newest sample
  so it snaps to the destination instead of streaking across the world, then smooth interpolation resumes - automatic,
  no consumer code. A debug **`WorldClientConfig.PresentationTraceEnabled`** exposes
  `WorldClient.PresentationTrace` - a per-frame CSV-dumpable trace of the presentation internals (render time,
  interpolation delay, seconds-since-snapshot, per-remote starvation-hold flag, snapshot arrivals, local
  reconcile-error) for characterising a movement-smoothness bug; off by default, zero overhead.
  Optional `WorldBounds`/`IPhysicsWorld?` ctor params (mirroring `WorldServer`, since 8.0.0) make the client predict
  against the same play-area bound + static physics bodies the server is authoritative over, so a
  solid-prop world predicts straight instead of rubber-banding (null = terrain only).
  Each `EntityRenderState` carries the EXACT movement flags for every entity (local: predicted; remote: replicated
  `MovementState`): `Grounded` + `VerticalVelocity` (jump/fall), `Swimming` (the swim feature), and `ClimbRate` (the
  signed step-climb rate driving the stair glide, decoded from `MovementState.ClimbRateQ` - written per tick by BOTH
  the single-`World` `WorldServer` and the sharded `PlayerMovementSystem`), so an animator
  bridge reads them straight instead of finite-differencing the terrain-following position - swim in particular is
  impossible to derive from position (a swimmer glides horizontally like a walker), so the replicated bit is the only
  source. Read-only local-avatar shorthands: `LocalRenderState` (whose `.Swimming` mirrors the flag) / `LocalGrounded`
  / `LocalVerticalVelocity`, plus
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
  `KhaozEngine.WorldStore.SqlServer` (prod / Azure SQL). A game attaches its own **durable per-player state**
  (XP, skills, inventory, quest log) by setting `WorldPersistenceConfig.CaptureGameState` /
  `ApplyGameState` (`PlayerGameStateCapture` / `PlayerGameStateApply`, handed a `PlayerPersistenceContext` of
  `Slot` + `AccountId`): an opaque blob that rides the SAME record (`PlayerRecord.Game`, base64 in the JSON),
  dirty comparison, interval save, flush-on-drain and load-on-join thread-marshalling as position. Capture runs
  on the server thread at each save; apply runs on the server thread as the load-on-join position is applied. The
  engine never interprets the bytes - the game owns the format and its migration (run a
  [`MigrationChain`](../KhaozEngine.Persistence) in the apply hook) - and, being account-keyed, the blob is
  unaffected by cell handoff (unlike registered components, which migrate with the entity). While a load-on-join is
  in flight the account is guarded (the periodic pass and save-on-leave skip it) so a save landing mid-load can't
  overwrite the stored record with default-spawn state and erase the blob; `capture` returning null/empty is
  destructive (it *erases* the stored blob - "no game state", not "keep existing"), never a "not loaded yet" signal.
  `OnStoreError` surfaces a faulted background load/save (store outage). The periodic pass batches every dirty
  player's record into one `IWorldStore.SaveManyAsync` call instead of one `SaveAsync` per player. A faulted batch
  leaves every player in it dirty for the next pass (save-on-leave still uses a single-record `SaveAsync`).
  - **Load validation and quarantine.** Two optional hooks on `WorldPersistenceConfig`, both evaluated on the
    server thread inside the load-on-join apply step: **`Bounds`** (`WorldBounds`, the same type the movement
    clamp uses) rejects a loaded position outside the play area. **`ValidateGameState`**
    (`PlayerGameStateValidate`, handed the same `PlayerPersistenceContext` plus the raw blob, returning a
    `PlayerGameStateVerdict.Valid()`/`Invalid(reason)`) is the game's plausibility check on its durable blob
    (schema parse, stat clamps, inventory sanity), only invoked when the record actually carries a blob. A
    record that fails either check is quarantined WHOLE rather than partially applied: its raw, undecoded bytes
    are copied verbatim to `{QuarantineKeyPrefix}{KeyPrefix}{accountId}` (default
    `quarantine:player:{accountId}`, where `QuarantineKeyPrefix` defaults to `"quarantine:"`) in the same
    `IWorldStore`, the player is left at its default spawn (a **fresh spawn** - never placed from the bad
    record), and **`OnRecordQuarantined`** (`event Action<string, string>`, accountId + reason) fires. The
    `lastSaved` dirty-tracking baseline is deliberately NOT advanced for a quarantined record (it only advances
    on a successful apply), so the fresh-spawn state stays dirty against the store and the **next periodic save
    overwrites the bad primary** record, while the quarantine copy survives untouched as a **forensic copy**
    (never itself restored automatically) for offline inspection. The `loadsInFlight` guard clears on
    quarantine exactly as it does on a successful apply, so persistence resumes for that account immediately.
  - **Undecodable record vs. store outage.** A record whose stored JSON fails to parse (`PlayerRecord.Decode`
    throws) is NOT treated as a store fault: it read fine, it just will not decode, so it routes straight into
    the quarantine path above (clearing the `loadsInFlight` guard, so the account resumes persisting on its
    next dirty pass) instead of faulting the load task. A genuine store READ failure (an outage) is different
    and is deliberately left unresolved: the guard stays set so the intact stored record remains protected from
    a clobbering save until a later rejoin retries the load, and the fault surfaces through `OnStoreError`
    rather than `OnRecordQuarantined`. Before this, an undecodable record took the outage path too, which left
    the guard set forever for a record that could never successfully decode on any retry, silently stopping
    that player's persistence for the rest of the session.
- **`CellPersistence`** (+ `CellPersistenceConfig`, `WorldMetaRecord`) wires an
  [`IWorldStore`](../KhaozEngine.WorldStore) into a [`Sharding`](../KhaozEngine.Sharding) `ShardHost`-based server
  through **`ICellPersistenceHost`** (the surface `ShardedWorldServer` implements) so a cell's authoritative
  non-player entities survive a restart: lazy load-on-cell-create (subscribes to `ShardHost.CellCreated`, applies
  on the server thread inside `Update`), a periodic snapshot of cells dirty since their last save
  (`SaveIntervalSeconds`, default 30s), and a `WorldMetaRecord` NetId high-water mark so restored entities never
  collide with a fresh spawn after restart. Cell records are keyed `cell:{x}:{y}` under a versioned blob header.
  `PreloadAsync` instantiates
  every saved cell at boot (enumerating over an `IEnumerableWorldStore`), `LoadMetaAsync` resumes the NetId
  allocator, and `FlushAsync` quiesces in-flight loads and saves at shutdown. Players are excluded (already
  persisted player-keyed by `WorldPersistence`), and ghosts and migrating entities are excluded too - this is
  cell-owned, non-player state only. Mirrors `WorldPersistence` but keyed by cell coordinate instead of account,
  including the batched periodic pass: every dirty cell's snapshot goes through one `IWorldStore.SaveManyAsync`
  call instead of one `SaveAsync` per cell (the meta write and quarantine writes stay single-record saves).
  - **Schema evolution + restore hardening (since 9.33.0).** `CellPersistenceConfig.RegisterMigration(fromVersion,
    migrate)` registers ordered `CellSnapshotMigration` (`byte[] body -> byte[]`) steps that bring an older blob
    forward on load, before restore. The chain is validated at construction (contiguous, no gaps, none at/beyond
    `SchemaVersion`), mirroring `KhaozEngine.Persistence.MigrationChain`. Author a step with
    `KhaozEngine.Replication.SnapshotBlobReader`/`SnapshotBlobWriter`. Restore is non-throwing: a blob that fails to
    decode (bad header, corrupt frame, a migration threw, or restore rejected it) is QUARANTINED (its original bytes
    copied to `quarantine:cell:{x}:{y}`, the cell starts fresh) rather than thrown, so a poisoned key never crash-loops
    the server. An extension frame whose id the current registry does not know is RETAINED and re-persisted verbatim
    (retain-and-rewrite), so a registry regression no longer strips data at rest. All of this is surfaced through
    `CellPersistence.Issue` (`event Action<CellPersistenceIssue>`): `Migrated` (from -> to), `SkippedTooOld` /
    `SkippedTooNew`, `QuarantinedCorrupt` (with the decode error), and `RetainedUnknownExtensions` (with the count).
    A current-`SchemaVersion` blob still restores byte-identically, so a save with no migrations behaves as before.
  - **Engine-provided migrations (since 10.0.0).** The engine now ships its own built-in cell-blob migrations, folded
    into any config's chain unless it opts out (`CellPersistenceConfig.IncludeEngineMigrations`, default true; a
    consumer step OVERRIDES an engine step of the same from-version). The first is **`NetIdBlobMigration.WidenV1ToV2`**,
    the 10.0.0 `NetId` widening: it rewrites a stored 32-bit-id body (schema v1) to 64-bit (schema v2), leaving every
    component byte identical (only the per-entity id field grows 4 -> 8 bytes, node 0). The default `SchemaVersion`
    advanced to 2, so a server on the default config brings a 9.x cell blob forward with no wiring. A 10.0.0 blob (v2)
    is `SkippedTooNew` on a pre-10.0.0 build, so an accidental downgrade quarantines rather than corrupts (but will not
    load): once a server has written 64-bit blobs it cannot be downgraded.
  - **Store-outage hygiene (since 10.4.1).** `CellPersistence.OnStoreError` (`event Action<Exception>`, mirrors
    `WorldPersistence.OnStoreError`) surfaces a faulted background cell save, meta write, or quarantine write. The
    driver prunes the faulted task every `Update` so a store outage can't grow the pending list unbounded or make the
    boot sequence (`LoadMeta -> Preload -> Flush`) or the shutdown `FlushAsync` throw. A faulted cell save stays dirty
    and retries on the next pass; a faulted quarantine write is dropped (the cell already started fresh).

No render, window, or GPU dependency: the servers are headless and the client glue is render-free (a sample
renders a capsule per `EntityRenderState`). `WorldServer` is the single-`World` slice; `ShardedWorldServer` is
the multi-cell variant (overworld sub-project 6b).

## Game messages (since 9.27.0)

A generic, game-defined message channel alongside the movement protocol, so a game (attack, interaction, pick-up,
chat, inventory transaction, …) is not forced into a side channel. Payloads are **opaque bytes**: the engine
frames, demuxes, rate-limits and size-caps them, but never deserializes them - the game owns the payload format.

- **Client -> server.** `WorldClient.SendGameMessage(ushort kind, ReadOnlySpan<byte> payload, NetChannelReliability
  reliability)` sends a message; both servers raise **`OnGameMessage(int slot, ushort kind, ReadOnlySpan<byte>
  payload)`** on the host thread during `Poll`. The `kind` is the game's discriminator; the span is only valid for
  the duration of the call (copy it with `payload.ToArray()` to keep it).
- **Server -> client.** `SendGameMessageTo(slot, kind, payload, reliability)` targets one client and
  `BroadcastGameMessage(kind, payload, reliability)` fans out to all; both surface on the client as
  **`WorldClient.GameMessageReceived(ushort kind, ReadOnlySpan<byte> payload)`**.
- **Reliability.** `NetChannelReliability.ReliableOrdered` gives ordered exactly-once delivery at the transport, so
  a command consumer needs no seq of its own; `UnreliableSequenced` is a lossy latest-wins state ping.
- **Hostile-input hardening (client -> server), to the same bar as the move path.** The per-connection
  `AntiCheat` rate limiter runs in front of game messages (they share the move flood budget), and a payload larger
  than **`WorldServerConfig.MaxGameMessageBytes`** / **`ShardedWorldServerConfig.MaxGameMessageBytes`** (default
  1024) is dropped and flagged `SuspiciousReason.OversizedMessage` via `OnSuspiciousActivity` - never thrown.
- **Framing / version skew.** A client message rides the existing `0xC5` control-marker family with its own
  sub-marker, demuxed ahead of the move; by construction it can never alias the 2 / 6 / 18 byte control / ack /
  move shapes (see the aliasing contract in `MoveProtocol`). Server frames use a new `ServerFrameKind.GameMessage`
  an older client silently ignores (unknown frame kind), so **server -> client is version-skew-safe downstream**.
  The other direction is NOT protected by the framing: a server that predates the feature has no game-message decode,
  so it flags a SHORT game-message frame (< 18 bytes) as malformed but MISPARSES one whose total length is >= 18
  (a payload of >= 13 bytes) as a spurious finite move. The `WorldClientConfig.ProtocolVersion` handshake (below) is
  the actual protection - a game-aware client must not send a game message until the handshake confirms the peer
  understands it. Gate adoption on the handshake. Quest / inventory / chat systems themselves stay game-side; this
  is only the transport seam.

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
  - **Wire-format generation (enforced automatically since 10.2.0).** `MoveProtocol.WireProtocolVersion` (= 3; 2 was
    the 10.0.0 `NetId`-widening line, 1 the pre-10.0.0 32-bit line) labels the incompatible on-the-wire generations.
    10.0.0 widened `NetId` to 64-bit (the snapshot/delta id field and the frame header, `[localNetId:long][ackSeq:int]`,
    grown 8 -> 12 bytes); generation 3 (the swim feature) added the `MovementState.Swimming` byte to the movement
    built-in codec (not length-prefixed, so an old client cannot skip it). Neither has a dual-format wire, so peers on
    different generations MUST reject each other at connect rather than misparse a frame. As of 10.2.0 the engine
    enforces this for you: `WorldClient` always folds the
    generation into its Hello (even with no `ProtocolVersion`) and `WorldServer` / `ShardedWorldServer` always install
    a **`WireGenerationAuthenticator`** that rejects a mismatch, or a peer presenting none (a pre-10.2.0 / 9.x client),
    cleanly as `DisconnectReason.IncompatibleVersion`. Folding `;wire{N}` into your version string is no longer needed
    (the pre-10.2.0 advice); the `ProtocolVersion` gate above is now purely your game version, layered on top. A bare
    `NetClient` used against a `WorldServer` / `ShardedWorldServer` must present the wire layer itself via
    `ProtocolHandshake.BuildClientToken`.
- **Graceful decode (last resort).** `WorldClient.OnSnapshot` decodes via `ClientReplicationView.TryApply`, so an
  undecodable snapshot (an unregistered BUILT-IN component type id from a newer core protocol) becomes the same
  clean `DisconnectReason.IncompatibleVersion` disconnect plus a **`SnapshotDecodeFailed`** event - never an
  unhandled exception in the consumer's frame loop. (An unregistered *consumer extension* id, at/above the floor,
  is instead skipped - see the server-owned-entities section below - so a newer server's added component never
  disconnects an older client.) Pair with the `KhaozEngine.Updates` startup gate
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

**NativeAOT.** The durable persistence DTOs (`PlayerRecord`, `WorldMetaRecord`, and the `WorldStoreBanStore` ban
record) encode and decode through a source-generated `System.Text.Json` context, so they round-trip under
`PublishAot` without reflection. The context runs in metadata mode, so the encoding is byte-for-byte identical to
the previous reflection output - records already stored via `IWorldStore` keep loading, and a null game blob still
encodes as `null`. The gate is `KhaozEngine.Server.AotProbe`, which exercises these round-trips in its published
native run. `NewLine` is pinned to `"\n"`, matching `JsonDefaults.IndentedWrite`, so a persisted record is
canonical LF on every OS rather than the platform newline (Windows previously persisted CRLF). Existing CRLF
blobs still read fine and rewrite to LF the next time they are saved.

The **`ServerAdmin`** facade composes an `IAdminControllable` server, an optional `IBanStore`, and an optional
`IEnumerableWorldStore`: `BanAsync` persists and kicks if the account is online; `ListAccountsAsync(prefix)`
materializes the account enumeration. Unwired capabilities throw `NotSupportedException` (feature-detect via
`BansSupported` / `AccountsSupported`).

**Game-registered admin actions (since 10.131.0).** `ServerAdmin` also carries a name-keyed action registry:
`RegisterAction(string name, Func<JsonElement?, CancellationToken, Task<AdminActionResult>> handler)` (and a
synchronous convenience overload taking `Func<JsonElement?, AdminActionResult>`), `ActionNames`, and
`TryGetAction`. A name must match `^[a-z0-9][a-z0-9-]{0,63}$`. An invalid or already-registered name throws
`ArgumentException`. The registry is a `ConcurrentDictionary`, so registering is safe from any thread, though
registration normally happens once at startup before the endpoint starts.

```csharp
var admin = new ServerAdmin(server, banStore, accountStore);
admin.RegisterAction("set-time", payload =>
{
    float t = payload?.GetProperty("timeOfDay").GetSingle() ?? 0f;
    gameClockQueue.Enqueue(t);
    return AdminActionResult.Accepted();
});
```

A handler runs on the caller's thread (an HTTP request thread for `KhaozEngine.Server.Admin`), so it must never
touch simulation state directly: enqueue mutations to the host thread and return published snapshots for reads,
exactly like `IAdminControllable` above. `AdminActionResult` is `Ok(payload = null)` for a query (serialized as
JSON when a payload is given), `Accepted()` for an enqueued mutation, or `BadRequest(error)` to reject the
request.

For the opt-in Kestrel HTTPS endpoint that exposes `ServerAdmin` (including registered actions, under
`GET /actions`, `GET /actions/{name}`, `POST /actions/{name}`) as a REST API, see
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

## Teleport epoch: hard cut + client signal (since 10.65.0)

An intentional teleport must CUT (avatar + camera), not glide, even when the destination is near. A monotonic
**teleport epoch** rides the replicated `MovementState` (wire generation 4) and reaches the local owner's
`ClientPrediction` basis via `PlayerMoveState`. The server advances it ONLY at teleport sites, through the
`IWorldPersistenceHost.SetPlayerState(slot, state, teleport: true)` param (load-on-join placement, admin `Teleport`,
self-rescue) on both `WorldServer` and `ShardedWorldServer`. Normal per-tick movement preserves it (the single-World
sim carries it through `PlayerMoveSimulator.Step`; the sharded `PlayerMovementSystem` writes movement fields in place
and never touches it). `ClientPrediction.Reconcile` force-cuts on an epoch advance regardless of `HardSnapDistance`.

`WorldClient` surfaces one uniform signal for join, reconnect, AND in-session teleports: the **`LocalTeleported`**
event plus a monotonic **`LocalTeleportEpoch`** counter (poll it frame-to-frame if you prefer). A consumer uses it to
snap the follow camera (`FollowCamera3D.Warp`) and optionally run a screen transition (see `KhaozEngine.Render3D`
`ITransition`). Mismatched wire generations are rejected at connect by the always-on `WireGenerationAuthenticator`.

## Reconnect input backlog (since 8.8.0)

Holding the movement key through a long auto-reconnect outage no longer freezes the player on rejoin. Two guards:

- **Client:** `WorldClient.SendInput` is a no-op (predicts nothing, sends nothing, returns `-1`) unless
  `ConnectionState == Connected`. A loop that calls it every frame regardless of state is safe: input produced
  during the outage is dropped here instead of predicting the avatar away from authority and inflating the
  sequence counter, so rejoin resumes cleanly.
- **Server:** `WorldServerConfig.MaxInputBacklog` / `ShardedWorldServerConfig.MaxInputBacklog` (default 8 ticks;
  0 disables) caps how far behind live the server can fall under a deep per-player backlog. When a slot's queued
  moves exceed it, the server skips the stale ones and applies the most recent (latest-wins), instead of crawling
  a flush/burst out one move per tick. Built on `RemoteCommandQueue`'s `catchUpThreshold`.

## Server-owned non-player entities + consumer components (since 9.16.0)

Replicate a server-spawned NPC / enemy the client can tell apart from a player, and read any game component off it
per entity. All four pieces are additive and default to today's behaviour.

- **Injectable registry.** `WorldServer` / `ShardedWorldServer` / `WorldClient` each take an optional
  `ReplicationRegistry? registry` ctor param (default = movement-only). Build the shared registry with
  **`MoveProtocol.CreateRegistry(configure)`** and register your own components inside `configure` at ids >=
  **`MoveProtocol.FirstConsumerTypeId`** (= `ReplicationRegistry.FirstExtensionTypeId`, 16). Call it identically on
  the server and every client. Extension components are length-prefixed on the wire, so a client that predates a
  given component **skips** it instead of disconnecting (an older client keeps running against a newer server, and
  a new client against an older server just reads the component as absent).
- **Spawn.** **`ShardedWorldServer.SpawnEntity(x, z, configure)`** (and **`WorldServer.SpawnEntity`** for the
  single-world server) spawns a server-owned non-player entity, allocating a non-colliding `NetId` from the same
  authoritative allocator player joins draw from (honouring the `CellPersistence` high-water) and placing it in the
  owning cell. `configure(world, entity)` adds its components (an NPC kind / HP / faction registered above the
  floor). Persisted with its cell, replicated through the normal AoI + ghost + handoff pipeline.
- **Brain.** **`OnBeforeTick`** (`event Action<float>?`, both servers) fires at the start of `Tick(dt)` before the
  snapshot pass, where a consumer NPC / enemy brain writes each entity's `ReplicatedPosition` so its move reaches
  clients the same tick.
- **Read.** **`WorldClient.TryGetComponent<T>(int netId, out T)`** reads a replicated component off the entity with
  that net id (the `EntityRenderState.Id` value). Use it to read a server-assigned discriminator (NPC kind, HP,
  faction) and pick a model. Returns `false` against an older server that never sends `T` (no handshake, no
  disconnect). `MmoServerSample` demonstrates the whole seam with a `Creature` kind component.

## Per-registration replication channels (since 9.28.0)

A registered component can declare **which** consumers see its bytes, via an optional
[`ReplicationChannels`](../KhaozEngine.Replication) flag on `Register<T>` (`Replicate` / `Persist` / `Migrate`, plus
`OwnerOnly` as a `Replicate` modifier). Default is `Replicate | Persist | Migrate`, so nothing changes unless a game
opts in. Both servers thread the serving channel + the receiving client's own player net id through automatically, so
these two shapes just work end to end:

- **Server-only state on an NPC** (`Persist | Migrate`, no `Replicate`): a mob's aggro / threat table, loot roll,
  quest counter, door-lock internals - survives cell handoff and a server restart, but is never on the replication
  wire and never ghosted, so it can't leak to any client.
- **Owner-only private state on a player** (`Default | OwnerOnly`): private inventory, quest flags, exact HP -
  replicated only to that player's own client, never to another player who has it in area-of-interest (closing the
  map-hack surface). It still persists and migrates like any owned component.

`MmoServerSample` demonstrates both: the `Creature` NPC carries a hidden `AggroCounter` (`Persist | Migrate`) and each
player carries an `OwnerOnly` `PrivateStats`. Because the flags gate only the server write side, an existing client is
unaffected. **Cell-blob note:** changing a component's channels or byte layout changes what future saves write into a
cell's persist blob; since 9.33.0 the cell blob has a real migration path plus a quarantining restore and
unknown-extension retention (see the `CellPersistence` schema-evolution note above), so a layout change brings old
blobs forward instead of skipping or corrupting them.

## Replicated dynamic rigid bodies (physics props)

A server-authoritative dynamic body (a crate, a barrel, a physics prop stepped by `KhaozEngine.Physics`) replicates to
clients that **interpolate** it exactly like a remote player, via two built-in components:

- **`DynamicBodyState`** (built-in type id `MoveProtocol.DynamicBodyTypeId` = 4) carries the body's **orientation
  quaternion** plus its linear/angular **velocity**. It rides **alongside** the body's `ReplicatedPosition` (the position
  drives area-of-interest, exactly as a player's `ReplicatedPosition` + `MovementState` pair). The orientation is
  **interpolatable**: it **slerps** between snapshots on the client's fixed-delay buffer (the same machinery that glides
  a remote player's position), so a tumbling crate rotates smoothly between the ~tick-rate snapshots. Velocity is a rate,
  carried for extrapolation / effects (impact dust, spin blur) and not itself blended.
- **`DynamicBodyReplication`** is the server-side sampler. Spawn a server-owned entity with `SpawnEntity`, add the body
  to your `IPhysicsWorld`, then `Track(netId, handle, entity)` to pair them. Each tick, from `OnBeforeTick` and **after
  you step the physics world**, call `Sample()`: it writes each awake body's `GetDynamicPose` / `GetDynamicVelocity`
  into that entity's `ReplicatedPosition` + `DynamicBodyState`, so the fresh pose lands in the same tick's snapshot.

The server owns the sim; the **client never simulates a replicated body** (no client-side prediction for bodies) - it
renders the interpolated authoritative pose read via `WorldClient.TryGetComponent<DynamicBodyState>` (orientation +
velocity) and the body's interpolated `ReplicatedPosition` (surfaced on each `EntityRenderState`).

**Sleep gating.** A body Bepu has put to sleep (`IsAwake` false) is not re-sampled, so a resting prop stops generating
snapshot churn (like a still remote player that need not stream). The pose written on the last awake tick IS the rest
pose, so the client's interpolation converges to it and then holds (the fixed-delay buffer clamps at the newest sample);
a body woken later (a collision, `SetDynamicVelocity`) resumes sampling. Removing the entity server-side propagates to
clients as a normal AoI despawn.

## Area-of-interest delta replication (since 9.18.0)

The live serving path sends each client only what changed inside its interest set per tick (an
[`AoiDeltaReplicator`](../KhaozEngine.Replication)) instead of a full snapshot of every in-AoI entity every tick.
On a 16-player dense hotspot at 30 Hz idle that is ~25 vs ~573 bytes/client/tick - a 95%+ cut, and static entities
(NPCs, names) stop paying full freight. It is **on by default** and upgrades independently of the client, at the
same version-skew bar as 9.16.0.

- **Config.** **`WorldServerConfig.DeltaReplication`** / **`ShardedWorldServerConfig.DeltaReplication`** (default
  **true**; set false to force full snapshots for every client) and **`WorldClientConfig.RequestDeltaReplication`**
  (default **true**). Reliability is phase 1: reliable-ordered, deltas built from each client's last acknowledged
  baseline, so a dropped delta/ack self-heals on the next tick.
- **Handshake.** On join a delta-capable `WorldClient` sends a **`MoveProtocol.ClientControlKind.DeltaCapable`**
  hello (a 2-byte control an older server decodes as an unknown control and harmlessly ignores). The server upgrades
  that slot to **`ServerFrameKind.Delta`** frames; the client applies them with `ClientReplicationView.ApplyDelta`,
  reconciles exactly as for a snapshot, and acks each applied seq (**`MoveProtocol.EncodeReplicationAck`**, a 6-byte
  frame distinct in length from a move or a control) so the server advances its baseline.
- **Compatibility (both directions, no disconnect).** A full snapshot is the `baseline -1` delta, so the wire is a
  strict superset. An older client never advertises `DeltaCapable`, so a new server keeps serving it full snapshots;
  a new client against an older server never receives a `Delta` frame, so it keeps applying full snapshots and sends
  no acks. The 9.16.0 skip-unknown-extension + malformed-length guards are unchanged on the delta wire.
