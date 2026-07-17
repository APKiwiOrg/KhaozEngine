# NPC Navigation Design (KhaozEngine.Navigation)

Design approved 2026-07-16. Roadmap near-term item 1: engine-owned pathfinding that respects terrain
walkability plus prop and static collision, so game NPC brains consume a path instead of hardcoded
pursuit. First consumer: Ruinborne wolves (playtest finding 2026-07-14: wolves pinned behind rocks).

## Problem

NPC brains today compute a fresh straight-line direction to their target every tick and feed it to
`CharacterMovement.StepTowards`. The swept collide-and-slide only redirects along the local contact
tangent for that one tick, so an NPC presses into a prop's flank forever when the target is on the far
side. There is no waypoint, path, or detour concept anywhere in the engine or any consumer. Ruinborne's
`NpcCollisionTests` prove the wolf never clips the rock, and nothing makes it go around.

There is also no discretized walkability data for the overworld. Terrain is an analytic continuous
field (`TerrainField.SampleHeight`), walkability is a single-point slope predicate
(`TerrainCollision.IsWalkable`), and prop blocking lives in `WorldColliders` (XZ footprints) and
`IPhysicsWorld` (3D statics). Dungeons are the exception: `DungeonLayout` already ships a per-floor
walkable tile raster with `DungeonCellKind.IsWalkable`.

## Decisions made (with rationale)

1. **Both substrates in v1: overworld and dungeon interiors.** The dungeon adapter is near-free
   because `DungeonLayout` is already a walkable grid, and designing the walkability source as a seam
   from day one prevents a terrain-bound design.
2. **Engine owns the query AND the follower.** Replan policy (goal drift, corridor divergence,
   cooldown, waypoint advance) is the part every game would hand-roll badly, and it is game-agnostic.
   Game brains shrink to "pursue this point". The raw query stays public underneath.
3. **Statics only in v1.** Players, other NPCs, and dynamic bodies are invisible to the planner.
   Local collide-and-slide absorbs incidental bumps and the follower's divergence replan recovers from
   shoves. Dynamic avoidance is a later layer composing at the follower's output, and the seam for it
   is the `worldDir` output stage, deliberately left clean.
4. **Immutable bake per world or instance at load.** Matches the existing whole-zone
   `RuinbornePhysics.Populate` lifecycle. Rasterization is cell-local, so a future `RebakeRegion`
   needs no structural change. No current consumer mutates the live world.
5. **Grid A* behind a planner seam, not a navmesh.** The recorded roadmap question, decided by
   weighted trade-off:

   | Criterion (weight) | Grid, concrete API | Navmesh | Grid behind seam |
   |---|---|---|---|
   | Solves wolves + dungeons now (x3) | 9 | 9 | 9 |
   | Implementation cost and risk (x3) | 9 | 2 | 8 |
   | Path quality (x2) | 7 | 9 | 7 |
   | MMO-scale headroom, streamed cells (x2) | 6 | 8 | 7 |
   | Dungeon substrate fit (x2) | 10 | 5 | 10 |
   | Headless testability and determinism (x2) | 9 | 6 | 9 |
   | Future options open (x1) | 5 | 7 | 9 |
   | **Weighted total (max 150)** | **123** | **96** | **125** |

   A navmesh's quality edge only matters at scales no consumer has, and its cost (a Recast-class
   in-house build or a heavyweight dependency) lands entirely up front. Cell-sized grids stream
   naturally with the sharded world later. The planner seam (`IPathPlanner`) costs two interfaces now
   and keeps "grid vs navmesh" permanently answerable as "both" if a game ever pulls for it.

## Architecture

New package `KhaozEngine.Navigation`, GPU-free, joins the `Foundation` umbrella.

- Dependencies: `Primitives`, `Collision` (reuses `WorldColliders` footprints and `GridRay` DDA for
  line-of-sight), `Terrain` (slope sampling in the overworld baker).
- Deliberately NO `Locomotion` dependency. The follower emits a plain `Vector2` world direction that
  the caller feeds to `CharacterMovement.StepTowards`. Agent radius and max slope arrive as plain
  floats from the game's `MoveTuning`.
