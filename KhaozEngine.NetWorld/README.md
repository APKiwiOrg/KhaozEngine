# KhaozEngine.NetWorld

Render-free networked-world layer that wires the [KhaozEngine.Locomotion](../KhaozEngine.Locomotion)
movement core to the authoritative netcode stack ([Netcode](../KhaozEngine.Netcode) +
[Replication](../KhaozEngine.Replication)).

- **`PlayerMoveSimulator`** (`ITickSimulator`) runs `CharacterMovement.Step` both server-authoritatively
  and inside client prediction, so the two stay in lockstep. Three read surfaces past the constructor:
  **`Frame`** (settable, `WorldFrame.Origin` by default) is the island frame this instance steps in - every
  state `Step` returns is stamped with its anchor, and only the owning island sets it, together with its
  physics world's rebase, never mid-step and never per entity. **`SamplerSpace`** (read-only, fixed at
  construction by the `samplerSpace` ctor parameter, default `SamplerSpace.World`) is which space the
  `groundHeight`/`groundNormal`/medium delegates passed to the constructor read: `World` means they take
  absolute coordinates and the step converts for them (zero adoption work), `Frame` means they already read
  frame-local coordinates and the step passes them straight through. See the island-frame section below and
  `SamplerSpace`'s own doc for the full contract, including why `World` is WRONG, not merely imprecise, for a
  sampler backed by the island's own rebased physics world (e.g. a `PhysicsGroundProbe`) - that world raycasts
  in its own rebased space, so wrapping the call back out to absolute makes every ray miss. **`Bounds`**
  (read-only) mirrors the `WorldBounds?` passed to the constructor: it is the one sampler `SamplerSpace` does
  not govern (a play area is authored content, so the step always converts for it, in both modes), and the
  property exists so a consumer can read back which bound this instance is clamping against instead of
  keeping its own copy in sync. Nothing in the engine reads it today (`WorldServer`/`ShardedWorldServer`/
  `WorldClient` all hold their own `bounds` locally), so it is a seam for a future or game-side caller.
- **`WorldServer`** is a single-`World` authoritative movement server: a `NetServer` session layer spawns
  one player entity per connection, drains that client's queued `MoveCommand` each tick, runs the ground-
  clamped sim, and serves each client its area of interest prefixed with that client's net id + last-acked move
  seq. By default (since 9.18.0) it serves per-client AoI **deltas** (only what changed since each client's
  acknowledged baseline; see the delta section below) and falls back to a full `SnapshotWriter.WriteFiltered`
  snapshot for a client that hasn't opted in.
- **`ShardedWorldServer`** (+ `ShardedWorldServerConfig`) runs that same movement stack across a
  [`KhaozEngine.Sharding`](../KhaozEngine.Sharding) `ShardHost` grid of cells, so the world scales past a single
  `World`: each tick routes every client's `MoveCommand` to the cell that owns its player, steps each cell's OWN
  `PlayerMovementSystem` - one instance per cell, holding that cell's island frame and that cell's own physics
  world (`ShardedWorldServerConfig.PhysicsWorldFactory`) - via `ShardHost.Tick` (scheduler-fanned, deterministic), transfers authority for boundary
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
  solid-prop world predicts straight instead of rubber-banding (null = terrain only). The physics world must be able
  to rebase, since the client adopts the server's island frame and moves that world with it.
  **`IslandFrame`** is the frame the client's prediction currently steps in, adopted from the wire, never derived,
  and **`FrameChanged`** fires (from, to, delta) when it moves - see the island-frame section below. Everything the
  client exposes stays absolute world metres.
  Each `EntityRenderState` carries the EXACT movement flags for every entity (local: predicted; remote: replicated
  `MovementState`): `Grounded` + `VerticalVelocity` (jump/fall), `Swimming` (the swim feature), and `ClimbRate` (the
  signed step-climb rate driving the stair glide, decoded from `MovementState.ClimbRateQ` - written per tick by BOTH
  the single-`World` `WorldServer` and the sharded `PlayerMovementSystem`), so an animator
  bridge reads them straight instead of finite-differencing the terrain-following position - swim in particular is
  impossible to derive from position (a swimmer glides horizontally like a walker), so the replicated bit is the only
  source. Since 17.26.0 the same list carries **`FacingYaw`** (the authoritative heading, decoded from
  `MovementState.FacingYawQ` for a remote and predicted un-quantized for the local player), which the position
  delta could not supply at all for a stationary entity. **`LandingImpactSpeed`** rides alongside it but is
  LOCAL-ONLY (always 0 for remotes, whose landing effects come from the replicated `Grounded` transition), and is
  the local player's PREDICTED landing so presentation can react on the predicted tick. Read-only local-avatar shorthands: `LocalRenderState` (whose `.Swimming` mirrors the flag) / `LocalGrounded`
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
  - **Eviction support surface.** `RequestLoad(coord)` starts a cell's store load explicitly (what `CellCreated`
    triggers, idempotent per coordinate), `IsBusy(coord)` reports an outstanding load or write for a cell,
    `SaveCellAsync(coord, snapshot)` writes one cell immediately outside the periodic pass and reports when it
    landed, `TryGetLastSaved(coord, out snapshot)` reads the dirty-tracking baseline, and `ForgetCell(coord)` drops
    the per-cell bookkeeping for an unloaded coordinate so it loads from the store again. These exist for
    `CellEvictor` below and are usable directly.
