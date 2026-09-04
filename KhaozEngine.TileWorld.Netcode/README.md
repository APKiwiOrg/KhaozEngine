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
id and the local player, and neither extrapolates. `client.CollectRemoteTiles(buffer)` is the delayed read for
everybody at once, for a per-frame pass over the whole crowd. `docs/USING-KHAOZENGINE.md` carries the worked
example.

**A crowd on one tile draws ONE body.** Every body draws on the tile centre, so a stack of them is a smear of
overlapping meshes, and the body a player can least afford to lose in it is their own. `TileDrawPriority` picks
the one to draw per tile: the local player on their own tile, and on both tiles of a step in flight, with the
highest net id everywhere else. It is the OSRS PID ruling with a stable key, it is presentation only, and it is
in the types list below.

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
  anything on this lattice disengages. 41 payload bytes on the wire. `IsStepping`
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
  walk it), `Interact` (route to a reach tile of a target, face it, act as the last step commits) or `Attack`
  (lock onto a target and chase it while it moves). The MODE
  rides on every command, `None` included, so the run toggle lives on the tick stream rather than on the click.
  `Attack` needs a KIND of its own rather than a flag on `Interact`, because `Target` spans two id spaces that
  overlap EXACTLY: a `TileObject.Id` is a document counter from 1 and a net id is `(nodeId << 48) | counter` from
  1, so object id 7 and the seventh spawned entity are the same 64 bits and one resolver could not tell which
  space a click meant. The kind is the discriminator.
- **`TileMoveMode`** - walk or run, a two-value selector rather than a speed.
- **`TileStepTicks`** - ticks per step, per mode. Both heads must hold the same pair, or a step commits a tick
  apart and every step reads as a misprediction.
- **`TileMoveOptions`** - the pathfinder knobs both heads must agree on: `AgentSize`, `MaxPathRadius` and
  `MaxRouteSteps`, the longest route one click may produce, counted in the steps still to take from the tile the
  player is committed to.
- **`TileIdentity`** - the cosmetic display name, replicated to everyone in interest. Never a rules input.
- **`PendingTileCommand`** - the command drained for a player this tick. Registered on the `Migrate` channel
  ALONE, so it crosses a cell handoff and reaches no client and no persistence blob. The movement pass resets it
  to `Continue` at the mode the step left, and it does so at tick step 2 while the handoff runs at step 3, so
  what crosses a border is the tick's neutral rather than a click waiting to be applied twice.

**Simulation**

- **`TileMoveSimulator`** - the ONE discrete stepper both heads run, pure over its inputs and integer-only.
  `Accepts` is THE definition of whether a command applies at all, `Step` advances one tick, and `BeginWalk`,
  `BeginInteract` and `BeginAttack` are the three route starts. It takes TWO target seams, `targets` for the
  object space and `combatTargets` for the entity space, the second appended LAST in the constructor so an
  existing positional call keeps meaning what it said. `Follow` runs at the top of every `Advance`: while a
  `CombatTarget` is held it re-paths to a reach tile whenever the target's committed tile moved, stands when it is
  already in reach, STEPS OFF the target's own tile when a catch left it standing there (a tile inside the footprint
  is not in reach, so holding it is a fight that can never start), and clears the lock when the target stops
  resolving or has no reachable tile. That in-reach stand also writes `Facing` toward the target, on EVERY tick it
  answers in range rather than once as the attacker lands, so a combatant turns with a target that moves around it
  and the step-off does not leave it looking 180 degrees away from what it is swinging at. `Step` takes the
  stepped entity's own net id as its fourth argument, read by that follow and by nothing else: it is what tells
  an `Attack` naming the attacker ITSELF, which stands, apart from
  one naming another entity on the same tile, which steps off. A step commits its tile at its START, after the `CanStep` re-check, so
  a blocker is felt when the step would begin rather than when the foot lands. The step in progress is never
  abandoned either, and it needs no special case for it: a route is always pathed from `Tile`, which is the tile
  the step in flight is entering, so a direction change while moving never drags the avatar back toward the tile
  it was leaving. The route cap counts the steps still to take from that tile.
- **`TileMovementSystem`** - runs the simulator over every OWNED entity inside a cell's own fixed tick,
  skipping ghosts and migrating entities so nothing is stepped twice in one tick. It holds TWO simulators and
  picks on the `TileActor` tag, so an actor paths at its own `ActorMove` radius while a player paths at the
  click radius. The one-argument constructor still exists and runs one simulator over everything.
