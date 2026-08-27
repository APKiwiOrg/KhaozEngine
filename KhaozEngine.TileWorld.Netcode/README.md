# KhaozEngine.TileWorld.Netcode

Server-authoritative, client-predicted movement over a `KhaozEngine.TileWorld` world, in the OSRS shape: click,
walk at once, the server is right when the two disagree.

A player IS a `TileCoord`, a `TileDirection` facing and a `TileMoveMode`, and a step COMMITS every N ticks along a
deterministic `TileRoute`. Nothing accumulates a float, so the same commands replayed from the same state land on
byte-identical output on both heads, which is what lets the client show a walk before the server has confirmed it
and correct only on a genuine disagreement.

**A step commits its tile when it STARTS.** `TileMoveState.Tile` names the tile the simulation OWNS, from the tick
the step into it begins, and `TileMoveState.StepFrom` names the one being left. The remaining ticks of the step
glide the DRAWN body from one to the other, so the rules run ahead of the picture by strictly less than one step
and a click is always answered against the tile the player is committed to. That is what makes a 250 ms tick feel
immediate rather than laggy, and it is why an interaction resolves as the walk's LAST step starts rather than when
the avatar gets there. Draw through `TilePresenter`, never off `Tile`.

A SIBLING of `KhaozEngine.NetWorld`, never a dependent of it. The two movement stacks share the generic layers
(`Netcode`, `Replication`, `Sharding`, `Simulation`, `WorldStore`) and nothing else, so a tile server never carries
the float locomotion stack and this package has no path to `NetWorld` at all. An architecture test proves it rather
than the csproj implying it.

GPU-free and headless. The whole test suite runs both heads in one process over an in-memory transport.

In the `KhaozEngine.Server` umbrella.

## Three invariants worth reading before the API

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

**The DRAWN BODY GLIDES its whole step, linearly, and the lag that leaves is the bound a game designs against.**
Committing at the start of a step puts the rules ahead of the picture, and the body walks in behind them at a
constant speed over the step's own tick count, arriving exactly as the next step commits. That is the OSRS model,
and it is a RULED behaviour rather than a tuning default: there is no knob for it and there is deliberately none.
Two tighter curves were built inside this same unreleased version and both were rejected at the owner's hand, a
fixed-seconds glide window (which stutters, structurally) and a damped chase (which does not, and still felt
wrong). `docs/design/TILE-WORLD-NETCODE-DESIGN-2026-08-22.md` section 5.2 carries the four rounds with the
measurements.

**THE INVARIANT: the drawn body lags its committed tile by up to one STEP.** Half a tile on average, zero at the
instant it lands, never ahead. Combat, reach, occupancy and what a click resolves against are all answered about
the committed tile, so a design that reads committed tiles is reading something a player watching the avatar
cannot see. A REMOTE's BODY adds the delayed timeline `TileWorldClientConfig.InterpolationDelayTicks` names on
top, two ticks by default and a whole tick each: at a 1/6 s tick that is 0.33 s more. Size a design that DRAWS
other players against the SUM. Its committed TILE need not pay that second half, see the two reads below.

**The mitigation is VISIBILITY, and it is the game's to draw.** Shrinking the lag is the wrong axis and was tried
twice: at any lag the invisible truth is still invisible, and the motion has to be distorted to buy it. Drawing
the truth costs the motion nothing. So this package's job is to leave the reads clean, and they are:
`client.Prediction.PredictedState` gives the local player's committed `Tile` and remaining `Route` with no
allocation and nothing a snapshot stale, and `TilePresenter.PoseAt(tile)` maps any tile onto the same centre a
standing body draws on. A remote's route is owner-only on the wire, so a path highlight is a local-player overlay
only.

**A remote's committed tile has TWO reads, and picking the wrong one is silent.**
`client.TryGetRemoteTile(netId, out tile)` is on the DELAYED render timeline, so it agrees with the body
`TryGetRemotePose` draws and both sit `InterpolationDelayTicks` behind the server. That agreement is exactly what
an overlay drawn ON the body wants and exactly what a RULE must not have.
`client.TryGetLatestRemoteTile(netId, out tile, out ticksOld)` is on the newest APPLIED snapshot, so it trails by
the transport latency plus at most one snapshot interval, and it reports how old the answer is in ticks so an
overlay can fade a stale marker rather than draw a confident one. That age is a LOWER bound on the truth, because
no client can see the one-way flight time, so a threshold built on it wants headroom. Both are allocation free, both refuse an unknown
id and the local player, and neither extrapolates. `docs/USING-KHAOZENGINE.md` carries the worked example.