- **`CellEvictor`** (+ `CellEvictionConfig`, `ICellEvictionHost`) unloads idle cells without losing their state, the
  other half of `ShardHost`'s create-on-demand grid: without it a long-running world keeps every cell anyone has ever
  visited alive forever. Each scan it asks a `KhaozEngine.Sharding.ICellEvictionPolicy` which live cells are
  disposable, snapshots each candidate through `CellPersistence`, and removes it from the host **only once that write
  has landed**. A failed write, a cell that changed while the write was in flight, or a host that refuses all leave
  the cell in place for a later scan. An evicted coordinate that is routed to again (a spawn, a handoff destination,
  an explicit `EnsureCell`) restores **synchronously**, from the in-memory snapshot cache
  (`CellEvictionConfig.MaxCachedSnapshots`, default 1024) on the `CellCreated` hook the host raises inside the create
  call, so a cell recreated as a handoff destination is fully populated before it adopts the crossing entity and
  before its first tick. Past the cache it falls back to the driver's normal asynchronous load. What survives an
  unload is exactly what survives a restart (the `Persist` channel), and a cell holding a joined player's entity is
  pinned and never evictable. `ICellEvictionHost` extends `ICellPersistenceHost` with `CanEvictCell`, `EvictCell`
  and `TryReadEvictionSignals`, and `ShardedWorldServer` implements it.

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
  - **Wire-format generation (enforced automatically since 10.2.0).** `MoveProtocol.WireProtocolVersion` (= 10)
    labels
    the incompatible on-the-wire generations. 1 was the pre-10.0.0 32-bit line, and 2 was 10.0.0 widening `NetId` to
    64-bit (the snapshot/delta id field and the frame header, `[localNetId:long][ackSeq:int]`, grown 8 -> 12 bytes).
    Generations 3 to 7 each added a field to the movement built-in codec, which is NOT length-prefixed, so an old
    client cannot skip the extra bytes: 3 added `MovementState.Swimming`, 4 `MovementState.TeleportEpoch`, 5
    `MovementState.ClimbRateQ`, 6 `MovementState.SpeedScaleQ` (the per-entity speed scale), and 7
    `MovementState.HorizontalVelocityXQ` / `HorizontalVelocityZQ` (the carried airborne arc, so a client corrected
    mid-flight rebuilds it from the wire instead of resetting it). 8 added a whole new built-in component,
    `PickupState` at id 5 (world pickups, see below): unframed like every built-in, so a client whose registry has no
    id 5 would hard-fail its decode the first time a pickup entered its area of interest, mid-session rather than at
    connect. 9 was the floating-origin wire (`ReplicatedPosition` as a frame stamp plus a frame-local offset, see
    below), and 10 is authoritative facing: the move frame's `run` byte became a flags byte (bit 1 is
    `MoveCommand.FaceCamera`, and `MoveSize` stays 18) and the movement built-in gained
    `MovementState.FacingYawQ`, one bump for both. There is no
    dual-format wire, so peers on
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
Target a player by `PlayerRef.Slot(n)` or `PlayerRef.Account("...")`. `SetSpeedScale(PlayerRef, float)` rides the
same queue on both heads (it is a gameplay mutation, not an admin action, so it is not on `IAdminControllable`).
See "Per-entity speed scale" below.

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

`WorldClient` surfaces it as the **`LocalTeleported`** event plus a monotonic **`LocalTeleportEpoch`** counter (poll it
frame-to-frame if you prefer). A consumer uses it to snap the follow camera (`FollowCamera3D.Warp`) and optionally run
a screen transition (see `KhaozEngine.Render3D` `ITransition`). Mismatched wire generations are rejected at connect by
the always-on `WireGenerationAuthenticator`.

