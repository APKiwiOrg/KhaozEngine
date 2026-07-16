# KhaozEngine.Navigation

Engine-owned NPC navigation, render-free and deterministic throughout: a clearance-grid walkability
representation baked once and queried many times, a grid A* planner behind a swappable seam, and a
per-agent follower that turns a moving goal into a per-tick steering direction for
`CharacterMovement.StepTowards`.

## Clearance grid (`NavGrid`)

`NavGrid.FromWalkable(width, height, cellSize, originX, originZ, walkable, yMin, yMax)` rasterizes a
`(cx, cz) -> bool` walkable predicate over a rectangular XZ region, then bakes a clearance transform once.
Each cell stores a clearance byte: the approximate distance from the cell center to the nearest blocked
cell center, in half-cell units (a two-pass 2-3 chamfer distance transform - an orthogonal neighbor step
costs 2, a diagonal step 3), saturated at 255. A blocked cell stores 0, and space outside the grid counts
as blocked, so clearance falls off toward the borders. In world meters, `clearanceMeters = stored *
CellSize * 0.5f`, and a cell is passable for an agent of a given radius when its clearance is nonzero and
`clearanceMeters` is at least that radius (`NavGrid.IsPassable`).

**One bake serves every agent radius.** The rasterization and the clearance transform run once in
`FromWalkable`. `IsPassable`/`ClearanceMetersAt` are the only per-query radius check, so a rat and a bear
query the same grid with no rebake. `YMin`/`YMax` record the vertical band a layer represents
(`ContainsY`), used by `NavSpace` to resolve which layer a world Y falls on.

## Multi-layer spaces (`NavSpace` / `NavLink`)

A `NavSpace` stacks one or more `NavGrid` layers, each covering its own vertical band, joined by directed
`NavLink` stair connections (`FromLayer`/`FromX`/`FromZ` -> `ToLayer`/`ToX`/`ToZ`). A two-way stair is
modeled as two links, one per direction. `NavSpace.Single(grid)` wraps a single layer with no links.
Every link endpoint is bounds-checked against its layer at construction, else `ArgumentException`.

`NavSpace.LayerOf(y)` resolves the layer index a world Y belongs to: with a single layer, always 0.
Otherwise the lowest-index layer whose `NavGrid.ContainsY` is true wins. If no layer contains `y`, it
returns the layer minimizing the distance from `y` to its band center `(YMin + YMax) * 0.5`, considering
only layers with a finite band, ties going to the lowest index. If every layer has an infinite band, it
returns 0.

`KhaozEngine.Dungeon`'s `DungeonNav.Bake` is a worked multi-layer example: one `NavGrid` layer per
dungeon floor, joined by directed `NavLink` pairs at every stair run.

## Baking the overworld (`NavGridBaker`)

`NavGridBaker.BakeOverworld(terrain, colliders, minX, minZ, maxX, maxZ, cellSize, maxSlopeRadians,
extraBlocked, yMin, yMax)` bridges the existing overworld representation into a `NavGrid`, so
pathfinding never re-touches `TerrainCollision` or `WorldColliders` at query time. A cell is blocked when
any of: the terrain slope at its center exceeds `maxSlopeRadians` (per `TerrainCollision.IsWalkable`),
`extraBlocked` returns true for its center (an optional gameplay-authored exclusion, e.g. a scripted
no-go zone), or a nearby `WorldCollider` overlaps a probe circle at its center of radius `cellSize *
0.70710678` (half the cell diagonal) - the conservative center-point test that catches any collider
touching a corner of the cell, so the bake never marks a cell passable when part of it is actually
covered, at the cost of occasionally blocking a cell a collider only clips at a corner.

v1 conservatism: a collider blocks regardless of its finite `WorldCollider.Top`. An overhang or low
platform a creature could duck under is still treated as fully solid for navigation (height-aware
clearance is a later pass). Width and height are `(int)MathF.Ceiling((max - min) / cellSize)` on each
axis, so the baked region may extend slightly past `maxX`/`maxZ` when the span is not an exact multiple
of `cellSize`.

```csharp
using KhaozEngine.Navigation;

NavGrid grid = NavGridBaker.BakeOverworld(
    terrain, colliders,
    minX: -50f, minZ: -50f, maxX: 50f, maxZ: 50f,
    cellSize: 0.5f, maxSlopeRadians: MathF.PI / 4f,
    extraBlocked: (x, z) => scriptedNoGoZone.Contains(x, z));
```

## Path planning (`IPathPlanner` / `GridPathPlanner` / `PathQueryBudget`)

`IPathPlanner.FindPath(start, goal, agentRadius, budget)` is the seam: callers depend on the interface so
a different planner can be swapped in without touching call sites. `GridPathPlanner` is the shipped grid
A* implementation over a `NavSpace`.

