# Tile-world netcode: click-to-walk on a 250 ms tick, server-authoritative, sharded by region (2026-08-22)

Status: designed, unbuilt. Program issue: [#670](https://github.com/APKiwiOrg/KhaozEngine/issues/670). Sub-project 2
of the Grimhollow program, following sub-project 1 ([TILE-WORLD-DESIGN-2026-08-15.md](TILE-WORLD-DESIGN-2026-08-15.md),
[#629](https://github.com/APKiwiOrg/KhaozEngine/issues/629)), whose tile world, collision map, pathfinder and
renderer this design moves players across. Game-side issue:
[Grimhollow #6](https://github.com/APKiwiOrg/Grimhollow/issues/6). Written against engine 17.39.0 and Ruinborne main.

Two rounds, each with its own plan: R1 is the engine package `KhaozEngine.TileWorld.Netcode` with headless loopback
tests, R2 is the Grimhollow heads on it (server, auth, client player and camera).

## 1. Problem

The engine has one MMO movement stack and it is continuous: `KhaozEngine.NetWorld` replicates a float
`ReplicatedPosition` plus a `MovementState` of capsule physics (vertical velocity, grounded, swimming, climb rate),
predicts through `PlayerMoveSimulator` over `Locomotion.MoveState`, and persists a float `PlayerRecord`. The tile
world needs the opposite shape: a player IS a tile, a plane and a facing, movement is a discrete step every N ticks
along a deterministic route, the server resolves one step per tick and the client is allowed to show the walk
before the server confirms it. OSRS is the reference: click, walk at once, the server is right when they disagree.

What can and cannot be reused was verified against the code before any of this was decided (section 3). The short
version: the tick engine, prediction, replication, sharding, persistence store and identity are all generic and
reusable verbatim. The one thing that is not is `NetWorld`'s server and client, which construct the float
integrator internally and take terrain in their constructors.

## 2. Decisions taken in the brainstorm, with rationale

1. **A true tick, 250 ms (4 Hz).** OSRS runs 600 ms. The feel comes from tick-aligned actions, not from smooth
   motion, and 250 ms keeps that feel while making the game read as faster. Movement rates are tick counts: **run is
   one tile per 2 ticks (500 ms per tile), walk is one tile per 4 ticks (1 s per tile)**, diagonal steps cost the
   same as cardinal ones. The alternative, a 30 Hz sim with a 250 ms action cadence layered on it, was declined as
   two clocks to reason about for a smoother remote glide the presenter already provides.
2. **Client-predicted walking, server authoritative.** On click the client runs the same `FindPath` over the same
   baked collision map and starts walking on the next tick. The server runs the identical path. Every snapshot
   carries the tile at the acked tick and the client snaps only on mismatch. Declined: a server-authoritative display
   (one RTT plus a tick of nothing after every click, the thing OSRS visibly does not do).
3. **Sharded from day one, on `ShardHost` and `CellSim`.** A single-process server with a per-region sim as the
   unit was offered as the cheaper seam. The user chose the existing sharding stack, and the adaptation that makes it
   fit without fighting its float grid is to feed it tile coordinates as the plane (section 7).
4. **Discord identity end to end in SP2.** A guest-first client with the token gate in place was offered. The user
   chose the full path: the exchange service, the client OAuth flow through the engine's `IdentitySession`, and a
   whitelist store. All of it is engine plumbing already exercised by Ruinborne, the game-specific part is two files.
5. **Walk plus the interaction seam, no real actions.** Clicking an `Interactive` archetype routes to a reach tile,
   faces the target, and sends one `Interact`. The server validates reach on its tick and runs a one-deep action
   queue whose only action here is a logged no-op with an `OnInteract` event. Declined: walk only (the reach rules
   are where tile MMOs get subtle and they sit on the movement code), and a first real action (stairs as a plane
   change adds multi-plane presentation to a sub-project whose deliverable is walking).

Persistence and the wire protocol were not questions: `SqliteWorldStore` locally, `SqlServerWorldStore` hosted,
one env var picks, the tile record is additive, and the tile protocol is its own framing inside `Netcode`'s
transport.

## 3. What the engine already has, verified

Tick rate is a per-config float, never a constant: `WorldServerConfig.TickSeconds = 1f/30f` (`WorldServer.cs:16`),
`ShardedWorldServerConfig` (`:14`), `WorldClientConfig` (`:10`). `FixedTickHost(float tickSeconds)`
(`Simulation/FixedTickHost.cs:21`) accepts any positive value and sheds backlog past `maxTicksPerFrame = 8`
(`:55`, `:70`). At 250 ms the tick-relative knobs are already close: `InterpolationDelayTicks = 2f`
(`WorldClientConfig.cs:31`) is 500 ms, `DisconnectTimeoutSeconds = 3f` (`:35`) is twelve snapshots, and only
`MaxInputBacklog = 8` ticks (`WorldServer.cs:56`) wants lowering to 2. Snapshot cadence is welded to the sim tick
(`WorldServer.Tick`, `:470-500`), which at 4 Hz is exactly the cadence wanted.

Prediction is pluggable at `Netcode`: `ITickSimulator<TState,TCommand>.Step(in state, in command, float dt)`
(`ITickSimulator.cs:8`), `IPredictedState<TSelf>` (`IPredictedState.cs:10`, a `Vector2 Position`, `float Vertical`,
`uint TeleportEpoch`, `Vector2 FrameAnchor`, `WithPosition`, `WithRenderState`), `ClientPrediction<TState,TCommand>`
(`ClientPrediction.cs:24`, `Predict` `:259`, `Reconcile` `:292` replaying the unacked window through the same
simulator `:331`). `RenderedState` (`:132-144`) already lerps the previous tick to the current one over
`TickSeconds`, which is the tile glide. `RemoteCommandQueue<T>` (`RemoteCommandQueue.cs:18`) is generic and drains
one command per player per tick. **It is baked at `NetWorld`:** `PlayerMoveSimulator` is constructed inside
`WorldServer` (`:191`), `WorldClient` (`:134`) and `ShardedWorldServer.Frame.cs` (`:126`), and all three take
`Func<float,float,float> groundHeight` and `MoveTuning` as required constructor parameters. `PendingMove` and the
server's queue are `MoveCommand`-typed. The client-to-server demux keys the move frame on LENGTH 18
(`MoveProtocol.cs:243-248`), so any new protocol on that transport must not emit 18-byte data frames.

Replication is movement-agnostic: `ReplicationRegistry.Register<T>(typeId, write, read, lerp, channels,
discreteSample)` (`ReplicationRegistry.cs:51`), extension ids from 16 are length-prefixed and skippable (`:24`),
`discreteSample: true` gives nearest-sample with no blending, the right thing for integer tiles. The only float
touchpoint is `InterestGrid.Insert(netId, x, y)` (`InterestGrid.cs:28`), a plain spatial hash.

Sharding: a shard is a square cell `CellCoord(int X, int Y)` from `CellCoord.FromWorld(x, y, cellSize)`
(`CellCoord.cs:12`, `:34`). `ShardHost(cellSize, tickSeconds, registry, interestCellSize, ...)` (`ShardHost.cs:99`)
creates cells on demand. `CellSim` (`CellSim.cs:26`) owns an ECS world, a replicator, an interest grid, its own tick,
border ghosting (`ApplyGhostSnapshot` `:222`) and exactly-once handoff (`AdoptFromMigrate` `:479`,
`ReleaseMigrating` `:502`). `ShardedWorldServer` enforces `InterestRadius <= OverlapMargin` (`:81-84`).

Persistence: `IWorldStore` (`IWorldStore.cs:16`) is a keyed blob store. `PlayerRecord` (`PlayerRecord.cs:15`) is a
version, `float X, Y, Z` and an opaque `byte[]? Game`, tolerant JSON. `WorldPersistence` (`WorldPersistence.cs:200`)
owns the save interval, dirty pass, quarantine, guest policy and resume hints, and is driven through
`IWorldPersistenceHost`. Both store backends share one `world_store(key, data, updated_at)` table
(`SqliteWorldStore.cs:33`, `SqlServerWorldStore.cs:45`). Ruinborne picks by `RUINBORNE_SQL_CONNECTION` and has never
used the Sqlite backend (`Ruinborne.Server/Program.cs:161-165`).

Identity: `IIdentityValidator`, `VerifiedIdentity`, `IdentitySession`, `SessionToken.Mint/TryVerify` and the Discord
provider are engine. The connect gate is `IConnectionAuthenticator` (`Netcode/IConnectionAuthenticator.cs:12`) with
`HmacTokenAuthenticator` (`SignedToken.cs:229`) and `AllowAllAuthenticator`. Ruinborne's `ConnectionGate.Wrap(inner,
protocolVersion, worldHash, log)` (`Ruinborne.Server/Auth/ConnectionGate.cs`) and its two-file `Ruinborne.Auth`
exchange (`AuthExchange.cs:28-51`) are the only game-side pieces.

Interaction: nothing. `Simulation` is a tick host and two job schedulers, `Ecs` is a pure archetype ECS, and the
only proximity code is `WorldPickups`' float-radius auto-collect. `TileArchetype.Interactive`
(`TileWorldCatalogs.cs:72`) is an authored flag nothing consumes.

Tile world: `TileCollision.CanStep(map, x, z, plane, dir, agentSize)` (`TileCollision.cs:19`) and
`TilePathfinder.FindPath(map, plane, start, goal, agentSize, maxRadius)` (`TilePathfinder.cs:54`) are deterministic
and were written for server-authoritative replay (`:35-36`). The collision map is derived by
`TileCollisionBaker.Bake(doc, catalogs)` (`TileCollisionBaker.cs:14`) from a GPU-free `TileWorldFile.Load(directory)`
(`TileWorldFile.cs:125`), so both heads bake their own from the same files. `TileWorldHash` identifies a world
build. `FindPath` allocates its scratch per call, [#669](https://github.com/APKiwiOrg/KhaozEngine/issues/669).

## 4. Package plan

One new package, `KhaozEngine.TileWorld.Netcode`, GPU-free, referencing `TileWorld`, `Netcode`, `Replication`,
`Sharding`, `Simulation` and `WorldStore`. It does NOT reference `NetWorld`: the two movement stacks are siblings
over the same generic layers, and pulling `NetWorld` in would drag `Locomotion` into every tile server. It joins the
`Server` umbrella (server half) and `Game3D` (client half) the way `NetWorld` does, decided per type at the plan
stage by which head constructs it.

Two engine changes outside the package:

- `ConnectionGate` moves from Ruinborne into `Netcode` as `ConnectionGate(inner, protocolVersion, worldHash, log)`
  (engine-first rule: two games need the identical gate). Ruinborne's copy becomes a one-line alias until its next
  repin.
- `WorldPersistence`'s core becomes record-agnostic (section 8). `NetWorld.WorldPersistence` keeps its name, its
  public surface and its tests, as the float binding over that core.

## 5. State, commands and the simulator

`TileMoveState : IPredictedState<TileMoveState>`: `TileCoord Tile` (x, z, plane), `TileDirection Facing`,
`TileMoveMode Mode` (Walk, Run), `byte StepTicks` (ticks spent in the current step), `TileRoute Route` (an immutable
tile array plus the index of the next tile, `TileRoute.None` when idle), `uint TeleportEpoch`, and the pending
interaction target. The contract members derive from those: `Position` is the tile position plus the step fraction
toward the next route tile (`StepTicks / TicksPerStep` along the step's delta), `Vertical` is the plane height,
`FrameAnchor` is zero (no frame anchoring on a tile world, the coordinates are small integers). The route is a
reference to an immutable array and is recomputed deterministically on replay, so carrying it in the state keeps the
state a value and the simulator pure.

`TileCommand`, sent once per tick exactly like today's move command, a dozen bytes at 4 Hz: `Kind` (None, WalkTo,
Interact), `TileCoord Goal`, `TileMoveMode Mode`, `long Target` (net id or object handle for Interact). `None` means
"keep doing what you are doing", which is the command the client sends while a route plays out, and what makes
`ClientPrediction`'s one-command-per-tick sequence model fit without a "walking" flag on the wire.

`TileMoveSimulator : ITickSimulator<TileMoveState, TileCommand>` owns the shared inputs (the collision map, the
document for footprints, the tick counts) and is the ONE stepper both heads run:

- `WalkTo`: `FindPath(map, plane, state.Tile, goal)` into a new `Route`, mode taken from the command, and the tick
  that carries the command COUNTS as the first tick of the first step, so a click never costs a tick of standing
  still. An unreachable goal walks to the nearest reachable tile, the OSRS rule `FindPath` already implements. A
  goal on another plane is dropped whole rather than pathed on the player's own plane.
- `Interact`: `TileReach.Nearest(map, plane, state.Tile, targetFootprint)` picks the reach tile, routes to it, and
  records the target so arrival faces it and raises the action.
- `None`: take the mode from the command (it rides on every kind, and a change lands at the START of the next step
  rather than re-cutting the one under way), then advance `StepTicks`, which EVERY tick does, a tick carrying a
  command included. When it reaches `TicksPerStep(Mode)` (2 run, 4 walk), re-check `CanStep` from the
  current tile into the next route tile. Legal: move, face the step direction, reset `StepTicks`, advance the
  index. Blocked (a dynamic blocker appeared): re-path once from the current tile to the route's end, and if that
  also fails, drop the route and stand. Both heads run this identically, so a blocker only causes a mismatch when
  the two heads saw different blockers, which is exactly the case the reconcile snap is for.

Remote players replicate `TileMoveState` as an extension component with `discreteSample: true`, and the presenter
(section 9) glides them across a step over its tick count from the previous sample, so a remote at run speed is
seen stepping every second snapshot with motion in between.

## 6. Reach and the action seam

`TileReach` is a pure function over the collision map and a footprint. The reach set of an N x M footprint on a
plane is every tile cardinally adjacent to a footprint tile for which `CanStep` FROM the footprint tile OUT onto
that tile is legal, which asks three things: no wall on the footprint tile's edge, the candidate is not blocked,
and no mirrored wall on the candidate's own edge facing back. That one rule encodes OSRS's behaviour: a wall
between you and the booth denies reach, a diagonal never counts, a candidate nobody could stand on is not offered,
and a 2x2 object has up to eight reach tiles minus the denied ones. `Nearest` orders them by BFS distance from the
player (the pathfinder's own distance field), then by the same scan order `FindPath` uses, so both heads choose the
same tile.

This was written INWARD in an earlier draft (`CanStep` from the candidate into the footprint tile) and inward is
wrong twice over, because a step never inspects the BLOCKED flag of the tile it leaves. Starting on the candidate
puts the blocked test on the TARGET, and every real target is blocked, being a booth or a rock, so the reach set of
anything worth clicking comes out empty. It also never tests the candidate for being blocked at all, so a solid
tile beside an unblocked target (a doorway, a ladder) is offered as somewhere to stand. Outward asks the same two
wall questions, the same pair either way round, and puts the blocked test on the candidate where reach needs it,
which is the form that stays correct when the target itself is solid. `TileReach`'s class doc carries the same
reasoning, so the direction is not flipped back later.

Server side, `TileActionQueue` holds at most one pending action per player: `(target, kind, issuedTick)`. On each
tick after movement, a pending action whose player stands on a reach tile of the target, on its plane, is
validated and raised through `TileWorldServer.OnInteract(playerNetId, target)`, then cleared. Reissuing a command
(another click) replaces the pending action, OSRS style. In SP2 the only consumer of `OnInteract` is a log line,
and the Grimhollow client shows a localized "nothing interesting happens". When `TileReach` returns no reachable
tile, the server sends a `CannotReach` game message, and the client, which has the same map, pre-checks on click
and shows it immediately without waiting.

## 7. Server

`TileWorldServer` is built on `ShardHost` and `CellSim` with **tile coordinates as the plane**: the host is
constructed with `cellSize = TileRegion.Size` (64) and every insert, query and `CellCoord.FromWorld` call takes
`(tileX, tileZ)` as the floats. A cell is then exactly a `RegionCoord`, floor division on integers with no negated-z
off-by-one at boundaries (`TileWorldSpace` negates z for rendering only and is never consulted here). Interest radius
is 15 tiles (OSRS's view distance) and the overlap margin 16, satisfying `InterestRadius <= OverlapMargin`. Planes do
not shard: a cell holds every plane of its region and interest is filtered by plane in the snapshot.

Per cell: the ECS world with `TileMoveState` (replicated, extension id assigned in `TileProtocol`), `TileIdentity`
(display name, replicated), a `RemoteCommandQueue<TileCommand>` per resident player, and the cell's own replicator
and interest grid serving AoI snapshots. The tick order inside a cell is: drain one command per player, step every
player through `TileMoveSimulator`, resolve the action queue, capture and replicate. Ghosting and exactly-once
handoff across a region boundary are `Sharding`'s as they stand, triggered when a step lands on a tile in another
region. `OnBeforeTick`, `OnGameMessage` and `OnInteract` are the game seams, drain (`BeginDrain(notice, grace)`) and
the ban store are the operational ones, copied in shape from `ShardedWorldServer`.

Known and accepted cost: `ShardedWorldServer`'s connection lifecycle, residency and rate-limiting plumbing is
re-implemented here for tiles rather than extracted into a generic core. Extracting it from Ruinborne's live server
is a refactor of a shipping MMO stack and is not this sub-project. It is filed as a follow-up the moment the two
servers are seen to converge.

## 8. Persistence

`WorldPersistence`'s behaviour is what a tile server needs and is too subtle to duplicate: save every N seconds,
save dirty records on a pass, quarantine a record that fails validation instead of overwriting it, do not persist
guests, resume hints for a player who reconnects mid-save. The core becomes generic over the state and record types
and lives beside `IWorldStore` (the plan decides the exact package, `WorldStore` is the natural home since it is the
seam both bindings share), and `NetWorld.WorldPersistence` keeps its name, its public surface, its config type and
its tests as the float binding. `TilePlayerRecord` is `Version`, `TileX`, `TileZ`, `Plane`, `Facing` and the opaque
`Game` blob, tolerant JSON through a source-generated context like `PlayerRecord`. The key stays
`player:{accountId}` where the account id is the verified token subject. `SqliteWorldStore` locally and in tests,
`SqlServerWorldStore` hosted, one env var picks, fail-closed when the hosted store is set without the token secret,
the Ruinborne rule.

## 9. Client

`TileWorldClient`: connects with the signed token through the gate, owns `ClientPrediction<TileMoveState,
TileCommand>` over the same `TileMoveSimulator` built from the same world files, sends one `TileCommand` per tick
from its own `FixedTickHost` phase-offset from the server's, applies snapshots through `ClientReplicationView` for
remotes and `Reconcile` for the local player, and raises `CannotReach`, `RefusedAtDoor(reason)` and `Disconnected`.
`TilePresenter` is the bridge to the view: from a `TileMoveState` plus the rendered step fraction it produces a world
position and yaw through `TileWorldSpace`, for the local player from `ClientPrediction.RenderedState` and for remotes
from the view's samples, gliding a remote across a step over that step's tick count. Nothing in the client touches
the GPU, so the whole of it is headless-testable, and the Grimhollow head only feeds it clicks and draws what the
presenter says.

## 10. Protocol and the connect gate

`TileProtocol` defines the wire: the connect payload (token bytes, protocol version, world hash), `TileCommand`
framing with the sequence number, the snapshot frame (`[localNetId][ackSeq][replication snapshot]`, the shape
`MoveProtocol.EncodeSnapshotFrame` uses), the game-message envelope (`kind` plus payload, length-capped), and the
extension component ids. It shares no bytes with `MoveProtocol` and is never demuxed on length. `ConnectionGate` in
`Netcode` refuses at the door with a reason token the client localizes: wrong protocol version, wrong world hash,
bad or expired token, banned.

## 11. Grimhollow heads (R2)

`Grimhollow.Shared`: port, protocol version, `TickSeconds = 0.25f`, `RunStepTicks = 2`, `WalkStepTicks = 4`.
`Grimhollow.Server`: the Ruinborne host shape, in order: session log, load the world headless, bake collision, shard
config, store by env var, token auth and gate, `TileWorldServer`, persistence, tick loop, drain on SIGINT and
SIGTERM, heartbeat, into the existing Dockerfile. `Grimhollow.Auth`: Ruinborne's two-file Discord exchange over
`IIdentityValidator`, plus a minimal accounts store (subject, display name, whitelisted, banned) on the same Sqlite
or SqlServer choice. Client: a login screen (Discord sign-in through `IdentitySession`, every string through the
catalog), the fly rig replaced by a player with an OSRS-style orbit camera, left click on the ground through
`TileRaycast.Pick` sends `WalkTo`, on an `Interactive` archetype sends `Interact`, a run toggle, remote players as a
placeholder kit piece with name plates, and the cannot-reach line. Presence ("in Hollowmere") is the existing `KhaozEngine.Social`
`SocialPresenceController` over `Social.Discord`'s `DiscordSocialProvider`, wired in R2 with no new engine code. Dev
loop: the fly camera stays reachable behind a dev toggle so world authoring does not lose its viewer.

## 12. Failure handling

- Unreachable goal: walk to the nearest reachable tile (the pathfinder's rule), never refuse a click.
- Click off the loaded regions or off the map: dropped client-side with a log line, never sent.
- Dynamic blocker mid-route: re-path once, then stand. A head that saw a different blocker snaps on reconcile.
- Malformed command (plane out of range, non-finite, unknown kind, goal outside the search radius): dropped and
  counted, the existing `RateLimiter` bounds the rate, repeated abuse is the ban store's business.
- Region handoff mid-step: the step completes in the new cell, the route survives (tile coordinates are global).
- Persistence: a record that fails validation is quarantined under the quarantine prefix, the player spawns at the
  world's spawn marker, and `OnRecordQuarantined` fires for the operator.
- Refused at the door: the client shows the localized reason and does not retry on its own.
- Server overload: `FixedTickHost` sheds backlog past eight ticks, and the tick is 250 ms, so shedding is visible
  in the heartbeat long before players feel it.

## 13. Test plan

Headless, in `KhaozEngine.TileWorld.Netcode.Tests` (a new per-area test project referencing only what it uses):

- Simulator: same inputs, same route, byte-identical state after N ticks on two instances. Run steps every 2 ticks
  and walk every 4, diagonals cost the same, a corner is never cut (the `CanStep` rule observed through movement).
- Reach: the reach sets of 1x1, 2x1 and 2x2 footprints with and without walls, `Nearest` tie-breaking matches scan
  order, a fully walled object has no reach tile.
- Prediction: a client and a server over an in-process transport walking a route, with the client's predict phase
  offset from the server tick (the loopback lesson from the Ruinborne harness). Zero corrections on a clean map.
  Then a server map with one extra blocker: exactly one snap, then agreement.
- Action queue: arrival on a reach tile raises `OnInteract` once, a second click replaces the pending action, a
  walled target yields `CannotReach`.
- Sharding: two cells, a player walking across the region boundary, the net id survives, the route survives, a
  watcher in the other cell sees the ghost before and the resident after.
- Persistence: round trip through a temp `SqliteWorldStore`, a corrupt record is quarantined and the player spawns
  at the marker, guests are not written.
- Protocol and gate: encode and decode every message, fuzz the decoders with truncated frames, and the four refusal
  reasons each refuse with the right token.
- The generic persistence core: `NetWorld`'s existing `WorldPersistence` tests pass unchanged against the binding.

Grimhollow: a capture of the player standing in Hollowmere through `TileWorldSnapshot`, and a windowed walk for the
user (click, run toggle, cannot-reach on a walled booth).

## 14. Release split

R1, engine: `TileWorld.Netcode` complete with its tests, `ConnectionGate` promoted, the persistence core made
generic, one minor version. R2, Grimhollow: the three projects and the client player, one game version. R1 has no
consumer until R2 and that is accepted: the package's consumer-visible value is proven by the loopback tests, which
exercise both heads end to end in one process.

## 15. Deferred, with the reason

- Real actions (bank, stairs as a plane change), NPCs, chat, combat: sub-project 3 onward. SP2 leaves `OnInteract`,
  the reach rules and the action queue as the seam they plug into.
- A generic server core extracted from `ShardedWorldServer` and `TileWorldServer`: after both exist and the
  duplication is visible rather than predicted.
- Multi-process shard deployment: the cell is the unit already, one process runs all of them until a world needs
  more.
- Pooled pathfinder scratch (#669): when a server paths enough agents for it to show in a profile.
- Character models: the placeholder kit piece until a character pipeline exists.