The signal means the local player's position changed DISCONTINUOUSLY: the join placement, an in-session server
teleport, or a reconnect that resumed the session somewhere else. **A transport reconnect that resumes the same
position does not fire it** (#409). Prediction is still reseeded across the reconnect (fresh transport, fresh server
slot, fresh net id, fresh authoritative entity), but none of that moves the player, so a consumer answering the event
with a world-scale reaction - re-centring a terrain streamer's ring, rebuilding an occlusion cache - no longer pays it
every time a lossy link drops. The resume counts as a teleport only when the player moved by at least
`PredictionSettings.HardSnapDistance` while the client was away. Tighten it via `WorldClientConfig.Prediction`.

`RemoteTeleports` follows the same advance-only rule: a remote's replicated epoch going backwards (its `MovementState`
momentarily unreadable) is not a teleport, and neither is the recovery back off that dip.

## Per-entity speed scale: haste, slow, root (since 14.26.0)

One player can move at a different speed from everyone else on the server, for as long as the game says, with the
client predicting it and the server staying authoritative.

```csharp
server.SetSpeedScale(PlayerRef.Slot(slot), 5f);   // haste
server.SetSpeedScale(PlayerRef.Slot(slot), 0.5f); // slow
server.SetSpeedScale(PlayerRef.Slot(slot), 0f);   // root
server.SetSpeedScale(PlayerRef.Slot(slot), 1f);   // back to normal - this is how a buff expires
```

**The engine owns the multiplier and its plumbing, not the buff.** There is no duration, no stacking, no expiry and
no concept of what granted it: the game drives all of that and ends a boost by calling the setter again with `1f`,
exactly as `Teleport` moves a player without owning any concept of why. Two effects at once are the caller's
product to compute.

- **Where it lives.** `MoveState.SpeedScale` is what the step reads, and it replicates as `MovementState.SpeedScaleQ`.
  Both are needed: `PlayerMoveState.From` rebuilds the client's reconcile basis from the replicated components
  ALONE, so a scale living only on `MoveState` would reset on every correction and the pending command window
  would replay at base speed for the whole duration of the buff.
- **Wire cost is one byte,** quantized as the OFFSET FROM 1 at `MovementState.SpeedScaleQuantum` (1/16), so
  `SpeedScaleQ == 0` decodes to exactly 1.0 - which is what every unboosted player carries on every tick. The
  quantum is a power of two, so 0, 0.5, 1, 1.5, 2, 5 and 8 all land exactly and both heads agree bit-exactly. The
  setter clamps to `[0, MovementState.MaxSpeedScale]` (8) and quantizes BEFORE the sim sees the value, so the
  server never runs a speed it cannot describe to its clients (a requested 1.1x becomes 1.125x on both ends).
- **Server-authored only.** It is deliberately NOT on `MoveCommand`, which is what the client sends: a hostile
  client would set its own multiplier. `SetSpeedScale` is the only author.
- **The anti-cheat knows about it** (since 14.27.0 by reading the velocity the step reports, rather than any
  per-term reconstruction: `MoveState.CommandedVelocity`, which was the scalar `CommandedSpeed` field until
  16.0.0). `MovementAnomaly.CorrectionDistance` folds the scale into its intended-target
  calculation. Without that, a legitimately hasted player steps far past an unscaled target and every boosted tick
  reads as a large correction, so the streak reports them as a speed hacker. A boosted client fighting a wall or a
  play-area bound still raises the signal.
- **Composes, never replaces.** It multiplies into the existing speed product alongside the grounded/`AirControl`
  term and the medium's wade scale. So a hasted player who jumps travels correspondingly further horizontally
  (jump HEIGHT is untouched - this is a horizontal scale), and the boost persists into a swim. Under
  `MoveTuning.AirMomentum` the airborne composition drops the `AirControl` term (see the next section), and a
  scale change mid-flight no longer retunes the committed arc.
- **NPCs get it free.** The value lives on `MoveState`, so anything stepping through `CharacterMovement` - a
  server-only creature driven by `StepTowards`, a single-player controller - can set it directly and rides no wire.
  Only what replicates is bound by `MaxSpeedScale`.

## Airborne momentum (since 16.0.0, opt-in)

`MoveTuning.AirMomentum` (default `false`) makes a jump travel its whole arc at the speed it launched at. The
locomotion model itself is in `KhaozEngine.Locomotion/README.md`. What NetWorld adds is the replication that makes
it survive a correction, plus one anti-cheat change that had to land with it.

