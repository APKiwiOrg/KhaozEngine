# Tile combat and server-owned actors: melee on the tick, one basic monster (2026-08-27)

Status: DESIGN, not started. Sub-project 3 of the Grimhollow program, following sub-project 1
([TILE-WORLD-DESIGN-2026-08-15.md](TILE-WORLD-DESIGN-2026-08-15.md),
[#629](https://github.com/APKiwiOrg/KhaozEngine/issues/629)) and sub-project 2
([TILE-WORLD-NETCODE-DESIGN-2026-08-22.md](TILE-WORLD-NETCODE-DESIGN-2026-08-22.md),
[#670](https://github.com/APKiwiOrg/KhaozEngine/issues/670)), whose tick, simulator, reach rules, action queue and
snapshot machinery this design builds on and does not replace. Written against engine 18.1.0 and Grimhollow 0.2.1,
which pins 18.1.0 (`Grimhollow/Directory.Build.props:34`).

**Read sub-project 2's sections 5.1, 5.2 and 6 first.** The lead-commit model, the glide invariant and the reach
rules are the ground this stands on, and this document cites them rather than restating them. The one contract
worth repeating here because everything below is an application of it:

> Every rules question (combat, reach, region, occupancy, what a click resolves against) is answered about
> `TileMoveState.Tile`, the tile the body is still walking into.

That is the standing ruling in [Grimhollow #25](https://github.com/APKiwiOrg/Grimhollow/issues/25) and section 5.2's
invariant. Combat is the first system that actually spends it.

Two rounds, each with its own plan: R1 is the engine (actors and the combat seam in
`KhaozEngine.TileWorld.Netcode`) with headless loopback tests, R2 is Grimhollow's skill core, content, click routing
and combat presentation.

## 1. Problem

The tile stack can walk and it can touch. It cannot fight, and there is nothing in the world to fight.

What sub-project 2 left behind is a seam, not a gap: `TileActionQueue` holds one pending action per player,
`TileMoveSimulator.BeginInteract` routes to a reach tile and remembers the target, and
`TileWorldServer.OnInteract` fires the moment the walk's last step commits. The engine deliberately knows nothing
about what an interaction DOES (`KhaozEngine.TileWorld.Netcode/README.md:305-307`). Three things are missing before
that seam carries a fight:

1. **Nothing in the world moves but players.** There is no `TileWorldServer.SpawnEntity`, no actor, no brain, no
   spawner. The float sibling has all of it (`KhaozEngine.NetWorld/ShardedWorldServer.cs:340`, doc at `:325-339`,
   "a server-owned non-player entity (an NPC, enemy, resource node, ...)"), and the tile server has only
   `SpawnPlayer(int slot, string accountId, string displayName)` (`TileWorldServer.cs:275`).
2. **An interaction is a ONE SHOT against a STATIONARY thing.** `BeginInteract` paths once
   (`TileMoveSimulator.cs:203-231`), and `FaceTarget` DROPS the target when the walk ends off its reach set
   (`:316`, `:322`). A target that moved between the click and the arrival therefore answers CannotReach. A fight is
   the opposite shape: a lock that re-follows every tick and fires repeatedly on a cooldown.
3. **Nothing has health.** There is no damage, no death, no respawn, and no way for a client to draw a hitsplat.

The goal of this sub-project is the smallest complete fight: click a monster, walk to it, hit it on a cadence, take
hits back, kill it or die to it, and see all of that. Everything that makes combat DEEP (ranged, magic, drops,
threat, gear, more skills) is later rounds on the seams this one builds.

## 2. Rulings taken, restated tight

These were decided by the owner before this document. They are recorded, not relitigated.

1. **Melee only.** Ranged and magic are later rounds on the seams built here. `TileCollisionFlags.ProjectileBlocked`
   (`KhaozEngine.TileWorld/TileCollisionFlags.cs:33`) stays reserved and unset, which it already is: a tree-wide
   grep finds four hits, all of them the declaration or a doc line.
2. **One basic monster.** Server-owned, spawns at authored points, wanders on a leash, fights back when attacked,
   dies, respawns after a timer. No drops, no aggro tables, no shops (sub-project 5).
3. **Hitpoints is the FIRST SKILL of a minimal skill and XP core, and that core is GAME-side this round.** One
   consumer is not an engine abstraction. If a second game wants a skill core, the engine-first rule works through
   the fit-failure ledger (`AGENTS.md`, "Consumer fit-failure pairs"), not through a speculative promotion now.
4. **Death teleports the player to the Hollowmere spawn at full health.** The teleport machinery exists and is
   tested: `TileWorldServer.SetPlayerState(slot, state, teleport: true)` (`TileWorldServer.cs:235`) advances
   `TileMoveState.Epoch`, and section 5.2's discontinuity rule makes the client CUT rather than glide.
5. **Combat reads COMMITTED tiles, never drawn positions.** Grimhollow #25 clauses 1 and 3, and section 5.2's
   invariant. The true-tile marker exists so the player can read the tile combat will judge them on
   (`Grimhollow.Core/Client/TrueTileMarker.cs:88`, `Marked(bool joined, in TileMoveState state) => state.Tile`).

Two consequences of ruling 5 that are load-bearing later and are stated here so they are not rediscovered:

- **Ruling 5 is free on the SERVER and expensive on the CLIENT.** The server holds every entity's live committed
  tile, so its answers have no lag term at all. A client's view of ANOTHER entity carries
  `TileWorldClientConfig.InterpolationDelayTicks` (2 by default, 0.33 s at Grimhollow's tick) on top of the glide,
  which Grimhollow already recorded as the knob a combat contract would have to move
  (`Grimhollow/docs/ENGINE-INTEGRATION.md:335-341`). So the rules are exact and the picture is not, and the design
  has to say which side of that line each decision falls on.
- **The local bound is a step plus a TICK, not a step.** Measured at 1.22 tiles through Grimhollow's loopback
  harness against a 1.25 bound, filed as [#735](https://github.com/APKiwiOrg/KhaozEngine/issues/735). Sizing
  anything against a flat one step is sizing against a number the engine does not actually hold.

## 3. What the stack already has, verified

Checked against the code before anything below was decided.

**Actors need almost no new movement code.** `TileMovementSystem.Update` is an ENTITY QUERY, not a player loop:
`world.ForEach((Entity e, ref TileMoveState state, ref TileRouteState route, ref PendingTileCommand pending) => ...)`
(`TileMovementSystem.cs:35-45`), skipping only `Ghost` and `Migrating`. Its doc says "every OWNED player entity" and
its code has no player predicate. Any entity carrying those three components in any cell steps through the same
`TileMoveSimulator` players do, with the same cadence, the same `CanStep` and the same lattice. The interest grid
picks it up for free (`CellSim.DoRebuildInterest`, `CellSim.cs:180-188`, over whatever
`TileWorldServer.PositionOf` answers for, `TileWorldServer.cs:387-393`, which is anything with `TileMoveState`), and
so does the plane filter (`TileWorldServer.Tick.cs:275-281`).

**The sharding layer was built expecting NPCs.** Nothing in `CellSim` or `ShardHost` mentions a slot or a session:
ownership is `netId -> Entity` (`CellSim.cs:39`) and `netId -> CellCoord` (`ShardHost.cs:53`), and `BindClient`
(`ShardHost.cs:511`) is a separate, optional viewer binding. `ShardHost.SpawnOwned(worldX, worldY, netId, out cell)`
(`:280-287`) is the whole entity-creation path, and `SpawnPlayer` uses it at `TileWorldServer.cs:310` and then binds
the client at `:320` as a distinct step. The ghosting doc even names the case
(`ShardHost.cs:331-339`, "a mob's server-only state (a `Persist`/`Migrate`-only aggro table)").

**The tick body already has the hook.** `TileWorldServer.OnBeforeTick` (`TileWorldServer.cs:165-168`) is documented
"for a head's own systems (npc brains, spawners, timed content)", and anything it writes ships in the SAME tick's
snapshot. It fires at `TileWorldServer.Tick.cs:99`, before the command drain.

**The reach rules already ARE melee adjacency.** `TileReach.Set(map, footprint, plane)` (`TileReach.cs:52`) is
every tile CARDINALLY adjacent to a footprint tile that the footprint tile could step out onto: no wall on the
footprint's edge, the candidate is somewhere an agent can stand, no mirrored wall facing back. For a 1x1 footprint
that is exactly the four cardinal neighbours minus the denied ones, which is exactly OSRS melee range against a
1x1 NPC. `Contains` (`:90`) is the in-range test and `TryNearest` (`:130`) is the walk to it.

**The wire has room and needs no new frame for the command.** The client command frame is a fixed 24 bytes,
`[tag:1][seq:4][kind:1][goalX:4][goalZ:4][plane:1][mode:1][target:8]` (`TileProtocol.cs:21-22`), carrying every
field on every kind so the decoder branches on nothing (`TileCommand.cs:29-31`). Extension component ids 16, 17 and
18 are taken (`TileProtocol.Components.cs:17-23`) and `FirstGameTypeId` is 24 (`:28`), so ids 19 to 23 are the
engine's free window. `TileMoveState` is 33 fixed bytes (`:115`, and the field-by-field sum confirms it).

**And the things that are genuinely missing, named:**

- No `TileWorldServer.SpawnEntity` or despawn, and no public accessor for the server's `NetIdAllocator`
  (`TileWorldServer.cs:49`, private, one call site at `:309`). The float sibling exposes both
  (`ShardedWorldServer.cs:191`, `:194`).
- No NPC, AI, brain, behaviour-tree, blackboard or agent abstraction anywhere in the engine. A tree-wide search for
  a type named for any of those returns one hit and it is a test fixture
  (`KhaozEngine.Server.Tests/NetWorld/EntityReplicationSeamTests.cs:25`). `KhaozEngine.Navigation` shipped its
  planner and follower (`docs/INDEX.md:47`, complete at 10.123.0) and deliberately left the brain game-side
  (`NPC-NAVIGATION-DESIGN.md:26-28`), and it is float world-space anyway, so the tile stack cannot use it.
- No client event when a remote appears or leaves. `TileWorldClient` raises `RefusedAtDoor`, `Disconnected`,
  `NoticeReceived`, `CannotReach`, `Teleported` and `OnGameMessage` and nothing else. The diff is already computed
  every frame inside `RefreshRemoteSamples` (`TileWorldClient.Snapshots.cs:204-212`) and simply not surfaced.
- No cell-blob persistence in the tile stack. `TileWorldPersistence` stores player records keyed by account id, and
  `CellSim.SnapshotOwned` is never called from this package. Nothing would persist an actor across a restart.
- **`PendingTileCommand` does not survive a handoff, and for an actor that is a defect rather than a detail.** It is
  deliberately never registered for replication (`PendingTileCommand.cs`, "reaches no client, no persistence blob and
  no handoff capture"), `ProcessHandoffs` captures only the `Migrate` channel (`ShardHost.cs:428`), and
  `AdoptFromMigrate` rebuilds through a throwaway view (`CellSim.cs:539`). A player is immune because step 1 of
  every tick rewrites the component (`TileWorldServer.Tick.cs:121`). An actor that walks over a region boundary
  arrives WITHOUT it and silently falls out of `TileMovementSystem`'s three-component query. Section 5.3 is the
  answer.
- **Two `long` id spaces that overlap exactly.** `TileObject.Id` is a document-wide counter starting at 1
  (`TileWorldDocument.cs:31-32`, `:117`), and `NetIdAllocator` is `(nodeId << 48) | counter` with the counter
  starting at 1 and node 0 for this server (`NetIdAllocator.cs:17-20`, `:36-44`). So object id 1 and the first
  player's net id are the same `long`, and nothing partitions them. `TileCommand.Target` is one field
  (`TileCommand.cs:37`) and `ITileTargets.TryGetFootprint(long target, ...)` is one method (`ITileTargets.cs:15`).
  Section 6.1 is the answer.

## 4. Engine or game, and why this is ONE package

**The split.** The engine owns everything that is about the LATTICE and the TICK: actors as entities that run the
same stepper, their lifecycle and replication, the target lock and the follow, adjacency, the cooldown counter, the
hit pipeline's plumbing, health as a component, the death event, and the wire events presentation needs. The game
owns everything that is a NUMBER or a NOUN: the accuracy roll, the max hit, defence, attack speed in ticks, the
monster's stats and body, where a dead player goes, and every player-facing string.

The line is drawn where a second game would disagree. Two games will not disagree about whether a cardinally
adjacent attacker on the same plane may swing. They will disagree about every number in the swing.

**One package, grown, not a sibling.** `KhaozEngine.TileWorld.Netcode` gains files. The layering argument, weighed
rather than assumed:

- **Everything combat needs is already inside this package and none of it is public in the shape a sibling would
  need.** The follow has to live in `TileMoveSimulator` (section 6.2), adjacency is `TileReach`, the target lock
  rides `TileMoveState`, the resolution runs inside `TileWorldServer`'s tick body between `host.ProcessHandoffs`
  and the serve, and the wire ids come out of `TileProtocol`. A `KhaozEngine.TileWorld.Combat` sibling would need
  every one of those as a public extension point, which is a larger and worse API than the feature.
- **The wire id window is shared whichever way it is split.** Ids 19 to 23 are the engine's free extension window
  before `FirstGameTypeId` (`TileProtocol.Components.cs:28`). A sibling package would allocate out of the same
  window and would therefore have to be registered by `TileProtocol.CreateRegistry` anyway, which is an upward
  dependency wearing a different name.
- **A code-free split saves a consumer nothing.** Both halves land in the same two umbrellas (`Server` for the
  server half, `Game3D` for the client half), so a game that wanted no combat would still restore it.
- **The honest cost of growing is the file-size ratchet, and it is payable.** `TileWorldServer` is already four
  partials and `TileProtocol` is three. Actors and combat get their OWN types and their own partials
  (`TileWorldServer.Actors.cs`, `TileWorldServer.Combat.cs`, `TileProtocol.Combat.cs`), never an extra hundred
  lines bolted onto an existing file. That is the ratchet working as designed rather than a reason to split the
  package.

The package's `ProjectReference` set does not change: `TileWorld`, `Diagnostics`, `Netcode`, `Replication`,
`Sharding`, `Simulation`, `WorldStore`. In particular `KhaozEngine.Navigation` is NOT added (float world-space, and
the tile stack has `TilePathfinder`) and `KhaozEngine.NetWorld` stays out, which the csproj's own description calls
the point.

## 5. The actor model

An ACTOR is a non-player entity that runs the SAME `TileMoveSimulator` players run: same cadence rules, same
`CanStep`, same lattice, same route cap. Server-owned, never predicted by a client, and replicated through the
same snapshot machinery that already carries remote players. It is a player minus a connection, and section 3 is
why that is nearly literal.

An actor entity carries: `NetId` (built in), `TileMoveState`, `TileRouteState`, `PendingTileCommand`,
`TileHealth` (section 6.6), a `TileActor` tag, and a server-only `TileCombatState` (section 6.5). Four of those
seven are what a player already carries, which is the whole design.

It carries NO `TileIdentity`, deliberately. A player's display name is a verified fact the connect token produced
and is the one string the engine lets a server put on the wire. A monster's name is PROSE, and
`TileServerReason.cs:5-12` states the rule the whole stack follows: the server owns no catalog and must never
author player-facing text. So a monster's name is resolved client-side from its kind (section 8.5), and section 5.4
is where that decision shows up as bytes.

### 5.1 Lifecycle: definitions, spawners, respawn

A `TileActorDefinition` is what a spawner builds from: a definition id, max health, the step cadence, the leash and
wander radii, the respawn delay in ticks, and an opaque `int Kind` the game reads to attach its own content. It is
engine-shaped data with one game-shaped hole, deliberately: the engine must not learn what a goblin is.

A `TileActorSpawner` owns one authored point: `(definition, homeTile, state)` where state is `Empty`, `Alive(netId)`
or `Waiting(ticksLeft)`. Its whole behaviour, per tick, is three lines: if `Waiting`, count down and spawn at zero.
If `Alive` and the entity is gone, go to `Waiting(respawnDelay)`. Nothing else.

`TileActorHost` owns the spawner list and is driven from the server's tick body. Spawner order is the order the
spawners were ADDED, never a dictionary enumeration, for the reason `TileActionQueue` states about its own
dictionary (`TileActionQueue.cs:35-37`): a hash layout must never reach a decision.

**Actors are ephemeral by construction, and that is correct rather than a shortcut.** The tile stack persists player
records only, and a respawning monster has nothing worth persisting: its position is its spawner's, its health is
full on spawn, and its identity is its definition. On a server restart every spawner rebuilds its actor from the
same authored point, which is the state a player would see after a respawn anyway. Actors are marked
`Transient` with `TransientScope.DurableOnly` (`KhaozEngine.Sharding/Transient.cs:16-22`, whose doc names
"whole-zone agent state that goes dormant while its cell is unloaded" as exactly this case), so a cell capture that
one day exists does not carry them.

**Spawn and despawn need two new members on `TileWorldServer`**, both modelled on `ShardedWorldServer.SpawnEntity`
(`ShardedWorldServer.cs:340`), which is the shipped precedent on the float stack:

```
public long SpawnActor(TileCoord at, in TileActorSpawn spec);
public bool DespawnActor(long netId);
```

`SpawnActor` allocates from the same private `NetIdAllocator`, calls `host.SpawnOwned(at.X, at.Z, netId, out cell)`
and sets the components, and deliberately does NOT call `host.BindClient`. That one omission is the whole
difference between an actor and a player, and it is why nothing downstream needs a player predicate: net ids do not
know about connections, and the only place a binding is required is `ShardHost.HomeInterest`
(`ShardHost.cs:586-600`), which is the VIEWER side.

Net ids are never recycled (`NetIdAllocator.cs:36-44`), so a respawned actor is a NEW entity with a new id. That
removes a whole class of bug for free: nobody can be left holding a target that silently re-aims at the corpse's
replacement.

### 5.2 The behaviour seam

The engine owns the tick scheduling and the movement. The game supplies the decisions. The seam is one method:

```
public interface ITileActorBehaviour
{
    TileActorIntent Decide(in TileActorContext context);
}
```

`TileActorContext` is a read-only view handed in by the engine: the actor's committed tile as of the START of the
tick, its home tile, its definition, its health, its current combat target, who last damaged it and on which tick,
and a deterministic per-actor RNG the engine seeds. `TileActorIntent` is a small tagged struct: `Idle`,
`WalkTo(tile)`, `Attack(netId)`, or `Break` (drop the target and go home). The engine turns an intent into a
`PendingTileCommand` and lets the ordinary movement pass execute it.

That is a behaviour interface, not a scripting system, and the boundary is drawn at exactly one place: an intent
names a TILE or a TARGET and never a route, a step, a facing or a tick. Everything about HOW the actor gets there
stays inside the stepper both heads run, so an actor can never move in a way a player could not.

**The engine also ships a default implementation, and that is deliberate.** `TileWanderBehaviour` is wander plus
leash plus retaliate plus chase, parameterised by the definition:

- **Wander.** While no target is held, pick a reachable tile inside `WanderRadius` of home, walk it, then idle for
  a randomised pause inside a configured band. Both choices come from the actor's own seeded RNG, so a replay of
  the same server from the same seed produces the same wander.
- **Retaliate.** A damaging hit sets the target to its attacker, unless a target is already held. First attacker
  wins, which is the simplest rule that is not an aggro table (ruling 2 puts those in a later round).
- **Chase.** While a target is held, `Attack(target)`. The follow itself is the stepper's (section 6.2), so this
  intent is one value, re-issued, and costs the behaviour nothing per tick.
- **Leash.** When the actor's committed tile leaves `LeashRadius` of home, `Break`: drop the target, walk home, and
  restore health to full on arrival. Full restore on arrival rather than on break, so a monster dragged out and
  abandoned is not instantly healthy where the player left it.

The alternative shapes were both worse. An engine with only the SEAM and no default means the first monster in the
first game is a hundred lines of pathfind-and-leash the second game rewrites, which is the fit-failure the
engine-first rule exists to prevent. An engine with only the DEFAULT and no seam blocks the second monster the day
it needs to do anything else. Shipping both costs one interface.

### 5.3 What a migrated actor loses, and the fix

Section 3 named it: `PendingTileCommand` is not on any replication channel, so a handoff drops it and the actor
falls out of `TileMovementSystem`'s query. Task 1's review sharpened the list: the `TileActor` TAG itself is
dropped by a handoff capture too, and an untagged migrated actor does not merely stop moving, it falls back to
the PLAYER simulator branch in `TileMovementSystem` and its per-call path scratch. So the host's per-tick
re-add below covers BOTH the tag and the command, and the host iterates its own net ids rather than an ECS
query on the tag, which is why a momentarily untagged actor cannot escape it. There are two candidate fixes and only one of them is right.

**Rejected: register `PendingTileCommand` on the `Migrate` channel.** It would work, and it would also put a
per-tick-mutated command on a channel whose contract is durable state, and it would make the component's own doc
("reaches no client, no persistence blob and no handoff capture") false for the sake of one consumer.

**Taken: `TileActorHost` re-adds the component unconditionally, every tick, before the movement pass.** The actor
decision step already writes `PendingTileCommand` for every live actor on every tick, exactly as step 1 of the tick
body does for every player. So the fix is not a fix at all: it is the same immunity players already have, obtained
the same way. The rule is worth stating explicitly anyway, because a later optimisation that skips writing the
command for an idle actor would silently reintroduce the bug at a region boundary, and it is the kind of bug that
only reproduces on one tile of one map.

The other handoff trap is the route. `TileMoveState`'s encoding does not carry `Route`
(`TileProtocol.Components.cs:60-64`), so a freshly adopted entity reads as IDLE until
`TileProtocol.AssembleMoveState` puts `TileRouteState` back on it. Actor code reads state through
`TryGetActorState`, which goes through `AssembleMoveState` exactly as `TileMovementSystem.cs:44-45` and
`TileWorldServer.Actions.cs:69` do. Reading the raw component instead is the documented failure at
`TileProtocol.Components.cs:94-100`: an actor that crossed a region boundary mid walk would read as ARRIVED a
region early.

### 5.4 What an actor costs, and the cap

**On the wire, per actor, per serve, per viewer whose interest set holds it.** Framing is
`ReplicationRegistry.cs:83-102` (extension components are `[typeId:2][7-bit len:1][data]`) and
`SnapshotWriter.cs:148-165` (`[netId:8]` then components then a `[0:2]` terminator):

| Part | Bytes |
|---|---|
| net id | 8 |
| `TileMoveState`, 41 payload after section 6.1 adds the target | 44 |
| `TileHealth`, 4 payload | 7 |
| terminator | 2 |
| **total** | **61** |

Three components an actor does NOT pay for. `TileRouteState` is `OwnerOnly` (`TileProtocol.Components.cs:78-79`)
and an actor has no owner, so a route never reaches a viewer. `TileCombatState` is not on the `Replicate` channel
at all (section 6.5). `TileIdentity` is absent, so an actor is CHEAPER than a remote player rather than merely
comparable, and the known limit that it rides every snapshot
([#679](https://github.com/APKiwiOrg/KhaozEngine/issues/679)) does not scale with monsters. The game's own
discriminator (section 8.3) adds whatever the game makes it, and a one-byte kind costs four bytes framed.

Twenty actors in one viewer's area of interest is 1220 bytes on top of the 26 byte frame header, four times a
second, so about 5.0 KB/s per viewer. That is affordable, and it is affordable because the numbers are small, not
because the serve is clever: the tile serve writes the viewer's WHOLE interest set every tick with no delta
(`TileWorldServer.Tick.cs:146`, and the known limits at `README.md:294-297`, filed as
[#699](https://github.com/APKiwiOrg/KhaozEngine/issues/699) and
[#680](https://github.com/APKiwiOrg/KhaozEngine/issues/680)). Actors make those two issues matter sooner. They do
not make them worse per entity.

**On the CPU, and this is the real cap driver.** `TilePathfinder.FindPath` allocates its scratch per call:
`int[side*side]` plus `byte[side*side]` plus a queue plus the result list (`TilePathfinder.cs:61-66`), where
`side = 2*maxRadius+1`. At the default radius of 64 that is 129 squared, about 83 KB of Gen0 per call
([#669](https://github.com/APKiwiOrg/KhaozEngine/issues/669)). `TileReach.TryNearest` runs one of those PER
CANDIDATE, up to eight (`TileReach.cs:112-115`, `:140-154`). An actor that re-paths every tick at the player's
radius would churn most of a megabyte per second on its own.

Two knobs answer it, both in the design rather than in a later optimisation:

1. **Actors get their own `TileMoveSimulator` instance with actor-tuned `TileMoveOptions`.** The simulator is
   stateless and shared by every entity that uses it (`TileMoveSimulator.cs:38-40`), so a second instance is free.
   An actor's `MaxPathRadius` is sized to its leash rather than to a player's click: at radius 12 the scratch is
   25 squared, about 3 KB, a 26-fold saving. `TileMovementSystem` holds both simulators and picks on
   `world.Has<TileActor>(e)`, which keeps one pass and one place the `Ghost`/`Migrating` skip lives. The same
   second instance is where a larger `AgentSize` would eventually go (section 12).
2. **The follow re-paths only when the target's committed tile CHANGED.** A stationary target costs zero
   pathfinding per tick. Section 6.2 has the rule.

`TileWorldServerConfig` gains `MaxActorsPerCell` (default 64), enforced as a REFUSAL at `SpawnActor` rather than a
silent drop, following the pattern the rest of the config uses of taking a refusal at the door
(`TileWorldServer.cs:325-329`). Hollowmere is nine regions, so the provisional world budget is 576 actors and the
number a viewer actually pays for is whatever stands inside a 31 by 31 tile window.

## 6. The combat model

### 6.1 Targeting: a new command kind, because the id spaces collide

`TileCommandKind` gains `Attack = 3`. Its `Target` is a NET ID.

**The wire does not change.** The command frame is a fixed 24 bytes carrying every field on every kind
(`TileProtocol.cs:21-22`), so `Attack` reuses the `long Target` that `Interact` already uses and the decoder gains
one admitted value. That is the cheapest possible addition and it is worth naming as a benefit of the fixed-frame
decision sub-project 2 took for a different reason.

**A separate KIND is mandatory, and the reason is section 3's overlap.** `TileObject.Id` counts from 1
(`TileWorldDocument.cs:117`) and this server's net ids count from 1 with node 0
(`NetIdAllocator.cs:17-20`, `:36-44`). Object id 7 and the seventh spawned entity are the same 64 bits. A single
`Target` field with a single resolver could not tell which space a click meant, and the failure mode is silent:
clicking a barrel would sometimes attack a player. The KIND is the discriminator, and it is the only one available
without widening the frame or tagging the ids, both of which are larger changes for the same answer.

So the simulator holds TWO target resolvers, both `ITileTargets`:

```
public TileMoveSimulator(TileCollisionMap map, TileStepTicks stepTicks,
    ITileTargets? targets = null, TileMoveOptions? options = null, ITileTargets? combatTargets = null)
```

`combatTargets` is APPENDED LAST rather than placed beside `targets`, which is the shipped shape and not the one
first drafted here. Every existing call site passes `options` positionally as the fourth argument (the server builds
two simulators, the client one), so inserting ahead of it would be a source break in an additive release. Appended
last, every one of those calls still means what it said.

`targets` is the document space (`TileDocumentTargets`, unchanged). `combatTargets` is the entity space, and it has
one implementation per head, exactly as sub-project 2's target seam does:

- **Server: `TileEntityTargets`** over the live cells. `Refresh(IReadOnlyList<CellSim>)` walks every cell's OWNED
  entities once and fills a `Dictionary<long, TileCoord>` keyed by net id, and `TryGetFootprint(netId, ...)` is a
  keyed lookup into that map, answering `new TileRect(tile.X, tile.Z, 1, 1)` and `tile.Plane`. Ghosts and migrating
  mirrors are excluded by construction rather than by a check, so a border mirror never answers under the owned
  entity's net id.
- **It SNAPSHOTS ONCE PER TICK rather than reading through, and that is the one place it deliberately differs from
  `TileDocumentTargets`.** An authored object cannot move within a tick, so reading it through is free and correct.
  An entity moves on every tick, and the follow that consults this runs INSIDE `TileMovementSystem`'s pass over a
  cell's archetypes, so a read-through resolver would answer with the target's tile from before or after its own
  step depending on the ECS iteration order. That is not cosmetic: an attacker that saw its target's POST-step tile
  would re-path on the same tick the target commits, which collapses the one-tick miss window section 6.4's trace
  depends on and changes how a chase resolves.
- **The snapshot is what makes step 2 order-independent in FACT rather than in claim.** Every read the follow makes
  inside a tick is a keyed lookup into a map that was fully built before the pass began, so no archetype order can
  reach a decision, and `Refresh` itself is order-independent for the same reason in miniature: every write is keyed
  on a unique net id, so a different walk order writes the same map. A read-through resolver would instead need the
  movement pass to PROMISE an order, which is a convention rather than a structure.
- **The client half is still by construction, for one whole reconcile, and the same argument carries it.**
  `OnSnapshot` captures the honest tiles once and then hands `ClientPrediction.Reconcile` the basis, and the replay
  runs every pending command in ONE loop with nothing writing that capture in between, so all of them resolve the
  target to the same tile and replaying the same reconcile twice gives the same state twice. Both heads therefore
  hold the seam still for exactly as long as they need it still: the server for a tick, the client for a reconcile.
- **The price, stated plainly: `Step` is no longer a pure function of state plus command.** The same state and the
  same `Attack` give two different routes if the resolver moved between the two calls. The simulator still holds no
  state of its own, so the property that actually matters is unchanged and it is the CALLER that owns the seam's
  stillness. That is why the stillness is spelled out on both heads above rather than left implicit.
- **Client: `TileRemoteTargets`** over `TileWorldClient`, resolving through the honest read R0 landed,
  `TryGetLatestRemoteTile` (`TileWorldClient.Snapshots.cs:209`), and the local player's own prediction for its
  own net id. The delayed `TryGetRemoteTile` stays what an on-body overlay reads and is never a rules input,
  which is the two-reads split R0 exists for.

**The two heads still resolve the same target to slightly DIFFERENT tiles, and the residue is accepted.** With
R0 the client's rules-side read trails the server by transport latency plus at most one snapshot interval
rather than by `InterpolationDelayTicks`, so the window shrank from two to three ticks to usually under one,
and what remains is the one-way latency no client can see. A client predicting its own approach to a moving
monster can therefore still path toward a tile the server has just left. That is not a new class of disagreement: it is exactly the
"two heads saw different blockers" case sub-project 2's reconcile snap exists for
(`TILE-WORLD-NETCODE-DESIGN-2026-08-22.md` section 5, `Repath`). The first step of an approach is almost always
identical on both heads because two tiles a step apart usually share a first step, so the responsiveness the lead
commit bought is kept and the correction lands, if at all, on a later step of the walk. Turning prediction OFF for
the approach is a client-side one-liner (send `Attack`, predict `Continue`) because the server is authoritative
either way, so the fallback costs no rewrite. Which one ships is a FEEL round in R2, the same way the glide was
(section 5.2, four rounds).

### 6.2 Following: the chase belongs in the one stepper

`TileMoveState` gains `long CombatTarget`, taking its wire form from 33 bytes to 41.

That is a deliberate 8 bytes on every entity's every snapshot, and the alternative was to keep the target in a
separate component present only on entities actually fighting, which would be free for the ninety-five per cent of
entities that are not. It was rejected on the package's founding property: `TileMoveSimulator` is
`ITickSimulator<TileMoveState, TileCommand>` and sees the state and the command and nothing else, so a target that
lives outside `TileMoveState` cannot be followed inside the one stepper both heads run. Following it anywhere else
means a SECOND movement authority the client cannot predict, and a client that cannot predict its own approach pays
a round trip on every re-path of every chase. Eight bytes against that is not a close call.

`CombatTarget` and `InteractTarget` are mutually exclusive and each clears the other, for the reason
`TileActionQueue.cs:38-45` gives about its own pair: two records of one intent, where the one that outlives the
other fires against something the player visibly walked away from. Applying `Attack` clears `InteractTarget`,
applying `WalkTo` or `Interact` clears `CombatTarget`, and a `WalkTo` therefore BREAKS a fight, which is how a
player disengages and is the same rule OSRS uses.

The follow, per tick, inside `Advance`:

1. If `CombatTarget` is 0, nothing.
2. Resolve it through `combatTargets`. A target that no longer resolves (dead, despawned, out of the resolver's
   view) clears `CombatTarget` and the actor or player stands. This is the free half of death handling: the seam's
   contract already says an id stops resolving the moment the thing it named stops existing.
3. A target on ANOTHER PLANE clears `CombatTarget` too. Reach never crosses planes (`TileReach.cs:93`, `:136`) and
   the rest of the package refuses cross-plane rather than coercing, so a fight broken by a staircase is broken
   rather than chased through the floor.
4. If `TileReach.Contains(map, footprint, plane, state.Tile)` is already true, DROP THE ROUTE and stand. An attacker
   in range does not shuffle.
5. Otherwise, re-path only if the target's committed tile CHANGED since the last re-path (its previous tile rides
   the state's own route end, so nothing new is stored), through `TileReach.TryNearest`. A target that cannot be
   reached at all clears `CombatTarget` and answers `CannotReach`, the same answer and the same token
   (`TileServerReason.CannotReach`) an unreachable interaction gets.

Rule 5 is what keeps section 5.4's CPU budget honest: a stationary target costs no pathfinding, and a target moving
at run cadence costs one `TryNearest` every two ticks.

### 6.3 Adjacency: cardinal, and it is not a new function

**Melee range is `TileReach.Contains(map, targetFootprint, targetPlane, attackerTile)`.** No new rule, no new
function, no second definition.

For a 1x1 target that expression is the four cardinal neighbours minus the ones a wall or a blocked tile denies,
which is exactly OSRS melee against a 1x1 NPC. The alternative, CHEBYSHEV adjacency including the four diagonals,
was weighed and rejected on four grounds:

1. **OSRS precedent is cardinal for 1x1, and the precedent is the brief.** A tile MMO whose melee reaches diagonally
   is a different game with different geometry, not a tuning variant of this one.
2. **It would be a SECOND definition of range.** `TileReach` is already the package's answer to "close enough to act
   on it" (`TileReach.cs:79-81`), and it is the answer an interaction, a reach walk and an arrival facing all use.
   A combat range that disagreed with it would be invisible until a player stood one tile off a thing they clicked.
3. **The wall rules come free and they are the interesting geometry.** `TileReach`'s outward step asks three
   questions (no wall on the target's edge, the candidate is standable, no mirrored wall facing back,
   `TileReach.cs:11-22`), so a fence between you and a monster denies melee with no combat code at all. That is the
   safespot, and it falls out rather than being built.
4. **A diagonal would need the corner rule too.** `TileCollision.CanStep`'s diagonal case refuses corner cutting
   (`TileCollision.cs:15-18`), so a Chebyshev melee range would have to decide whether a corner blocks a swing, and
   whichever way it decided would be a rule nothing else in the package holds.

The known limit is `TileReach`'s own and it is stated in its doc three times (`:23-25`, `:42-43`, `:82-84`): every
tile in the set is an ANCHOR tile for a ONE TILE actor. Both parties are 1x1 this round, so the set is exact.
Section 12 defers larger footprints with that as the reason.

### 6.4 One tick, stated deterministically

Four steps are added to `RunOneTick` (`TileWorldServer.Tick.cs:96`), in bold:

| # | Step |
|---|---|
| 0 | `OnBeforeTick`, the head's own systems |
| **0c** | **`combatTargets.Refresh(liveCells)`, the entity target space snapshotted for the whole tick** |
| 0b | snapshot `tickSlots` |
| 1 | drain ONE command per player slot, `Admit` it, write `PendingTileCommand` |
| **1b** | **actor decisions: every spawner ticks, then every live actor's behaviour runs and writes `PendingTileCommand`** |
| 2 | `host.Tick(dt, maxTicksPerFrame: 1)`, movement for every entity through its own simulator |
| 3 | `host.ProcessHandoffs()` then `host.SyncGhosts()` |
| 4 | `ResolveActions()`, the interaction queue |
| **4b** | **`ResolveCombat()`, roll then apply then die, then `ReportBrokenLocks()`** |
| 5 | serve each client its plane-filtered area of interest |
| **5b** | **`ReapDeadActors()`, the despawn every actor killed at 4b owes** |
| 6 | `AdvanceTick`, `TickCount++` |

`0c` is named for what it IS rather than for where it sits: it runs immediately after `OnBeforeTick`, ahead of the
slot snapshot, so a head's own spawns are in it. What is NOT in it is anything step 1b spawns on this tick, which is
harmless in R1 because nothing can hold a lock on an entity that did not exist last tick.

`ReportBrokenLocks` is the second half of `4b` and it FOLLOWS the roll, which is an ordering constraint rather than a
tidy grouping. It skips a player who died on this tick, and the list it asks is the one `ResolveCombat` has just
built, so moved ahead of the roll it reads the PREVIOUS tick's dead (the list is cleared inside `ResolveCombat`
itself, not between ticks) and tells a player they could not reach the fight they were being killed in. The case that separates the two is a lock the FOLLOW broke in step 2 whose holder is then killed at
`4b`: `TileCombatResolveTests.The_broken_lock_report_follows_the_roll_so_a_player_killed_on_the_same_tick_hears_nothing`
is what goes red for either half of it.

`5b` SITS BEHIND THE SERVE, and that placement is the whole reason section 7's `Killed` bit works. The despawn a
death owes an actor ran inside 4b at first, which took the corpse out of the world before step 5 built each viewer's
interest set from it, so the killing blow was filtered out of every frame and a head could only learn a monster died
by noticing an absence, which is the one thing the flag exists to avoid. Held until every client has been served, the
killing blow is still in interest when the set is built, and the corpse ships one more snapshot at zero health to a
viewer homed in its own cell before it leaves. It still runs BEFORE step 6, so the removal's own change tracking is
cleared on the tick it happened, exactly as it was at 4b. One qualification on the presentation half: a viewer in a
NEIGHBOURING cell is served the corpse's ghost, mirrored at step 3 before 4b applies the damage, so the last health
that viewer reads is the one before the killing blow and the `Killed` bit is the only thing that tells it the monster
died. That one-tick ghost lag is pre-existing and is the same wherever the despawn runs.

A tick that THROWS between 4b and 5b loses its reap, so the list is drained at the top of the next 4b rather than
cleared. On a healthy tick 5b already emptied it and the drain does nothing, which leaves 5b the only reap site that
ever runs on one.

**Every step is order-independent within itself, and that is the determinism argument rather than an ordering
imposed on the ECS pass.** Taking them one at a time:

- **1b reads tick-START tiles.** A behaviour is handed the actor's committed tile and its target's committed tile as
  they stood before ANY movement this tick, so no actor's decision can depend on another entity having already
  moved. The ECS iteration order over the archetype therefore cannot reach a decision. The RNG is per actor and
  seeded per actor, so one actor's wander cannot perturb another's.
- **2 reads only the baked map and the 0c snapshot.** Actors do not occupy tiles (section 12 defers it with the
  reason), so no entity's step depends on another entity's step, and the follow's target tile is a keyed lookup into
  a map built before the pass began, so no entity's CHASE depends on another entity's step either. The step commit
  is a pure function of state plus command plus map plus that snapshot. It is NOT a pure function of state plus
  command alone any more, which is the price section 6.1 states: the simulator holds no state of its own and the
  caller owns the seam's stillness.
- **4b is TWO PHASES: roll, then apply.** The roll phase reads every combatant's state as it stands at the START of
  the phase and produces a list of outcomes. The apply phase subtracts them all. So no hit's accuracy or damage can
  depend on another hit having landed, and the pass is order-independent for OUTCOMES. The ORDER of the rolls still
  decides which draw each attacker gets from its RNG, so it is fixed at `(attackStartedTick, netId)` ascending,
  which is the same shape and the same reasoning `ResolveActions` uses for its `(IssuedTick, slot)` sort
  (`TileWorldServer.Actions.cs:44-49`).

**Combat is NOT predicted, and that is a simplification worth stating loudly.** A client predicts movement and
never actions, for the reason `TileActionQueue.cs:31-34` gives: an outcome that depends on state the client does
not hold would show a result that never happened. A damage roll is exactly that. So the roll needs no cross-head
determinism at all, only server-side REPRODUCIBILITY for tests and replays, which the fixed order gives.

**The OSRS dance, worked.** Attacker A on `(x, 0)`, target B on `(x, 1)` and cardinally adjacent, both walking at
four ticks per step, B fleeing north from tick 0. Traced against the simulator's own two doors into `Start`
(`TileMoveSimulator.cs:241-264`), where a command arriving on a STANDING entity commits its step on that tick and a
landing entity commits the next one on the tick its glide fills:

| tick | B commits | A decides (tick-start tiles) | A commits | `A.Tile`, `B.Tile` | in range |
|---|---|---|---|---|---|
| 0 | `(x,2)` | sees B on `(x,1)`, in range, stands | | `(x,0)`, `(x,2)` | no |
| 1 | | sees B on `(x,2)`, re-paths | `(x,1)` | `(x,1)`, `(x,2)` | yes |
| 2 | | in range, stands | | `(x,1)`, `(x,2)` | yes |
| 3 | `(x,3)` | in range, stands | | `(x,1)`, `(x,3)` | no |
| 4 | | sees B on `(x,3)`, re-paths | `(x,2)` | `(x,2)`, `(x,3)` | yes |
| 5, 6 | | in range, stands | | `(x,2)`, `(x,3)` | yes |
| 7 | `(x,4)` | | | `(x,2)`, `(x,4)` | no |
| 8 | | re-paths | `(x,3)` | `(x,3)`, `(x,4)` | yes |

**The steady state is the whole result: A's commits lock exactly ONE TICK behind B's, so the pair is out of range
on the tick B commits and back in range on the next one.** One miss tick per step, three hittable ticks out of
four at walk cadence. A same-speed flee in a straight line therefore does NOT escape melee, which is OSRS, and it
is not arbitrated anywhere: it falls out of the lead commit plus the one-tick decision delay.

Three consequences worth reading off the same trace:

1. **The miss window is one tick, whatever the direction.** A diagonal step costs the same tick count as a cardinal
   one (`TILE-WORLD-NETCODE-DESIGN-2026-08-22.md` section 2, decision 1) and A follows diagonally at the same rate,
   so turning buys a fleeing target nothing on open ground. What breaks melee is GEOMETRY, not dancing: a wall, a
   corner or a doorway that makes the reach tile A must path to further away than the step B just took. That is the
   safespot, and section 6.3 is why it needs no combat code.
2. **The miss window is one tick out of `StepTotal`, so it grows as the cadence quickens.** At walk (4) a fleeing
   target is out of range for a quarter of the ticks, at run (2) for half of them. Fleeing is worth more at speed,
   which is a gameplay consequence of the tick model rather than a tuned number, and it is worth knowing before
   anyone tunes `AttackTicks` against it.
3. **A target FASTER than its attacker escapes outright.** B running at two ticks per step against A walking at
   four gains a tile every four ticks, and the gap never closes. Nothing arbitrates that either. It is the cadence,
   and it is why a monster's step cadence is content (section 8.3) rather than an engine constant.

**Both act on the same tick.** If A and B are each other's target and both cooldowns are ready on tick T, the roll
phase reads both at full health and both hits are rolled. The apply phase subtracts both. If each was lethal, BOTH
DIE, and A's swing lands even though B's killing blow is applied in the same pass, because the swing was rolled
before either landed. That is the direct consequence of roll-then-apply and it is the reason to prefer it over
resolving attackers one at a time: the alternative makes the outcome of a mutual kill depend on net id ordering,
which is an arbitrary tiebreak deciding who lives.

### 6.5 The hit pipeline, and where the game plugs in

`TileCombatState` is the attacker-side server component: `byte AttackTicks` (the cadence the game supplied),
`byte CooldownRemaining`, `long LastDamagedBy`, `long LastDamagedTick` and `long LastCombatTick`. The last two are
deliberately different facts, and R1 shipped them apart after the first cut folded them together. The damage pair is
"who hurt me", which a retaliation reads, so it moves only on a swing that LANDED (a hit for zero counts, per ruling
13.3 item 1, and a miss does not). `LastCombatTick` is "a combat event touched me", which the logout window in 13.3
item 3 reads, so it moves on every resolved swing in either direction, misses included: the player that ruling
exists to stop escaping is the one being attacked who has not clicked back, and folding the two made a fight of
misses count as no fight at all. It is registered on
`ReplicationChannels.Migrate` and NOT on `Replicate`, so it survives a region handoff and costs a viewer nothing.
That is precisely the channel combination `ShardHost.cs:331-339` describes for "a mob's server-only state", and it
is the reason section 5.4's per-actor cost has no line for it.

`ResolveCombat`, per tick:

1. **Eligibility.** For every entity with a non-zero `CombatTarget`: the attacker is alive, the target resolves
   through `combatTargets`, the target is alive, and `TileReach.Contains` says the attacker's committed tile is in
   the target's reach set. `CooldownRemaining` decrements once per tick regardless and FLOORS at zero, so an
   attacker who was out of range while it ran down swings on the first tick both conditions hold, which is OSRS and
   is what stops a chase from also being a cooldown reset.
2. **Roll.** The engine calls the game:

   ```
   public interface ITileCombatRules
   {
       TileAttackOutcome Roll(in TileAttackContext context);
       byte AttackTicks(long attackerNetId);
   }
   ```

   `TileAttackContext` carries both net ids, both committed tiles, both healths and the tick.
   `TileAttackOutcome` is `(bool Landed, ushort Damage, byte Kind)`. The engine never inspects `Kind`: it is the
   hitsplat colour and it is the game's vocabulary, the same way `TileProtocol`'s game-message `kind` is a raw
   `ushort` the engine never looks inside (`TileProtocol.Frames.cs:159`, `:186`).
3. **Apply.** `TileHealth.Current = max(0, Current - Damage)`, for every outcome, after every roll.
4. **Record.** Each outcome becomes an event in this tick's combat buffer (section 7), a miss included: a miss is
   `Landed = false, Damage = 0` and it still produces a hitsplat, because a fight with invisible misses reads as a
   broken fight.
5. **Reset.** `CooldownRemaining = AttackTicks(attacker)` for every attacker that swung, whether or not it landed.
   Acquiring a NEW target does not reset the cooldown, so target switching is neither a penalty nor free damage.
6. **Die.** Every entity whose health reached zero in this pass raises `OnDied(netId, killerNetId)`. Death is
   evaluated ONCE, after all applications, which is what makes case three of the dance work.

### 6.6 Health, death, and who decides what

`TileHealth` is `{ ushort Current; ushort Max; }`, an ECS component registered at extension id 19 on
`ReplicationChannels.Default | Migrate`, four payload bytes. The engine owns it MECHANICALLY: it applies damage, it
raises the death event, and it is the value a health bar reads. The engine owns none of its meaning: `Max` is
written by the game from its skill core, and a heal is a game-side write.

Death is a two-sided split, and the split is the same one section 4 draws:

- **The engine, for any entity:** clear the dead entity's own `CombatTarget`, and raise `OnDied`. It does NOT clear
  every OTHER entity's target pointing at the corpse, because it does not have to: the target stops resolving the
  moment the entity is gone, and step 2 of the follow already clears a target that does not resolve. One rule, one
  place.
- **The engine, for an ACTOR:** despawn it and put its spawner into `Waiting(respawnDelay)`. That is engine work
  because the spawner is engine-owned. The despawn is the one half of a death that does NOT happen inside the pass:
  it waits for step 5b, behind the serve, for the reason section 6.4 gives.
- **The game, for a PLAYER:** `OnDied` fires with the slot, and Grimhollow answers it with
  `SetPlayerState(slot, TileMoveState.At(spawn, facing), teleport: true)` plus a write of `TileHealth.Current = Max`.
  The teleport flag advances `Epoch` (`TileMoveState.cs:86-88`), which zeroes the client's correction offsets and
  puts the body on the new tile on the frame the snapshot lands rather than sliding it across the map
  (section 5.2, "Discontinuities CUT"). Ruling 4 is therefore satisfied by machinery that already exists and is
  already tested.

## 7. What presentation needs on the wire

A hitsplat cannot be derived from replicated health, and the temptation to try is worth closing off. The serve is a
full snapshot every tick, so a client CAN diff health between two samples, and the diff is wrong twice: two hits on
one tick collapse into one number, and a MISS moves health by zero and is therefore invisible. A fight rendered
from health deltas shows fewer, larger, later hitsplats than the fight the server ran.

So combat events are explicit, on a new server frame tag `ServerFrameCombat = 3` (`ServerFrameSnapshot` is 0,
`ServerFrameGameMessage` 1 and `ServerFrameNotice` 2, `TileProtocol.Frames.cs:43-51`):

```
[tag:1][count:1] then count x [attacker:8][target:8][amount:2][kind:1][flags:1]
```

Twenty bytes per event. `flags` bit 0 is `Landed` and bit 1 is `Killed`, so a death rides the blow that caused it
and a client never has to notice an entity's absence to know it died. The frame is sent only on a tick that
produced events, and only the events whose TARGET is in that viewer's interest set, so an ordinary tick costs
nothing and a busy one costs a viewer two or three events.

`TileWorldClient` raises one event per decoded record:

```
public event Action<TileCombatEvent>? CombatEvent;
```

Combat events go on their own frame rather than through the game-message envelope for one reason: the envelope's
`kind` is a `ushort` the GAME defines and the engine never inspects (`TileProtocol.Frames.cs:159`, `:186`), and
these are the ENGINE's events about a pipeline the engine owns. Putting them there would mean the engine reserving
a game-owned number.

**The client also gains the remote lifecycle events the actor bodies want**, and they cost nothing because the diff
is already computed:

```
public event Action<long>? RemoteEntered;
public event Action<long>? RemoteLeft;
```

`RefreshRemoteSamples` already builds the live set and prunes the gone ones every frame
(`TileWorldClient.Snapshots.cs:204-212`). Grimhollow currently re-derives the same diff itself for nameplate state
(`HollowmereSession.Draw.cs:133-139`, `PruneStalePlates`), which is a second copy of a diff the engine has in hand.
Surfacing it is the smaller change and it is what a per-monster hitsplat stack keys on.

## 8. The game side: Grimhollow

### 8.1 The skill and XP core

Game-side, ruling 3. Three types in `Grimhollow.Core`:

- **`SkillId`**, an enum with `Hitpoints = 0` and room for the combat skills that come later. Open by construction
  rather than a single constant, because the whole point of shipping a CORE with one skill is that skill two costs
  a table row.
- **`SkillBook`**, per-skill experience with `Level(SkillId)` derived from XP through a fixed table, and
  `AddXp(SkillId, double)` returning whether a level was crossed. The table is OSRS's curve, provisional in exactly
  the way section 8.2 means.
- **`Vitals`**, the binding from the book to `TileHealth`: `Max = MaxHitpointsFor(Level(Hitpoints))`, written to the
  engine component on join, on level up and on respawn.

Where the numbers live is already answered by the game's own docs.
`Grimhollow/docs/architecture/ARCHITECTURE.md:454-455` says there are no gameplay numbers yet and that when there
are, that section is where they and their change procedure get documented. This round is the first to owe an entry
there.

### 8.2 Provisional formulas, and why they are shaped this way

**Every number below is PROVISIONAL and is expected to move in R2's feel round.** What is not provisional is the
SHAPE, and the shape is chosen so that the three combat skills landing later replace constants without touching a
formula.

With only Hitpoints in the book, the player has no Attack, Strength or Defence to read, so those three come in as
game-supplied constants and the monster's come from its content record. The rolls are OSRS-shaped:

| Quantity | Provisional form |
|---|---|
| attack roll | `(effectiveAttack + 8) * (attackBonus + 64)` |
| defence roll | `(effectiveDefence + 9) * (defenceBonus + 64)` |
| hit chance | `att > def ? 1 - (def + 2) / (2 * (att + 1)) : att / (2 * (def + 1))` |
| max hit | `floor(0.5 + effectiveStrength * (strengthBonus + 64) / 640)` |
| damage on a hit | uniform 0 to max hit inclusive |
| damage on a miss | 0, and it still draws a hitsplat |
| experience | `1.33 * damage` to Hitpoints, the only skill there is to award |
| attack speed | 10 ticks, 2.5 s at Grimhollow's 250 ms tick |

Ten ticks is the closest whole-tick match to OSRS's four-tick standard weapon at 600 ms. Sub-project 2's tick was
chosen at 250 ms so the game READS faster than OSRS without abandoning tick-aligned action
(`TILE-WORLD-NETCODE-DESIGN-2026-08-22.md` section 2, decision 1), and matching OSRS's swing duration exactly may
turn out to fight that. Eight ticks (2.0 s) is the obvious first thing to try, and it is a one-line change because
`AttackTicks` is a seam member and not a constant.

Starting Hitpoints is level 10, so a level 10 player has 10 hit points if `MaxHitpointsFor` is the identity. Ten is
a very small number to spread a uniform 0-to-max-hit roll across, and the provisional answer is
`MaxHitpointsFor(level) = level * 10`, so a fresh character has 100 and a hit for 8 reads as a hit rather than as a
tenth of a life. Whether Grimhollow wants OSRS's small numbers or its own larger ones is what a playtest answers,
and it is why the mapping is a function rather than an equality.

### 8.3 Content: the monster, its marker, its body

**Stats.** One `TileActorDefinition` plus a Grimhollow-side stat record: max hitpoints, attack, strength and
defence levels, the three bonuses, attack speed in ticks, wander radius, leash radius, respawn delay, and the step
cadence. A monster that walks at the player's walk cadence and cannot run is the simplest thing that produces a
readable chase, and it is the provisional starting point.

**Spawn markers, and the one real obstacle.** Hollowmere already authors sparse markers and already ignores most of
them: `assets/worlds/hollowmere/regions/r_1_1.json` carries `smithy`, `spawn`, `store`, `chapel` and `bank`,
`r_2_2.json` carries `cave`, and the other seven regions carry none. A `TileMarker` is
`{ Name, X, Z, Plane, Tags }` (`KhaozEngine.TileWorld/TileObject.cs:30-47`), the name is document-unique, and the
`Tags` list is authored today and read by NOTHING in Grimhollow. So a monster spawn point is an already-shipped,
already-authored shape (`{ "name": "rat_1", "x": .., "z": .., "plane": 0, "tags": ["monster", "<kind>"] }`) and this
round is the first thing anywhere to read `Tags` for MEANING. `TilePrefab` copies them through an extract and a
stamp (`KhaozEngine.TileWorld/TilePrefab.cs:159`, `:230`) without ever looking inside one, and nothing else touches
them at all.

The obstacle is finding them. Markers live inside REGION files rather than in the manifest, so enumerating them
costs a region load, which Grimhollow already wrote down at `Grimhollow.Core/World/HollowmereWorld.cs:195-198`
("A world large enough for that fallback to hurt wants a marker index in the manifest, which the engine does not
carry yet"). For actor spawning it does not bite, and it is worth saying why rather than fixing something that is
not broken here: the SERVER owns actors, the server loads the world eagerly through `TileWorldFile.Load` (which is
the basis on which `HollowmereSpawn.FromDocument` calls `document.FindMarker` at `HollowmereSpawn.cs:27`), and
`TileWorldDocument.AllMarkers()` (`TileWorldDocument.cs:236`) is one pass over resident regions at boot. The client
never needs to find a spawn marker at all, because it learns about monsters from snapshots. So the marker index is
a real future need (section 12) and not this round's.

`AllMarkers()` is documented as returning markers "in no particular order", so the spawner list is built by SORTING
the tagged markers by name before adding them, which makes the spawn order authored rather than incidental. That
matters because spawn order decides net id assignment, which decides the roll order in section 6.4.

**The body.** The same synthetic-archetype trick `AvatarMesh` already uses: a `TileObjectArchetype` built in code
rather than added to `assets/catalogs/archetypes.json`, "because putting it in the catalog would make it placeable
in the editor and bakeable into collision, and it is neither" (`Grimhollow.Core/Client/AvatarMesh.cs:17-19`). A
`MonsterMesh` alongside `AvatarMesh`, its own `MeshRef` to a kit glb, resolved through the same shared
`GltfMeshResolver` so the kit cache is shared and a missing file falls back to the greybox box. One mesh instance
draws every monster, exactly as one `AvatarMesh` draws every body today (`HollowmereSession.Draw.cs:34-35`).

**Telling a monster from a player, client-side.** There is no `TileActor` on the client, because that tag is a
server-side ECS marker and not a replicated component. Two options were weighed and the second is taken: a monster
carries a game-registered discriminator component at an id above `FirstGameTypeId` (24), which is the pattern
`MmoServerSample/MmoProtocol.cs:29` already uses on the float stack ("Players carry NO `Creature`, so a client
tells an NPC from a player by its presence"). The alternative, inferring it from the absence of a nameplate or from
an id range, is a rule that breaks the first time a player is nameless.

### 8.4 Click to attack

`ClickRouter.Route` today does exactly two things (`Grimhollow.Core/Client/ClickRouter.cs:74-90`):
`TileRaycast.Pick` for the ground tile, then `SmallestInteractiveOver` for an authored object over that tile, then
`Interact` or `WalkTo`. An actor fits none of it, and the reasons are structural rather than incidental:

- `TileRaycast.Pick` returns a `TileHit` of `{ X, Z, Plane, Point, Distance }`
  (`KhaozEngine.TileWorld/TileRaycast.cs:7`) and says nothing about anything standing on the tile.
  `ClickRouter`'s own doc says so (`:21-22`).
- The object join is a static document query, `document.ObjectsIn(rect, plane)` over authored `TileObject`s
  (`ClickRouter.cs:98-113`). A monster is not in the document at all: it is an entity on `Client.View` and
  `Client.World`, reachable only through `Client.RemoteNetIds` and `TryGetRemotePose`.
- `RouteResult.Target` is documented as an object id (`ClickRouter.cs:14`), which is section 3's id-space collision
  arriving on the game side.

**So actor picking is a separate pass, and it is picked from the PICTURE.** For every net id in `RemoteNetIds`
carrying the monster discriminator, take its DRAWN pose from `TryGetRemotePose`, test the click ray against a
bounding cylinder at that pose, and keep the nearest hit to the camera. If one hits, the route is
`TileCommand.Attack(netId, mode)`. If none hits, fall through to the existing ground-and-object logic unchanged.

**Picking from the drawn body and judging from the committed tile is not a contradiction, and the distinction is
worth naming because it is the general rule.** IDENTITY is picked from the picture, POSITION is read from the
rules. The player clicks a body and gets a net id, which is a fact no lag can make wrong. Everything downstream
(reach, the approach, adjacency, the swing) then asks about that id's committed tile, which the server always holds
exactly. That is why targeting a moving monster is honest even though its drawn body lags its committed tile by a
step plus the interpolation delay.

Two smaller consequences. `RouteResult` gains a discriminator so `HollowmereSession.Click` can tell an attack from
an interact without inspecting the command kind twice, and `ClickRouter` gains a dependency on the client's remote
view, which it does not have today (it is a static class over a document and catalogs). That dependency is why
actor picking is a separate method rather than a fourth branch inside `Route`.

There is still no client-side pre-check, and there should not be one. `ClickRouter.cs:30-32` states the rule:
`TileWorldClient.Queue` already drops what the simulator would refuse, and a second copy of a rule that has to
match the server's is how the two drift. A stale comment at `Grimhollow.Server/SoloServerHost.cs:179-180` claims a
client pre-check exists. It does not, and it should be corrected while this round is in the file.

**Auto-retaliate (ruled ON, section 13.1) lives here, in the command stream.** When a `CombatEvent` hit names the
owned player as its target while the predicted state's `CombatTarget` is zero, the session queues
`Attack(attacker)` exactly as if the player had clicked the attacker. The server never fabricates player intent,
the command replays like any click, and turning the behaviour off later is the absence of that one queue call
behind a setting.

### 8.5 Presentation

**Health bars are a style swap, not new rendering.** The engine's `Nameplate` already carries
`IReadOnlyList<NameplateBar> Bars` (`KhaozEngine.Render3D/Nameplate.cs:50`) with `NameplateBar` as
`{ Fraction, Fill, Track, Overlay }` (`:13-31`). Grimhollow builds plates with `NameplateStyle.TextOnly` and sets
no bars (`HollowmereSession.Draw.cs:119-122`). A monster health bar is a style change plus a one-element `Bars`
list fed from the replicated `TileHealth`, and `NameplateEdgeBehavior.Deflect` (`Nameplate.cs:72`) already handles
plates near the top of the screen, which is where a bar over a body you are standing next to ends up.

Bars are drawn only over ENGAGED bodies (the player's target, and anything whose target is the player). That is the
OSRS convention and it is also what keeps a wander of idle monsters from filling the screen with bars.

**Hitsplats** are a per-target stack of `(amount, kind, secondsLeft)` fed by `CombatEvent`, drawn over the body's
drawn pose and aged per frame. Per-monster client state follows the pattern the nameplates already established, a
dictionary keyed by net id plus a prune (`HollowmereSession.Draw.cs:31-32`, `:133-139`), except that with section
7's `RemoteLeft` the prune becomes a handler instead of a per-frame set diff.

**The player's own HP readout is Grimhollow's first player-facing HUD element.** Everything on screen today is
either the developer `ViewerHud` (explicitly exempt from localization, `ViewerHud.cs:43-45`), the login screen, or
a toast. So the readout is where the localization rule starts applying to the HUD: every string through a
`StringId`, in the `NoticeStrings` shape (`static readonly StringId` plus an `All` list so the catalog-coverage
test catches a missing entry, `NoticeStrings.cs:20-42`), with the entries added to `Strings.resx` FIRST.

New strings this round: a death line, a respawn line, and the monster's display name. The monster name is a
`StringId` resolved CLIENT-side from the discriminator rather than a string sent from the server, because
`TileServerReason`'s doc states the rule the whole stack follows (`TileServerReason.cs:5-12`): the server owns no
catalog and must never author player-facing prose. `TileIdentity.DisplayName` is the exception the engine already
makes for a player's own verified name, and a monster's name is not that.

**Death and respawn, as the player sees it.** The killing blow's `Killed` flag plays the death splat, the entity
leaves the next snapshot, and for the player's own death the teleport epoch cuts the body onto the Hollowmere spawn
with health restored. A localized death notice goes through `SessionNotices` and the engine `ToastStack`
(`SessionNotices.cs:27-38`), the one channel Grimhollow already routes engine notices through, so a death line
replaces rather than stacks with a cannot-reach line.

**One rendering constraint that catches every new overlay.** The ground overlays draw through
`ITileWorldScene.DrawMesh`, the engine's OPAQUE lit pass, which discards the alpha lane of `ModelVertex.Color`
(`Grimhollow.Core/Client/RouteHighlight.cs:49-53`, and `Grimhollow/docs/ENGINE-INTEGRATION.md:357-364`, filed as
[#734](https://github.com/APKiwiOrg/KhaozEngine/issues/734) and
[Grimhollow #27](https://github.com/APKiwiOrg/Grimhollow/issues/27)). Any combat ground overlay this round adds, a
target ring under the monster for instance, cannot use alpha and has to read as solid geometry.

## 9. What changes in each existing type

Engine, `KhaozEngine.TileWorld.Netcode`:

| Type | Change |
|---|---|
| `TileMoveState` | gains `long CombatTarget`. Wire form 33 to 41 bytes. Joins `Equals` and `GetHashCode`, which is what makes a mispredicted target a reconcile rather than a silent divergence. |
| `TileCommandKind` | gains `Attack = 3`. No wire-format change (`TileProtocol.cs:21-22` is already fixed-width with a `long Target`). |
| `TileCommand` | gains an `Attack(long netId, TileMoveMode)` factory beside `WalkTo` and `Interact`. |
| `TileMoveSimulator` | gains a second `ITileTargets` for the entity space, an `Attack` case in `Step`, a `BeginAttack` beside `BeginInteract`, and the per-tick follow inside `Advance` (section 6.2). `Accepts` grows the `Attack` case, so the one definition of acceptance stays one definition. |
| `TileMovementSystem` | holds a second simulator and picks on `world.Has<TileActor>(e)`. The `Ghost`/`Migrating` skip stays in one place. |
| `TileWorldServer` | gains `SpawnActor` / `DespawnActor`, `OnDied`, `OnActorSpawned`, a `TileActorHost`, and two tick steps (1b and 4b). Actors and combat land as `TileWorldServer.Actors.cs` and `TileWorldServer.Combat.cs`, never as growth on the existing four partials. |
| `TileWorldServerConfig` | gains `MaxActorsPerCell` and the actor `TileMoveOptions`. |
| `TileProtocol` | registers `TileHealth` at extension id 19, adds `ServerFrameCombat = 3` and its codec as `TileProtocol.Combat.cs`. Ids 20 to 23 stay free below `FirstGameTypeId`. |
| `TileWorldClient` | gains `CombatEvent`, `RemoteEntered`, `RemoteLeft`, and a `TileRemoteTargets` it builds for its own simulator. |
| `TileServerReason` | unchanged. An unreachable attack answers the existing `ke:cannot-reach`, because it is the same fact. |
| `TileWorldServer.Sessions.cs` | gains the combat logout delay (ruled in, section 13.3): a leaving player whose last combat event is within the configured window is not removed, their entity LINGERS attackable until the window lapses and then persists and drains normally. The window is `TileWorldServerConfig` surface with the game choosing the number. |
| `TileActionQueue` | unchanged. Combat does not queue: the lock lives on the state and re-fires on a cooldown, which is a different shape from a one-shot pending action. |
| `TileReach` | unchanged. Section 6.3 is why. |
| `TilePresenter` | unchanged. An actor is posed exactly as a remote player is. |
| `TileWorldPersistence` | unchanged. Actors are ephemeral (section 5.1). |

New engine types: `TileActor` (tag), `TileHealth`, `TileCombatState`, `TileActorDefinition`, `TileActorSpawner`,
`TileActorHost`, `TileEntityTargets`, `ITileActorBehaviour`, `TileActorIntent`, `TileActorContext`,
`TileWanderBehaviour`, `ITileCombatRules`, `TileAttackContext`, `TileAttackOutcome`, `TileCombatEvent`,
`TileRemoteTargets`.

Game, Grimhollow:

| Type | Change |
|---|---|
| `ClickRouter` | gains actor picking as a separate method and a dependency on the client's remote view. `RouteResult` gains a discriminator. |
| `HollowmereSession` | subscribes `CombatEvent`, `RemoteEntered`, `RemoteLeft`. Owns the hitsplat stacks and the engaged set. |
| `HollowmereSession.Draw.cs` | draws monster bodies, health bars (a `NameplateStyle` swap plus `Bars`) and hitsplats. `PruneStalePlates` becomes a `RemoteLeft` handler. |
| `AvatarMesh` | unchanged. `MonsterMesh` is its sibling. |
| `NoticeStrings` / `Strings.resx` | new combat strings, catalog first. |
| `Grimhollow.Server` | builds the spawner list from tagged markers, implements `ITileCombatRules`, answers `OnDied` for players. |
| `docs/architecture/ARCHITECTURE.md` | the first gameplay-numbers entry, which `:454-455` already promises. |
| `SoloServerHost.cs:179-180` | correct the stale comment about a client pre-check that does not exist. |

## 10. Rounds

Three rounds, in the SDD shape this program already runs (a plan under `docs/superpowers/plans/`, then per-task
brief, report and review under `.superpowers/sdd/<date>-<slug>/`, no task started before its predecessors are green
and committed).

**R0, the remote pipeline (ruled first, section 13.2).** Before combat exists, a remote's committed tile becomes
honestly readable client-side, because the monster's true tile must be as legible as the player's own and today
`TryGetRemoteTile` sits behind `InterpolationDelayTicks`. The round STARTS by verifying what of
[#696](https://github.com/APKiwiOrg/KhaozEngine/issues/696) still holds (this document already flags its text as
possibly part-superseded by the round-four presenter), then delivers whatever remains of the honest read, updates
`TryGetRemoteTile`'s contract, and re-verifies Grimhollow's declined true-tile-for-remotes decision at
`HollowmereSession.Draw.cs:62-65` against the new read. Small round, its own review, no combat types in it.

**R0 LANDED, as a SIBLING read rather than a change of meaning.** `TryGetLatestRemoteTile` answers off the newest
applied snapshot (captured in `TileWorldClient.OnSnapshot` at the one instant `World` holds it, before
`AdvancePresentation` overwrites it with the delayed timeline), with an overload reporting the answer's age in
ticks for R2's fade. `TryGetRemoteTile` keeps its name, its timeline and its agreement with `TryGetRemotePose`,
which is the property an overlay drawn ON a body needs and the one a change of meaning would have destroyed. No
consumer called it, so the sibling costs nothing anyone has to migrate. #696 was verified superseded and closed
with the loopback numbers: the drawn body lags max 1.4 ticks and mean 0.95 at BOTH cadences, which is the delay
alone rather than a step-quantized reconstruction. Grimhollow's decline at `HollowmereSession.Draw.cs:62-65` still
stands for the MARKER it is about (a marker under a remote wants the delayed read, and drawing the honest tile
there would disagree with the body under it), and R2 reads the honest one for the combat overlay instead.

**R1, engine.** Actors, the combat seam, the wire, the tests, one minor version. Task shape:

1. `TileActor`, `TileHealth`, `TileCombatState`, `SpawnActor` / `DespawnActor`, and the registry entry for id 19.
2. `TileActorDefinition`, `TileActorSpawner`, `TileActorHost`, tick step 1b, and the handoff rule from section 5.3.
3. `ITileActorBehaviour`, `TileActorIntent`, `TileActorContext`, `TileWanderBehaviour`.
4. `TileCommandKind.Attack`, `TileEntityTargets`, `TileRemoteTargets`, `TileMoveState.CombatTarget`, the simulator's
   attack case and the follow.
5. `ITileCombatRules`, `ResolveCombat` (tick step 4b), `OnDied`, and the combat logout linger in
   `TileWorldServer.Sessions.cs` (section 13.3).
6. `ServerFrameCombat`, its codec, `TileWorldClient.CombatEvent`, `RemoteEntered` / `RemoteLeft`.
7. The doc sweep: README catalog, the package README's type list and known limits, `docs/USING-KHAOZENGINE.md`,
   `docs/DEPENDENCY-SEAMS.md` if any edge moved, `CHANGELOG.md`.

R1 has no consumer until R2, which is the same trade sub-project 2 took and accepted
(`TILE-WORLD-NETCODE-DESIGN-2026-08-22.md` section 14): the value is proven by loopback tests that run both heads
in one process.

**R1 LANDED in 18.1.0**, all seven tasks, riding the staged version rather than cutting one. The shipped surface is
the package README's type list and `docs/USING-KHAOZENGINE.md`'s actor and combat sections, so it is not restated
here. What IS worth recording is that the round produced TWO deltas this section did not have, both of them
structural rather than cosmetic:

- **A death's despawn became its own tick step, 5b, BEHIND the serve.** Section 6.4's table had the despawn inside
  4b, and there it takes the corpse out of the world before step 5 builds each viewer's interest set from it, so
  the killing blow is filtered out of every frame and a head can only learn a monster died by noticing an absence.
  Phase 3 now collects, 5b reaps, and 5b still runs before step 6, so the removal's change tracking is cleared on
  the tick it happened. The list is DRAINED at the top of the next 4b rather than cleared, because a throw anywhere
  in the serve loop would otherwise discard a despawn the world was still owed and leave a corpse standing forever
  against the cell's actor cap. The table has the row and the reasoning now.
- **The player health contract is written down, with a counter behind it.** Section 6.6 gives the engine the
  mechanics and the game the meaning, and the consequence it does not state is that `SpawnPlayer` writes no
  `TileHealth` at all, so a game that never calls `SetHealth` has players who can neither swing nor be hit with
  nothing raised, logged or thrown. It surfaced as a task 6 test that was watching a fight which never started.
  `TileWorldServer.SkippedHealthlessCombatantCount` counts the skip in BOTH roles (a counter rather than a
  `Debug.Assert`, because CI runs Release), the absent component only and never a corpse at zero, and the sentence
  is on `TileHealth` and `SetHealth` for the IDE and in both docs for the reader.

Section 6.1 was rewritten in flight to the shipped snapshot semantics, and section 6.4 gained its `0c` row, both
inside the round. The rest of the shipped-versus-specified deltas are in `docs/INDEX.md`'s row for this document.

**R2, Grimhollow.** The skill core, the rules implementation, the content, the click routing, the presentation, and
the feel round. One game version. The feel round is where two things get ruled: the attack cadence (section 8.2)
and whether the approach to a moving target is predicted (section 6.1).

## 11. Test plan

Headless, in `KhaozEngine.TileWorld.Netcode.Tests`, which already exists and already references only what it uses.

- **Actor movement is player movement.** An actor and a player given the same route from the same tile produce
  byte-identical `TileMoveState` sequences over N ticks. This is the test that would go red if actors ever grew a
  movement rule of their own.
- **The handoff trap.** An actor walks across a region boundary and keeps walking. This fails today for the reason
  section 5.3 names, and it is the regression test for the unconditional command write.
- **Adjacency is `TileReach`.** A hit lands on the four cardinals and on none of the four diagonals, and a wall
  between attacker and target denies the hit with no combat code involved. The safespot case is the interesting one.
- **The dance.** Section 6.4's traced table, asserted tick by tick: A's commits lock one tick behind B's, the pair
  is out of range on exactly the tick B commits, and the same trace run diagonally gives the same one-tick window.
  Then the escape case (a running target against a walking attacker), asserting the gap only grows. This is the
  test that pins the steady state rather than the first two ticks of it.
- **Roll then apply.** Two combatants each with a lethal hit ready on the same tick: both events are emitted and
  both die. Then the same scenario with the entities spawned in the opposite order, asserting the same outcome, so
  the test actually pins order-independence rather than one ordering.
- **Reproducibility.** Two servers from the same seed running the same scripted commands produce the same combat
  event sequence, which is what the fixed `(attackStartedTick, netId)` roll order buys.
- **The cooldown rules.** It floors at zero out of range and swings on the first tick both conditions hold. A target
  switch does not reset it. An attacker that missed still pays it.
- **Break rules.** A `WalkTo` clears `CombatTarget`. A target that stops resolving clears it. A cross-plane target
  clears it. A leash break drops the target and walks home.
- **Respawn.** Kill an actor, assert the entity is gone, advance the delay, assert a NEW net id spawns on the same
  tile at full health.
- **Wire.** Encode and decode every combat frame, fuzz the decoder with truncated frames the way the existing
  protocol tests do, and assert an event whose target is outside a viewer's interest is not sent to that viewer.
- **Prediction.** A client and a server over the in-process transport, the client attacking a server-driven moving
  actor: assert the approach reconciles rather than diverging, and count the corrections so a later change that
  makes them worse is visible as a number.

Grimhollow: a headless capture of a monster standing in Hollowmere, and a windowed playtest for the feel round
(click a rat, watch the approach, trade hits, die, respawn).

## 12. Deferred, with the reason

- **Ranged and magic.** Owner ruling 1. `ProjectileBlocked` (`TileCollisionFlags.cs:33`) stays reserved and unset,
  and it is where line of sight will go. The seams this round builds (the entity target space, the cooldown, the
  hit pipeline, the combat frame) are what those rounds plug into: a projectile is a hit whose range test is a line
  rather than an adjacency.
- **Drops, aggro tables, shops.** Owner ruling 2, sub-project 5. `TileCombatState` already holds
  `LastDamagedBy` / `LastDamagedTick` on a `Migrate`-only channel, which is where a threat table would live and is
  the case `ShardHost.cs:331-339` names.
- **PvP.** Out of scope. **What it would need is a predicate and a picker, not a rewrite:** the combat target space
  is ALREADY net ids, and a player already carries every component an attacker needs, so the missing pieces are an
  `ITileCombatRules` member answering whether a given attacker may target a given entity (the engine has no notion
  of a PvP zone or of consent, and should not acquire one), and a client picker that offers player bodies as well
  as monster bodies. Everything in sections 6.2 to 6.5 works unchanged.
- **Multi-tile (2x2 and larger) monsters.** Weighed for this round and deferred, because it is TWO structural
  changes rather than a size field. First, `AgentSize` is a property of the SIMULATOR
  (`TileMoveOptions.AgentSize`, read at `TileMoveSimulator.cs:78`, `:182`, `:273`, `:346`) rather than of the
  entity, so a 2x2 monster needs the size to move onto the state or onto a per-definition simulator instance.
  Second, `TileReach` states three times that its set is anchor tiles for a ONE TILE actor and that an actor with a
  larger footprint "acts from any tile it covers, which this does not model" (`TileReach.cs:23-25`, `:42-43`,
  `:82-84`), so adjacency against or from a large body is a rule the package does not have. Section 5.4's separate
  actor simulator is deliberately the seam that first change lands on. There is also no test coverage for
  `agentSize > 1` anywhere in the tile suites today, so the first large monster is also the first exercise of a
  shipped-but-unproven path.
- **Actors as movement blockers.** Players walk through monsters this round. Making an actor block would put a
  DYNAMIC entry in a collision map that is baked from files and that each head bakes for itself
  (`TileCollisionBaker`), so the two heads would disagree on every occupied tile and every chase would become a
  correction storm. Sub-project 2 already names the shape of that failure ("a head that saw a different blocker
  snaps on reconcile"). The eventual answer is a server-owned occupancy overlay the client mirrors from the same
  replicated actor tiles, and it is only honest once a client's view of those tiles is tight, so it is gated behind
  the remote-timeline work rather than merely postponed.
- **Actor persistence across a restart.** Section 5.1: nothing worth persisting, and the tile stack has no
  cell-blob persistence wired at all (`CellSim.SnapshotOwned` is never called from this package). A monster that
  respawns at its authored point after a restart is the same monster the player would have seen anyway.
- **Attack and death animations.** The bodies are kit pieces (`AvatarMesh.MeshRef` is `kit/barrel.glb`) and there
  is no character pipeline. Sub-project 2 deferred character models for the same reason.
- **A marker index in the world manifest.** Section 8.3: the server loads eagerly so it does not bite this round.
  It becomes real when a world is large enough that the server streams, or when a CLIENT needs to find a marker it
  has not loaded.
- **Per-client snapshot deltas** ([#699](https://github.com/APKiwiOrg/KhaozEngine/issues/699)) and the cost of
  building each full snapshot ([#680](https://github.com/APKiwiOrg/KhaozEngine/issues/680)). Actors make both
  matter sooner without making either worse per entity, so this round measures rather than fixes.
- **Pooled pathfinder scratch** ([#669](https://github.com/APKiwiOrg/KhaozEngine/issues/669)). Section 5.4's two
  knobs (a small actor path radius, and re-pathing only on a target tile change) are what make the current
  allocation affordable. The pool is the answer when a profile says so, and this round is what produces the
  profile.

## 13. Open questions, resolved by the owner (2026-08-27)

Three were left open when this document was drafted. All three were ruled the same day, and the rulings are
folded into sections 8.4, 10 and 9 respectively. Recorded here so the fork and its answer stay together.

1. **A player AUTO-RETALIATES.** A monster that attacks an idle player provokes an automatic counterattack,
   OSRS's default. Mechanism in section 8.4: when a `CombatEvent` hit lands on the owned player while their
   `CombatTarget` is zero, the client queues `Attack(attacker)`, one line in the command stream, and the server
   never invents player intent. A later settings screen may expose it as a toggle.
2. **The remote pipeline gets fixed FIRST.** The monster's true tile must be honestly readable client-side
   before combat ships, so the one-sided legibility gap closes rather than being accepted: the #25 promise is
   the contract's own, and combat is being built on it. This is round R0 in section 10, starting from
   [#696](https://github.com/APKiwiOrg/KhaozEngine/issues/696) whose text this document already flags as
   possibly part-superseded (the round verifies before it builds). The cheap acceptance argument (cardinal
   adjacency is coarse) was considered and declined by the owner.
3. **There IS a combat logout delay.** A player in combat (a combat event touched them within the window) who
   disconnects is not removed at once: the entity LINGERS in world until the window lapses, still attackable,
   then persists and leaves through the ordinary drain. Section 9's `TileWorldServer.Sessions.cs` row carries
   it, R1 task 5 builds it, and the window length is a game config beside the other combat numbers.


