# Nav Hop-Link Bake Design (KhaozEngine.Navigation)

Design for the first deliberate slice of the roadmap item "NPC navigation: vertical worlds (multi-level
overworld)". The follow-on to the 11.2.0 step-surface bake ([NAV-STEP-SURFACES-DESIGN.md](NAV-STEP-SURFACES-DESIGN.md)).
First consumer: Ruinborne wolves chasing a player onto a mid-height rock the wolf must jump onto, not
walk up.

This slice is scoped to **same-grid short vertical hops**: bake-generated links that let the planner (and
the follower) cross from a standable cell to a standable neighbor whose rise exceeds the step budget but
stays within a jump budget, on one `NavGrid` layer. It does NOT do the full phase-2 work (layered
many-surfaces-per-column extraction, roofed interiors, bridges). It advances that item by proving the
link-generation-plus-follower-seam half of it on the single-layer overworld, and leaves the multi-layer
extraction as the remaining phase-2 work. `NavSpace` layers, `NavLink`, and cross-layer planning already
shipped and are reused wholesale here.

## Problem

The 11.2.0 step-surface bake (`NavGridBaker.BakeOverworldSteps` plus `StepMask`) blocks the higher side of
any drop taller than `stepHeight`, a hard guarantee that stops the grid planner from walking across a
too-tall step. The side effect (recorded as inherent to a single grid in the step-bake non-goals) is that a
standable top taller than the step budget becomes an **unreachable island**: its rim cells bake blocked,
its interior stays passable but joins nothing. A ~1 m dome rock a player jumps onto is exactly this. The
wolf paths to the rock base, returns a `Partial` route, and noses the rock forever because no walkable cell
chain reaches the top.

The engine already holds the data to fix it. `NavGrid` carries the per-cell surface height field
(`SurfaceHeightAt`, from the step bake) and the passability mask (`IsPassable` / `ClearanceAt`). Where a
passable lower cell sits across a blocked rim from a passable higher cell, and the rise between them is
within a jump budget, that pair is a hoppable transition. Nothing generates a link there today, and nothing
tells the follower's consumer "traverse this by jumping" so it can play a lunge instead of ground steering.

## Decisions made (with rationale)

1. **Same-grid hops are represented as `NavLink`s, not a new NavGrid annotation.** `NavLink` is already a
   directed cell-to-cell edge, its `FromLayer == ToLayer` case is already legal (validated only for bounds
   by `NavSpace`), and `GridPathPlanner` already crosses links as graph edges with far-endpoint
   re-checking and un-smoothed endpoint emission. A same-grid hop is just a `NavLink` whose two endpoints
   live in one layer. Reusing the type means the planner's link machinery carries hops with no new graph
   concept, and the output rides `NavSpace.Links` where a consumer can already read it. A `NavGrid`-local
   "hop annotation" would duplicate the adjacency, cost, and reconstruction paths the link machinery
   already has. The one thing `NavLink` lacked is a way to say *what kind* of transition it is, so it gains
   a `NavLinkKind Kind` discriminator (below).

2. **`NavLink` gains a `NavLinkKind Kind` discriminator (`Stair` default, `Hop`).** The follower must
   surface a special traversal for hops but NOT for the existing cross-layer stair links (a stair is walked
   between its two waypoints today, and that must not change). So the link carries its kind. `Kind` is
   added as a non-positional `init` property defaulting to `NavLinkKind.Stair`, so every existing six-arg
   `new NavLink(...)` (all of `DungeonNav`) stays a `Stair` with byte-identical behavior, the record's
   positional constructor and two-field deconstruction are untouched, and equality now includes `Kind`
   (both sides `Stair` for existing links, so no test moves). An enum, not a bool, keeps the door open for
   future kinds (climb, drop, teleport) per the standing extensibility preference.