- **`MoveState.HorizontalVelocity` replicates** as `MovementState.HorizontalVelocityXQ` / `HorizontalVelocityZQ`,
  two `short`s quantized at the fixed `MovementState.HorizontalVelocityQuantum` (1/256, so 0.0039 m/s resolution
  and a +/-127.996 m/s reach, clamped to `MovementState.MaxHorizontalSpeed` of 127 per axis). Same reason
  `SpeedScaleQ` rides the wire, with a sharper failure: `PlayerMoveState.From` rebuilds the reconcile basis from
  the replicated components ALONE and `ClientPrediction.Reconcile` overwrites unconditionally, so a carried
  velocity missing from the seed does not lag, it RESETS to zero on every correction. A client corrected
  mid-flight would drop its arc, rebuild one from whatever the command happened to be, and replay the pending
  window on that, every time a correction lands.
- **The quantum is a power of two on purpose.** A carried velocity is simulation state on BOTH heads and feeds
  the next tick, so unlike a per-tick signal its error does not wash out on the following frame, it compounds for
  the length of the flight. At 1/256 the decode is exact and both heads hold the same float. The two axes are
  quantized independently, so a decoded SPEED can sit up to about 0.003 m/s off the encoded one.
- **The anti-cheat had to move off the scalar.** `MoveState.CommandedSpeed` (a `float` field) became
  `MoveState.CommandedVelocity` (a `Vector2`), with `CommandedSpeed` surviving as a computed property.
  `MovementAnomaly.CorrectionDistance` used to pair the exported speed with the COMMAND direction, and under
  momentum the direction of travel is the conserved velocity: a player who releases input mid-flight at 30 m/s
  keeps flying at 30 m/s while the command collapses to zero, so the intended target sat back at the capsule and
  the whole legitimate arc measured as a full-speed denial on every airborne tick. A momentum flight would have
  been reported as speed hacking within a few frames. Reading the exported velocity is exact under both models: with
  momentum off it is exactly `moveDir * CommandedSpeed`. A momentum flight driven into a wall is still measured
  and still raises the signal.
- **`MovementState.CommandedSpeed` is renamed to `CommandedVelocity`** and retyped to `Vector2`. It is the sharded
  head's sim-local persistence slot for the same value, rides no codec, and so costs no wire bytes.
- **This is a wire break: generation 6 -> 7.** Client and server must ship together, gated at the handshake by
  the always-on `WireGenerationAuthenticator`.

## Authoritative facing and the landing seam (since 17.26.0)

Phase 1 of `docs/design/PHYSICS-LOCOMOTION-DESIGN-2026-08-02.md`. The locomotion half (the `FaceCamera` flag, the
`FacingYaw` convention, the `FacingTurnSpeed` knob, the `LandingImpactSpeed` latch) is in
`KhaozEngine.Locomotion/README.md`. What NetWorld adds is the replication, the server read seam, and the two
render-state exports.

- **`MovementState.FacingYawQ`** replicates `MoveState.FacingYaw` as a `short` at the fixed
  `MovementState.FacingYawQuantum` (`pi / 32768`, one 65536th of a full turn), so the `short`'s entire range is
  exactly one revolution, every representable value is a legal heading, and the resolution is 9.6e-5 rad (0.0055
  degrees). `QuantizeFacingYaw` / `DecodeFacingYaw` are the pair. Decoded 0 is exactly 0 radians, a legal heading
  facing -Z rather than a sentinel, so a spawn, a missed `TryGet` and a pre-facing save all read as facing
  forward. A plain scaled encoding is right here where `SpeedScaleQ` needed an offset-from-1 one, for the same
  underlying reason: the zero default has to mean the harmless thing.
- **`QuantizeFacingYaw` WRAPS rather than clamps.** An angle has no out-of-range VALUE, only a non-canonical
  representative, so clamping a character handed `3*pi` would park it at the range's edge facing the wrong way,
  and since the heading is carried state it would never self-correct. The one integer that can fall out of range
  is `pi` itself (32768 units), and the unchecked cast wraps it to -32768, which decodes to `-pi`: the same
  heading, so the wrap is correct arithmetic rather than a defect.
- **The quantum is an exact negative power of two multiple of `pi`,** for `HorizontalVelocityQuantum`'s reason and
  with the same bite. A heading is simulation state that FEEDS THE NEXT TICK, so a decimal quantum's rounding
  inside the multiply would not wash out on the following frame, it would sit between the two heads for the whole
  length of a turn.
- **It has to ride the wire at all** for `HorizontalVelocityXQ`'s reason: `PlayerMoveState.From` rebuilds the
  reconcile basis from the replicated components ALONE and `ClientPrediction.Reconcile` overwrites
  unconditionally, so a heading missing from that seed does not lag behind the server, it RESETS to 0 on every
  correction and the character restarts its turn from due -Z several times a second.