## The types

**State and commands**

- **`TileMoveState`** - one player's committed tile, the tile the step in flight is walking out of (`StepFrom`),
  facing, mode, step progress (a tick COUNT out of a tick TOTAL), route, teleport epoch and interaction target.
  Both an `IPredictedState<TileMoveState>` and an ECS `IComponent`, so `ClientPrediction` and
  `ReplicationRegistry` carry the same type verbatim. `Position` is DERIVED in TILE units, the glide from
  `StepFrom` into `Tile`, and `Vertical` is the plane INDEX, so the state needs no world document. `IsStepping`
  (`StepFrom != Tile`) is the one definition of "a step is in flight", and it is NOT the same question as a live
  route: a route empties on the tick its last step starts. The direction the body is WALKING is
  `TileRoute.Direction(StepFrom, Tile)`, never `Facing`: `Facing` is where the player is LOOKING, and the arrival
  turn writes it toward an interaction target on the tick the last step STARTS, so on every walked interaction the
  two disagree for the whole of that step and a locomotion blend taken off `Facing` walks the avatar sideways into
  the booth. Ask `IsStepping` first, because `Direction` throws on a standing body's identical pair.
- **`TileRoute`** - the walk in progress as the tiles after the start plus the index of the next one. A value, so
  reconciliation replays it rather than mutating it, and advancing is one integer. Equality compares the REMAINING
  tiles, so a route rebuilt from its wire form equals the one the server holds.
- **`TileRouteState`** - the remaining walk as one step DIRECTION per tile, measured from the owner's current tile.
  Its own component because it is owner-only (plus Persist and Migrate): an observer does not need it, and the
  owner does, since a reconciliation basis without its route stands the player still.
- **`TileCommand`** / **`TileCommandKind`** - one tick of intent: `None` (keep going), `WalkTo` (path to a goal and
  walk it) or `Interact` (route to a reach tile of a target, face it, act as the last step commits). The MODE
  rides on every command, `None` included, so the run toggle lives on the tick stream rather than on the click.
- **`TileMoveMode`** - walk or run, a two-value selector rather than a speed.
- **`TileStepTicks`** - ticks per step, per mode. Both heads must hold the same pair, or a step commits a tick
  apart and every step reads as a misprediction.
- **`TileMoveOptions`** - the pathfinder knobs both heads must agree on: `AgentSize`, `MaxPathRadius` and
  `MaxRouteSteps`, the longest route one click may produce, counted in the steps still to take from the tile the
  player is committed to.
- **`TileIdentity`** - the cosmetic display name, replicated to everyone in interest. Never a rules input.
- **`PendingTileCommand`** - the command drained for a player this tick, ECS-only and never replicated.

**Simulation**

- **`TileMoveSimulator`** - the ONE discrete stepper both heads run, pure over its inputs and integer-only.
  `Accepts` is THE definition of whether a command applies at all, `Step` advances one tick, `BeginWalk` and
  `BeginInteract` are the two route starts. A step commits its tile at its START, after the `CanStep` re-check, so
  a blocker is felt when the step would begin rather than when the foot lands. The step in progress is never
  abandoned either, and it needs no special case for it: a route is always pathed from `Tile`, which is the tile
  the step in flight is entering, so a direction change while moving never drags the avatar back toward the tile
  it was leaving. The route cap counts the steps still to take from that tile.
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
  drawing. `LocalPose` and `TryGetRemotePose` are the BODIES to draw. `Prediction.PredictedState` (its `Tile` and
  its `Route`) is the RULES for the local player, which is what the true-tile overlay reads. A REMOTE has two tile
  reads and they answer different questions: `TryGetRemoteTile` is on the delayed render timeline, so it agrees
  with the body `TryGetRemotePose` draws and is right for an overlay drawn ON that body, while
  `TryGetLatestRemoteTile` is on the newest applied snapshot, so it is right for anything the RULES will answer.
  Its overload also reports how many ticks old the answer is, for an overlay that fades a stale marker rather than
  lying with it.