- The dungeon baker lives in `KhaozEngine.Dungeon`, which gains a `Navigation` project reference
  (clean direction, `Dungeon -> Navigation`). `Navigation` never learns about `DungeonLayout` or
  MapDoc.

Three public seams:

- **`NavSpace`**: the baked walkability artifact. One or more `NavGrid` layers plus inter-layer links
  (dungeon stairs). Overworld = one layer. Immutable after bake.
- **`IPathPlanner`**: `FindPath(from, to, agentRadius, budget) -> PathResult`. One v1 implementation,
  `GridPathPlanner`. This is the seam a future navmesh planner would implement.
- **`PathFollower`**: per-agent steering state. Goal in, per-tick `worldDir` out. Owns waypoint
  advance and replan triggers.

## Data model

`NavGrid` stores one byte per cell: **quantized clearance** (distance to the nearest obstacle, in
half-cells), not a walkable bit. Walkable for radius r is `clearance >= r`, so one bake serves every
agent size (wolf and future ogre share the grid, no per-radius rebakes). The clearance transform is a
two-pass chamfer sweep after rasterization. Cells that fail the slope predicate or sit inside a
collider store zero and read as blocked.

Grid metrics: `CellSize` (default 0.5 m), world-space origin, width and height in cells, and for
multi-layer spaces a Y height band per layer so the agent's floor resolves from its Y position.

Sizing at real content: Ruinborne's whole zone (260x260 m) at 0.5 m cells is 520x520, about 270 KB.
A 1 km squared world is about 4 MB. Per-shard-cell grids (60 u cells, 120x120) are about 14 KB each.

## Baking

**Overworld** (`NavGridBaker`, in `Navigation`): for each cell center, terrain slope predicate
(`TerrainCollision.IsWalkable`) plus inside-any-collider test via `WorldColliders.Query` (cylinder and
oriented-box footprints), plus an optional game-supplied `extraBlocked(x, z)` predicate (water,
out-of-bounds regions). Colliders with a finite standable `Top` still block in v1. This is
conservative: nav routes around low props the character could technically step onto. Recorded as a
follow-up.

Low standable props, ramps, and staircases are now walkable via the step-aware
`NavGridBaker.BakeOverworldSteps` bake (a separate entry point from `BakeOverworld` above), see
[NAV-STEP-SURFACES-DESIGN.md](NAV-STEP-SURFACES-DESIGN.md).

**Dungeon** (`DungeonNav.Bake(DungeonLayout)`, in `Dungeon`): one `NavGrid` layer per floor straight
from `DungeonCellKind.IsWalkable`, stair cells become inter-layer links between the floors they
connect.

**Invalidation seam (designed, not shipped):** rasterization is cell-local, so a future
`RebakeRegion(rect)` runs the bake loop over a sub-rect. Nothing in v1 assumes whole-grid bakes beyond
the public constructors.

## Planner

`GridPathPlanner` implements `IPathPlanner` over a `NavSpace`:

- **Fast path first.** If `GridRay.IsClear` from start to goal at the agent's clearance, return the
  trivial two-point path with no A* at all. Open-terrain chase, the overwhelmingly common case, costs
  one DDA walk.
- **A\***, 8-connected with corner-cut prevention, octile heuristic, uniform cost. A per-cell cost
  field is a recorded follow-up seam (it would be a parallel array, the clearance byte leaves no
  room).
- **String-pulling** on the raw cell path via `GridRay` line-of-sight, so the output is a short list
  of world-space waypoints, not a cell staircase. Smoothing never crosses an inter-layer link, stairs
  stay explicit waypoints.
- **Node budget.** Every query carries a max-explored-nodes cap. On cap or exhaustion the planner
  returns a partial path toward the closest-approach node, flagged `Partial`, so a chaser still closes
  distance to an unreachable target instead of freezing.
- **Endpoint snapping.** Off-grid or blocked endpoints snap to the nearest walkable cell within a
  bounded radius (an agent shoved into a collider, a player standing on a prop). Snap failure returns
  `Unreachable`.
- **Stateless and reentrant.** Pure math, no time, no randomness. Deterministic given identical
  inputs, documented with the same contract wording as `SpatialHashGrid`. A future async request queue
  wraps it without change. v1 is synchronous, which is adequate by orders of magnitude at current and
  near-term scale (one wolf today, and about 100 agents replanning once a second over 120x120-cell
  regions is negligible at 30 Hz).