- **`MoveCommand.FaceCamera` rides the move frame's flags byte** (bit 0 run, bit 1 faceCamera), which was a bare
  run bool through generation 9. Packing rather than appending is load-bearing: the client-to-server demux keys a
  move on LENGTH 18 (the game-message encoder pads specifically to avoid landing on 18), so a 19-byte move frame
  would have been read as a game message by every server. Unknown flag bits are ignored rather than rejected: a
  frame with a stray bit is still a well-formed move, and rejecting it would drop that client out of the sim
  entirely instead of mis-reading one command bit.
- **`WorldServer.OnAfterTick(float dt)` / `ShardedWorldServer.OnAfterTick(float dt)`** (`event Action<float>?`,
  no-op until subscribed) are the mirror of `OnBeforeTick` and the read seam for `MoveState.LandingImpactSpeed`
  (`TryGetPlayerState(slot, out var p)` then `p.Move.LandingImpactSpeed`). The next tick overwrites the latch, so
  the same work done from `OnBeforeTick` always measures the previous tick's world.
- **One semantic on both heads: it fires after frames in which authoritative movement RAN.** `WorldServer` steps
  unconditionally, so every `Tick` qualifies and the qualifier costs it nothing. `ShardedWorldServer` drives its
  cells off a fixed-tick accumulator, so a frame shorter than `TickSeconds` produces no movement sub-tick and the
  hook stays silent. Without that, a short frame would re-deliver the previous tick's landing once per short
  frame, which is a DUPLICATE application of fall damage rather than a missed one. It is gated on the sum of every
  cell's tick counter taken either side of the cell step, because cells hold independent accumulators and no
  single cell's counter answers for the frame.
- **`MovementState.LandingImpactSpeed`** is the sharded head's SIM-LOCAL slot for the same value (the
  `ClimbRateEwma` precedent), because `PlayerMovementSystem` rebuilds a fresh `MoveState` from the component every
  tick and a step OUTPUT has nowhere else to survive to the end of `Tick`. It is deliberately absent from the
  movement codec and from the `Migrate` capture, so it costs no wire bytes, is always 0 on a client, and a landing
  that coincides with a cell handoff drops the one-tick signal. `PlayerMovementSystem` zeroes it explicitly for a
  `Ghost` or `Migrating` entity its cell sim skipped, so a skipped tick cannot leave a stale impact behind to read
  as a landing that never happened. Carried state (the heading, the arc, the swim flag) is deliberately NOT zeroed
  there, because zeroing a carried field on a skipped tick would spin every ghost back to facing -Z.
- **`MovementState.SupportGranted`** (17.29.0) is the sharded head's SIM-LOCAL slot for
  `MoveState.SupportGranted`, on the same reasoning and with the same handling (absent from the codec and from
  `Migrate`, cleared explicitly for a skipped `Ghost` or `Migrating` entity). It says whether the step RESOLVED
  support this tick, read before the jump consumed it, which is the fact `Grounded` beside it cannot carry: a
  player holding the jump button reports `Grounded` false on every tick of a hop cycle, so a support grant on
  ground a character should never have found footing on is invisible to a server-side anomaly check
  ([#468](https://github.com/APKiwiOrg/KhaozEngine/issues/468) measured a 21 m cliff climb reading zero footing
  grants while taking one every few seconds). `false` on a client and across a handoff, which reads as "found no
  footing this tick" - the safe direction, since a missed grant costs one sample and a fabricated one accuses a
  legitimate player.
- **`EntityRenderState.FacingYaw` and `EntityRenderState.LandingImpactSpeed`** are the client-side exports. The
  heading is predicted un-quantized for the local player and the decoded replicated value for a remote,
  discrete-sampled to the same delayed render time as the interpolated position, so a remote's flags, heading and
  feet never skew apart. It is exactly the feed `CharacterSample.FacingYaw` / `ReplicatedCharacterAnimators` want,
  though the two bases sit half a turn apart (the bridge reads 0 as +Z, the sim reads 0 as -Z) and nothing wires
  them for you: pass it through `WithFacingYaw` and carry the half turn on
  `CharacterAnimatorTuning.FacingYawOffset`. `EntityRenderState` gained two constructor overloads for the pair, so
  every existing construction site compiles and reads 0 for both.
- **This is a wire break: generation 9 -> 10.** One bump covers the flags byte and `FacingYawQ` together. The
  movement built-in is not length-prefixed, so an old client cannot skip the two bytes and is rejected cleanly at
  connect by the always-on `WireGenerationAuthenticator`. **Client and server must ship together.**

## Island frames and the frame-relative wire (the floating-origin MAJOR)

Simulating at 100 km from the world origin costs precision: float32's quantum out there is 7.8 mm, and the
movement step's carried state accumulates it every tick (measured on production code at about 1.7 m of divergence
per 20 s at 100 km, against 0 m at the origin). An ISLAND FRAME removes the magnitude rather than widening the
type. A simulation island is one `World` plus one `IPhysicsWorld`, and a frame is a property of that SPACE, never
of an entity in it. `WorldServer` is exactly one island, so it has one frame and follows one player.
`ShardedWorldServer` has one island per cell, which is what lets a shard server serve players spread over a whole
100 km map.