- **`TileReach`** / **`TileActionQueue`** / **`TilePendingAction`** / **`TileActionKind`** - the OSRS reach rule and
  the one-deep pending action. `TileReach.Set` is every tile cardinally adjacent to a footprint tile that the
  footprint tile could step OUT onto, `Contains` is the in-range test, `TryNearest` picks the reach tile by real
  path length with scan order as the tie-break, and `FacingToward` turns the arriving actor toward what it came
  for. `TryNearest` refuses a footprint further than `maxRadius` + 1 away without searching, since no reach tile of
  one is inside the pathfinder's window: the answer is the same false, and it is what stops a client naming a far
  target it has never seen from buying up to eight full window floods per command. Past that it prunes per
  candidate, skipping one outside the window or already at or past the best length found so far, both of which
  the loop would have discarded after paying for the search: a walk is eight-connected, so its step count is
  never below the Chebyshev distance to its goal. The chosen tile and the tie rule are unchanged by either.
  `agentSize` and `maxRadius` are validated at the top of `TryNearest` rather than left to the first search, so a
  bad argument throws whether the target is open, walled in, out of range or on another plane.
- **`ITileTargets`** / **`TileDocumentTargets`** / **`TileEntityTargets`** / **`TileRemoteTargets`** - the seam
  that resolves a target id to a footprint and a plane, and its three implementations across TWO id spaces.
  `TileDocumentTargets` is the OBJECT space, backed by the document over `TileObjectArchetype.Interactive` and
  read through on every call, so an id stops resolving the moment the thing it named stops existing. It answers
  the INVERSE too: `TryGetTargetAt(tile, out long id)` is the click-to-target search, the whole footprint rather
  than the anchor tile, lowest id first when two targets overlap so both heads resolve one click the same way.
  Compose it with `TileRaycast.Pick`, whose hit is a ground tile, and a click is resolved in two lines.
  `TileEntityTargets` is the server's ENTITY space, a per-tick SNAPSHOT over the live cells refreshed once before
  anything moves, which is what makes the actor pass and the movement pass order-independent in fact rather than
  in claim: every read is a keyed lookup into a map built before either pass began. `TileRemoteTargets` is the
  client's entity space, the honest `TryGetLatestRemoteTile` for a remote and the prediction for the local player,
  and the client builds its own rather than taking one, because the only honest answer to where an entity is on a
  client is that client's newest snapshot. A `Ghost` is EXCLUDED and therefore reads as gone, which is the answer
  the follow acts on. A `Migrating` entity is HELD instead, for `MigratingGraceRefreshes` consecutive refreshes
  (four by default, one second at a 250 ms tick), answering with the frozen pre-handoff tile it is not moving off:
  an in-process link finishes the whole handshake inside one `ProcessHandoffs` so the window is never used, and a
  NETWORKED link spans calls, where dropping on the first unresolvable refresh breaks every fight whose target
  crosses a region boundary. The hold is bounded so a handshake that never completes cannot pin a lock, and the
  destination cell's owned copy always wins over the source's frozen one.

**Actors and combat**

An ACTOR is a player minus a connection. It carries `TileMoveState`, `TileRouteState` and `PendingTileCommand`, so
it steps through the same `TileMoveSimulator` for free and can never move in a way a player could not.

- **`TileActor`** - the tag marking a server-owned entity. ECS-only and never replicated, which is why the host
  rewrites it every tick: a region handoff captures only the registered components, so a crossing actor would
  otherwise arrive on the far side no longer an actor.
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
  that go on ONE entity (`MaxHealth`, `AttackTicks`, `Facing`, `Mode`), and the door refuses a zero `MaxHealth`, an
  off-map or off-plane tile and a cell already at `MaxActorsPerCell` by answering 0 rather than throwing, so a
  spawner can never take a tick down with it. `RefusedActorSpawnCount` counts the refusals. An actor is
  `Transient` at `DurableOnly`, so a cell eviction FREEZES it rather than ending it, and both doors instantiate
  the coordinate before they resolve: the cap counts a frozen actor rather than admitting a spawn on top of it,
  and the despawn reaches one rather than leaving it to come back as an entity nothing indexes.
- **`TileActorDefinition`** / **`TileActorSpawner`** / **`TileActorSpawnerState`** - what a spawn POINT is authored
  from (id, max health, step mode, attack cadence, wander and leash radii, respawn delay, and a game-owned `Kind`),
  and the spawner that owns one home tile, its live actor and its respawn countdown. `LeashRadius` is checked
  against `TileWorldServerConfig.ActorMove.MaxPathRadius` where the definition arrives, at `TileActorHost.Add`,
  because a leash beyond the pathfinder's window is a walk home it cannot plan in one go.