## Follower

`PathFollower` is per-agent mutable state the brain owns. One call per tick:
`Tick(currentPosition, goalPosition, dt) -> PathFollowOutput` where the output carries `worldDir`
(a `Vector2` ready for `StepTowards`), a state enum (`Following`, `Arrived`, `Unreachable`), and the
active waypoint for debugging.

Replan triggers, all configured by a `PathFollowConfig` with defaults:

- Goal drifted more than `GoalRetargetTolerance` from where it was when the current path was planned
  (a moving player).
- Agent diverged more than `CorridorTolerance` from the current path segment (shoved, slid, blocked).
- Waypoint reached within `AcceptRadius` advances the index. Final waypoint reached flips to
  `Arrived`.
- A `ReplanCooldown` floor so an agent pinned against a moving target does not replan every tick.

The dynamic-avoidance seam is the follower's output: a later avoidance layer adjusts `worldDir` after
the follower and before `StepTowards`, touching neither planner nor follower.

## Error handling

Mostly by construction inside the two components: unreachable goals degrade to partial paths, bad
endpoints snap, a stuck agent triggers corridor replan, and the worst case (no path, budget exhausted,
snap failure) is an explicit `Unreachable` state the brain reacts to (leash home, idle). Never an
exception on the query path, never a spin.

## Testing

All headless xUnit in `KhaozEngine.Tests`:

- **Baker:** synthetic analytic terrain plus hand-placed colliders, assert expected clearance at known
  cells (flat clear, steep slope blocked, shrinking clearance near cylinder and box footprints,
  `extraBlocked` respected). Chamfer verified against brute-force distance on a small grid.
- **Planner:** optimality on open grids (octile distance match), corner-cut never happens, partial
  flag on walled-off goals, node budget respected, endpoint snapping, fast-path short-circuit when
  line-of-sight is clear, determinism (same inputs twice give identical waypoints).
- **Dungeon:** bake a small generated `DungeonLayout`, assert a ground-floor to upper-floor path
  crosses exactly through the stair links, and layer resolution from Y picks the right floor.
- **Follower:** frame-by-frame state machine tests (waypoint advance, goal-drift replan, corridor
  replan, cooldown, `Arrived` and `Unreachable` transitions).
- **Acceptance, consumer-shaped:** an integration test wiring `PathFollower` output into real
  `CharacterMovement.StepTowards` against a physics world containing a rock collider between agent and
  goal, asserting the agent's position arrives behind the rock within a bounded tick budget (the
  plan fixes the number). Mirrors Ruinborne's
  `NpcCollisionTests` scene and reads the consumer-visible value through the real wiring.

## Ruinborne adoption contract (game-side, separate work)

After the engine release ships: Ruinborne bumps its pin, bakes a `NavSpace` at server boot next to
`RuinbornePhysics.Populate` (same lifecycle, whole zone), `NpcStepContext` carries the `NavSpace` and
planner, and `NpcChaseBrain`'s Approach state feeds its goal point through a per-wolf `PathFollower`
instead of computing `worldDir` directly. Hold and Retreat can stay straight-line initially. Returning
should adopt pathing in the same change (a leashing wolf gets stuck on props exactly like a chasing
one). Windowed playtest of wolf-chases-player-around-rock is the final gate.

## Explicit non-goals (v1)

- Dynamic obstacle avoidance (players, NPCs, dynamic bodies). Seam reserved at the follower output.
- Runtime region invalidation. Seam shaped, not shipped.
- Async or time-sliced path request queue. Planner is stateless so a queue wraps it later.
- Per-cell traversal cost weights (mud, roads). Follow-up parallel array.
- Cross-cell planning over streamed per-shard grids. Today a `NavSpace` covers whatever bounds the
  game bakes.
- Navmesh planner. The `IPathPlanner` seam is the reserved slot if a game ever pulls for it.

## Deferred follow-ups

Items surfaced during implementation and review append here, same convention as the roadmap: delete
an item once it ships, the detail moves to `CHANGELOG.md`.