**ON by default on both heads**, because the wire now carries the frame so both heads step in the same space. A
game that never leaves the world origin never re-anchors, and its cell (0,0) frame is `WorldFrame.Origin`, whose
anchor is exactly `Vector3.Zero`, so the default path there is byte-identical to before.

### Breaking, in one place each

- **`ReplicatedPosition.Value` is READ-ONLY.** The component is a frame stamp (`Frame`, a `WorldFrame` from
  `KhaozEngine.Primitives`) plus a frame-local offset (`Local`), and `Value` reads the absolute world position off
  them. Every reader is unchanged. Every WRITER is now a build error, deliberately: a `Value = p` write reset the
  stamp silently, which was recoverable while the wire was absolute and is not once the stamp rides it. The fix at
  each site is one question - where did this position come from? - answered by `FromWorld(absolute, frame)` (from
  outside the sim: an authored spawn, a persisted record, an admin teleport) or `InFrame(frame, local)` (out of
  the sim, or out of a physics world already in that frame). Move one with `WithLocal` / `ToFrame`.
- **Wire generation 9.** Position encodes `[frameX:short][frameZ:short][localX,localY,localZ:float]`, 16 bytes
  against 12. The four extra bytes buy a float payload bounded at a couple of hundred metres, so the wire stops
  being the first thing that quantizes a position. Client and server must ship together, which the always-on
  `WireGenerationAuthenticator` enforces at connect.
- **`ShardedWorldServer` no longer takes an `IPhysicsWorld`.** It takes
  `ShardedWorldServerConfig.PhysicsWorldFactory`, a `Func<CellCoord, IPhysicsWorld>` called once per cell. One
  world cannot serve cells that step in different frames.
- **`WorldClient` refuses a physics world that cannot rebase**, the mirror of `WorldServer`'s own guard: the
  client adopts the server's frame and its physics world's `Origin` has to move with it.
- **Persisted cell blobs are brought forward, not lost.** `PositionFrameBlobMigration.FrameV2ToV3` is folded into
  `CellPersistence`'s default chain, so an existing save boots straight into a framed server. It stamps
  `WorldFrame.Origin` ahead of the untouched absolute triple, which denotes the identical world position, and the
  owning cell converts it into its own frame on restore.

### The knobs

- **`WorldServerConfig.FrameAnchoring` / `ShardedWorldServerConfig.FrameAnchoring`** (both default true). On the
  flat head the island re-anchors to follow one player. On the sharded head each cell's frame is
  `WorldFrame.Nearest(cell centre)`, FIXED at the cell's creation, so that head performs no runtime rebase at all:
  an entity's frame changes only at a cell handoff, which `ShardHost.ProcessHandoffs` already runs as a discrete,
  exactly-once, ordered event. Read a cell's frame from `CellSim.Frame`, or ahead of time from
  `ShardedWorldServer.FrameFor(coord)` / `ShardHost.FrameFor(coord)`.
