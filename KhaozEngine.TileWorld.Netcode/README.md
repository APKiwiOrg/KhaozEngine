# KhaozEngine.TileWorld.Netcode

Server-authoritative, client-predicted movement over a `KhaozEngine.TileWorld` world, in the OSRS shape: click,
walk at once, the server is right when the two disagree.

A player IS a `TileCoord`, a `TileDirection` facing and a `TileMoveMode`, and a step COMMITS every N ticks along a
deterministic `TileRoute`. Nothing accumulates a float, so the same commands replayed from the same state land on
byte-identical output on both heads, which is what lets the client show a walk before the server has confirmed it
and correct only on a genuine disagreement.

A SIBLING of `KhaozEngine.NetWorld`, never a dependent of it. The two movement stacks share the generic layers
(`Netcode`, `Replication`, `Sharding`, `Simulation`, `WorldStore`) and nothing else, so a tile server never carries
the float locomotion stack and this package has no path to `NetWorld` at all. An architecture test proves it rather
than the csproj implying it.

GPU-free and headless. The whole test suite runs both heads in one process over an in-memory transport.

In the `KhaozEngine.Server` umbrella.

## Two invariants worth reading before the API

**Tick length and step ticks are CONFIGURATION, never constants.** A tile game's whole sense of pace is those
numbers, and the engine has no business picking either. `TileWorldServerConfig.TickSeconds` and
`TileWorldClientConfig.TickSeconds` are `required`, and `TileStepTicks` carries the per-mode step cost.
`TileStepTicks.Default` is walk 4 and run 2, deliberately plain rather than tuned: nothing in the engine reads it
except a caller that supplied nothing.

**Tile coordinates ARE the shard plane.** The server is built with a cell edge of `TileCells.CellSize` (one
`TileRegion.Size`), and every interest insert, query and `CellCoord.FromWorld` call takes `(tileX, tileZ)` as its
floats, so a cell is exactly a region and a crossing is exactly a region crossing. `TileWorldSpace`, which maps a
tile to render metres and negates z on the way, is consulted in exactly ONE file, `TilePresenter.cs`. Server code
never touches it. Planes do not shard: a cell holds every plane of its region, and what separates two floors is the
SERVE, which filters a viewer's area of interest to the viewer's own plane.

## The types

**State and commands**

- **`TileMoveState`** - one player's tile, facing, mode, step progress (a tick COUNT out of a tick TOTAL), route,
  teleport epoch and interaction target. Both an `IPredictedState<TileMoveState>` and an ECS `IComponent`, so
  `ClientPrediction` and `ReplicationRegistry` carry the same type verbatim. `Position` is DERIVED in TILE units
  and `Vertical` is the plane INDEX, so the state needs no world document.
- **`TileRoute`** - the walk in progress as the tiles after the start plus the index of the next one. A value, so
  reconciliation replays it rather than mutating it, and advancing is one integer. Equality compares the REMAINING
  tiles, so a route rebuilt from its wire form equals the one the server holds.
- **`TileRouteState`** - the remaining walk as one step DIRECTION per tile, measured from the owner's current tile.
  Its own component because it is owner-only (plus Persist and Migrate): an observer does not need it, and the
  owner does, since a reconciliation basis without its route stands the player still.
- **`TileCommand`** / **`TileCommandKind`** - one tick of intent: `None` (keep going), `WalkTo` (path to a goal and
  walk it) or `Interact` (route to a reach tile of a target, face it, act on arrival). The MODE rides on every
  command, `None` included, so the run toggle lives on the tick stream rather than on the click.
- **`TileMoveMode`** - walk or run, a two-value selector rather than a speed.
- **`TileStepTicks`** - ticks per step, per mode. Both heads must hold the same pair, or a step commits a tick
  apart and every step reads as a misprediction.
- **`TileMoveOptions`** - the pathfinder knobs both heads must agree on: `AgentSize`, `MaxPathRadius` and
  `MaxRouteSteps`, the longest route one click may produce.
- **`TileIdentity`** - the cosmetic display name, replicated to everyone in interest. Never a rules input.
- **`PendingTileCommand`** - the command drained for a player this tick, ECS-only and never replicated.

**Simulation**

- **`TileMoveSimulator`** - the ONE discrete stepper both heads run, pure over its inputs and integer-only.
  `Accepts` is THE definition of whether a command applies at all, `Step` advances one tick, `BeginWalk` and
  `BeginInteract` are the two route starts.
- **`TileMovementSystem`** - runs the simulator over every OWNED player entity inside a cell's own fixed tick,
  skipping ghosts and migrating entities so no player is stepped twice in one tick.
- **`TileReach`** / **`TileActionQueue`** / **`TilePendingAction`** / **`TileActionKind`** - the OSRS reach rule and
  the one-deep pending action. `TileReach.Set` is every tile cardinally adjacent to a footprint tile that the
  footprint tile could step OUT onto, `Contains` is the in-range test, `TryNearest` picks the reach tile by real
  path length with scan order as the tie-break, and `FacingToward` turns the arriving actor toward what it came
  for.
