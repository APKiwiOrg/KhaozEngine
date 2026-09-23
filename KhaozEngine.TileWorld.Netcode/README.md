# KhaozEngine.TileWorld.Netcode

Server-authoritative, client-predicted movement over a `KhaozEngine.TileWorld` world, in the OSRS shape: click,
walk at once, the server is right when the two disagree. Plus server-owned ACTORS, which are a player minus a
connection, and tick-based MELEE combat, which is a followed interaction rather than a system of its own.

A player IS a `TileCoord`, a `TileDirection` facing and a `TileMoveMode`, and a step COMMITS every N ticks along a
deterministic `TileRoute`. Nothing accumulates a float, so the same commands replayed from the same state land on
byte-identical output on both heads, which is what lets the client show a walk before the server has confirmed it
and correct only on a genuine disagreement.

**A step commits its tile when it STARTS.** `TileMoveState.Tile` names the tile the simulation OWNS, from the tick
the step into it begins, and `TileMoveState.StepFrom` names the one being left. The remaining ticks of the step
glide the sampled state from one to the other, so the rules lead that state by at most one grid step. The local
render pose adds the prediction layer's inter-tick easing, which can add one tick of travel to the lag. A click is
always answered against the tile the player is committed to. That is what makes a 250 ms tick feel
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

**THE ZERO-CORRECTION MOTION INVARIANT has two body bounds.** A remote body is at most one grid step behind the committed tile read from
the same delayed timeline. With no active reconciliation offset, the local body is at most one grid step plus one local command tick of travel behind
`Prediction.PredictedState.Tile`, because `ClientPrediction.RenderedState` eases from the previous predicted
position between command ticks. At the default four-tick walk and two-tick run cadences those local bounds are
1.25 and 1.5 grid steps. Grid step means Chebyshev distance on the tile lattice. A diagonal step is `sqrt(2)` tile
sizes in Euclidean world distance, so a world-space radius multiplies these bounds by `sqrt(2)`. The loopback tests
observe 1.225 walking and 1.45 running because their first sampled frame has already advanced one tenth of a tick.
The theoretical instant remains the base motion bound.

`LocalPose` also carries the prediction layer's active planar reconciliation offset. That term preserves visual
continuity across an ordinary correction below `HardSnapDistance`, then decays toward zero. It can point away from
the newly committed tile and add to the base motion lag. A conservative instantaneous bound is the base motion
term plus the magnitude of the active offset. The offset has no separate fixed cap because repeated sub-snap
corrections can re-anchor it. A hard snap or teleport clears it. Combat, reach, occupancy and clicks use the
committed tile and never this presentation position.

A remote compared with the current server truth also adds the delayed timeline
`TileWorldClientConfig.InterpolationDelayTicks` names, two ticks by default and a whole tick each. At a 1/6 s tick
that is 0.33 s more. Size a design that draws other players against the sum. Its committed tile need not pay that
second term, see the two reads below.

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
id and the local player, and neither extrapolates. `client.CollectRemoteTiles(buffer)` is the delayed read for
everybody at once, for a per-frame pass over the whole crowd, and `client.CollectRemoteSteps(buffer)` is that
read with each remote's step progress beside its tile, which is what a presentation rule pacing itself off the
bodies needs. `docs/USING-KHAOZENGINE.md` carries the worked example.

**A settled crowd on one tile can draw ONE body.** Every one-tile body draws on its tile centre, so a stack of them is a smear of
overlapping meshes, and the body a player can least afford to lose in it is their own. `TileDrawPriority` picks
the one to draw per tile: the local player on their own tile, and on both tiles of a step in flight, with the
highest net id everywhere else. It is the OSRS PID ruling with a stable key, it is presentation only, and it is
in the types list below. The answer is a WEIGHT rather than a boolean, because a tile commits when a step STARTS
and the body glides in over the rest of it: a body that loses a tile spends what is left of its step fading out,
so it walks visibly under the winner rather than vanishing a step before it gets there. The opt-in
`SettledStacksOnly` policy instead leaves every moving body at full weight, gives moving bodies no tile claim,
and cuts settled losers to zero through a caller-supplied comparison. A body AT REST covers its whole footprint
under either policy, so a one-tile body standing in a cow's rump is in the cow's stack and one of the two is
hidden.

## The types

**State and commands**

- **`TileMoveState`** - one player's committed tile, the tile the step in flight is walking out of (`StepFrom`),
  facing, mode, step progress (a tick COUNT out of a tick TOTAL), route, teleport epoch and interaction target.
  Both an `IPredictedState<TileMoveState>` and an ECS `IComponent`, so `ClientPrediction` and
  `ReplicationRegistry` carry the same type verbatim. `Position` is DERIVED in TILE units, the glide from
  `StepFrom` into `Tile`, and `Vertical` is the plane INDEX, so the state needs no world document. It also carries
  `CombatTarget`, the NET ID this entity is locked onto and the reason the chase lives inside the one stepper both
  heads run rather than in a second movement authority a client cannot predict. `CombatTarget` and
  `InteractTarget` are mutually exclusive, each clearing the other, and a `WalkTo` clears both, which is how
  anything on this lattice disengages. It carries the entity's body size too: `FootprintSize` is the edge of a
  square anchored on `Tile` as its SOUTH-WEST corner (1 through `TileMoveState.MaxFootprintSize`, which is 8, and a
  default state is one tile), and `Footprint` is that square as a `TileRect`. A one-tile state is 41 payload bytes
  on the wire, plus one optional domain byte while an `InteractEntity` is pending, exactly as before footprints. A
  size above 1 always writes the domain byte (zero when no entity interaction is pending) and then one size byte, so
  a large body costs two trailing bytes. A legacy 41-byte state defaults to the authored-object domain and one tile,
  and an older reader decodes a large body as one tile, so both heads upgrade together. `IsStepping`
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
- **`TileCommand`** / **`TileCommandKind`** - one tick of intent, six kinds: `None` (keep going), `WalkTo` (path to
  a goal and walk it), `Interact` or its explicit alias `InteractObject` (route to an authored object),
  `InteractEntity` (route to an entity), `Attack` (lock onto an entity and chase it while it moves), or `Steer`
  (take one step toward a held `TileDirection`, with no search). The MODE
  rides on every command, `None` included, so the run toggle lives on the tick stream rather than on the click.
  Every entity operation needs a KIND of its own because `Target` spans two id spaces that overlap EXACTLY: a
  `TileObject.Id` is a document counter from 1 and a net id is `(nodeId << 48) | counter` from 1, so object id 7
  and the seventh spawned entity are the same 64 bits. `Interact` always means an authored object and keeps its
  original bytes. `InteractEntity` writes kind 4 into the same 24-byte frame. A pre-addition server rejects that
  kind as unknown rather than resolving its target through the object domain.
  `TileCommand.Steer(direction, mode)` writes kind 5 into that same frame and rides the direction in `Target` as
  its `TileDirection` byte value, read back through `TileCommand.SteerDirection`. `Kind` already decides what
  `Target` means, so that is the existing rule rather than a new pun. The decoder refuses a steer frame whose
  target is outside the eight directions, whole, exactly as it refuses an unknown kind. A pre-addition decoder
  refuses kind 5 as unknown, so a consumer moves its own game protocol version when it adopts this pin.
- **`TileMoveMode`** - walk or run, a two-value selector rather than a speed.
- **`TileStepTicks`** - ticks per step, per mode. Both heads must hold the same pair, or a step commits a tick
  apart and every step reads as a misprediction.
