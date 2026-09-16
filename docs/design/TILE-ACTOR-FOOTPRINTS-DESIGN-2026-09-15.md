# Tile actor footprints (NxN bodies)

Date: 2026-09-15

Status: Implemented in 18.50.0. Signed off by the owner on 2026-09-15 (section 13), built in
the rounds of section 14.

Issue: [#897](https://github.com/APKiwiOrg/KhaozEngine/issues/897). Resolves
[#741](https://github.com/APKiwiOrg/KhaozEngine/issues/741). First consumer: Grimhollow's 2x2 cow,
[Grimhollow#236](https://github.com/APKiwiOrg/Grimhollow/issues/236).

Lifts the multi-tile deferral in section 12 of
[TILE-COMBAT-ACTORS-DESIGN-2026-08-27.md](TILE-COMBAT-ACTORS-DESIGN-2026-08-27.md).

## 1. Problem

Every tile actor today is one tile. Grimhollow's cow model is about 2.5 m long on a one-tile body, so a player
can stand inside its head and swing at it, and a retaliating cow hits from somewhere that does not look
adjacent. The engine has no way to say an actor covers more than its anchor tile.

## 2. Rulings carried (owner, 2026-09-15, not reopened here)

1. The anchor is the SOUTH-WEST tile. The footprint is the NxN square north and east of it (+x, +z).
2. Melee is cardinal adjacency to any tile of the target's footprint, from any tile the attacker's footprint
   covers. A body inside the target's footprint cannot swing and steps out to a reach tile first. That rule is
   what resolves #741.
3. Pathing and collision need every footprint tile open in the actor's traversal profile.
4. A large body draws centred on its footprint, so clients must know the size.
5. The committed-tile contract stays (Grimhollow #25). The drawn body tracks committed tiles inside the glide
   window. No OSRS drawn-body lag.
6. Actors blocking movement stays out of scope. The occupancy overlay deferral in the combat design stands.

## 3. What exists, verified at 18.49.0

- `TileCollision.CanStep(map, x, z, plane, dir, agentSize)` already takes an NxN agent and asks every
  footprint tile to take the step (`TileCollision.cs:19-27`). `TilePathfinder.FindPath` passes `agentSize`
  straight through (`TilePathfinder.cs:106`). Nothing in the tile suites exercises a size above 1.
- The size is a SIMULATOR property: `TileMoveOptions.AgentSize`, read at `TileMoveSimulator.cs:265, 306, 439,
  496, 578`. Grimhollow sets it to 1 on both `Move` and `ActorMove`. No other consumer sets it.
- `TileReach` answers anchor tiles for a ONE TILE actor and says so three times (`TileReach.cs:23-25, 42-43,
  79-81`). `TryNearest` walks an NxN agent to one-tile candidates, which under-reports.
- `TileEntityTargets.TryGetFootprint` and `TileRemoteTargets.TryGetFootprint` both answer a 1x1 rect on the
  committed tile. The combat roll builds its own 1x1 rect (`TileWorldServer.Combat.cs:256`).
- `TilePresenter.Pose` draws on the tile centre, anchor plus half a tile (`TilePresenter.cs:193-194`).
- The wander validates a goal by its anchor tile alone (`TileWanderBehaviour.cs:108-111`). Spawn placement
  checks the home tile alone, and only on a non-default traversal profile (`TileWorldServer.Actors.cs:106-112`).
- Follow rule 4 holds a lock forever when the target is the attacker itself (`TileMoveSimulator.cs:405-410`).
  The roll never fires from there, so a self-locked player is permanently `IsInCombat`
  (`TileWorldServer.Sessions.cs:242-247`). That is #741's live half.

## 4. Decision 1: where the size lives

Two shapes can carry a per-entity size into the one stepper.

**A. On the entity's state.** `TileMoveState` gains a footprint size. The simulator reads it off the state it is
already handed. Every reader that holds a `TileMoveState` (the entity target snapshot, the combat roll, the
presenter, a client's remote sample) gets the size with no second lookup.

**B. On a per-definition simulator.** The traversal registry keys simulators by (profile, size), the
`TileActor` tag carries the size, and `TileMovementSystem` picks the simulator per entity. The stepper still
needs the TARGET's size for reach, so B also needs a replicated component the entity snapshot and the client
can read. That makes B two carriers for one fact.

| Criterion | A: state | B: per-size simulator |
|---|---|---|
| One stepper sees everything it needs from state plus command | 10 | 7 |
| Survives a region handoff with no restamp | 9 | 6 |
| Clients and presenter learn it with no new read path | 9 | 6 |
| Blast radius on existing types | 6 | 7 |
| Wire and CPU cost | 9 | 8 |
| Room to grow (a transformed player, the future occupancy overlay) | 8 | 6 |
| **Total** | **51** | **40** |

A's handoff score is the decisive structural one. `TileMoveState` rides the `Migrate` capture through its own
codec, so the size crosses a region boundary inside the component that already crosses. B's tag is on no
channel (`TileActor.cs:10-16`) and is restamped at step 1b, which runs AFTER step 0c snapshots the entity
targets, so a freshly migrated large actor would answer as 1x1 to every lock for one tick. A's cost is that it
touches the most central struct in the package: equality, hash and codec all change. That is contained work
with a clear test surface, and it is paid once.

**Recommended: A.** Section 5.4 of the combat design named B as the seam. It was a reasonable guess before the
entity target snapshot and the handoff restamp existed, and those two facts are what reverse it.

### 4.1 The shape of the field

- `TileMoveState.FootprintSize`, an `int` property over a `byte` backing field where 0 reads as 1. The default
  struct and `TileMoveState.At` therefore stay one tile with no caller change.
- `TileMoveState.Footprint`, a derived `TileRect(Tile.X, Tile.Z, n, n)`.
- Range 1 through `TileMoveState.MaxFootprintSize = 8`. The cap is content hygiene rather than a limit of the
  lattice: reach candidates grow as `4(M + N - 1)` and a body bigger than 8 is a boss that deserves its own look
  at the numbers first. Raising it later is a one-line change.
- Equality and hash compare the NORMALIZED size, so a 0 and a 1 are the same state.

### 4.2 `TileMoveOptions.AgentSize` becomes a floor

Nothing in the fleet sets it above 1, and removing it now would break Grimhollow's build for no benefit. The
stepped size is `Math.Max(state.FootprintSize, options.AgentSize)`, stated once as
`TileMoveSimulator.FootprintOf(in TileMoveState)`. The combat roll resolves the attacker through the same member on
the attacker's own simulator, so the follow and the roll cannot disagree about the attacker's size. The TARGET
side is always the state's own size on both heads. A follow-up issue removes `AgentSize` at the next major.

Shipped in 19.0.0 as the removal: `TileMoveOptions.AgentSize` and `TileMoveSimulator.AgentSize` are gone, there is no
floor, and `FootprintOf(state)` is the state's own footprint ([#900](https://github.com/APKiwiOrg/KhaozEngine/issues/900)).

## 5. Decision 2: how clients learn the size

| Criterion | Field on the replicated move state | Separate replicated component | Client lookup from the game's kind |
|---|---|---|---|
| The engine's own client rules can use it (prediction, presenter) | 10 | 9 | 3 |
| One source of truth, no drift between heads | 10 | 9 | 4 |
| Cost on the wire | 9 | 8 | 10 |
| Works for a head that replicates no kind at all | 10 | 10 | 1 |
| **Total** | **39** | **36** | **18** |

The lookup loses on the fact that matters most. The CLIENT predicts the local player's approach to a monster
through `TileRemoteTargets`, inside the engine. If that resolver answers 1x1 for a 2x2 cow, the client stops on a
reach tile of the anchor that is inside the cow, the server steps the player off it, and every approach to a
large target reconciles. The engine's own resolver needs the size without asking a game catalog.

**Recommended: the field on the move state**, which is what Decision 1 A gives for free.

### 5.1 Wire layout

`TileMoveState` encodes 41 bytes plus one optional trailing byte today, the interaction domain, written only
while an entity interaction is pending (`TileProtocol.Components.cs:173-189`). The new layout:

- Size 1: byte-identical to today.
- Size above 1: the domain byte is ALWAYS written (0 when there is no entity interaction), then one size byte.

An older reader consumes its 41 bytes, reads the domain byte exactly as before, and the replication reader skips
the rest of the frame. A new reader reads each trailing byte only when the payload holds it and clamps the size
to 1 through `MaxFootprintSize`. The `Migrate` capture uses the same codec, so the size crosses a handoff.

Cost: a large actor pays two payload bytes, so 63 bytes per serve instead of 61. A 1x1 entity pays nothing.

Both heads must upgrade together for a size above 1 to mean anything, because an older client draws and
predicts a cow as 1x1. Grimhollow vendors both heads from one pin, so that is its normal adoption.

## 6. The reach rule for a footprint

This is ruling 2 written as geometry, and it is ONE predicate used everywhere a range question is asked: the
follow's rule 4, the follow's rule 5 memo, the interaction arrival in `FaceTarget`, and the combat roll.

Let `T` be the target's footprint and `A = rect(from, N)` the attacker's.

**In range** when `A` does not overlap `T`, and some tile `p` of `A` is in `TileReach.Set(map, T, plane)`. The
existing one-tile set already encodes cardinal adjacency and the three wall questions, so a fence between the
attacker's nearest tile and the target still denies that tile, and another tile of the attacker that is not
walled off still reaches. Nothing new is invented about walls.

**Inside** is any overlap between `A` and `T`. A body that overlaps cannot swing, and the follow routes it out
through rule 5 exactly as #751 routes a 1x1 body off its target's tile today.

### 6.1 The anchor candidate set and its order

`TryNearest` needs candidates to path to, and a candidate is now an ANCHOR tile for an NxN agent:

```
for p in TileReach.Set(map, T, plane)            // the existing order, unchanged
  for dz in 0..N-1, dx in 0..N-1                  // z ascending, then x ascending
    a = (p.x - dx, p.z - dz)
    skip if rect(a, N) overlaps T or a is already listed
    emit a
```

Complete: every in-range anchor has some `p` of its rect in the set and is emitted from it. Sound: every emitted
anchor has that `p` and no overlap. For `N = 1` the inner loop is the single offset (0, 0), nothing overlaps and
nothing repeats, so the list is EXACTLY today's `Set`, in today's order. That is what keeps every existing
1x1 tie break, every golden route and a mixed-version client agreeing on a 1x1 world.

Up to `4(M + N - 1)` candidates against an MxM target when nothing is walled. The admission refusal widens from
`maxRadius + 1` to `maxRadius + N`, because the west and south candidates sit N tiles out from the footprint.
The per-candidate Chebyshev prune is unchanged and still sound.

### 6.2 Facing

`FacingToward` for a footprint attacker answers the side on which `A` touches `T`. Two non-overlapping rects
that are cardinally adjacent touch on exactly one side (touching in x needs overlap in z, which rules out
touching in z), so the answer is unique. For `N = 1` it is the answer the existing four-neighbour scan gives.

### 6.3 #741: a target that is the attacker

Rule 4 holds a self lock today because stepping off one's own footprint lands inside it again forever. Under one
predicate that lock can never be in range, so the follow CLEARS it, the same answer rule 5 gives any target with
no reach tile. On the server that surfaces as the ordinary `CannotReach` through `ReportBrokenLocks`. The client
runs the same follow with its own `LocalNetId`, so it predicts the clear. The permanent `IsInCombat` goes with the
lock.

Rejected alternative: refusing a self `Attack` silently at admission. It needs a second rule on each head (the
server's `Admit`, the actor admission and the stepper), and a lock written onto a state through `SetPlayerState`
would still need the follow's clear anyway.

## 7. Standing and pathing with a footprint

**`TileCollision.CanStand(map, x, z, plane, size)`**, new in `KhaozEngine.TileWorld`: every tile of the footprint
is not Blocked (which also covers a region the map does not hold, since those read Blocked) and no WALL lies on
an edge between two tiles of the footprint. For size 1 it is exactly `!IsBlocked`.

**`CanStep` for a size above 1 also requires `CanStand` at the destination.** The per-tile step already crosses
every internal edge along the axis of travel, but not the perpendicular ones, so today a 2x2 could straddle a
fence and walk along it. OSRS refuses that shape for its large NPCs, and a cow pen fenced with walls is exactly
where Grimhollow would see it. Size 1 is untouched.

The pathfinder itself does not change: a route is a list of anchor tiles, the window is centred on the start
anchor, and every step goes through `CanStep` with the size. The editor's `QueryService.IsWalkable` delegates to
`CanStand` so an author checking a marker gets the same answer the server will.

## 8. Actors

- **`TileActorDefinition.FootprintSize`** (default 1) and **`TileActorSpawn.FootprintSize`** (init, default 1),
  both refused outside 1 through 8 at the door. The spawn writes the size onto the actor's move state.
- **Placement.** `TileActorHost.Add` and `SpawnActor` refuse a home whose footprint fails `CanStand` on the
  actor's traversal map. For size 1 the default profile keeps its legacy rule (a blocked home still spawns),
  because content predates this. For a size above 1 the check runs on EVERY profile, because no content predates
  it. The respawn retry in `TrySpawn` uses the same test.
- **`TileActorHost.CanPlace(definition, home)`**, a non-throwing form of the same check, so a game's content test
  can validate every authored marker without catching exceptions. The engine's `TileWorldValidator` knows nothing
  about which markers are actors, so marker validation stays game side and calls this.
- **Entity targets.** `TileEntityTargets` captures `state.Footprint` rather than a 1x1 rect, and so does the
  client's `TileRemoteTargets` off its newest snapshot.
- **The combat roll** asks the one predicate with the target's footprint and the attacker's size through
  `FootprintOf` on the attacker's own simulator.
- **`TileActorContext.FootprintSize`**, an init property read off the actor's state. The definition cannot answer
  for an actor spawned without a spawner, which is handed the fallback definition.

### 8.1 Decision 3: what the leash, the wander and interest measure from

| Measure | Recommended | Why |
|---|---|---|
| Leash | anchor to home anchor | Home is an anchor, and the whole body moves together, so anchor to anchor equals centre to centre. Nearest footprint tile to home would read a body as closer when it is north-east of home than south-west of it. |
| Wander radius | goal anchor within the radius of home anchor | The same frame as the leash, so a wander can never pick a goal the leash then breaks off. |
| Wander goal validity | `CanStand` over the whole footprint | An anchor-only check lets the pathfinder's nearest-reachable fallback park a 2x2 against the obstruction, which is the failure the current check exists to prevent. |
| Interest radius | anchor, unchanged | Interest is a grid query on the entity's position, and that position is also what decides cell ownership and handoff. Measuring from the footprint would either move handoffs by half a body or inflate every viewer's query. The cost is that a large body enters a viewer's interest up to N - 1 tiles late on its north and east edges, at a 15 tile radius. A game with big bosses pads `InterestRadius`. |

## 9. Presentation

- **`TilePresenter.Pose`** centres on the footprint: anchor plus `N / 2` on each axis instead of plus one half.
  The glide runs anchor to anchor, so the offset is constant through a step and the body tracks the committed
  tiles inside the same glide window a 1x1 body does. `TryGetRemotePose` goes through `Pose`, so a consumer's
  large body is centred with no change on its side.
- **`TilePresenter.PoseAt(TileRect footprint, int plane, TileDirection facing)`**, the overlay form, for a
  footprint marker or a nameplate anchor that is not a body.
- **`TileWorldClient.TryGetRemoteFootprint`**, off the delayed timeline so it agrees with the drawn body, for click
  bounds and target highlights.
- **`TileDrawPriority` stays one body per ANCHOR tile.** A 1x1 body standing on a non-anchor tile of a cow
  overlaps it on screen. That is presentation, it does not affect rules, and a footprint-aware settled stack is its
  own small piece of work, deferred in section 12.

## 10. Players

No change for a 1x1 player. The player simulator, its wire bytes and every 1x1 reach order stay identical, which
section 6.1 proves for the candidate order and section 5.1 for the codec. What a player notices is the target
side: an approach to a large monster stops on a reach tile of its whole footprint, predicted, and a player
standing inside a large body steps out before swinging.

`SetPlayerState` refuses a state with a footprint above 1. Players stay one tile this round, and a large player
would reach presentation rules (the local body's two-tile claim in `TileDrawPriority`) that nothing has designed
for.

## 11. Consumer-facing API

Additive unless marked.

- `TileMoveState.FootprintSize`, `TileMoveState.Footprint`, `TileMoveState.MaxFootprintSize`.
- `TileActorDefinition.FootprintSize`, `TileActorSpawn.FootprintSize`, `TileActorContext.FootprintSize`.
- `TileActorHost.CanPlace`.
- `TileCollision.CanStand`. **Behaviour:** `CanStep` and `FindPath` refuse a size-above-1 step onto a footprint
  with an internal wall.
- `TileReach.Set`, `Contains` and `FacingToward` gain an agent-size overload. **Behaviour:** `TryNearest`'s existing
  `agentSize` now shapes the candidates as well as the walk. Identical for size 1.
- `TileMoveSimulator.FootprintOf`.
- `TilePresenter.PoseAt(TileRect, int, TileDirection)`, `TileWorldClient.TryGetRemoteFootprint`,
  `TileWorldClient.TryGetLatestRemoteFootprint` (the newest-snapshot twin, which `TileRemoteTargets` resolves a
  remote to). **Behaviour:** `Pose` centres a large body.
- **Behaviour:** an `Attack` naming the attacker itself clears on the tick it is applied and answers
  `CannotReach`, rather than holding the lock forever.
- **Behaviour:** `SetPlayerState` refuses a footprint above 1.
- **Wire:** `TileMoveState` carries two trailing bytes for a size above 1. Both heads upgrade together.

## 12. Deferred, with the reason

- **Actors as movement blockers.** Ruling 6. The occupancy overlay stays gated behind the remote timeline.
- **Footprint-aware `TileDrawPriority`.** Section 9.
  [#899](https://github.com/APKiwiOrg/KhaozEngine/issues/899).
- **A multi-goal reach search.** Candidates grow with footprints, and a player at radius 64 against a walled-in
  4x4 can flood up to 16 windows. One breadth-first search visiting the same goals returns the identical path
  (discovery order does not depend on the goal), so it is a pure optimisation. The cow is 8 candidates, the same as
  a 2x2 authored object today, so this waits for a profile, beside
  [#669](https://github.com/APKiwiOrg/KhaozEngine/issues/669).
  [#901](https://github.com/APKiwiOrg/KhaozEngine/issues/901).
- **Removing `TileMoveOptions.AgentSize`.** Section 4.2. Next major. Shipped in 19.0.0.
  [#900](https://github.com/APKiwiOrg/KhaozEngine/issues/900).
- **[#756](https://github.com/APKiwiOrg/KhaozEngine/issues/756), the in-phase chase.** A step clock problem, not a
  geometry one. A footprint does not change it and this work does not fix it.
- **Interest measured from the footprint.** Section 8.1.
  [#906](https://github.com/APKiwiOrg/KhaozEngine/issues/906).
- **Sizes on `TileAttackContext`.** No rule reads them yet. Ranged combat is the round that will.
  [#907](https://github.com/APKiwiOrg/KhaozEngine/issues/907).

## 13. Choices, ruled by the owner (2026-09-15)

All seven were taken as recommended.

1. **Where the size lives.** Recommended: on `TileMoveState` (section 4), not a per-definition simulator.
2. **How clients learn it.** Recommended: the field on the replicated move state (section 5), not a separate
   component or a client lookup from the game's kind.
3. **Measurement.** Recommended: leash and wander anchor to anchor, wander goals validated over the whole
   footprint, interest radius from the anchor unchanged (section 8.1).
4. **What counts as inside for a large attacker.** Recommended: any overlap with the target's footprint (section 6).
5. **Walls inside a footprint.** Recommended: a large body cannot stand across a wall (section 7).
6. **A self lock (#741).** Recommended: the follow clears it and the server answers `CannotReach` (section 6.3).
7. **Players.** Recommended: no change, and `SetPlayerState` refuses a footprint above 1 (section 10).

## 14. Rounds

Each round lands as its own commits with its suite green and zero warnings.

- **R1, the lattice.** `CanStand`, `CanStep` refusing an internal wall for a size above 1, pathfinder suites at 2x2 and
  3x3 (narrow corridors, doorways, a fence line, corner cutting, a region edge, determinism), `IsWalkable`
  delegating. `TileReach` footprint overloads with the size-1 order pinned against the existing `Set`.
- **R2, the stepper and the wire.** `TileMoveState` size, codec, equality and hash. `FootprintOf` through step,
  walk, interact, follow, face and repath. The #741 self clear. `TileRemoteTargets` and the presenter.
- **R3, actors and the server.** Definition and spawn fields, placement and `CanPlace`, entity targets, the combat
  roll, the wander and the context field, `SetPlayerState`'s refusal. The full pairing matrix below.
- **R4, ship.** Package READMEs, `docs/USING-KHAOZENGINE.md`, the combat design's section 12 pointer, changelog,
  version, pack, tag (Grimhollow is pinned and waiting), and the adoption note on Grimhollow#236.

## 15. Test plan

Every row runs for attacker size in {1, 2, 3} against target size in {1, 2, 3}, which is all nine pairings and
covers small on large, large on small and large on large.

| Area | What is pinned |
|---|---|
| `TileReach` | in range from every adjacent anchor on all four sides, no range from a diagonal, none on overlap, a wall denying one attacker tile while another reaches, facing per side, `TryNearest` choosing the shortest walk with the scan-order tie |
| Size-1 regression | the anchor set for `N = 1` equals `Set` element for element, and the existing reach, follow and combat suites pass unchanged |
| Stepper | an approach from open ground stops on the first in-range anchor and never shuffles, a body overlapping steps out in one route, a moving target re-paths only when its footprint leaves the route end's range, a self lock clears |
| Server combat | the roll fires exactly on in-range ticks post movement, a large actor retaliates and chases a small player and vice versa, a mutual kill between two large bodies |
| Actors | wander goals always satisfy `CanStand`, the leash breaks on anchor distance, placement refusals (blocked footprint tile, internal wall, unloaded region, default profile at size 2), `CanPlace` agrees with `Add`, a handoff keeps the size |
| Wire and client | size round trip, size-1 bytes unchanged, an older-reader shaped payload still decodes, a client predicting an approach to a 2x2 matches the server with zero corrections, `Pose` centres by size |