A query snaps both endpoints onto a passable cell (within `PathQueryBudget.SnapRadius`, else
`NavPath.Unreachable`), then takes a line-of-sight fast path when the goal is directly visible on the
start's layer, otherwise runs an 8-connected A* search. The search prevents diagonal corner-cutting (a
diagonal step needs both orthogonal companions passable), crosses layers over `NavSpace.Links` (each link
is a graph edge of nominal one-cell cost, its far endpoint re-checked for the agent radius), and caps its
work at `PathQueryBudget.MaxExpandedNodes` expansions. On an unreachable goal it returns a
`NavPathStatus.Partial` route to the closest node it reached, or `NavPath.Unreachable` when it never got
past the start. The raw cell chain is then string-pulled: within each same-layer run it greedily keeps
only the farthest cell still in clear line of sight from the current anchor, collapsing collinear or
diagonally-clear runs to a few turn waypoints. Both endpoints of every link crossing are always emitted
(paths never smooth across a layer change). Deterministic: fixed neighbor order and a monotone insertion
counter break every A* tie the same way.

Two documented limitations. The octile heuristic is admissible on the goal layer only when every link
moves at most one cell in XZ (true of `DungeonNav`'s stair links) - a link that jumps far in XZ for its
flat one-cell cost can make the heuristic overestimate, so A* can return a valid but suboptimal `Complete`
path. Off the goal layer the heuristic is zero (an admissible lower bound across a link, at the cost of a
Dijkstra-like sweep before the crossing), which pins the start as the closest-approach minimum, so a
cross-layer query that reaches the goal's layer but cannot reach the goal returns `Unreachable` rather
than `Partial` (single-layer `Partial` behavior is unaffected).

`PathQueryBudget` is a value type, cheap to construct per query: `MaxExpandedNodes` (the A* expansion
cap before it gives up and returns `Partial`) and `SnapRadius` (the max world-unit nudge onto a passable
cell). `PathQueryBudget.Default` is 4096 expanded nodes, a 3 world-unit snap radius. The
`PathPlannerExtensions.FindPath(planner, start, goal, agentRadius)` overload uses the default budget.

## Following a path (`PathFollower`)

`PathFollower` is per-agent steering state that turns a moving goal into a per-tick world-space direction,
replanning through an `IPathPlanner` only when it must. A game brain owns one instance per agent and calls
`Tick(position, goal, agentRadius, dt)` every frame. Not thread-safe: one agent, one thread.

A replan is due when: there is no stored path, the stored path is fully consumed, the goal drifted past
`PathFollowConfig.GoalRetargetTolerance` from where it was planned, or the agent strayed past
`PathFollowConfig.CorridorTolerance` from the corridor segment leading to the active waypoint. A due
replan only fires once `PathFollowConfig.ReplanCooldownSeconds` has drained, so a persistently unreachable
goal or a jittery corridor breach does not spam the planner every tick.

`PathFollowConfig` knobs (all `init`, `PathFollowConfig.Default` for the values below):

- **`AcceptRadius`** (default 0.6) - distance at which a waypoint or the goal counts as reached.
- **`GoalRetargetTolerance`** (default 1.5) - how far the goal may move from where it was planned before
  a replan is due.
- **`CorridorTolerance`** (default 2.5) - how far the agent may stray from the planned corridor before a
  replan is due.
- **`ReplanCooldownSeconds`** (default 0.5) - minimum time between replans.
- **`Budget`** (default `PathQueryBudget.Default`) - handed to `IPathPlanner.FindPath` on every replan.

`Tick` returns a `PathFollowOutput` (`WorldDir`, `State`, `ActiveWaypoint`). `WorldDir` is a unit vector
while `State` is `PathFollowState.Following`, and zero otherwise. `State` is `Arrived` once within
`AcceptRadius` of the goal, or `Unreachable` when the planner cannot find a route (the follower keeps
retrying, gated by the cooldown, in case the world changes). A `NavPathStatus.Partial` path that runs out
steers straight at the raw goal for one tick while the next replan (once the cooldown allows) picks up a
fresh route. `WorldDir` is the raw follow direction only: a dynamic-avoidance layer (steering around other
agents or late-appearing obstacles) is expected to run after the follower and before
`CharacterMovement.StepTowards`, adjusting `WorldDir` without touching the follower's own path state.

```csharp
using KhaozEngine.Navigation;
using KhaozEngine.Locomotion;

// Bake once: one clearance grid serves every agent radius that queries it.
NavGrid grid = NavGrid.FromWalkable(width, height, cellSize, originX, originZ, walkable);
var planner = new GridPathPlanner(NavSpace.Single(grid));
var follower = new PathFollower(planner);   // PathFollowConfig.Default

// Every AI tick:
PathFollowOutput output = follower.Tick(agent.Position, goal.Position, agentRadius, dt);
if (output.State == PathFollowState.Following)
{
    agent = CharacterMovement.StepTowards(agent, output.WorldDir, run: false, dt,
        groundHeight, tuning, world: physics);
}
```

## Determinism

Baking is deterministic (fixed scan order, integer math in the clearance transform), and so is planning:
`GridPathPlanner` iterates the eight neighbors in a fixed order (four orthogonals, then four diagonals)
and breaks every A* priority tie with a monotone insertion counter, so the same grid and query always
produce the same waypoints.

Depends on `KhaozEngine.Primitives`, `KhaozEngine.Collision` (`WorldColliders` footprints in
`NavGridBaker`), and `KhaozEngine.Terrain` (`TerrainCollision` slope in `NavGridBaker`). In the
`Foundation` umbrella metapackage.

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