3. **Hop links are generated at bake time from the step grid's own height field and mask, by a dedicated
   pass (`NavHopLinks.Generate`).** The generation needs only what the step bake already produced: per-cell
   surface height (`SurfaceHeightAt`, null on a blocked cell, which doubles as the passability probe) and
   the cell grid. So the pass takes a baked `NavGrid` and emits `IReadOnlyList<NavLink>`, mirroring how
   `StepMask` and `ClearanceTransform` are separately testable stages. It is public so a game that already
   baked a step grid can add hops without rebaking, and `NavGridBaker.BakeOverworldHops` is the turn-key
   wrapper that bakes the step grid and returns a `NavSpace` with the hops in one call.

4. **The generation rule is "a passable cell, across a run of blocked cells, reaches a passable cell whose
   rise is in the jump band", which makes ramps win for free.** For each passable cell L and each of the 8
   fixed directions, walk outward: the immediately adjacent cell must be blocked (a real rim or wall, not
   open ground), every further intervening cell up to the landing must also be blocked, and the first
   passable cell reached (at distance k, 2 <= k <= `maxHopCells`) is the landing candidate T. Emit a hop
   L to T when `abs(H(T) - H(L))` is in `(stepHeight, jumpHeight]`. The "all intervening cells blocked"
   guard is the key: where the cells between two heights are *passable*, an ordinary walkable ramp already
   bridges the gap, so no hop is emitted and the planner walks the ramp. A hop appears ONLY where walking
   is impossible (a genuine cliff face, which the step bake blocked). Ramps win at generation time, not by
   cost tuning. `maxHopCells` defaults to 2 (a step-blocked rim is always exactly one cell thick, since the
   step test is a single-neighbor compare), configurable up for thicker walls.

5. **Direction is symmetric within the jump band, so a hop up and its matching drop down both fall out
   naturally, and a drop-down-only link never does.** Scanning every (cell, direction) ordered pair with
   the `abs(rise)` band test emits the low-to-high link when scanning from the low cell and the high-to-low
   link when scanning from the high cell. Both endpoints are within the same jump budget, so a pair the
   agent can jump up, it can also drop down: the reverse is not a separate capability, it is the same pair
   seen the other way. A **drop-down-only** transition (a drop deeper than `jumpHeight`, where the agent
   could fall but never climb back) is asymmetric, fails the `abs(rise) <= jumpHeight` band, and is never
   generated. That asymmetric case is an explicit non-goal, and it falls out of scope for free rather than
   needing a separate guard. The follower seam carries the endpoints, so the consumer tells a jump-up from
   a drop-down by comparing their resolved heights and plays the right motion.

6. **A hop costs more than walking, via a single planner-level `hopCostCells` knob keyed on `Kind`.** The
   planner charges a `Stair` link its existing one-cell cost (`grid.CellSize`, unchanged) and a `Hop` link
   `hopCostCells * grid.CellSize`. `hopCostCells` is one optional `GridPathPlanner` constructor argument
   (default 4), the only cost tuning this feature adds. Two properties follow from the default. First, where
   both a ramp and a hop reach the same place, the planner sums the ramp's per-cell walk costs against the
   hop's flat cost, so a ramp detour shorter than the hop penalty wins (a hop is not free). Second, the
   default keeps A* optimal: the octile heuristic assumes pure walking, so it stays admissible as long as a
   hop is never cheaper than walking its own XZ displacement. The longest hop at `maxHopCells = 2` spans a
   2-cell diagonal (octile ~2.83 cells), so `hopCostCells >= 2.83` guarantees the heuristic never
   overestimates a hop-using path. The default of 4 clears that with margin. Below it, A* stays correct but
   may return a valid non-optimal route, the same caveat the existing planner already documents for
   far-jumping links.