- **`TileMoveOptions`** - the pathfinder knobs both heads must agree on: `MaxPathRadius` and `MaxRouteSteps`, the
  longest route one click may produce, counted in the steps still to take from the tile the player is committed to.
  There is no size knob. `AgentSize` was removed in 19.0.0
  ([#900](https://github.com/APKiwiOrg/KhaozEngine/issues/900)), so the simulator steps, paths and reaches every
  body at its own state's `FootprintSize` and nothing raises that from the outside.
- **`TileIdentity`** - the cosmetic display name, replicated to everyone in interest. Never a rules input.
- **`PendingTileCommand`** - the command drained for a player this tick. Registered on the `Migrate` channel
  ALONE, so it crosses a cell handoff and reaches no client and no persistence blob. The movement pass resets it
  to `Continue` at the mode the step left, and it does so at tick step 2 while the handoff runs at step 3, so
  what crosses a border is the tick's neutral rather than a click waiting to be applied twice.

**Simulation**

- **`TileMoveSimulator`** - the ONE discrete stepper both heads run, pure over its inputs and integer-only.
  `Accepts` is THE definition of whether a command applies at all, `Step` advances one tick, and `BeginWalk`,
  `BeginInteract` and `BeginAttack` are the three route starts. It takes TWO target seams, `targets` for the
  object space and `combatTargets` for the entity space used by both `InteractEntity` and `Attack`, the second
  appended LAST in the constructor so an existing positional call keeps meaning what it said. An entity interaction
  is accepted only while that net id resolves on the actor's plane. `Follow` runs at the top of every `Advance`: while a
  `CombatTarget` is held it re-paths to an in-range anchor whenever the target's footprint has moved out of the
  route end's range, stands when it is already in reach, STEPS OUT of the target's footprint when its own body
  overlaps it (a body inside its target is not in reach, so holding it is a fight that can never start), and clears
  the lock when the target stops resolving or has no reachable anchor. Every one of those questions is asked of the
  body at `FootprintOf(state)`, which is the state's own `FootprintSize`, against the target's WHOLE
  footprint, through the one `TileReach` predicate. That in-reach stand also writes `Facing` toward the target, on
  EVERY tick it answers in range rather than once as the attacker lands, so a combatant turns with a target that
  moves around it and the step-out does not leave it looking 180 degrees away from what it is swinging at. `Step`
  takes the stepped entity's own net id as its fourth argument, read by that follow and by nothing else: an
  `Attack` naming the attacker ITSELF can never be in range, because its footprint moves with the body, so the
  follow CLEARS that lock on the tick it is applied and drops the route, and the body stops on the step it had
  already committed. The server answers the click with `CannotReach` through its ordinary broken lock report
  ([#741](https://github.com/APKiwiOrg/KhaozEngine/issues/741)).
  A lock naming another entity on the same tiles steps out instead, which is why the id is needed at all. A step
  commits its tile at its START, after the `CanStep` re-check, so
  a blocker is felt when the step would begin rather than when the foot lands. The step in progress is never
  abandoned either, and it needs no special case for it: a route is always pathed from `Tile`, which is the tile
  the step in flight is entering, so a direction change while moving never drags the avatar back toward the tile
  it was leaving. The route cap counts the steps still to take from that tile. `Step` and `BeginWalk` also have
  overloads taking a `TilePathfinderScratch`. The caller owns that mutable scratch and must never share it across
  concurrent searches. Omitting it preserves the allocating behavior used by client prediction.
  A `Steer` command goes through the same stepper: it replaces intent exactly as `WalkTo` does and then hands its
  held direction to the step doors, which read it on a BOUNDARY tick only, so a steer arriving mid step can
  neither restart nor redirect the step in flight.
- **`TileSteerResolver`** - the pure slide-else-stop rule behind a held direction, shared by both heads and by any
  head that wants to preview a step. `Resolve(map, tile, wanted, footprintSize)` answers `wanted` when it is open,
  for a blocked DIAGONAL whichever of its two axis steps is open (the Z axis step tested before the X axis step, so
  both heads pick the same one of two honest answers), and null when the body stands. It throws
  `ArgumentOutOfRangeException` for a direction outside the eight rather than answering null: the decoder and
  `TileMoveSimulator.Accepts` are the gates a frame passes through, so a value that far out is an in-process
  misuse and a silent stand would hide it as a wall. A blocked steer still turns the body to FACE the held
  direction, so a key pressed into a fence reads as an answer rather than as a dropped input.
- **THE PACE SEAM is one private method.** `TileMoveSimulator` asks `TileStepTicks` how long a step lasts in
  `StepTotalFor` and nowhere else, and a steered step and a routed step are both stamped from it through one
  shared commit body. Steering therefore adds NO speed value: a game that one day needs a haste or a slow changes
  that one method, and a click, a held key, an actor and a chase inherit it together.
- **`TileMovementSystem`** - runs the simulator over every OWNED entity inside a cell's own fixed tick,
  skipping ghosts and migrating entities so nothing is stepped twice in one tick. It holds the player simulator
  and the actor traversal registry. A `TileActor` tag selects a registered actor simulator, so each profile reads
  its own map while every actor keeps the server's `ActorMove` radius. The cell owns one player pathfinder scratch
  buffer and lazily creates one per actor profile it encounters. No mutable scratch is shared by the server, the
  simulators or scheduler-fanned cells. The one-argument and two-simulator constructors keep their original
  behaviour for heads that build this system directly.
- **`TileReach`** / **`TileActionQueue`** / **`TilePendingAction`** / **`TileActionKind`** - the OSRS reach rule and
  the one-deep pending action. `TileReach.Set` is every tile cardinally adjacent to a footprint tile that the
  footprint tile could step OUT onto, `Contains` is the in-range test, `TryNearest` picks the anchor to walk to by
  real path length with scan order as the tie-break, and `FacingToward` turns the arriving actor toward what it
  came for. Those one-tile forms answer for a ONE TILE agent. `Set`, `Contains` and `FacingToward` each have an
  agent-size overload taking `agentSize`, for an NxN agent anchored on its south-west tile: it is in range when its
  footprint does not overlap the target's and covers at least one tile of the one-tile set, so a wall denying one
  of its tiles leaves another tile free to reach, and `FacingToward` answers the one side the two squares touch on.
  `Set(map, footprint, plane, agentSize)` lists the in-range ANCHORS, derived from the one-tile set in its own order
  (each reach tile offers the anchors whose square holds it, dz then dx ascending, first occurrence kept), which is
  `4(M + N - 1)` of them against an MxM target on open ground and element for element the one-tile set at size 1.
  It asks reach only, so it can list an anchor no agent of that size could stand on. `TryNearest`'s existing
  `agentSize` now shapes those candidates as well as the walk, and pathing is what filters the unstandable ones
  out, because the search never enters a cell the whole body does not fit on. `TryNearest` refuses a footprint
  further than `maxRadius` + `agentSize` away without searching, since no candidate of one is inside the
  pathfinder's window: the answer is the same false, and it is what stops a client naming a far target it has
  never seen from buying a window flood at all. Past that it runs ONE search over the whole candidate list
  through `TilePathfinder.FindPathToAny`, never one per candidate. Same chosen tile, same walk and the same
  scan-order tie rule, for one window instead of up to `4(M + N - 1)` of them against a target nobody can reach.
  Its overload accepting `TilePathfinderScratch` hands that search its working memory.
  `agentSize` and `maxRadius` are validated at the top of `TryNearest` rather than left to the first search, so a
  bad argument throws whether the target is open, walled in, out of range or on another plane.
- **`TileInteractionReach`** / **`TileInteractionReachPolicy`** - the opt-in interaction sibling of `TileReach`.
  `Default` delegates to `TileReach` answer for answer. `IncludeDiagonals` adds the four footprint corners only
  when `TileCollision.CanStep` admits the diagonal, so blocked neighbours, edge walls, corner flags and the
  no-corner-cutting rule all remain in force. `IncludeOverlap` adds anchors whose whole actor footprint passes
  `TileCollision.CanStand` and overlaps the target. The flags compose. `Set`, `Contains`, `TryNearest` and
  `FacingToward` consume the same policy, so click prediction, path selection, arrival revalidation and the
  authoritative pending action share one answer. Candidate order is cardinal, then SW, SE, NW, NE, then overlap
  anchors in target scan order. Combat continues to use `TileReach` directly.
- **`ITileTargets`** / **`TileDocumentTargets`** / **`TileEntityTargets`** / **`TileRemoteTargets`** - the seam
  that resolves a target id to a footprint and a plane, and its three implementations across TWO id spaces.
  `TileDocumentTargets` is the OBJECT space, backed by the document over `TileObjectArchetype.Interactive` and
  read through on every call, so an id stops resolving the moment the thing it named stops existing. It answers
  the INVERSE too: `TryGetTargetAt(tile, out long id)` is the click-to-target search, the whole footprint rather
  than the anchor tile, lowest id first when two targets overlap so both heads resolve one click the same way.
  Compose it with `TileRaycast.Pick`, whose hit is a ground tile, and a click is resolved in two lines.
  `TryGetAimPoint(target, out Vector2 tilePlanar, out int plane)` is the PRESENTATION half, a default interface
  method every implementation inherits: the footprint centre, in the tile units `PoseAt(Vector2, float,
  TileDirection)` takes, which is what a body holding a lock is drawn looking at. Override it on a body whose
  centre is the wrong place to look at (the head of a long serpent, the door of a building) and every pose the
  client draws follows it. It resolves and refuses exactly where `TryGetFootprint` does, so a stale lock points
  nowhere new, and nothing in the rules reads it.
  `GetInteractionReachPolicy(target)` is the rules half and defaults to `TileInteractionReachPolicy.Default`.
  An authored-object resolver can override it directly. Entity resolvers remain engine-owned snapshots, so the
  seven-parameter `TileWorldServer` and `TileWorldClient` constructors append the same
  `Func<long, TileInteractionReachPolicy>` selector. Their original six-parameter constructors remain unchanged
  and forward with no selector. Both heads must pass the same selector. A selector may opt one entity family into
  diagonal or overlapping interaction without changing combat, other entity interactions or the wire format.
  `TileEntityTargets` is the server's ENTITY space, a per-tick SNAPSHOT over the live cells refreshed once before
  anything moves, which is what makes the actor pass and the movement pass order-independent in fact rather than
  in claim: every read is a keyed lookup into a map built before either pass began. `TileRemoteTargets` is the
  client's entity space, the honest `TryGetLatestRemoteFootprint` for a remote and the prediction for the local
  player, and the client builds its own rather than taking one, because the only honest answer to where an entity
  is on a client is that client's newest snapshot. Both entity resolvers answer an entity's WHOLE footprint, its
  `TileMoveState.Footprint`, never a one-tile rect on its anchor, because a client that resolved a large body as one
  tile would stop an approach inside it and be corrected every time. The local branch has one live consumer, an
  `Attack` naming the player's own id: it answers the same footprint the server's own resolver does, so the two heads
  read one target rather than merely landing on the same answer. The self clear does not ride on it, because the
  follow asks identity BEFORE it asks either seam, so a self attack clicked mid walk predicts the stop even on a head
  whose resolver cannot answer that id. A `Ghost` is EXCLUDED and therefore reads as gone, which is the answer
  the follow acts on. A `Migrating` entity is HELD instead, for `MigratingGraceRefreshes` consecutive refreshes
  (four by default, one second at a 250 ms tick), answering with the frozen pre-handoff footprint it is not moving off:
  an in-process link finishes the whole handshake inside one `ProcessHandoffs` so the window is never used, and a
  NETWORKED link spans calls, where dropping on the first unresolvable refresh breaks every fight whose target
  crosses a region boundary. The hold is bounded so a handshake that never completes cannot pin a lock, and the
  destination cell's owned copy always wins over the source's frozen one.

**Actors and combat**

An ACTOR is a player minus a connection. It carries `TileMoveState`, `TileRouteState` and `PendingTileCommand`, so
it steps through the same `TileMoveSimulator` algorithm and can never move by a rule the player stepper does not
understand. Its registered traversal profile can give that algorithm a different collision topology.

- **`TileActor`** - the tag marking a server-owned entity. ECS-only and never replicated, which is why the host
  rewrites it every tick: a region handoff captures only the registered components, so a crossing actor would
  otherwise arrive on the far side no longer an actor. The tag carries the actor's opaque traversal profile, and
  the host restores that from the server's live actor index after the handoff.
- **`TileHealth`** - `Current` and `Max`, four payload bytes on the default channels so a health bar has something
  to read. The engine owns it MECHANICALLY and owns none of its meaning: it subtracts a game-rolled amount and
  raises the death event at zero. **A spawned PLAYER carries none.** See the health contract below.
- **`TileCombatState`** - the swing cadence (`AttackTicks`), the cooldown, the damage record (`LastDamagedBy` /
  `LastDamagedTick`, written by a swing that LANDED, a blocked zero included, and not by a miss), the swung-at
  record (`LastAttackedBy` / `LastAttackedTick`, written by EVERY swing aimed at it, miss included, and what
  the default behaviour's retaliation reads: aggression answers the swing, the wound is for threat),
  `LastCombatTick` (either direction, misses included, which is what `CombatLogoutTicks` reads) and the lock
  age pair (`TargetSeen` / `TargetSinceTick`, which is what makes the roll order oldest lock first). Registered
  on the MIGRATE channel alone, so it survives a handoff and reaches no client at all.
- **`TileActorSpawn`** / **`TileWorldServer.SpawnActor`** / **`DespawnActor`** - the door. The spec is the numbers
  that go on ONE entity (`MaxHealth`, `AttackTicks`, `Facing`, `Mode`, plus the non-positional
  `TraversalProfile` and `FootprintSize`). The default profile preserves the constructor map and the original
  positional construction and deconstruction. A non-default profile must be registered and its map must let the
  whole footprint stand at the spawn tile (`TileCollision.CanStand`). A `FootprintSize` above 1 is checked that way
  on EVERY profile, the default included, because no content predates it, and one outside 1 through 8 throws. The
  size is written onto the actor's move state, so it replicates and crosses a region handoff with it. A malformed
  spawn (a zero `MaxHealth`, an off-plane or unloaded tile, a placement the profile refuses) THROWS, because it is a
  caller bug. A cell already at `MaxActorsPerCell` answers 0 instead, so a spawner running inside a tick is never
  taken down by a transient capacity limit. `RefusedActorSpawnCount` counts those capacity refusals. An actor is
  `Transient` at `DurableOnly`, so a cell eviction FREEZES it rather than ending it, and both doors instantiate
  the coordinate before they resolve: the cap counts a frozen actor rather than admitting a spawn on top of it,
  and the despawn reaches one rather than leaving it to come back as an entity nothing indexes.
- **`TileStaticEntitySpawn`** / **`TileWorldServer.SpawnStaticEntity`** / **`DespawnStaticEntity`** - the retained
  interaction-entity door. The spec carries facing and footprint size. A spawn writes an idle `TileMoveState`, so
  normal interest replication and `InteractEntity` reach use the same path as every other entity. It writes no
  actor, route, pending-command, health or combat state. The entity is `Transient` at `Always`, so a cell removal
  drops it and its server index together. Despawn is type-safe and idempotent by answer. A game attaches its own
  replicated identity immediately through `Host.TryGetOwner` and owns any durable source or journal lifecycle.
- **`TileActorDefinition`** / **`TileActorSpawner`** / **`TileActorSpawnerState`** - what a spawn POINT is authored
  from (id, max health, step mode, attack cadence, wander and leash radii, respawn delay, a game-owned `Kind`, an
  optional registered `TraversalProfile`, and `FootprintSize`), and the spawner that owns one home tile, its live
  actor and its respawn countdown. `FootprintSize` (default 1, refused outside 1 through 8 at the spawner door) makes
  the actor an NxN body anchored on its home as the SOUTH-WEST corner: pathing, reach, the wander and placement all
  use the whole square, and the presenter centres the body on it. The leash and the wander radius both measure
  ANCHOR to home anchor, and a wander goal must let the whole footprint stand. Placement refuses a home whose
  footprint fails `CanStand` on the actor's traversal map, at `TileActorHost.Add` and on every respawn, with the
  default profile keeping its legacy rule for a one-tile definition (a blocked home still spawns).
  `TileActorHost.CanPlace(definition, home)` asks that placement question without throwing, for a game's content
  test over its authored markers. It checks placement only, not `Add`'s other door rules. `LeashRadius` is checked
  against `TileWorldServerConfig.ActorMove.MaxPathRadius` where the definition arrives, at `TileActorHost.Add`,
  because a leash beyond the pathfinder's window is a walk home it cannot plan in one go. Cell eviction keeps the
  documented respawn countdown. When it expires, the spawner retires any former actor restored from the evicted
  cell before it checks the cap and builds the replacement. A non-default home, and any home for a footprint above
  1, must be open for the whole footprint when the definition is added. If that topology later blocks the home,
  respawn waits and retries on a later tick. A head can also set
  `TileActorHost.SpawnAdmission` to keep a ready authored spawner unavailable until a synchronous game-owned
  condition passes. Denial allocates no actor and retries on the next authoritative tick.
- **`TileActorHost`** (`server.Actors`) - `Add(definition, home)` to register a spawner, `CanPlace(definition, home)`
  to ask whether its footprint fits there without throwing, `Command(netId, command)`
  to latch one command onto one actor, `RegisterTraversalProfile(profile, map)` to register a non-default topology
  before the first authoritative tick, `SpawnAdmission` for synchronous game-owned spawn availability,
  `Behaviour` and `Seed` for the decision seam, `Spawners`,
  `TryGetSpawnerOf`, `Forget` (the despawn hook, dropping the unspent latch, the birth tile and the spawner link
  together) and `PendingCommandCount` (its own actor latch count, not the client's). Its tick is step 1b: every spawner respawns or counts
  down, then every live actor gets its decision translated into a command, plus the tag and
  `PendingTileCommand` rewrite above. It iterates its own net id list rather than an ECS query on the tag,
  because a query over the tag cannot see the one actor that most needs the write.
  The actor loop resolves each normal actor's owner once and reuses that cell and entity for movement state,
  combat state, health and the two unconditional writes. Caller callbacks can despawn or hand off the actor while
  deciding, so a dead, ghosted or migrating cached entity falls back to one fresh owner resolution before any
  post-callback combat read or write.
  In the 576-actor idle-behaviour workload this reduced the median complete server tick from 0.461 ms to 0.310 ms,
  with five 1,000-tick runs after 100 warmup ticks.
- **`ITileActorBehaviour`** / **`TileActorIntent`** / **`TileActorIntentKind`** / **`TileActorContext`** - the one
  decision seam. An intent names a TILE (`WalkTo`), a TARGET (`Attack`), `Break` (drop the target, walk home, and
  drop the damage record with it), `Stand` (cancel the route, hold the tile, KEEP the damage record: waiting for
  a fight rather than giving one up) or `Idle`, and never a route, a step, a facing or a tick. The context is a
  TICK-START view (the actor's tile, its home, its definition, its health, its target's tile through the same
  per-tick snapshot the follow reads, its damage record and whether each half of that record still RESOLVES
  (`LastDamagedByResolved` / `LastAttackedByResolved`, false once the entity it names has left the world, so a
  rule can skip an attacker who logged out without the record being touched), whether it is walking, who is
  locked onto IT
  (`TargetedBy`, lowest net id when several are, one tick behind a freshly accepted attack), the tick and its own
  random stream), plus `TraversalProfile`, its registered `TraversalMap` and `FootprintSize`, so no actor's decision
  can depend on another having moved first. `FootprintSize` is read off the actor's own move state rather than its
  definition, because an actor spawned without a spawner is handed the fallback definition. The profile, map and
  size are non-positional init properties to preserve existing context construction and deconstruction. One behaviour instance is SHARED by every actor, so a game that wants different
  behaviour per monster dispatches on `Definition.Kind` inside one implementation.
- **`TileActorRandom`** - a splitmix64 value type, `For(seed, netId, tick)`, so a behaviour needs no per-actor
  storage and a replay reproduces every draw. Deliberately not `System.Random`, whose sequence is not stable
  across .NET releases. **It MUTATES, and the context hands it over an `in` parameter, so copy it to a local and
  draw from the copy**: `context.Rng.Next(10)` called twice takes a defensive copy each time and hands back the
  identical number, silently and deterministically.
- **`TileWanderBehaviour`** - the engine's shipped default and the thing to replace rather than to extend: leash,
  chase, retaliate, stand-your-ground, wander, in that order, stateless. The stand rule is the feel fix a click
  game wants: an actor something has locked onto stops walking away before the first blow lands, instead of
  finishing the wander leg it had rolled. The wander drops a goal the actor's whole footprint cannot stand on
  (`TileCollision.CanStand` at `TileActorContext.FootprintSize`) rather than walking toward it, and the leash
  measures anchor to home anchor. Not installed by any constructor, so an actor with no behaviour stands
  exactly where it was put. The parameterless constructor reads `TileActorContext.TraversalMap`, and
  `CreateWithTiming` supplies custom pause and retaliation timings without overlapping the original constructor.
  The original constructor still accepts a fallback map for contexts a consumer builds by hand, while a
  server-supplied profile map wins.
- **`ITileCombatRules`** / **`TileAttackContext`** / **`TileAttackOutcome`** - where the GAME plugs into the hit
  pipeline. The engine owns whether a swing is DUE (the cooldown) and whether it is LEGAL (adjacency through
  `TileReach`). That reach check uses the attacker's movement map, which keeps an actor's final chase geometry on
  its selected topology. Players continue to use the constructor map. It asks the one footprint predicate the follow
  asks, of the tiles both bodies ended the tick on: the target's whole footprint, and the attacker at its own
  simulator's `FootprintOf`, so the follow and the roll cannot disagree about the attacker's size.
  `TileAttackContext` carries both BODIES as well as both tiles since 19.0.0
  ([#907](https://github.com/APKiwiOrg/KhaozEngine/issues/907)): `AttackerFootprint` is the attacker's square through
  that same `FootprintOf`, `TargetFootprint` is the target's own state's, both committed on the server timeline and
  both the very rects the reach check above the roll read. They are TRAILING and DEFAULTED, so a context built by
  hand with the seven original positional arguments still compiles and answers an empty rect for each. Melee reads
  neither, since range was settled before the roll. A ranged, area or size-scaled rule measuring geometry itself is
  what they are there for. `CanAttack(attackerNetId, targetNetId)` is
  the admission rule for acquiring a target. A false answer is rewritten to `Continue` before a player command or
  actor latch can create a lock, chase, swing, or roll. Its default permits every target for source compatibility.
  `Roll` is called once per eligible attacker per tick, in the engine's fixed order and BEFORE any of the tick's
  damage is applied, so no roll sees another roll's result, and `AttackTicks` is the per-attacker cadence. Build an
  outcome through `Hit` or `Miss`: the two fields are read independently, so a hand-built
  `new TileAttackOutcome(false, 50, 0)` is a miss that takes 50 health.
- **`TileCombatEvent`** - one resolved swing, explicit rather than derived. `Amount` is the ROLLED damage, so an
  overkill reports more than was taken (award experience off the target's health, not off this), and `Killed`
  rides the blow that caused the death so a client never has to notice an absence to know something died.

### Actor traversal profiles

The profile value is an opaque game-owned key. Build the alternate topology, register it during server setup, and
assign it to the definition or direct spawn. The callback overload of `TileCollisionBaker.Bake` replaces only the
ground blocking rule. It receives absolute world tile X and Z plus a zero-based plane for every tile in every
loaded region. The baker then runs the ordinary solid, diagonal, wall and wall-corner object passes unchanged.

```csharp
var swimmer = new TileActorTraversalProfile(1);
TileCollisionMap swimmerMap = TileCollisionBaker.Bake(document, catalogs,
    (x, z, plane) => !GameGroundRules.CanSwimmerStand(document, catalogs, x, z, plane));

server.Actors.RegisterTraversalProfile(swimmer, swimmerMap);
server.Actors.Add(new TileActorDefinition
{
    Id = "marsh-creature",
    MaxHealth = 5,
    TraversalProfile = swimmer,
}, new TileCoord(18, 27, 0));

server.Actors.Behaviour = new TileWanderBehaviour();
```

Register every non-default key before the first authoritative tick and before any spawn or spawner that names it.
The map is held by reference, so later dynamic blockers affect destination checks, routes, committed steps,
repaths, chase, wander, leash return and respawn. The profile follows an actor through region handoff. Unknown keys
are refused at the public spawn doors, and an unresolved ECS tag freezes instead of falling back to another map.
Mutate a retained map only on the server's owning thread between ticks. The player simulator and client prediction
always keep the constructor map.

**Wire**

- **`TileProtocol`** - the tile wire. Every frame carries a leading TAG byte, so the demux is by tag and never by
  length. `CreateRegistry(planeCount, configure)` builds the world-bound `ReplicationRegistry` both heads share
  and refuses a ground item or pending command whose whole-int plane is outside that world. The legacy
  `CreateRegistry(configure)` overload stays unbounded because it has no world count. `AssembleMoveState` is the one
  sanctioned way to put a route back onto a decoded or migrated state, `BuildConnectToken` builds the token the
  door reads, and the frame codecs are the command, the snapshot, the opaque game message, the notice and the
  combat frame. `ServerFrameCombat` (`EncodeCombat` / `TryDecodeCombat`, at most `MaxCombatEvents` of them) is its
  own frame family rather than a game message, because the game-message `kind` is a number the GAME defines and
  these are the ENGINE's events about a pipeline the engine owns. It is a frame at all because a MISS moves health
  by zero and two hits on one tick collapse into one delta, so a fight drawn from replicated health shows fewer,
  larger, later hitsplats than the fight the server ran. The count rides in one byte, so a tick that resolved more
  swings than one frame holds is CHUNKED across several: `EncodeCombat(events, start, count)` is the overload that
  slices one, and it is what the serve uses so an over-long viewer slice costs that viewer an extra packet rather
  than taking the tick down for every player. The whole-list overload still throws above the cap, which is the
  right answer for a game building a frame by hand.
- **`TileServerReason`** - the stable wire reason tokens a tile server sends. Not display text.
- **`TileCells`** - the one place tile space meets the shard grid: `CellSize`, `CoordOf(tile)` and
  `RegionOf(cell)`.

**Server**

- **`TileWorldServer`** (+ **`TileWorldServerConfig`**) - the authoritative server, a `ShardHost` whose cell grid is
  the tile region grid. `Poll` pumps the transport, `Tick` runs the world, and the seams are `OnBeforeTick`,
  `OnAfterMovement`, `OnInteract`, `OnInteractEntity`, `OnGameMessage`, `OnCannotReach`, `PlayerJoined` and
  `PlayerLeaving`.
  `OnInteract` carries authored object ids and `OnInteractEntity` carries entity net ids. It is also the
  `IPersistenceHost<TileMoveState>`. The seat index reads BOTH ways, `TryGetPlayerNetId` and `TryGetPlayerSlot`,
  because the combat seams all name net ids while a game's per-seat state is keyed by slot. The reverse answers
  false for an actor's id and forgets a seat on the same leave that frees it. **The tick is EIGHT steps**, not five, with the head's own systems ahead of
  the first of them: drain one command per player, the actor step (1b), step every cell, authority handoff and
  border ghosting, the action queue, combat (4b), serve every client its area of interest, then the despawn every
  actor killed this tick owes (5b). The reap sits BEHIND the serve deliberately: a corpse taken out of the world at
  4b is gone before each viewer's interest set is built, so the killing blow would be filtered out of every frame
  and a head could only learn a monster died by noticing an absence. A throw inside the serve does not lose the
  reap, which is drained at the top of the next combat pass.

  `OnAfterMovement` is the same-tick consumer deadline between border ghosting and action resolution. It fires once
  per whole tick with the configured tick duration. A handler sees newly admitted `Attack` and `WalkTo` state plus
  settled region ownership, and anything it writes still reaches interaction resolution, combat and an owner-cell
  snapshot served that tick. Border ghosts refresh the write on the next tick because sync has already completed.
  `OnBeforeTick` remains the earlier seam for systems that must author state before commands and movement.

  Actors are `Actors` (the `TileActorHost`, including its authored-spawner `SpawnAdmission` gate), `SpawnActor`,
  `DespawnActor`, `TryGetActorState`, `ActorCount`,
  `ActorNetIds`, `OnActorSpawned`, `RefusedActorSpawnCount` and `LargestFootprintSize` (the largest body spawned so
  far, which is what the serve inflates its interest query by, see the interest section below). `OnActorSpawned` fires with the spawner link
  ALREADY in place, so a handler attaching a game's own component can read
  `Actors.TryGetSpawnerOf(netId, out var spawner)` and dispatch on `spawner.Definition`. An actor built straight
  through `SpawnActor` has no spawner and answers false. Combat is `CombatRules`, `OnCombatEvent`, `OnDied`,
  `CombatEventsThisTick` and `SkippedHealthlessCombatantCount`, with `TryGetHealth` / `SetHealth` /
  `TryGetCombatState` as the reads and the one write, `ForgetAttacker` as the one field a game can drop (see
  what a dead player leaves behind, below), `DelayAttack` as the one number (see charging attack time, below), and
  `CancelPendingAction` as the quiet way a game abandons an interaction without exposing the action queue.
  Connection pressure is visible through `PendingConnectionCount` and `RefusedPendingConnectionCount`, and the run
  gate through `GatedRunCount` (commands admitted at `Walk` because `TileWorldServerConfig.CanRun` refused the
  slot a run, 0 with no gate configured).
  `TileWorldServerConfig.MaxPendingConnections` caps clients that connected but have not completed Hello. Zero keeps
  the prior unlimited behavior. A positive cap sheds excess connections before they can hold server state.
  `TileWorldServerConfig` gained `MaxActorsPerCell` (the
  per-REGION monster budget, since a cell is a region), `ActorMove` (the actor's own `TileMoveOptions`, whose
  default drops `MaxPathRadius` from 64 to 12 because `FindPath` allocates `(2r+1)^2` scratch per call, about 83 KB
  at 64 and 3 KB at 12) and `CombatLogoutTicks` (zero by default, and one number with two jobs: how long a
  dropped fighter's body lingers attackable, and the lookback that decides whether a leaving player was fighting
  at all).
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
  lying with it. The footprint reads follow the same split: `TryGetRemoteFootprint(netId, out rect, out plane)` is
  the square a remote covers off the delayed sample, on the same timeline as its body (which lands centred on it and
  trails it by up to a step while gliding), for click bounds and a target highlight, and
  `TryGetLatestRemoteFootprint` is that square off the newest applied snapshot, which is what `TileRemoteTargets`
  answers a rule with. Both refuse an unknown id and the local player, whose square is
  `Prediction.PredictedState.Footprint`. `TryGetRemoteStepProgress` and the bulk `CollectRemoteSteps` add how far through its step a remote
  is, 0 as the step commits and 1 once the body is at rest, off the same sample the pose is drawn from. `PendingCommandCount` is how far the local prediction runs ahead of the newest basis: 0 or 1 on a loopback, the
  round trip in ticks plus one on a real link, and a climbing-and-staying value when the server is applying this
  client's input late, which `TileWorldServer.InputDepth(slot)` reads from the server side. `NetStats` is the link readout beside the session ones, a live `NetTransportStats` forwarded from
  the transport (round trip, loss, cumulative byte counters), so a HUD does not need to keep the transport it built.
  A transport that tracks nothing answers `NetTransportStats.Unavailable`, an all-zero DISCONNECTED value that says
  nothing about the session, so `IsJoined` stays the read for that. Three more events land here: `CombatEvent` per swing whose TARGET is in this client's own area of
  interest (misses included, and the thing a hitsplat is drawn from), and `RemoteEntered` / `RemoteLeft`, the
  lifecycle pair a per-remote overlay stack is built and pruned on. The diff behind the pair is already computed
  every frame, so it costs nothing beyond one array per frame that actually carries churn.
- **`TileWorldClient.SetSteering(direction, mode)`** / **`Steering`** - the held direction a keyboard player walks
  on, beside the click door. A LEVEL where `Queue` is an EVENT: a head sets it every frame from its own input and
  the command clock reads it on every tick, so a steering tick can never be lost to frame timing. On a command
  tick with nothing queued and a direction held, the client predicts and sends `TileCommand.Steer` instead of
  `Continue`. A queued click WINS its tick, and steering resumes on the next one. `mode` rides the level and is
  NOT adopted as `RunMode`, unlike a queued click's: a head that lets a modifier invert the pace while steering
  passes the inverted mode here, and the saved toggle is what the next `Continue` carries once the keys are
  released. `SetSteering(null, mode)` releases the level, after which the step in flight lands and nothing
  starts, so the body never takes an extra tile.
- **`TileSteering.FromAxes(right, forward, cameraForward)`** - the pure quantization from a head's movement keys
  to the one `TileDirection` a steer carries, so every tile game shares one. `right` is right minus left and
  `forward` is forward minus back, each clamped to -1..1. **`cameraForward` is a WORLD-space look vector**, exactly
  what a camera's `Forward` answers, and the helper converts it through `TileWorldSpace` itself, because tile north
  is world -z. A consumer that hands in a tile-space vector gets a silently north-south MIRRORED result. Only the
  ground-plane part is read and it need not be normalized. The answer is null for no input, for opposite keys that
  cancel, and for a camera looking straight down. An exact octant boundary resolves to the counter-clockwise
  neighbour, the same answer every time. Client only, and its output is an integer direction, so no float it
  computes reaches the simulation.
- **`TilePresenter`** / **`TilePose`** - the pure map from a tile point to a world position and a yaw, and the only
  file in the package that consults `TileWorldSpace`. Two answers, and mixing them up is the one mistake here.
  `Pose(state, extraTicks)` is the BODY: the linear glide from `StepFrom` into `Tile` by the step's own tick
  count, carried forward by the fraction of a tick since the state was sampled and clamped at the end of the step.
  Against that same sample its bound is one Chebyshev grid step. A large body is centred on its footprint, anchor
  plus half its edge on each axis, and the glide runs anchor to anchor so that offset is constant through a step.
  `TryGetRemotePose` goes through `Pose`, so a consumer's large remote is centred with no change on its side.
  `LocalPose(prediction)` adds no footprint offset, because the local player is always one tile, and additionally carries the
  prediction layer's inter-tick easing. With no active reconciliation offset, its bound against
  `PredictedState.Tile` is one grid step plus one local command tick of travel. An active offset adds its current
  magnitude to that conservative bound until it decays or a hard snap clears it.
  `StepFraction(state, extraTicks)` is the fraction that glide interpolates on, exposed so a rule that must run in
  lockstep with a body (a fade, a squash, a footfall) measures the number the body is drawn at rather than a second
  estimate of it, and it reads 1 for a body at rest.
  `PoseAt(tile)` is the RULES: a whole tile's centre, with no glide, which is what a true-tile marker, a route
  highlight, a minimap or an editor draws on. The `PoseAt(planar, vertical, facing)` overload takes a smoothed
  or fractional position for the same mapping when the caller already holds one. `LocalPose(prediction)` is the body for a caller holding its own
  `ClientPrediction`, and `client.LocalPose` is that call already wired. Holds no state and no tuning, so
  replacing it when the document loads cannot change how anything moves. A pose names the FOOTPRINT centre, which
  for a one-tile body is the tile centre, half a tile in from the corner on each axis, the middle of that tile's
  ground quad and the point a 1x1 `TileObjectProps` prop is anchored at, so a head draws at `pose.Position` without
  re-centring it. `PoseAt(TileRect footprint, int plane, TileDirection facing)` is the overlay form for a footprint:
  its centre with no glide, for a footprint marker or a nameplate anchor that is not a body, and exactly
  `PoseAt(tile)` for a one-tile rect.
- **A POSE STANDS ON THE TERRAIN, not on the plane floor.** Once the planar centre is known the height comes from
  `ITileGroundHeight`, one `HeightAt(float tileX, float tileZ, int plane)` taking TILE units on the lattice and
  answering world METRES, sampled at the same centred point the position is built from. So a body on an authored
  slope has its feet on the ground quad it stands on, and so does a marker or a dropped item laid down through
  `PoseAt(tile)`. A gliding body resamples every frame at its interpolated planar position, which is what makes it
  FOLLOW a slope between two tile centres instead of stepping at the tile edge. `new TilePresenter(document)`
  wires `TileDocumentGroundHeight`, the document's own bilinear lattice and the same heights the terrain mesh and
  the props are built from, so a head that builds its presenter from the world file needs no call of its own, and
  that adapter is the single place tile units become world metres for a height read. That one-argument form reads
  the TERRAIN alone and consults no object, so a body on a bridge deck stands on the carved bed under it.
  `new TilePresenter(document, catalogs)` wires `new TileDocumentGroundHeight(document, catalogs)` instead, which
  answers the higher of the lattice height and `TileWalkSurfaces.TryHeightAt`, the highest
  `TileObjectArchetype.WalkSurfaces` top covering the point. A walk surface buried under the terrain loses to it,
  the plane is clamped for the surfaces exactly as it is for the lattice, and both the document and the catalogs are
  held and read through, so an edit is visible to the next pose. Build it with the same catalogs the props are drawn
  from, so the deck a body stands on is the deck on screen. The
  `(tileSize, planeHeight)` placeholder has no source and stays flat at the plane index times `PlaneHeight`, the
  only honest answer before a document is loaded, and `Ground` is null on exactly that one.
  `new TilePresenter(tileSize, planeHeight, ground)` takes a source explicitly, for a test with a synthetic slope
  or a head whose terrain is streamed. A plane with no authored heights keeps its derived lift, one `PlaneHeight`
  per plane over the lattice below it, and a fractional plane index, which is what a body easing between planes
  carries, reads between the two planes' own samples rather than popping. `Yaw` is untouched, and the aimed pose
  draws at the same height as the plain one for the same state.
- **A BODY HOLDING A LOCK IS DRAWN AIMING AT IT.** `TileMoveState.Facing` answers the cardinal side the two
  footprints touch on, which is exact for reach and up to 18 degrees off as a drawn yaw the moment either body is
  bigger than one tile: a player beside a 2x2 cow points at the column it touches, and so does the cow. So
  `client.TryGetRemotePose` and `client.LocalPose` aim a body that is NOT stepping and holds a `CombatTarget`, or
  an `InteractTarget` whose route has run out, at that target's `ITileTargets.TryGetAimPoint`. A remote resolves
  its target on the same DELAYED timeline its body is drawn from, so an attacker never leads a target that has
  already moved on the server, and the local body resolves on the newest capture, which is the read the reach
  rules already make. A mid-step body keeps its step facing, a body with no lock keeps the tile facing, and a
  target that stopped resolving falls back to it too. A one-tile body beside a one-tile target draws EXACTLY its
  cardinal, to the bit, so a game asserting `TilePresenter.Yaw(TileDirection)` against a pose for an ordinary
  fight stays true. `TilePresenter.Yaw(Vector2 from, Vector2 to)` is the formula on its own, the same hand and the
  same north as `Yaw(TileDirection)`, and `presenter.Pose(state, aimTilePlanar, extraTicks)` is the whole thing
  for a body a game places by hand. Presentation only: `Facing`, the reach rules, the server's `Facing` write and
  the wire are untouched, and a game's own turn smoothing keeps working because it only smooths toward whatever
  yaw the pose reports.
- **`TileDrawPriority`** - ONE BODY PER TILE AT REST, rebuilt per frame. `Rebuild(client, dt)` reads a live
  client, `Rebuild(localNetId, localTile, localLeaving, others, dt)` takes a caller's own roster and each actor's
  step progress, and the static `Select` is the winner rule with both output buffers owned by the caller. The
  answer is a WEIGHT: `Weight(netId)` is 0 through 1 and `IsDrawn(netId)` is that above zero, so an existing
  caller keeps working. A body that loses a tile it is stepping INTO falls to 0 exactly as it comes to rest
  there, so it walks visibly under the winner instead of vanishing a step early, and one that steps back OUT
  rises to 1 as that step lands. A loss or a gain with no step to ride (the winner moved, not the loser) crosses
  over `FadeSeconds`, default 0.25 s, and a body seen for the first time starts on its answer rather than fading
  in. The overloads without a `dt` cut instead, which is the rule as it behaved before weights. The local player
  is pinned at 1. `TryGetDrawn(tile, out netId)` asks by place and answers the tile's OWNER, `Drawn` and `Count`
  are every body being drawn, faders included (the live set, so a rebuild invalidates an enumeration in flight,
  and a count of BODIES rather than of tiles). The local player
  wins their own tile outright and every other tile goes to the highest net id on it, which is the OSRS PID
  ruling with a key that cannot flicker: a net id does not move for an actor's life, where an order keyed on
  distance or arrival time re-decides itself mid-step. The plane is part of the tile. The local player is judged
  on their PREDICTED tile and a remote on its committed tile off the DELAYED timeline (`TryGetRemoteTile`), so
  the hide runs on the same clock as the bodies. A step commits its tile when it starts, so the local player also
  claims the `StepFrom` it is walking out of and nothing is ever drawn over their own body. A remote keeps one
  tile and carries that one-step lead: a body on the tile a remote is leaving hides it until its step lands.
  "No local player" is the `NoLocalPlayer` sentinel rather than a negative id, because a packed net id can be
  negative. Allocation free per frame after the first rebuild, and presentation only: a hidden actor is still
  replicated, still clickable and still swinging. `Policy = TileDrawPriorityPolicy.SettledStacksOnly` changes
  that answer to binary idle-stack visibility. Moving bodies stay at weight 1 and claim no destination or
  departure tile. Once settled, the local player wins their tile, and `SettledComparison` chooses among all other
  bodies. A positive comparison means its first net id wins. Zero falls back to the higher net id, so equal game
  ranks remain stable. The callback sees only net ids, leaving all game classifications in the head. A local
  presentation within two float values of its tile centre is settled, preventing equal-endpoint interpolation
  rounding from dropping the local claim while preserving real step and correction motion. A body AT REST covers
  its WHOLE footprint under either policy: `Rebuild(localNetId, localTile, localFootprintSize, localLeaving,
  bodies, dt)` and its `bool localMoving` twin take `(netId, tile, stepProgress, footprintSize)` per body, and
  `Rebuild(client, dt)` reads each remote's `FootprintSize` off the same delayed sample its tile comes from. The
  stack collapses whole: bodies are resolved best first and each takes every tile it covers or none of them, so a
  body that loses one tile of its square is hidden rather than drawing the part nobody else claimed, and
  `TryGetDrawn` answers a large body on every tile it covers. A MOVING body keeps the answer it has today under
  each policy. Every overload without a size reads every body as one tile. Step progress is CLAMPED when it is
  finite, so a negative value is the start of a step and only a value that is not a number reads as 1.
- **`TileClientMessageHandler`** - the delegate an opaque server message arrives on.

**Persistence**

- **`TilePlayerRecord`** - the stored record under `player:{accountId}`: tile, plane, facing and the game's opaque
  blob. All integers, so a record round-trips exactly and the dirty comparison is a byte compare.
- **`TileWorldPersistence`** (+ **`TileWorldPersistenceConfig`**) - the TILE binding of
  `KhaozEngine.WorldStore.StatePersistence<TState>`. The save interval, the dirty pass, the load guard, quarantine,
  the guest policy and the rejoin hints are the shared core, and this type supplies the four tile-shaped answers.
  Built with the same baked `TileCollisionMap` the head runs on, so a stored record naming a plane or a region an
  edited world no longer has is quarantined and its player placed at the spawn, rather than reaching
  `TileWorldServer.SetPlayerState` and throwing out of the head's frame loop. `QuietRestoreDistance` defaults to
  half a tile here rather than the core's one, because this binding is a lattice and puts the PLANE on the
  position's Y: at the core's default a restore that moved a player a whole floor measured exactly 1, passed as no
  move, and the client glided between floors instead of cutting. Restore distance is measured from the original
  integer tile coordinates, so adjacent tiles stay distinct even beyond the exact integer range of a float.

## Ground items, whose lifecycle is the engine's and whose meaning is yours

A dropped stack on a tile is a replicated entity: `TileWorldServer.SpawnGroundItem(at, itemId, count,
ttlTicks)` places one (net id from the actors' own allocator, refused countably at
`TileWorldServerConfig.MaxGroundItemsPerCell`, throwing on a malformed placement exactly as
`SpawnActor` does), the server despawns it unprompted when its clock runs out (`OnGroundItemExpired`),
and `DespawnGroundItem` is the deliberate removal whose true-once answer is what a pickup racing the
expiry sweep keys on: move your payload only after it answers true. `TryGetGroundItem`,
`GroundItemCount` and `GroundItemNetIds` are the server-side reads. The per-cell cap also counts a drop held in an
evicted cell snapshot. Checking a cold coordinate materializes and restores that cell before deciding the spawn.

The component is two meaning-free integers plus the tile (`TileGroundItem`: `ItemId`, `Count`, `X`,
`Z`, `Plane`), deliberately not a dependency on `KhaozEngine.Items`: the engine owns existence,
replication, the plane filter and the clock, and a game owns what an item IS and what taking one
MEANS. A drop has no move state (it never moves), so clients read them through
`TileWorldClient.CollectGroundItems(buffer)` rather than `RemoteNetIds`, per frame, with no lifecycle
of their own: a despawned drop is simply absent on the next call. Items are `Transient` like actors:
a cell capture never persists them.

The intended pickup shape, all game code: click routes a walk to the drop's tile, arrival sends your
own TAKE message naming the net id, your handler re-proves tile proximity per request, moves the
stack into your own storage, and despawns.

`TileWorldServerConfig.GroundItemVisibleToSlot` optionally filters a ground item's entire entity for
each viewer. The callback receives the authenticated viewer slot and ground net ID after the normal
plane and area-of-interest filters. Null keeps every drop public. False omits the entity from that
viewer's snapshot, and a later change in either direction is carried by the ordinary interest delta.
The callback runs synchronously on the simulation tick and must be pure and non-throwing. A game keeps
its own owner identity and must still refuse a forged TAKE request from another player. Filtering
replication never authorizes a claim.

### An item INSTANCE on a drop, when a stack is not just an id and a count

A game whose items are individuals rather than quantities drops one through the six-argument overload,
`SpawnGroundItem(at, itemId, count, ttlTicks, instanceId, payload)`, and the four-argument call above
delegates to it with no instance, so nothing that already compiles changes. The identity and the bytes
ride a SIBLING component, `TileGroundItemInstance` (`InstanceId`, `Payload`), seated only when
`instanceId` is non-zero: a drop with no instance carries no component and pays nothing on the wire.
`TryGetGroundItemInstance(netId, out instance)` is the server read a claim goes through, beside
`TryGetGroundItem`. The client read is `TileWorldClient.CollectGroundItemInstances(buffer)` beside
`CollectGroundItems`: every drop that carries an instance, as `(NetId, Item, Instance)`, in one walk of the
entity set and the same cleared then filled shape. A drop with no instance is absent from it.

Both halves are opaque, exactly as `TileGroundItem`'s `ItemId` is opaque. The engine never decodes a
payload, has no way to, and never mints an instance id of its own: the same id and the same bytes come
out of a claim as went into the drop, whoever is claiming, so a drop-and-claim cycle cannot launder an
item into a fresh one. What the engine owns is still existence.

Three rules, all of them the codec's:

- **`TileProtocol.MaxInstancePayloadBytes` is 512**, mirroring `ItemSlot.MaxPayloadBytes` in
  `KhaozEngine.Items` (a mirror because this package carries no dependency on the item packages, held
  equal by a test in `KhaozEngine.Server.Tests`). The spawn throws above it, and so does the encoder.
- **A payload with instance id 0 throws.** Zero means the drop has no instance, no component is seated,
  and the bytes would go nowhere. It is a caller bug of the same shape as a drop of nothing.
- **The reader is total.** A declared length past the component's own framed payload, or above the cap,
  arrives as an instance with an EMPTY payload rather than as a dropped session. It is the one component
  reader in this package that answers instead of throwing, because the engine assigns these bytes no
  meaning and so has nothing to rebuild wrongly out of a short read.

It is a sibling component rather than five more fields on `TileGroundItem` because that component's
codec writes twenty bytes with no declared length, so a client built against it consumes twenty bytes
and then reads the next component's type id. A new field would make every already-shipped client
misparse the rest of the entity. A new extension id is length prefixed, so a client that never
registered it skips it and keeps reading, which is what makes this additive on a live wire. It is also
not registered `OwnerOnly`: that channel scopes a component to the client whose own net id equals the
ENTITY's, and a drop's net id is never a viewer's, so it would hide the instance from everybody
including the player who dropped it.

## Object states, an authored object that has left its authored form

A world document's objects are static: a `TileObject` is an id, an archetype, a tile, a plane, a rotation and
tags, and nothing on it changes at runtime. `TileObjectState` is the replicated mutable half, so a server can
say "object 412 is spent" and every client hears it.

An entity per DEPARTED object rather than one per object, which is the shape and the reason it is cheap: a
world nobody has touched carries no state entities at all, and a forest of a thousand trees with two stumps in
it carries two. The component is `ObjectId` (the document's `TileObject.Id`), an opaque `State` int, and the
tile (`X`, `Z`, `Plane`). `State` is meaning-free in exactly the way `TileGroundItem.ItemId` is: the engine
owns the lifecycle (set, replicate, expire, clear) and a game owns what 1 and 2 mean.

The tile rides in the component for the same reason a drop's does, and it is the half that is easy to leave
out. The interest grid asks an entity where it is and the serve asks it which plane it is on, so a component
that answers neither encodes and decodes perfectly, passes every codec test, and is shown to nobody.

Server side:

```csharp
// A chopped tree, spent for 60 ticks and then back on its own.
server.SetObjectState(objectId: 412, state: SpentTree, at: new TileCoord(150, 88, 0), ttlTicks: 60);
server.TryGetObjectState(412, out int state);
server.ClearObjectState(412);                 // the game's own revert, true once
server.OnObjectStateExpired += id => ...;     // the clock's revert, never the game's
```

`ttlTicks` is optional and 0 means no clock at all, so a state that stands until the game reverts it simply
does not arm one. A second `SetObjectState` for an object that already has one UPDATES it in place and keeps
the entity, so a client sees a value change rather than a despawn and a respawn. The expiry sweep runs before
the movement pass, so a revert decided this tick ships in this tick's snapshot. There is deliberately no
per-cell budget where a ground item has `MaxGroundItemsPerCell`: a drop's population is driven by an event
rate a kill farm can raise without limit, while there is at most one state per authored object.

Client side, either polled or evented:

```csharp
client.CollectObjectStates(buffer);           // allocation-free once the buffer has grown
client.TryGetObjectState(412, out int state);
client.ObjectStateChanged += (id, state) => view.OverrideArchetype(id, ArchetypeFor(state));
client.ObjectStateCleared += id => view.ClearOverride(id);
```

The events fire once per CHANGE rather than once per snapshot, which is what makes them safe to swap a mesh
out of. `ObjectStateCleared` also fires when an object leaves this viewer's area of interest, deliberately: the
engine cannot tell a head about an object it is not being served, so a head that kept drawing the last state it
heard would be drawing a guess with no expiry. The state comes back through `ObjectStateChanged` the moment the
object is in interest again, and the interest radius is measured in cells, so the boundary is a whole region
away from the tiles a head is drawing detail at.

The renderer half is `TileWorldView.OverrideArchetype` in `KhaozEngine.TileWorld.Render3D`, which draws one
placed object as a different archetype without touching the document.

**A state changes what is DRAWN and nothing else.** `TileDocumentTargets` and the baked collision map both read
the AUTHORED archetype, so a spent object still answers an `Interact` at its tile and still blocks what its
authored form blocked. Refusing is the game's job on both heads: reject the action server side where the rule
that spent the object lives, and suppress the menu row client side off the state you already hold.
https://github.com/APKiwiOrg/KhaozEngine/issues/823 is the engine follow-on that would carry state into target
resolution. `CollectObjectStates` carries no remaining ticks either, and there is no server reader for the
clock, so a head that wants a countdown keeps its own beside the state it set.

## The player health contract, which is the first thing a game with combat gets wrong

**A spawned PLAYER has no `TileHealth` at all.** An actor gets one from its spawn spec, and nothing writes a
player's, because `Max` is a number out of the game's own skill core and an engine default would be the engine
picking a gameplay value. The component is kept ABSENT rather than zeroed on purpose, since a zero-health player
would read as a corpse to every death check in the pass.

So a game with combat calls `server.SetHealth(netId, new TileHealth { Current = hp, Max = maxHp })` on join, on
level up and on respawn. **Until it does, that player can neither swing nor be hit.** The combat pass skips a
combatant carrying no health in BOTH roles, and it does it silently: nothing is raised, logged or thrown, the
client sees no hitsplat, and a fight simply never starts.

`TileWorldServer.SkippedHealthlessCombatantCount` is the reading that says so. It counts SKIPS rather than ticks
(a healthless player both swinging and being swung at on one tick adds two, a pack rolling at one adds one per
attacker), and it counts the ABSENT component only, never an ordinary corpse at zero. Any non-zero reading at all
names the same one fix, so watch it in a dev head. It is a counter rather than a `Debug.Assert` because CI runs
Release.

**Reading that health back on the CLIENT is two calls, and there is no convenience read for it.** `TileHealth` is
replicated to every viewer that holds the entity in interest, which is the whole reason it costs four bytes per
entity per snapshot, and a health bar takes it off this client's own mirrored world: `View` maps a net id to the
entity mirroring it, `World` holds the components replicated onto that entity. Absent means what it means on the
server, that nothing has written a health for that entity, so a bar is drawn for a combatant and not for a rock.
The two tile reads (`TryGetRemoteTile`, `TryGetLatestRemoteTile`) have no counterpart here yet.

```csharp
if (client.View.TryGetEntity(targetNetId, out Entity mirrored)
    && client.World.TryGet(mirrored, out TileHealth hp))
    DrawHealthBar(hp.Current, hp.Max);
```

## What a dead PLAYER leaves behind, which the engine deliberately does not clear

An ACTOR that dies is despawned at step 5b, so every lock naming it stops resolving and the follow drops it on the
next tick. A PLAYER is never despawned. The engine clears the dead player's own target, raises `OnDied` with its
slot and stops there, because where that body goes is the game's answer: a spawn point, a hospital, a revive where
it fell. The killer is therefore left holding both halves of the fight, and a game whose answer MOVED the body has
to end it, or that killer walks to the new tile and picks the same fight up again.

```csharp
server.OnDied += (deadNetId, killerNetId, slot) =>
{
    if (slot < 0) return;                        // an actor, and the reap answers that one
    IReadOnlyList<long> actors = server.ActorNetIds;
    for (int i = 0; i < actors.Count; i++)
    {
        long id = actors[i];
        if (!server.TryGetActorState(id, out TileMoveState st) || st.CombatTarget != deadNetId) continue;
        server.Actors.Command(id, TileCommand.WalkTo(st.Tile, st.Mode));   // the LOCK, through the one stepper
        server.ForgetAttacker(id, deadNetId);                              // the DAMAGE RECORD
    }
    game.RespawnPlayerInTown(slot);
};
```

**Both halves, or neither is worth writing.** A latched walk on the actor's own tile is what drops a lock, which is
the idiom the leash break itself uses, and on its own it lasts one tick: nothing else ages the damage record, so a
retaliating behaviour reads it the moment the actor holds no target and takes the same victim straight back.
`ForgetAttacker(netId, attacker)` drops the record only when it names that attacker, so a grudge against a third
party who was also swinging survives the death, and it never touches the lock, because the stepper owns that. Call
either half from `OnDied` rather than from `OnCombatEvent`: the combat pass raises `OnDied` after every one of the
tick's swings has landed, while `OnCombatEvent` fires per swing inside that application, where a later swing of the
same tick stamps the dropped record straight back.

**The sweep is O(live actors) per player death**, one state read per actor, because the engine holds no reverse
index from a target to the entities locked onto it: a second index is a second structure to keep correct on every
spawn, despawn and region handoff, and a death is rare enough that a scan bounded by the actor count is the cheaper
of the two. A world with far more actors than a few hundred, or one whose deaths are frequent enough to make the
scan show up in a tick, wants its own index keyed by target and this loop replaced by a lookup into it.

### Charging attack time for something that is not a swing

`DelayAttack(netId, ticks)` pushes an entity's next swing out by `ticks`, by ADDING to
`TileCombatState.CooldownRemaining`. It is the public writer for the swing cadence, and the only one: everything
else on that component is either read-only to a game or reached through the rules seam.

```csharp
// OSRS: a bite costs attack time rather than interrupting the fight.
if (game.TryEat(slot, item)) server.DelayAttack(playerNetId, ticks: 3);
```

**ADD, not max.** A bite taken with three ticks still to run delays the swing by the bite's own cost on top of the
wait already there. A max would make the same bite free whenever the attacker happened to be mid-cadence, which is
a fight that rewards eating at exactly the wrong moment. The wait saturates at 255 rather than wrapping, because a
wrap turns a long stun into no stun at all.

An entity that has never fought gets a `TileCombatState` created for it, carrying the delay and nothing else. That
matters for a PLAYER, who acquires the component lazily on the first tick the combat pass has something to write:
without the create, a delay would do nothing until the second fight and an Attack command issued on the next tick
would swing straight through it. The created state leaves `AttackTicks` at zero, so `ITileCombatRules.AttackTicks`
still answers the cadence at the first swing exactly as it does today.

The delay runs down once per tick like any other cadence, so it is a wait rather than a freeze. False means no live
cell owns the id, and a zero `ticks` writes nothing while still reporting whether the entity is there.

### Cancelling an interaction without refusing it

`CancelPendingAction(slot)` quietly abandons a pending interaction after the game accepts another action that
supersedes it, such as eating while walking to a tree. It atomically clears the server's one-deep action queue entry
and the player's `InteractTarget`. It does not raise `OnInteract` or `OnCannotReach`, and it sends no
`ke:cannot-reach` notice. False means the seat is missing or has no pending action, including a repeated call.

The player's route and step stay intact. Cancelling an interaction is not a movement command, so the authority
continues the walk already in progress. A client that also wants to stop its predicted body should send the usual
`WalkTo` through its command queue. That command remains useful for prediction and for clearing any older buffered
commands. The cancellation call does not touch `CombatTarget` or `TileCombatState`, so a game may cancel interaction
intent and charge a combat cooldown independently.

### Ending a fight from the game without a refusal

A `SetPlayerState` write that drops or changes `CombatTarget` is the game ending that fight on purpose (a pause
while an award commits, a scripted disengage), so the tick's broken-lock report skips it: no `OnCannotReach`
and no `ke:cannot-reach` follow, even when the write happens inside the tick from `OnCombatEvent`, which
`ResolveCombat` raises one step ahead of the report. The report covers only locks the simulator itself broke, an
unreachable or vanished target. A write that keeps the same target changes nothing the report reads.

## Interest is measured from the nearest footprint tile

A viewer holds an entity when the NEAREST tile of that entity's footprint is within `InterestRadius` of the
viewer's own anchor tile, Euclidean, which is the metric the interest grid has always used. So a 2x2 enters a
viewer's snapshot on the same tick a one-tile body on its near tile would, and an 8x8 seven tiles before its anchor
arrives. Cell ownership and region handoff are untouched and still measure from the anchor. The viewer's own
position is its anchor too, which costs nothing because a player is one tile.

An empty or negative-size footprint is defensive input no current writer produces. The interest predicate treats
one as its anchor, so a future source cannot make `Math.Clamp` throw inside the serve.

**Pad `OverlapMargin` for the largest body you author.** The serve asks the grid for
`InterestRadius + (N - 1) * sqrt(2)`, where N is `TileWorldServer.LargestFootprintSize`, the largest
`FootprintSize` the server has spawned: a body's anchor can sit its own diagonal behind the near tile that put it
in range, and the home cell has to be holding that anchor as a ghost. At the default 15 tile radius that is 16.42
for a 2x2 and 24.9 for an 8x8. The default `OverlapMargin` of 25 covers every legal body at the default radius, so
only a game that narrows the band or widens the radius has to do this sum. A body the margin cannot cover is
refused at `TileActorHost.Add` and at `SpawnActor`, with an `ArgumentOutOfRangeException` naming both numbers,
rather than throwing out of the first serve and taking the tick down for every player.

`LargestFootprintSize` never decreases. Despawning the world's only cow leaves the query as wide as the cow made
it, which costs a slightly wider grid sweep and cannot cost correctness.

A world of one-tile bodies pays NOTHING for any of this: the query radius is the configured one, the per-viewer
filter never runs, and the served set is what it was before footprints existed, value for value. A world with a
large body in it pays a wider grid sweep plus a predicate over each viewer's interest set, and no extra walk of the
cell's world, because the footprints the filter reads are collected by the pass the plane filter already makes.

## Nearby player interest and verified names

`CollectInterestSlots` exposes the same plane-filtered player set that the authoritative snapshot pass uses.
It includes players at or within `InterestRadius`, including the source player. It clears caller-owned storage
first, sorts the result by slot and returns the final count. Call it only on the simulation tick because it shares
the serve epoch and interest scratch with snapshot replication. A missing source clears the list and returns zero.

```csharp
using System.Collections.Generic;

var recipients = new List<int>();
server.CollectInterestSlots(senderSlot, recipients);
```

`TryGetPlayerDisplayName` reads the verified display name from a live player's authoritative `TileIdentity`.
It returns false and an empty string if the slot no longer owns a live player or identity. A present but anonymous
identity returns true with an empty name. A server can combine the two calls for nearby chat and discard the
submission if the sender, name or audience cannot be verified.

```csharp
using System;

if (!server.TryGetPlayerDisplayName(senderSlot, out string senderName)
    || string.IsNullOrWhiteSpace(senderName))
    return;

server.CollectInterestSlots(senderSlot, recipients);
if (recipients.Count == 0)
    return;

foreach (int recipientSlot in recipients)
    SendChat(recipientSlot, senderName, message);
```

## A server, in ten lines

```csharp
var document = TileWorldFile.Load(worldDirectory);
var map = TileCollisionBaker.Bake(document, catalogs);
// ONE registry, handed to BOTH heads. A game's own components register at or above TileProtocol.FirstGameTypeId.
var registry = TileProtocol.CreateRegistry(document.PlaneCount, RegisterGameComponents);

var server = new TileWorldServer(
    transport,
    new TileWorldServerConfig
    {
        TickSeconds = 0.25f,                       // the GAME's number, not the engine's
        StepTicks = new TileStepTicks(walk: 4, run: 2),
        Spawn = new TileCoord(64, 64, Plane: 0),
        MaxPendingConnections = 128,
        CanRun = slot => energy.Has(slot),         // null allows everyone. The authority behind run energy
        IsBanned = bans.IsBanned,
    },
    map,
    new TileDocumentTargets(document, catalogs),
    // OfWorldAndCatalogs, not OfWorld: the world digest alone cannot see an archetype gaining a CollisionKind,
    // so two heads with independently updated catalogs would pass the gate and disagree on every wall.
    ConnectionGate.Wrap(tokenAuth, protocolVersion: "grimhollow-1",
                        worldHash: TileWorldHash.OfWorldAndCatalogs(document, catalogs),
                        log: Console.WriteLine, isBanned: bans.IsBanned),
    registry);

server.OnInteract += (slot, netId, target) => game.Interact(slot, target);
server.OnInteractEntity += (slot, netId, targetNetId) => game.InteractEntity(slot, targetNetId);
server.OnAfterMovement += dt => game.StepActions(dt);

while (running)                                    // any frame clock: the server accumulates its own ticks
{
    server.Poll();                                 // ALWAYS before Tick, so a click lands on the next tick
    server.Tick(dt);
    persistence.Update(dt);
}
```

Monitor `server.PendingConnectionCount` for handshakes still in flight and
`server.RefusedPendingConnectionCount` for the total shed by `MaxPendingConnections`. Size the cap above the real
concurrent join burst from a launch or restart. A non-zero refusal count can mean either a flood or a cap set too
low for normal traffic.

`BeginDrain(TileServerReason.Draining, graceSeconds)` on SIGINT announces the token to every client at once, keeps
ticking through the grace so a player mid walk finishes it, and raises `IsDrainComplete` once the grace is spent
AND the sessions are closed. Flush persistence there, then exit. The countdown is the shared
`KhaozEngine.Simulation.Hosting.DrainController`. A second `BeginDrain` remains an idempotent no-op for this server, so it
does not resend the notice or restart the grace.

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

// The keyboard door, set every frame. camera.Forward is a WORLD-space vector.
TileDirection? held = TileSteering.FromAxes(right, forward, camera.Forward);
client.SetSteering(held, client.RunMode);

client.Poll();                                     // once a frame
client.Tick(dt);                                   // the command clock, one command per whole tick
client.AdvancePresentation(dt);                    // the render clock, before drawing

priority.Rebuild(client, dt);                      // one TileDrawPriority, held for the session

TilePose me = client.LocalPose;                    // the BODY, gliding into its committed tile
// Walk the collected list, not RemoteNetIds: that one is an IReadOnlyCollection, so a foreach over it
// boxes an enumerator every frame, and Drawn has the same shape.
client.CollectRemoteTiles(remotes);                // the head's own reused List<(long, TileCoord)>
foreach ((long id, TileCoord _) in remotes)
{
    float weight = priority.Weight(id);            // 0 hidden, 1 whole, in between walking under somebody
    if (weight > 0f && client.TryGetRemotePose(id, out TilePose them)) Draw(id, them, weight);
}

// The true-tile overlay, which is how the lead is made visible rather than smaller. Index the route from
// Route.Index: Tiles is an IReadOnlyList, so a foreach boxes an enumerator every frame.
TileMoveState rules = client.Prediction.PredictedState;
DrawMarker(client.Presenter.PoseAt(rules.Tile));
for (int i = rules.Route.Index; i < rules.Route.Tiles.Count; i++)
    DrawRouteTile(client.Presenter.PoseAt(rules.Route.Tiles[i]));
```

## Walking on a held key

A click names a goal and the simulator paths to it. A keyboard player holds a DIRECTION instead, which is its own
intent and has its own command kind. Faking it with `WalkTo` at the adjacent tile is wrong twice over: `WalkTo`
searches, and an unreachable goal walks to the nearest reachable tile, so a key held into a fence sends the body
on a detour, and it costs one search per tick per steering player on the server.

Set the level every frame from the head's own input:

```csharp
// Each frame, from the head's own input:
TileDirection? held = TileSteering.FromAxes(right, forward, camera.Forward);
client.SetSteering(held, client.RunMode);
```

**`camera.Forward` is WORLD space.** `TileSteering.FromAxes` takes a look vector rather than a yaw angle so no
consumer has to match an angle convention, and it converts that vector through `TileWorldSpace` itself, because
tile north is world -z. Hand it a tile-space vector and the answer is silently mirrored north to south. Only the
ground-plane part is read and it need not be normalized. The contract is the player's seat: forward walks the body
away from the camera, back toward it, right toward screen right, at every camera angle. It answers null for no
input, for opposite keys that cancel and for a camera looking straight down, and an exact octant boundary resolves
to the counter-clockwise neighbour, the same way every time.

What the level does on the wire is ordinary. On a command tick with nothing queued and a direction held, the
client predicts and sends `TileCommand.Steer(held, mode)` in place of `Continue`. A queued click WINS its tick and
steering resumes on the next one. The mode rides the level and is NOT adopted as `RunMode`, so a modifier that
inverts the pace while steering leaves the saved toggle to the next `Continue`.

The simulator resolves the direction on a BOUNDARY tick only, through `TileSteerResolver`: the direction itself
when it is open, for a blocked diagonal whichever of its two axis steps is open, otherwise a stand. A blocked
steer still turns the body to face the held direction. A steer replaces a route, a pending interaction and a
fight exactly as `WalkTo` does, and the server treats it as the deliberate disengage a walk is, so a lock it
breaks is not reported as a lost target. Releasing the keys means the next command is `Continue`, the step in
flight lands and nothing starts.

**Steering adds no pace value.** A steered step and a routed step are stamped by the same private `StepTotalFor`,
the one place this package asks `TileStepTicks` how long a step lasts, through one shared commit body. The run
gate is unchanged too: the server downgrades a run the game's `CanRun` refuses whatever the command kind is.

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

`CanRun` is the one knob that deliberately sits OUTSIDE that contract. It is consulted in admission for every
command whose mode is `Run`, over the player's slot, and returning false admits that tick's command at `Walk`
instead, whatever the client sent. Null, the default, allows running for everyone. The rewrite touches the mode and
nothing else, so the kind, goal and target still apply, and it lands at the start of the NEXT step exactly as a
client's own toggle does: the step already under way keeps the cadence it was stamped with. A game's run energy is
the intended caller. The client is expected to drop its own toggle cooperatively, which is what keeps both heads
predicting the same cadence, and the gate is the AUTHORITY behind that, so a patched client sending `Run` on an
empty bar is stepped at a walk and reconciles. It runs on the TICK THREAD once per tick per running player, so it
must be cheap, and nothing is caught: a throw comes out of the tick rather than being swallowed into a cadence
nobody chose. Watch `TileWorldServer.GatedRunCount` for how often it fired.

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

A notice frame declares its own length, and the decoder refuses one whose declared length does not account for the
WHOLE datagram, pad byte included. A lying length is the shape a probe takes and no legitimate sender produces one,
so the strictness is deliberate, but it constrains transport choice: a transport that pads every datagram out to a
fixed size cannot carry these notices, because the padding it adds is length the frame never declared.

## A payload too large for one game message

`TileFragmentedMessage` splits any `ReadOnlySpan<byte>` into chunks that each fit inside one game message, and
`TileFragmentReassembler` puts them back. Both are ITEM AGNOSTIC and know nothing about what they carry: the game
picks the kind, the stream id and the decoder, exactly as it does for an ordinary envelope.

```
[StreamId: byte]        // which logical stream, the GAME assigns these
[Sequence: uint16 LE]   // increments per transmission of that stream, wraps
[ChunkIndex: byte]
[ChunkCount: byte]      // 1 to 255
[Bytes: the rest]
```

A chunk is a game message PAYLOAD, not a frame, so the caller wraps each one with
`TileProtocol.EncodeGameMessage` under its own kind and sends it `ReliableOrdered`.
`TileFragmentedMessage.MaxChunkPayloadBytes` is `MaxGameMessageBytes` less the four byte envelope less the five
byte header, so a chunk carries 1015 bytes and 255 of them carry about 258 KB. Every chunk but the last carries a
FULL load, which is what lets a reader tell a truncated chunk from a legitimately short final one.

`Fragment` THROWS above `MaxPayloadBytes`, on the same grounds as the game message cap throw: a payload that long
is a local caller bug. Everything on the reading side is total and never throws, because those bytes came from a
remote peer.

```csharp
foreach (byte[] chunk in TileFragmentedMessage.Fragment(streamId: 1, sequence: page.Version, encodedPage))
    server.SendGameMessageTo(slot, kind: GameKinds.PageChunk, chunk);

// On the client, one reassembler per connection.
if (reassembler.TryComplete(payload, out byte streamId, out ReadOnlyMemory<byte> assembled, out string? reason))
    ApplyPage(streamId, assembled.Span);   // decode HERE, and quarantine what will not decode
else if (reason != null)
    Telemetry.Count(reason);               // refused, and the token says why
```

`streamId` is the id the chunk headers carried, set whenever the answer is true, so several streams on one
message kind (a bag, the worn slots and a bank, say) are told apart by the header rather than by a copy of the
stream inside the payload that could disagree with it. The three-out overload without it answers identically and
delegates to this one.

**One reassembler per connection slot.** The type holds the partial assemblies of ONE peer and no connection
table of its own, so a server keeps an array or a map of them beside its session table and forwards each peer's
chunks to that peer's instance. `Slot` is carried as identity and `DropConnection(slot)` refuses a slot that is
not its own, so a mis-wired forward cannot wipe the wrong peer's assemblies.

Four rules, and none of them is a timer, because a timer on a reliable ordered channel measures nothing:

- A chunk whose `Sequence` differs from the assembly in progress for its stream discards that assembly and starts
  a new one. That is what a server restarting a page mid transmission looks like, and it is not an error.
- At most `MaxPartialAssemblies` partial assemblies are held at once, which is four. A fifth evicts the one
  fed longest ago and increments `EvictedAssemblies`. A restart is not an eviction and is not counted as one.
  The bound is a hard constant with no constructor knob, so a host with more concurrently fragmented streams per
  peer evicts silently and `EvictedAssemblies` is the only reading that reports it.
- The last chunk hands the assembled bytes BACK through `TryComplete`. Nothing here decodes them, so a payload
  that will not decode is the caller's quarantine rather than a throw from the wire. A final chunk cut in its body
  is the case that reaches the caller, because the header declares no total length.
- A partial assembly still open when the connection drops goes with an explicit `DropConnection(slot)` the server
  calls from its own disconnect path.

It does not REORDER, deliberately. The channel is `ReliableOrdered`, so a chunk cannot arrive out of order or be
lost without the connection failing, and a chunk that is not the next one expected is refused rather than
buffered. `TryComplete` returns false two ways and the `reason` out tells them apart: null means the chunk was
accepted and more are expected, non null means it was refused. The two refusal tokens are `ke:fragment-malformed`
(a chunk this format never produces) and `ke:fragment-out-of-sequence` (a well formed chunk that is not the one
expected next, which also discards the assembly it contradicts). Both carry the `ke:` prefix for the same reason
`TileServerReason` does, so a game counting its own tokens alongside them can never collide.

Memory is bounded by what the peer actually SENT: a buffer grows with the bytes that arrive rather than with the
chunk count a header claims, so a lying `ChunkCount` buys nothing.

**A whole page is the FLOOR, not the steady state.** A game syncing item container pages over this fragmenter
sends a one frame DELTA for the ordinary case and falls back to a full page send only when the delta will not
fit. The split is by OWNERSHIP of the bytes: `ContainerPageDelta` builds the delta and
`ContainerPageSyncRequest` is the two byte resync a client answers with, both in `KhaozEngine.ItemInstances`,
because the entry body they carry is the container codec's, and this package fragments whatever bytes it is
handed and gains no items dependency at all. `ContainerPageDelta.TryBuild` answers -1 when the next change
would not fit one frame, and -1 is the caller's cue to `Fragment` the whole page instead, encoded for that
viewer by `ItemContainerPageCodec.EncodeProjected`, which projects every entry exactly as the delta does.
Never a second delta frame: two deltas for one page would have to be applied in order by a client that may
have missed the first, which is the reassembly problem this type already solves once.

Two facts a server composing the two owes its own code. `TileProtocol.MaxGameMessageBytes` is COPIED into
`ContainerPageDelta.MaxGameMessageBytes`, because that package is `Foundation` and this one is `Server`, and
`PageSyncFrameBoundTests` in `KhaozEngine.TileWorld.Netcode.Tests` is the one place that sees both constants
and holds them equal. And the resync request is RATE LIMITED at one page per client per tick, which is a
documented server rule rather than engine code on either side: the engine caps the frame and the game owns
the message kinds and the tick, so a server that serves every request it receives has handed an
unauthenticated peer an amplifier of two bytes in and about 7 KB out.

## Known limits in this release

- **`TileWorldServerConfig.MaxCommandsPerSecond` is a simulated-time rate.** Each whole tick tops the bucket up
  with `MaxCommandsPerSecond * TickSeconds` tokens. `Poll` only transports messages, so polling faster than the
  command clock does not raise the sustained ceiling. The default is 40 messages per simulated second.
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
- **No actions beyond the seam.** `TileActionKind` distinguishes authored-object and entity interactions so their
  overlapping ids reach `OnInteract` and `OnInteractEntity` respectively. The engine knows nothing about what an
  interaction does after the callback.
- **A large actor is an NxN body everywhere the RULES look, and since 19.0.0 everywhere interest and the draw
  rule look too.** Interest is measured from the nearest footprint tile (see the interest section above), and a
  settled body's draw stack is every tile of its square, so a one-tile body standing in a cow's rump no longer
  overlaps it on screen. Players stay one tile: `SetPlayerState` refuses a `FootprintSize` above 1.
- **Actors do not block movement.** Players walk through monsters. Making an actor block would put a DYNAMIC entry
  in a collision map each head bakes for itself from files, so the two heads would disagree on every occupied tile
  and every chase would become a correction storm. The honest answer is a server-owned occupancy overlay the
  client mirrors, which is gated behind a tighter client view of actor tiles rather than merely postponed.
- **Actors are not persisted across a restart.** There is nothing worth persisting, and the tile stack wires no
  cell-blob persistence at all. A monster that respawns at its authored point after a restart is the same monster
  the player would have seen anyway.
- **Combat is MELEE only.** `TileCollisionFlags.ProjectileBlocked` is still reserved and unset, and it is where
  line of sight will go. The seams this round builds (the entity target space, the cooldown, the hit pipeline, the
  combat frame) are what a ranged round plugs into: a projectile is a hit whose range test is a line rather than
  an adjacency.
- **The roll is NEVER predicted client-side.** A client predicts its own approach and never its own damage, so a
  hitsplat costs one round trip by design. That is what lets `ITileCombatRules` be a plain server-side seam with
  no cross-head determinism requirement at all, only server-side reproducibility for tests and replays.

The four limits above from actor blocking onward are the R1 deferrals of an in-flight program, each with its reason
in section 12 of `docs/design/TILE-COMBAT-ACTORS-DESIGN-2026-08-27.md`, tracked by
[#736](https://github.com/APKiwiOrg/KhaozEngine/issues/736). That section's multi-tile deferral was lifted by
`docs/design/TILE-ACTOR-FOOTPRINTS-DESIGN-2026-09-15.md`, whose own section 12 carries the footprint limits above.
One R1 finding was filed on its own and is closed:
[#738](https://github.com/APKiwiOrg/KhaozEngine/issues/738), a `Migrating` combat target reading as gone on a
networked shard link, answered by `TileEntityTargets.MigratingGraceRefreshes`, which holds the frozen pre-handoff
footprint for a bounded number of refreshes.

Design: `docs/design/TILE-WORLD-NETCODE-DESIGN-2026-08-22.md`,
`docs/design/TILE-COMBAT-ACTORS-DESIGN-2026-08-27.md` and
`docs/design/TILE-ACTOR-FOOTPRINTS-DESIGN-2026-09-15.md`.