- **`ITileTargets`** / **`TileDocumentTargets`** - the seam that resolves an interaction target id to a footprint
  and a plane, and the document-backed implementation over `TileObjectArchetype.Interactive`. Read through on every
  call, so an id stops resolving the moment the thing it named stops existing.

**Wire**

- **`TileProtocol`** - the tile wire. Every frame carries a leading TAG byte, so the demux is by tag and never by
  length. `CreateRegistry` builds the `ReplicationRegistry` both heads share, `AssembleMoveState` is the one
  sanctioned way to put a route back onto a decoded or migrated state, `BuildConnectToken` builds the token the
  door reads, and the frame codecs are the command, the snapshot, the opaque game message and the notice.
- **`TileServerReason`** - the stable wire reason tokens a tile server sends. Not display text.
- **`TileCells`** - the one place tile space meets the shard grid: `CellSize`, `CoordOf(tile)` and
  `RegionOf(cell)`.

**Server**

- **`TileWorldServer`** (+ **`TileWorldServerConfig`**) - the authoritative server, a `ShardHost` whose cell grid is
  the tile region grid. `Poll` pumps the transport, `Tick` runs the world, and the seams are `OnBeforeTick`,
  `OnInteract`, `OnGameMessage`, `OnCannotReach`, `PlayerJoined` and `PlayerLeaving`. It is also the
  `IPersistenceHost<TileMoveState>`.
- **`TileGameMessageHandler`** - the delegate an opaque game message arrives on.

**Client**

- **`TileWorldClient`** (+ **`TileWorldClientConfig`**) - prediction for the local player, a `ClientReplicationView`
  for everybody else, and its OWN command tick, phase-offset from the server's rather than driven by snapshot
  arrival. `Queue` on a click, `Tick` on the command clock, `Poll` once a frame, `AdvancePresentation` before
  drawing.
- **`TilePresenter`** / **`TilePose`** - the pure bridge from a tile state plus a step fraction to a world position
  and a yaw. The only file in the package that consults `TileWorldSpace`.
- **`TileClientMessageHandler`** - the delegate an opaque server message arrives on.

**Persistence**

- **`TilePlayerRecord`** - the stored record under `player:{accountId}`: tile, plane, facing and the game's opaque
  blob. All integers, so a record round-trips exactly and the dirty comparison is a byte compare.
- **`TileWorldPersistence`** (+ **`TileWorldPersistenceConfig`**) - the TILE binding of
  `KhaozEngine.WorldStore.StatePersistence<TState>`. The save interval, the dirty pass, the load guard, quarantine,
  the guest policy and the rejoin hints are the shared core, and this type supplies the four tile-shaped answers.
  Built with the same baked `TileCollisionMap` the head runs on, so a stored record naming a plane or a region an
  edited world no longer has is quarantined and its player placed at the spawn, rather than reaching
  `TileWorldServer.SetPlayerState` and throwing out of the head's frame loop.

## A server, in ten lines

```csharp
var document = TileWorldFile.Load(worldDirectory);
var map = TileCollisionBaker.Bake(document, catalogs);

var server = new TileWorldServer(
    transport,
    new TileWorldServerConfig
    {
        TickSeconds = 0.25f,                       // the GAME's number, not the engine's
        StepTicks = new TileStepTicks(walk: 4, run: 2),
        Spawn = new TileCoord(64, 64, plane: 0),
        IsBanned = bans.IsBanned,
    },
    map,
    new TileDocumentTargets(document, catalogs),
    ConnectionGate.Wrap(tokenAuth, protocolVersion: "grimhollow-1", worldHash: TileWorldHash.OfWorld(document),
                        log: Console.WriteLine, isBanned: bans.IsBanned));

server.OnInteract += (slot, netId, target) => game.Interact(slot, target);

while (running)                                    // any frame clock: the server accumulates its own ticks
{
    server.Poll();                                 // ALWAYS before Tick, so a click lands on the next tick
    server.Tick(dt);
    persistence.Update(dt);
}
```

`BeginDrain(TileServerReason.Draining, graceSeconds)` on SIGINT announces the token to every client at once, keeps
ticking through the grace so a player mid walk finishes it, and raises `IsDrainComplete` once the grace is spent
AND the sessions are closed. Flush persistence there, then exit.

## A client, in ten lines

```csharp
var map = TileCollisionBaker.Bake(TileWorldFile.Load(worldDirectory), catalogs);   // the SAME world files

var client = new TileWorldClient(
    transport,
    new TileWorldClientConfig
    {
        TickSeconds = 0.25f,                       // must equal the server's
        StepTicks = new TileStepTicks(walk: 4, run: 2),
    },
    map,
    targets,
    TileProtocol.BuildConnectToken("grimhollow-1", worldHash, authToken));   // the client builds its own registry

client.RunMode = runButtonHeld ? TileMoveMode.Run : TileMoveMode.Walk;
if (clicked) client.Queue(TileCommand.WalkTo(clickedTile, client.RunMode));

client.Poll();                                     // once a frame
client.Tick(dt);                                   // the command clock, one command per whole tick
client.AdvancePresentation(dt);                    // the render clock, before drawing

TilePose me = client.Presenter.LocalPose(client.Prediction);
foreach (long id in client.RemoteNetIds)
    if (client.TryGetRemotePose(id, out TilePose them)) Draw(id, them);
```

