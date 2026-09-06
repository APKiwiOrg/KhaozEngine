# Tile actor traversal profiles

Date: 2026-09-06

Status: Implemented in 18.30.0

## Problem

`TileWorldServer` currently gives every server-owned actor the same collision map. That is correct for
ordinary ground actors, but it prevents a game from expressing actors whose legal tiles differ. The missing
piece is a movement policy choice at the actor boundary, not a second movement implementation.

The feature must let a game register another baked topology, assign an opaque key to an actor, and keep every
movement decision on that topology. The engine must not learn what the topology represents. Water, flight,
doors, creature kinds, and combat rules stay game concerns.

## Decision

Add `TileActorTraversalProfile`, a small opaque value key. `Default` is zero and preserves the existing actor
map. A game registers each non-default key with `TileActorHost.RegisterTraversalProfile` before the first
server tick. Registration binds the key to a `TileCollisionMap` and builds another `TileMoveSimulator` with
the existing actor movement options, cadence, target resolver, and combat snapshot.

`TileActorDefinition.TraversalProfile` and `TileActorSpawn.TraversalProfile` are additive init properties.
They default to `TileActorTraversalProfile.Default`, so existing definitions and positional spawn construction
keep their source and runtime behaviour.

The server records the selected key beside each live actor net id. `TileActor` also carries the key while the
entity is resident. The host rewrites the tag from the server record before movement each tick. This is the
same restoration path already used for the server-only actor tag and pending command after a region handoff.

`TileMovementSystem` resolves an actor's simulator from its tag. Players continue through the original player
simulator, so prediction and player input are unchanged. Each cell owns one pathfinder scratch buffer per
profile it actually encounters. Simulators and profile registrations are shared and immutable once ticking
starts.

## Movement contract

One selected simulator answers every movement question after an actor exists:

- accepting a walk destination and finding its route
- validating a committed step and rebuilding a blocked route
- chasing an entity target
- checking the attacker's final adjacent reach after a chase
- carrying a route through a cell handoff
- walking home after a leash break

`TileActorContext` carries the selected profile and map. `TileWanderBehaviour` reads that map when it checks a
random destination. Its existing map constructor remains valid as a fallback for contexts built by hand. A new
parameterless constructor and `CreateWithTiming` factory read the server-provided map without creating an overload
collision for existing default-literal calls. Server-created contexts always carry the registered map.

Combat keeps its existing melee policy and game-owned roll seam. Only the final `TileReach.Contains` geometry
check changes for an actor. It reads the attacker's registered map, matching the map that chose and validated the
chase route. Players continue to use the server constructor map. Ranged attacks and combat-style admission remain
outside this design.

Spawn placement is checked against non-default profile maps. The default profile keeps the legacy rule that a
blocked home may still spawn, because changing that rule would break existing content. A managed spawner on a
non-default profile waits while its home is blocked and retries on later ticks. This makes respawn placement
use the same topology without turning a temporary map edit into a server failure.

## Registration and failure rules

Registration is an authoring operation. It is accepted only before the first authoritative tick. The default
key is already registered and cannot be replaced. Duplicate keys, the internal unresolved sentinel, null maps,
and plane-count mismatches are refused at the door.

A direct spawn or spawner definition that names an unregistered key is refused before an entity is created. If
an entity is constructed outside the server door with an unknown key, the movement system freezes it and clears
the pending command to `Continue`. It never falls back to the default map. A missing server-side actor record is
rewritten as the same unresolved sentinel after migration, which gives the same safe result.

## Alternate map ownership

The game owns the meaning of an alternate topology. `TileCollisionBaker.Bake` has an additive overload taking a
`Func<int, int, int, bool> groundBlocked`. The callback receives absolute world tile X and Z plus a zero-based
plane, once for every tile and plane in every loaded region. It replaces only the ordinary underlay and
`TileSettings.Blocked` ground rule. The existing solid, diagonal, wall and wall-corner object passes then run
unchanged, so a ground cell the callback opens still retains placed-object and directional-wall collision.
Missing regions remain absent and blocked.

Callback invocation order is not an API contract. A callback must be deterministic and independent of earlier
calls. A retained collision map may be changed only on the server's owning thread between authoritative ticks.

The callback can close or open any authored surface based on game-owned document and catalog meaning. The engine
does not name water, land, bridges, actor kinds or traversal styles. Registration accepts the resulting map by
reference and does not guess how it was derived or verify its semantic relationship to the default map.

## Determinism

The profile key is part of actor state, registration closes before ticking, and lookup never enumerates a
dictionary to make a gameplay decision. The existing deterministic pathfinder and actor random stream remain
unchanged. Mixed profiles can run in the same cell and tick because each actor selects one immutable simulator
and the cell selects the matching scratch buffer.

## Verification

Headless tests cover default compatibility, registration failures, unknown profile freezing, mixed actors,
route choice, directional and object collision, committed steps, repathing, wandering, leash return, respawn,
and region handoff. The TileWorld Netcode tests and the full solution run in Debug and Release before handoff.