7. **The follower surfaces a hop through a new `PathFollowState.Hopping` plus a `HopStart` on the output,
   and suspends ground steering for the duration.** The planner marks a hop link's landing waypoint
   `NavWaypointKind.Hop` during reconstruction (it already knows the crossing is a link, and now knows the
   link's kind). When the follower is steering toward a `Hop` waypoint, `Tick` returns `State = Hopping`,
   `WorldDir = Vector2.Zero` (ground steering suspended, so a consumer feeding `WorldDir` into
   `StepTowards` does not walk the agent into the cliff), `HopStart` = the takeoff (the previous waypoint,
   or the follower's plan origin when the hop is the first segment), and `ActiveWaypoint` = the landing. The
   consumer reads `Hopping`, resolves takeoff and landing Y from its own surface data
   (`NavGrid.SurfaceHeightAt` on the two cells, staying consistent with the existing rule that a
   `NavWaypoint` stores no Y), and drives its lunge motion. When the agent reaches the landing (within
   `AcceptRadius`), the follower advances past it exactly as it advances past any reached waypoint and
   resumes `Following` (or `Arrived`). See "Follower seam" below for the never-completed case.

8. **`NavWaypoint` gains a `NavWaypointKind Kind` (`Walk` default, `Hop`), added the same
   backward-compatible way as `NavLink.Kind`.** A non-positional `init` property defaulting to
   `NavWaypointKind.Walk`, so every existing `new NavWaypoint(pos, layer)` stays a `Walk` waypoint, the
   record's positional constructor and deconstruction are untouched, and the determinism tests that compare
   whole waypoints still hold (both sides `Walk`, or both `Hop`). The follower keys the hop seam off this
   field.

9. **Full backward compatibility, opt-in throughout.** `BakeOverworldSteps` is untouched and still returns a
   bare `NavGrid` with no hops. `NavSpace.Single(grid)` with no links plans exactly as today. A consumer
   gets hops only by calling `BakeOverworldHops` (or `NavHopLinks.Generate` plus `NavSpace`). Existing
   planner and follower tests are unaffected: `Stair` and `Walk` are the defaults, `Stair` link cost is the
   unchanged one-cell charge, and no path without hop links ever sees `Hopping`. Ruinborne opts in by
   swapping its `BakeOverworldSteps` boot call for `BakeOverworldHops` and handling `Hopping` in its wolf
   brain.

## Architecture

No new package, no new dependency edge. Everything lands in `KhaozEngine.Navigation`, whose deps stay
`Primitives`, `Collision`, `Terrain`. `NavGrid` itself is unchanged: generation reads its existing public
surface (`SurfaceHeightAt`, `IsPassable` / `ClearanceAt`, `InBounds`, `Width` / `Height`). The
`INavSurfaceProvider` surface-source seam and its recorded non-edge to `KhaozEngine.Physics`
(DEPENDENCY-SEAMS.md) are reused unchanged: `BakeOverworldHops` reads its heights through the same
provider `BakeOverworldSteps` does.

### New public surface

- **`enum NavLinkKind { Stair, Hop }`**: the link discriminator. `Stair` (default 0) is every pre-existing
  directed link (the `DungeonNav` stair connections), crossed by ordinary steering. `Hop` is a same-grid
  vertical lunge that surfaces the follower's hop seam.
- **`NavLink.Kind`** (`NavLinkKind`, `init`, default `Stair`): added to the shipped record without touching
  its positional constructor.
- **`enum NavWaypointKind { Walk, Hop }`**: the waypoint discriminator. `Walk` (default 0) steers normally.
  `Hop` marks a hop link's landing waypoint, which the follower surfaces as `Hopping`.
- **`NavWaypoint.Kind`** (`NavWaypointKind`, `init`, default `Walk`): added the same way.
- **`NavHopLinks.Generate(NavGrid grid, float stepHeight, float jumpHeight, int maxHopCells = 2, int layer = 0)
  -> IReadOnlyList<NavLink>`**: generates same-grid `Hop` links from a step-baked grid (one baked via
  `NavGrid.FromSurfaces` / `BakeOverworldSteps`, so it carries a height field). `layer` is the index the
  grid occupies in its owning `NavSpace` (0 for a single-layer space), stamped into every emitted link's
  `FromLayer` / `ToLayer`. Throws `ArgumentException` when `grid.HasSurfaceHeights` is false (hop generation
  needs the height field) and `ArgumentOutOfRangeException` when a numeric argument is out of range
  (matching `NavGridBaker`'s style). Deterministic.
- **`NavGridBaker.BakeOverworldHops(surface, minX, minZ, maxX, maxZ, cellSize, stepHeight, agentHeight,
  jumpHeight, maxHopCells = 2, extraBlocked = null, yMin, yMax) -> NavSpace`**: the turn-key bake. Bakes
  the step grid exactly as `BakeOverworldSteps` does, runs `NavHopLinks.Generate` over it, and returns
  `new NavSpace(new[] { grid }, hops)`. A game hands the result straight to `GridPathPlanner`.
- **`PathFollowState.Hopping`**: the follower is at a hop takeoff, steering toward a `Hop` landing.
  `PathFollowOutput.WorldDir` is `Vector2.Zero` (ground steering suspended).
- **`PathFollowOutput.HopStart`** (`Vector2`, `init`): the takeoff XZ while `State` is `Hopping`, zero
  otherwise. Paired with `ActiveWaypoint` (the landing XZ) it gives the consumer both ends of the lunge.
- **`GridPathPlanner(NavSpace space, float hopCostCells = 4f)`**: the constructor gains the single hop-cost
  knob. Existing `new GridPathPlanner(space)` calls are unaffected.

### New internal

- **`HopLinkGenerator`** (or the private body of `NavHopLinks`): the outward-ray scan that turns the grid's
  height field and mask into the `Hop` link list, unit-tested through the public `NavHopLinks.Generate`.

### Changed internals (`GridPathPlanner`)

- The link adjacency (`_linkEdges`) carries, per link, the precomputed traversal cost in meters
  (`grid.CellSize` for `Stair`, `hopCostCells * grid.CellSize` for `Hop`) so A* charges the right cost, and
  a `_hopEdges` set of `(fromId, toId)` node-id pairs so reconstruction can mark a hop landing.
- A* link expansion charges the per-link cost instead of the hardcoded `grid.CellSize`.
- Reconstruction, at a run boundary that is a hop edge, stamps `NavWaypointKind.Hop` on the emitted landing
  waypoint. Stair crossings emit a `Walk` landing exactly as today.

## Data model

`NavLink` is one enum wider (`Kind`), `NavWaypoint` one enum wider (`Kind`), both defaulted so existing
storage and equality are preserved. `NavGrid` is unchanged. A baked `NavSpace` for a hop-enabled zone is
one layer plus a flat list of `Hop` links along every hoppable cliff edge (bidirectional across the jump
band), typically O(cliff perimeter) links. For Ruinborne's 520x520 zone with a handful of rocks and ledges
this is a few hundred links, an adjacency dictionary the planner builds once at construction.

## Generation algorithm (`NavHopLinks.Generate`)

Inputs: a step-baked `NavGrid` (with height field), `stepHeight`, `jumpHeight`, `maxHopCells`, `layer`. For
each cell L in row-major order that is passable (`grid.SurfaceHeightAt(L)` is non-null, which returns both
"passable" and its height `hL` in one call), for each of the 8 neighbor directions in fixed order, walk
outward k = 1, 2, ... up to `maxHopCells`:

- k = 1 (the adjacent cell): if it is passable, this direction has open ground, no cliff, stop scanning it.
  If it is blocked, it is a candidate rim, keep walking outward.
- k >= 2: let cur = L + dir * k. If `cur` is out of bounds, stop this direction. If `cur` is blocked, it is
  a thicker rim, keep walking (still within `maxHopCells`). If `cur` is passable, it is the landing T with
  height `hT`: emit `new NavLink(layer, Lx, Lz, layer, Tx, Tz) { Kind = NavLinkKind.Hop }` when
  `abs(hT - hL)` is in `(stepHeight, jumpHeight]`, then stop this direction (the ray is resolved whether or
  not it emitted, a passable cell reached is the end of this cliff crossing).

Only pure orthogonal and pure diagonal rays are scanned (offsets `(+-k, 0)`, `(0, +-k)`, `(+-k, +-k)`), so
every emitted link spans a Chebyshev distance of at least 2 and is never mistaken for a grid step by the
planner's reconstruction. Each directed link is produced once (one direction, one distance per ordered
pair), so no deduplication is needed. `maxHopCells` caps the outward walk.

Determinism: fixed cell scan order (row-major), fixed 8-direction order, k ascending, float compares only,
no time or randomness. Emits the identical link list for an identical grid. A physics-probe surface
provider's determinism upstream is the game's responsibility, documented on the `INavSurfaceProvider` seam
already.

### Worked cases

- **Isolated dome rock, top 1.0 m, `stepHeight` 0.5, `jumpHeight` 1.2.** The step bake blocks the rock's
  rim (a 1.0 m drop to ground) and leaves the interior passable. A ground cell at the base scans toward the
  rock: k = 1 is the blocked rim, k = 2 is the passable interior at 1.0 m, rise 1.0 in the jump band, so a
  hop is emitted from the ground cell to the interior cell, and its reverse from scanning the interior cell
  outward. The wolf hops on and off. This is the headline fix.
- **Ramp or staircase, per-cell rise within `stepHeight`.** No cell is blocked (every neighbor drop is
  within budget), so the "adjacent cell must be blocked" guard fails at k = 1 in every direction and no hop
  is emitted. The planner walks the ramp. A distance-2 pair on the ramp (rise above `stepHeight`) is never
  reached because the k = 1 passable cell stops the ray first, and even if it were, the intervening cell is
  passable so no hop would fire.
- **Tall thin wall between two flat areas, same height on both sides.** k = 1 blocked (the wall), k = 2
  passable at the same height, rise ~0 which is below `stepHeight`, so no hop. You do not hop a flat fence.
- **Tall box, top 10 m, above `jumpHeight`.** The base cell scans up: k = 2 lands on the box top at 10 m,
  rise 10 above `jumpHeight`, so no hop. The box top stays an unreachable island exactly as the step bake
  leaves it, which is correct: the agent cannot jump 10 m.
- **Two-cell-thick plateau rim (`maxHopCells` 3).** k = 1 and k = 2 blocked, k = 3 passable in the jump
  band, one hop across the two-cell rim. With the default `maxHopCells` 2 this plateau stays unreachable, a
  documented conservative default.

## Planner cost and reconstruction

Cost: precompute each link's meters cost at construction (`Stair` -> `grid.CellSize`, `Hop` ->
`hopCostCells * grid.CellSize`, using the source layer's cell size), store it in the adjacency, and charge
it in A* link expansion. `Stair` links keep their exact prior cost, so every existing planner and
`DungeonNav` test is unmoved. Admissibility of the octile heuristic holds for `hopCostCells` at or above
the longest hop's octile displacement (~2.83 cells at `maxHopCells` 2), and the default 4 clears it, so A*
stays optimal. This is the same admissibility framing the planner already documents for its links.

Reconstruction: a same-grid hop spans Chebyshev distance >= 2, so `IsGridStep` returns false and the
existing run-split logic treats it as a link crossing, ending the takeoff run and opening the landing run,
emitting both endpoints un-smoothed. The only new work is stamping `NavWaypointKind.Hop` on the landing
waypoint when the boundary edge is a hop (looked up in `_hopEdges` by the two chain node ids). Stair
crossings emit a `Walk` landing as before, so the follower surfaces `Hopping` only for hops.

## Follower seam

The lifecycle, per `Tick`:

1. The agent walks toward the takeoff waypoint (`Following`, normal `WorldDir`) like any waypoint.
2. On reaching the takeoff (within `AcceptRadius`), the follower's existing advance-past-reached step moves
   its active index onto the `Hop` landing waypoint.
3. Steering toward a `Hop` waypoint, `Tick` returns `State = Hopping`, `WorldDir = Vector2.Zero`,
   `HopStart` = the takeoff (previous waypoint, or the plan origin when the hop is the first segment),
   `ActiveWaypoint` = the landing. Ground steering is suspended.
4. The consumer plays its lunge, moving the agent from takeoff toward landing over some ticks. Each tick
   re-emits `Hopping` (the landing is not yet within `AcceptRadius`).
5. On reaching the landing, the follower advances past it and resumes `Following` toward the next waypoint,
   or reports `Arrived` if the landing was the goal.

Never-completed hop: the follower owns no hop timer. If the consumer does not move the agent, the landing
never comes within `AcceptRadius`, so the follower re-emits `Hopping` every tick, which is the honest
state (the agent is trying to hop and has not been moved). The normal replan triggers still apply on top:
a drifting goal or a corridor breach fires a cooldown-gated replan, which either re-plans the same hop
(re-entering `Hopping` idempotently) or routes elsewhere, and `Reset` clears the state entirely. Because
`WorldDir` is `Vector2.Zero` during `Hopping`, a consumer that ignores the `Hopping` state and only feeds
`WorldDir` into `StepTowards` simply halts the agent at the takeoff, no worse than today's "noses the rock
forever" and now explicitly signaled. `ActivePath` stays the committed path and `ActiveWaypointIndex`
points at the landing throughout, so a debug overlay reads the corridor normally.

The follower stays height-free: it hands the consumer the two XZ endpoints and the consumer resolves Y from
its own surface data via `NavGrid.SurfaceHeightAt`, consistent with the existing "a `NavWaypoint` stores no
Y, callers resolve height from the layer's grid" rule.

## Consumer wiring (Ruinborne, game-side, separate work)

After the engine release ships, Ruinborne swaps its `BakeOverworldSteps` boot call for
`BakeOverworldHops`, passing the same `TerrainSurfaceProvider` (or its physics-probe `INavSurfaceProvider`)
plus a `jumpHeight` from the wolf `MoveTuning` (roughly 0.4 to 1.2 m, above `stepHeight`, below the jump
ceiling), and hands the returned `NavSpace` to `GridPathPlanner` with a tuned `hopCostCells`. The wolf
brain handles the new `PathFollowState.Hopping`: on seeing it, resolve the takeoff (`HopStart`) and landing
(`ActiveWaypoint`) heights from its surface data, drive a lunge/jump motion toward the landing, and let the
follower resume once the wolf arrives. No planner or brain change is needed to path onto the rock, only the
`Hopping` motion. Windowed playtest of wolf-hops-onto-a-rock-to-reach-the-player is the final gate.

## Non-goals (this slice)

- Multi-level surfaces at one XZ (bridges, overhangs, roofed interiors) and the layered extraction bake
  that would feed multiple `NavGrid` layers. That is the remaining phase-2 work this slice advances toward.
- Drop-down-only links (a drop deeper than `jumpHeight`, one-way). Out of scope by the symmetric band rule,
  which never generates them. A jump-band pair's drop-down direction is generated (it is the same pair).
- Pathfinding cost tuning beyond the single `hopCostCells` knob. `jumpHeight` and `maxHopCells` are jump
  geometry, not cost.
- A hop-arc clearance check (whether the parabola over the gap clears an overhang). The landing and takeoff
  cells are clearance-checked, the arc between is assumed free at this grid resolution.
- The follower driving the lunge motion itself. The follower signals the hop and suspends steering, the
  game owns the motion.
- Thinning links along a long cliff edge. Every hoppable cell pair within budget is emitted, which the
  planner handles fine at overworld scale. A thinning pass is a possible later optimization.

## Deferred follow-ups

Items surfaced during implementation and review append here, same convention as the roadmap and the
step-bake design: delete an item once it ships, the detail moves to `CHANGELOG.md`.