- **`TilePresenter`** / **`TilePose`** - the pure map from a tile point to a world position and a yaw, and the only
  file in the package that consults `TileWorldSpace`. Two answers, and mixing them up is the one mistake here.
  `Pose(state, extraTicks)` is the BODY: the linear glide from `StepFrom` into `Tile` by the step's own tick
  count, carried forward by the fraction of a tick since the state was sampled and clamped at the end of the step.
  `PoseAt(tile)` is the RULES: a whole tile's centre, with no glide, which is what a true-tile marker, a route
  highlight, a minimap or an editor draws on. The `PoseAt(planar, vertical, facing)` overload takes a smoothed
  or fractional position for the same mapping when the caller already holds one. `LocalPose(prediction)` is the body for a caller holding its own
  `ClientPrediction`, and `client.LocalPose` is that call already wired. Holds no state and no tuning, so
  replacing it when the document loads cannot change how anything moves. A pose names the tile CENTRE, half a
  tile in from the corner on each axis, which is the middle of that tile's ground quad and the point a 1x1
  `TileObjectProps` prop is anchored at, so a head draws at `pose.Position` without re-centring it.
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
// ONE registry, handed to BOTH heads. A game's own components register at or above TileProtocol.FirstGameTypeId.
var registry = TileProtocol.CreateRegistry(RegisterGameComponents);

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
                        log: Console.WriteLine, isBanned: bans.IsBanned),
    registry);

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
    TileProtocol.BuildConnectToken("grimhollow-1", worldHash, authToken),
    registry);                                 // the SAME one the server got, or its components never arrive

client.RunMode = runButtonHeld ? TileMoveMode.Run : TileMoveMode.Walk;
if (clicked) client.Queue(TileCommand.WalkTo(clickedTile, client.RunMode));

client.Poll();                                     // once a frame
client.Tick(dt);                                   // the command clock, one command per whole tick
client.AdvancePresentation(dt);                    // the render clock, before drawing

TilePose me = client.LocalPose;                    // the BODY, gliding into its committed tile
foreach (long id in client.RemoteNetIds)
    if (client.TryGetRemotePose(id, out TilePose them)) Draw(id, them);

// The true-tile overlay, which is how the lead is made visible rather than smaller. Index the route from
// Route.Index: Tiles is an IReadOnlyList, so a foreach boxes an enumerator every frame.
TileMoveState rules = client.Prediction.PredictedState;
DrawMarker(client.Presenter.PoseAt(rules.Tile));
for (int i = rules.Route.Index; i < rules.Route.Tiles.Count; i++)
    DrawRouteTile(client.Presenter.PoseAt(rules.Route.Tiles[i]));
```

## The determinism contract

The two heads run the SAME `TileMoveSimulator` over the SAME tiles. Four things must match, and a mismatch in any
of them turns every step into a correction:

1. **`TickSeconds`.** The server's tick length and the client's command tick are one number.
2. **`StepTicks`.** A step that fills on tick 4 for one head and tick 5 for the other starts the next one a tick
   apart, and a step commits its tile as it starts, so the two heads own different tiles for a whole tick.
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
- **A remote's BODY is drawn `InterpolationDelayTicks` behind.** Not a step behind: the one-step-behind
  reconstruction went with the lead commit in 18.1.0, which put `StepFrom` on the everyone channel, so an observer
  is handed the step's two tiles and glides FORWARD into the committed one. What is left is the delay itself,
  measured in this package's loopback at max 1.4 ticks and mean 0.95 at BOTH cadences, which is what a pure time
  delay looks like against a step-quantized one.
  [#696](https://github.com/APKiwiOrg/KhaozEngine/issues/696) is closed with those numbers. The delay is the price
  of surviving a lost snapshot, so shrinking it is a trade rather than a fix, and it applies to the BODY: a rule
  about a remote reads `TryGetLatestRemoteTile`, which is not held behind it.
- **Snapshots are FULL, not per-client deltas.** Every serve writes the viewer's whole area of interest.
  Per-client deltas need an ack channel and a capability handshake the tile wire does not have, which is
  [#699](https://github.com/APKiwiOrg/KhaozEngine/issues/699). The cost of BUILDING each full snapshot is
  [#680](https://github.com/APKiwiOrg/KhaozEngine/issues/680).
- **`TileIdentity` rides every snapshot.** The display name is a replicated component like any other, so every
  full serve re-sends it for every entity in the viewer's area of interest rather than once on first sight.
  Sending it once needs a per-client already-told set the tile wire does not have, which is
  [#679](https://github.com/APKiwiOrg/KhaozEngine/issues/679).
- **The ban check is a `Func<string,bool>` predicate, not a store.** `IBanStore` lives in `KhaozEngine.NetWorld`,
  which this package must never reference. Unifying the two ban seams is
  [#678](https://github.com/APKiwiOrg/KhaozEngine/issues/678).
- **No actions beyond the seam.** `TileActionKind` ships one kind, `Interact`, and `OnInteract` is where a game
  takes over. The engine knows nothing about what an interaction DOES.

Design: `docs/design/TILE-WORLD-NETCODE-DESIGN-2026-08-22.md`.