## The determinism contract

The two heads run the SAME `TileMoveSimulator` over the SAME tiles. Four things must match, and a mismatch in any
of them turns every step into a correction:

1. **`TickSeconds`.** The server's tick length and the client's command tick are one number.
2. **`StepTicks`.** A step that fills on tick 4 for one head and tick 5 for the other commits its tile a tick
   apart.
3. **`Move`** (`TileMoveOptions`): the agent size, the path radius and `MaxRouteSteps`. The route cap is enforced
   in the SIMULATOR rather than on the wire, so both heads truncate the same pathfinder result to the same tiles
   and a long click ends on the same destination.
4. **The collision map.** Both heads bake from the same world files. `TileCollisionBaker.Bake` over the same
   document is the contract, and the connect gate's world hash is what refuses a client that baked something else.

`PlaneCount` and `MaxGoalRadius` belong to it too, for a subtler reason: they are the server's two refusals of a
walk goal, and it REWRITES a refused goal to `TileCommand.Continue` at the mode the command carried rather than
dropping the tick. A client that does not mirror both predicts a walk the server never started.

Two behaviours that surprise a first reader, both deliberate and both shared by the two heads:

- **The tick that carries a command is a FULL tick.** It starts the walk and advances step progress by one, so a
  click never costs a tick of standing still.
- **A cross-plane command is dropped WHOLE**, its mode included, so a rejected tick reads exactly as though nothing
  arrived. Planes are separate walkable surfaces with no step between them, and pathing the goal on the player's
  own plane instead would walk them to an x and z they never clicked.

## The connect door

`TileProtocol.BuildConnectToken` builds what a client presents and `KhaozEngine.Netcode.ConnectionGate.Wrap`
composes what the server reads: version, then world, then the real token, then the ban check. Four refusals, three
of them carrying an engine wire token the client matches and localizes itself:

| Refusal | Reason token |
|---|---|
| Protocol version mismatch | `ke:incompatible-version:<requiredVersion>` |
| World mismatch | `ke:world-mismatch:<serverHash>\|<clientHash>` |
| Banned account | `ke:banned` |
| Bad or expired auth token | whatever the inner `IConnectionAuthenticator` returned |

`TileWorldClient.RefusedReason` and the `RefusedAtDoor` event carry the token. Once joined, the server's own
out-of-band notices carry `TileServerReason`: `ke:cannot-reach`, `ke:draining` and `ke:kicked`, all prefixed `ke:`
so a game's own tokens can never collide with them.

## Known limits in this release

- **`TileWorldServerConfig.MaxCommandsPerSecond` is spent per POLL, not per wall-clock second.** The bucket is
  topped up once per `Poll` with `MaxCommandsPerSecond * TickSeconds` tokens, so the sustained ceiling is
  `pollRate * MaxCommandsPerSecond * TickSeconds` messages per second. It is the `RateLimiter` contract the rest of
  the engine's servers run on, and the fleet-wide unit defect is
  [#681](https://github.com/APKiwiOrg/KhaozEngine/issues/681).
- **A remote is drawn one step behind.** A remote's route is owner-only, so an observer's snapshot carries a tile
  plus step progress, and the client glides the remote from the tile it LEFT to the tile it is on now.
  Replicating the step direction so a remote glides toward the tile it is entering is
  [#696](https://github.com/APKiwiOrg/KhaozEngine/issues/696).
- **Snapshots are FULL, not per-client deltas.** Every serve writes the viewer's whole area of interest.
  Per-client deltas need an ack channel and a capability handshake the tile wire does not have, which is
  [#699](https://github.com/APKiwiOrg/KhaozEngine/issues/699). The cost of BUILDING each full snapshot is
  [#680](https://github.com/APKiwiOrg/KhaozEngine/issues/680).
- **`TileWorldClient` builds its own registry.** The server's ctor takes a `ReplicationRegistry` so a game
  can register its own components at or above `TileProtocol.FirstGameTypeId`, and the client's does not, so
  those components are skipped on the way in rather than applied. Movement, the owner-only route and the
  display name are unaffected. Giving the client the same seam is
  [#700](https://github.com/APKiwiOrg/KhaozEngine/issues/700).
- **The ban check is a `Func<string,bool>` predicate, not a store.** `IBanStore` lives in `KhaozEngine.NetWorld`,
  which this package must never reference. Unifying the two ban seams is
  [#678](https://github.com/APKiwiOrg/KhaozEngine/issues/678).
- **No actions beyond the seam.** `TileActionKind` ships one kind, `Interact`, and `OnInteract` is where a game
  takes over. The engine knows nothing about what an interaction DOES.

Design: `docs/design/TILE-WORLD-NETCODE-DESIGN-2026-08-22.md`.