- **`TileActorHost`** (`server.Actors`) - `Add(definition, home)` to register a spawner, `Command(netId, command)`
  to latch one command onto one actor, `Behaviour` and `Seed` for the decision seam, `Spawners`,
  `TryGetSpawnerOf`, `Forget` (the despawn hook, dropping the unspent latch, the birth tile and the spawner link
  together) and `PendingCommandCount`. Its tick is step 1b: every spawner respawns or counts
  down, then every live actor gets its decision translated into a command, plus the tag and
  `PendingTileCommand` rewrite above. It iterates its own net id list rather than an ECS query on the tag,
  because a query over the tag cannot see the one actor that most needs the write.
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
  random stream), so no actor's decision can depend on another having moved first. One instance is SHARED by every
  actor, as a simulator is, so a game that wants different behaviour per monster dispatches on `Definition.Kind`
  inside one implementation.
- **`TileActorRandom`** - a splitmix64 value type, `For(seed, netId, tick)`, so a behaviour needs no per-actor
  storage and a replay reproduces every draw. Deliberately not `System.Random`, whose sequence is not stable
  across .NET releases. **It MUTATES, and the context hands it over an `in` parameter, so copy it to a local and
  draw from the copy**: `context.Rng.Next(10)` called twice takes a defensive copy each time and hands back the
  identical number, silently and deterministically.
- **`TileWanderBehaviour`** - the engine's shipped default and the thing to replace rather than to extend: leash,
  chase, retaliate, stand-your-ground, wander, in that order, stateless. The stand rule is the feel fix a click
  game wants: an actor something has locked onto stops walking away before the first blow lands, instead of
  finishing the wander leg it had rolled. Not installed by any constructor, so an actor with no behaviour stands
  exactly where it was put.
- **`ITileCombatRules`** / **`TileAttackContext`** / **`TileAttackOutcome`** - where the GAME plugs into the hit
  pipeline. The engine owns whether a swing is DUE (the cooldown) and whether it is LEGAL (adjacency through
  `TileReach`). This owns what it DOES. `Roll` is called once per eligible attacker per tick, in the engine's fixed
  order and BEFORE any of the tick's damage is applied, so no roll sees another roll's result, and `AttackTicks`
  is the per-attacker cadence. Build an outcome through `Hit` or `Miss`: the two fields are read independently, so
  a hand-built `new TileAttackOutcome(false, 50, 0)` is a miss that takes 50 health.