- **`ShardedWorldServerConfig.PhysicsWorldFactory`** builds each cell's own world. The consumer populates it, and
  the engine never adds a static to a cell world. Four points: it must hold every static within
  `CellSize / 2 + OverlapMargin` of the cell centre, its poses are relative to an `Origin` of
  `FrameFor(coord).Anchor`, it is disposed with the cell, and a static near a border legitimately exists in both
  neighbours' worlds (nothing reconciles the copies, because a static does not move). **With `FrameAnchoring` on,
  `ShardHost` validates the returned world's `Origin` against the cell's own anchor at creation and throws (naming
  the factory) on a mismatch** - left unchecked, every character the cell owns would silently no-clip, since
  `CharacterMovement` reads frame-local coordinates with no compensation for a wrong origin. With `FrameAnchoring`
  off a world left at `Vector3.Zero` is correct (every cell's anchor is `WorldFrame.Origin` then) and the guard
  never fires.
- **`SamplerSpace`** (on both server configs and on `WorldClientConfig`, and it must MATCH across them) says which
  space the game's sampler delegates read. `SamplerSpace.World` (the default) keeps them on absolute coordinates
  and the step converts for them: zero adoption work, and it still fixes the accumulating half, because the
  carried state is what compounds. `SamplerSpace.Frame` passes frame-local coordinates straight through, which is
  the full fix and is REQUIRED for a sampler backed by the island's own physics world (a `PhysicsGroundProbe`
  raycasts in that world's own space, so wrapping the call back out to absolute makes every ray miss).
  `WorldBounds` is the exception neither mode governs: a play area is authored content and stays absolute, so the
  step converts for it either way.
- **`CellSize` is validated against the divergence ceiling** when frame anchoring is on. A cell's frame sits at its
  centre, so its worst frame-local coordinate is bounded by its own size. `CellSize = 600` puts the planar
  magnitude past `WorldFrame.MaxLocalRadius` and is refused with the derivation in the message.

### Everything a consumer sees stays ABSOLUTE

`TryGetPlayerState`, `PlayerLeaving`, `ListOnline`, `ReplicatedPosition.Value`, cell keying, the interest grid,
`WorldClient.Snapshot()` and `WorldClient.LocalRenderState` all read absolute world metres, whatever frame an
island is simulating in. `PlayerMoveState.FrameAnchor` (stamped by `PlayerMoveSimulator.Step`) says which space a
state is in, and `ToAnchor` / `Absolute` convert. A state crossing the public surface carries `Vector2.Zero` with
an absolute position, so the two can never disagree.

The client-side rule is worth stating on its own, because breaking it produces no compile error and no exception:
the local avatar comes out of prediction frame-local while every remote comes out of `Value` absolute, both as a
`Vector3` in the same `EntityRenderState` list. The avatar would simply render an anchor delta away from the world
it is standing in.

### The frame is reachable from a `World` alone

A cell (and a framed flat head) publishes its frame into its own `World` as an `IslandFrame` singleton on a
reserved entity carrying no `NetId`, so anything holding only a world can read it with `world.GetIslandFrame()`
(from `KhaozEngine.Sharding`). That is what `WorldPickups`' spawn callback, a consumer's `OnBeforeTick` brain, a
cross-border ghost reader and `DynamicBodyReplication` all use, instead of four separate signature changes.

- **`DynamicBodyReplication.Sample` stamps rather than converts.** A pose comes back in the physics world's space,
  which IS the island's frame, so it is written with `InFrame`. Writing it as an absolute would re-quantize it at
  world magnitude, undoing exactly what the frame bought.
- **Every door an entity enters a cell by converts**: spawn, handoff (`CellSim.AdoptFromMigrate`), persistence
  restore, admin teleport, and ghost mirroring (`CellSim.ApplyGhostSnapshot`). The step loop carries a self-heal as
  a backstop, so a miss at any door is corrected exactly on the next tick rather than becoming a 128 m step - but
  it deliberately skips ghosts, which is why the ghost door converts on the mirror pass.

### Client prediction across a shift

`IPredictedState<TSelf>` gained `FrameAnchor` and a throwing `WithFrameAnchor` default member, and
`ClientPrediction.Reconcile` converts the carried presentation state into the incoming basis's frame before it
measures anything. Without that, an island re-anchor - a no-op in world space - would measure as a 128 m
prediction error, trip the hard-snap gate, and then glide the avatar a frame-width across the screen while the
render offset decayed. `WorldClient` adopts the stamp off the wire, rebases its own physics world with it, and
raises `WorldClient.FrameChanged` (from, to, delta) BEFORE the replay, so a consumer's own collider bookkeeping is
correct for the replayed steps. A consumer that only uses the engine's positions needs no handler.

### The re-anchor policy (single-island heads)

`WorldFrame.Grid` is 128 m and the trigger is a local axis past `WorldFrame.ReanchorRadius` (96 m), which
guarantees at least 64 m of travel between consecutive re-anchors. The island re-anchors after the tick's movement
has settled and before anything reads a position back, so no step ever observes a half-rebased island.

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
  clients the same tick. Its mirror **`OnAfterTick`** (17.26.0, both servers) fires at the END of a tick, after
  movement and after every client has been served, and is where post-step state is OBSERVED (the landing impact,
  below). A write made there reaches clients on the next tick's snapshot, so authoring state that must ship in the
  same frame stays `OnBeforeTick`'s job.
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

## World pickups (walk-over collectibles)

A server spawns a world entity, it sits there, a player gets close enough, the server decides whether that player may
take it, and it goes away. Loot drops, resource nodes, health packs, quest objectives and capture points are all that
one shape, and **`WorldPickups`** is it. Works identically over `WorldServer` and `ShardedWorldServer` (both implement
**`IWorldPickupHost`**), and is driven by the consumer, never by the engine.

```csharp
var pickups = new WorldPickups(server, new WorldPickupsConfig
{
    DefaultRadius = 1.5f,
    OnCollect = c => inventory.TryGrant(c.Slot, c.PayloadId),   // true = taken, false = declined
    OnRemoved = r => log.Pickup(r.PickupNetId, r.Reason),
});
server.OnBeforeTick += pickups.Update;   // so a collect's despawn reaches the SAME tick's snapshot

long orb = pickups.Spawn(dropPosition, payloadId: PackItem(itemIndex, quantity),
                         ownerNetId: killerNetId, radius: 1.5f, timeToLiveSeconds: 120f);
```

- **The engine owns the plumbing, not the meaning.** Spawn, replication, the owner tag, the time-to-live, the
  per-tick proximity test and the despawn are engine-side. Items, inventories, rarity and loot tables are not, and
  this introduces none of them: **`PickupState.PayloadId`** is an opaque 64-bit game-defined value carried verbatim to
  clients and handed back on collect, in the same spirit as `Teleport` moving a player without owning any notion of
  why. Pack an item index, an index plus a quantity, or a row id into your own table.
- **The ownership RULE is yours too.** Every collect is an `OnCollect` call that returns whether it was accepted. A
  declined pickup stays standing. Killer-only, party loot, need-before-greed, inventory-full and free-after-a-delay
  are all that one predicate plus **`SetOwner`**. With no handler nothing is ever granted, which is the safe default.
- **`ownerNetId` is a hard engine-side pre-filter** (`0` = unowned): a non-owner is never offered the pickup at all,
  so your handler is not asked about players who could not have it anyway. The engine owns the TAG, you own its value
  over time (`Spawn`, then `SetOwner`, which also re-tags the replicated component so clients can re-tint the orb).
- **Offer policy: once per entry, never per tick.** A player inside the radius is offered the pickup exactly once, so
  a durable no costs one callback rather than one per tick per player per pickup. Three ways to re-offer: **leaving
  and re-entering** the radius (always), **`Reoffer(netId)`** / `SetOwner` when the game KNOWS its decline went stale
  (a loot timer lapsed, a bag slot freed - re-offers next `Update`, standing still, no polling), or the opt-in
  **`WorldPickupsConfig.RetryDeclinedSeconds`** timer for a decline that goes stale without the game noticing.
- **Proximity is a linear scan** over live pickups against joined players, in a deterministic order (pickups by
  ascending net id, players by ascending slot), measured as a **full 3D distance** so a player on the floor above does
  not reach through it. A cylinder, a cone, a facing test or a line of sight goes in `OnCollect` as a decline.
- **`Despawn(netId)` / `DespawnAll()`** remove pickups explicitly, and a time-to-live expires one on its own. Every
  route propagates to clients as a normal AoI removal and raises `OnRemoved` with a `PickupRemovalReason` of
  `Collected` / `Expired` / `Despawned`.
- **Client side.** `PickupState` is a **built-in** replicated component (`MoveProtocol.PickupTypeId` = 5), riding
  alongside the pickup's `ReplicatedPosition` exactly as `DynamicBodyState` does for a physics prop. Read it with
  `WorldClient.TryGetComponent<PickupState>(netId, out _)` to pick a model, a rarity tint, or an "it's yours"
  highlight. Being a built-in it is unframed, which is why adding it bumped the wire generation to 8: adopt client and
  server together.
- **`SpawnEntity`'s missing halves shipped with it**, and are useful on their own:
  **`TryGetEntity(netId, out World, out Entity)`** and **`DespawnEntity(netId)`** on both servers, neither of which
  will ever touch a player entity.

**Persistence hazard, worth knowing before you ship.** `CellPersistence` snapshots every owned non-player entity in a
cell on an interval with **no per-entity opt-out**, so a live pickup can be caught in a save and resurrected on
restart. A restored pickup is a plain entity carrying `PickupState` that the seam knows nothing about: no
time-to-live, offered to nobody, standing forever. The component cannot opt out of the persist channel either, since
built-in ids are pinned to `ReplicationChannels.Default`. A game that persists cells should sweep at boot, before
spawning this run's pickups, which is what `ShardedWorldServer.DespawnEntity` is for (it resolves through the shard
host's ownership index, so it finds restored entities the seam never saw):

```csharp
var stale = new List<long>();
foreach (CellSim cell in server.Host.Cells)
    foreach (Entity e in cell.World.Query().With<PickupState>().Entities())
        if (cell.World.TryGet(e, out NetId id)) stale.Add(id.Value);
foreach (long netId in stale) server.DespawnEntity(netId);
```

`DespawnAll()` is the same-process equivalent and clears only what the seam is currently tracking.

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