- **`TileCombatEvent`** - one resolved swing, explicit rather than derived. `Amount` is the ROLLED damage, so an
  overkill reports more than was taken (award experience off the target's health, not off this), and `Killed`
  rides the blow that caused the death so a client never has to notice an absence to know something died.

**Wire**

- **`TileProtocol`** - the tile wire. Every frame carries a leading TAG byte, so the demux is by tag and never by
  length. `CreateRegistry` builds the `ReplicationRegistry` both heads share, `AssembleMoveState` is the one
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
  `OnInteract`, `OnGameMessage`, `OnCannotReach`, `PlayerJoined` and `PlayerLeaving`. It is also the
  `IPersistenceHost<TileMoveState>`. The seat index reads BOTH ways, `TryGetPlayerNetId` and `TryGetPlayerSlot`,
  because the combat seams all name net ids while a game's per-seat state is keyed by slot. The reverse answers
  false for an actor's id and forgets a seat on the same leave that frees it. **The tick is EIGHT steps**, not five, with the head's own systems ahead of
  the first of them: drain one command per player, the actor step (1b), step every cell, authority handoff and
  border ghosting, the action queue, combat (4b), serve every client its area of interest, then the despawn every
  actor killed this tick owes (5b). The reap sits BEHIND the serve deliberately: a corpse taken out of the world at
  4b is gone before each viewer's interest set is built, so the killing blow would be filtered out of every frame
  and a head could only learn a monster died by noticing an absence. A throw inside the serve does not lose the
  reap, which is drained at the top of the next combat pass.

  Actors are `Actors` (the `TileActorHost`), `SpawnActor`, `DespawnActor`, `TryGetActorState`, `ActorCount`,
  `ActorNetIds`, `OnActorSpawned` and `RefusedActorSpawnCount`. `OnActorSpawned` fires with the spawner link
  ALREADY in place, so a handler attaching a game's own component can read
  `Actors.TryGetSpawnerOf(netId, out var spawner)` and dispatch on `spawner.Definition`. An actor built straight
  through `SpawnActor` has no spawner and answers false. Combat is `CombatRules`, `OnCombatEvent`, `OnDied`,
  `CombatEventsThisTick` and `SkippedHealthlessCombatantCount`, with `TryGetHealth` / `SetHealth` /
  `TryGetCombatState` as the reads and the one write, and `ForgetAttacker` as the one field a game can drop (see
  what a dead player leaves behind, below). `TileWorldServerConfig` gained `MaxActorsPerCell` (the
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
  lying with it. `NetStats` is the link readout beside the session ones, a live `NetTransportStats` forwarded from
  the transport (round trip, loss, cumulative byte counters), so a HUD does not need to keep the transport it built.
  A transport that tracks nothing answers `NetTransportStats.Unavailable`, an all-zero DISCONNECTED value that says
  nothing about the session, so `IsJoined` stays the read for that. Three more events land here: `CombatEvent` per swing whose TARGET is in this client's own area of
  interest (misses included, and the thing a hitsplat is drawn from), and `RemoteEntered` / `RemoteLeft`, the
  lifecycle pair a per-remote overlay stack is built and pruned on. The diff behind the pair is already computed
  every frame, so it costs nothing beyond one array per frame that actually carries churn.
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
- **`TileDrawPriority`** - ONE BODY PER TILE, rebuilt per frame. `Rebuild(client)` reads a live client,
  `Rebuild(localNetId, localTile, localLeaving, others)` takes a caller's own roster, and the static `Select` is
  the rule with both output buffers owned by the caller. `IsDrawn(netId)` is the draw-loop test,
  `TryGetDrawn(tile, out netId)` asks by place, `Drawn` and `Count` are the chosen set (the live set, so a
  rebuild invalidates an enumeration in flight, and a count of BODIES rather than of tiles). The local player
  wins their own tile outright and every other tile goes to the highest net id on it, which is the OSRS PID
  ruling with a key that cannot flicker: a net id does not move for an actor's life, where an order keyed on
  distance or arrival time re-decides itself mid-step. The plane is part of the tile. The local player is judged
  on their PREDICTED tile and a remote on its committed tile off the DELAYED timeline (`TryGetRemoteTile`), so
  the hide runs on the same clock as the bodies. A step commits its tile when it starts, so the local player also
  claims the `StepFrom` it is walking out of and nothing is ever drawn over their own body. A remote keeps one
  tile and carries that one-step lead: a body on the tile a remote is leaving hides it until its step lands.
  "No local player" is the `NoLocalPlayer` sentinel rather than a negative id, because a packed net id can be
  negative. Allocation free per frame after the first rebuild, and presentation only: a hidden actor is still
  replicated, still clickable and still swinging.
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
  move, and the client glided between floors instead of cutting.

## Ground items, whose lifecycle is the engine's and whose meaning is yours

A dropped stack on a tile is a replicated entity: `TileWorldServer.SpawnGroundItem(at, itemId, count,
ttlTicks)` places one (net id from the actors' own allocator, refused countably at
`TileWorldServerConfig.MaxGroundItemsPerCell`, throwing on a malformed placement exactly as
`SpawnActor` does), the server despawns it unprompted when its clock runs out (`OnGroundItemExpired`),
and `DespawnGroundItem` is the deliberate removal whose true-once answer is what a pickup racing the
expiry sweep keys on: move your payload only after it answers true. `TryGetGroundItem`,
`GroundItemCount` and `GroundItemNetIds` are the server-side reads.

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
        Spawn = new TileCoord(64, 64, Plane: 0),
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

priority.Rebuild(client);                          // one TileDrawPriority, held for the session

TilePose me = client.LocalPose;                    // the BODY, gliding into its committed tile
// Walk the collected list, not RemoteNetIds: that one is an IReadOnlyCollection, so a foreach over it
// boxes an enumerator every frame, and Drawn has the same shape.
client.CollectRemoteTiles(remotes);                // the head's own reused List<(long, TileCoord)>
foreach ((long id, TileCoord _) in remotes)
    if (priority.IsDrawn(id) && client.TryGetRemotePose(id, out TilePose them)) Draw(id, them);

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

A notice frame declares its own length, and the decoder refuses one whose declared length does not account for the
WHOLE datagram, pad byte included. A lying length is the shape a probe takes and no legitimate sender produces one,
so the strictness is deliberate, but it constrains transport choice: a transport that pads every datagram out to a
fixed size cannot carry these notices, because the padding it adds is length the frame never declared.

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
- **Both parties in a fight are 1x1.** `TileReach` states three times that its set is anchor tiles for a ONE TILE
  actor, and `AgentSize` is a property of the SIMULATOR rather than of the entity, so a larger monster is two
  structural changes rather than a size field. `TileWorldServerConfig.ActorMove` is deliberately the seam the
  first of them lands on.
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

The five limits above are the R1 deferrals of an in-flight program, each with its reason in section 12 of
`docs/design/TILE-COMBAT-ACTORS-DESIGN-2026-08-27.md`, tracked by
[#736](https://github.com/APKiwiOrg/KhaozEngine/issues/736). One R1 finding is filed on its own:
[#738](https://github.com/APKiwiOrg/KhaozEngine/issues/738), a `Migrating` combat target reading as gone on a
networked shard link, which has a zero-tick window in process today.

Design: `docs/design/TILE-WORLD-NETCODE-DESIGN-2026-08-22.md` and
`docs/design/TILE-COMBAT-ACTORS-DESIGN-2026-08-27.md`.
