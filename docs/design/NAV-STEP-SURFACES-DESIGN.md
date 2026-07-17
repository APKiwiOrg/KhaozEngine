# Nav Step-Surface Bake Design (KhaozEngine.Navigation)

Design approved 2026-07-16. Roadmap item "NPC navigation: vertical worlds", phase 1 of 2. The
follow-on to the 10.123.0 navigation release. First consumer: Ruinborne wolves chasing players onto
rocks, platforms, and stairs. Closes the recorded standable-top-props non-goal from
[NPC-NAVIGATION-DESIGN.md](NPC-NAVIGATION-DESIGN.md).

Phase 2 (multi-level layered surfaces with inter-layer links) is NOT in this scope. This design only
has to leave a clean seam for it, and it does: the surface source is an interface a phase-2 extractor
extends, and `NavSpace`/`NavLink`/cross-layer planning already shipped.

## Problem

The overworld baker (`NavGridBaker.BakeOverworld`) tests a flat band above analytic terrain. For each
cell center it runs the terrain slope predicate at the analytic ground height, then blocks the cell
outright when any `WorldCollider` footprint overlaps a probe circle. A collider blocks regardless of
its finite `WorldCollider.Top`, so a low rock a wolf could step onto is a hard wall the planner routes
around. Ramps and staircases fare no better: a ramp prop's collider blocks its own footprint, and a
staircase baked as steep analytic terrain fails the slope gate. The recorded non-goal in
NPC-NAVIGATION-DESIGN.md names this exactly ("Standable-top props as walkable nav surface, walking
over low rocks, `WorldSurfaces` tops").

The engine already carries the missing height data. `KhaozEngine.Collision.WorldSurfaces.Query(x, z)`
returns the max top height of the standable prop surfaces covering a point (the same surfaces the
character controller stands on and jumps onto), and `KhaozEngine.Physics.PhysicsGroundProbe` casts a
downward ray through an `IPhysicsWorld` to read whatever surface (terrain, prop, building) sits under
a point. Neither is wired into the nav bake.

## Decisions made (with rationale)

1. **The height source is a provider seam (`INavSurfaceProvider`), not a hardcoded WorldSurfaces or
   physics call.** The roadmap phrases the source as "a downward physics probe or `WorldSurfaces` prop
   tops", i.e. either. A one-method interface lets the bake read a surface height + headroom at a
   world XZ point without knowing which. The engine ships one default provider
   (`TerrainSurfaceProvider`, terrain base raised by `WorldSurfaces` tops), and the physics-probe
   variant is realized by the GAME implementing the interface over its own `IPhysicsWorld` /
   `PhysicsGroundProbe`. This is also the phase-2 seam: a layered extractor implements a wider
   many-surfaces-per-column query behind the same concept, the single-surface `TrySample` staying the
   phase-1 read. See the dependency-layering note below for why this specific shape is what keeps
   Navigation out of the Physics dependency.

2. **The step budget folds into the boolean walkable mask at bake time, so the planner and follower do
   not change.** `GridPathPlanner` reads walkability only through `NavGrid.IsPassable(cx, cz,
   agentRadius)` (a per-cell clearance test), and `PathFollower` consumes only the resulting `NavPath`
   waypoints. Neither ever reads a surface height. So the rise-within-StepHeight rule is computed
   during the bake and collapses into which cells are blocked, feeding the existing clearance
   transform unchanged. Verified by reading the planner (`Blocks` -> `IsPassable`) and follower before
   committing to this.

3. **Rise-within-StepHeight is enforced by blocking the HIGHER side of every too-tall step, a hard
   guarantee, not a soft cost.** A single `NavGrid` layer cannot express "these two cells are both
   standable but you cannot step between them" as an edge property, because the planner treats every
   pair of adjacent passable cells as connected. The correct single-grid encoding: a cell is blocked
   when its surface drops to any finite-surface 8-neighbor by more than `StepHeight`. If
   `|H(A) - H(B)| > StepHeight`, the higher of A and B sees a too-tall drop and bakes as blocked, so
   the edge never joins two passable cells and the planner provably never routes across a step it
   should not. The cost is a one-cell rim erosion on the top of any standable feature that overlooks a
   drop taller than `StepHeight`, and features narrower than three cells never becoming standable.
   That conservatism is acceptable for phase 1 and disappears in phase 2's layered extraction. Low
   props, ramps, and stairs (rises within `StepHeight`) suffer no erosion at all, because every
   neighbor drop stays within budget.

4. **Headroom is a clearance the provider reports, gated against a plain `agentHeight` float.**
   `TrySample` returns the clear vertical space above the surface. The bake blocks a cell whose
   headroom is less than the agent height, so a creature cannot path under an overhang it does not
   fit beneath. The default provider reports open-sky (`PositiveInfinity`) headroom, so headroom never
   blocks until a game supplies a provider that measures it (a physics up-ray, phase 2 layering).

5. **Full backward compatibility: the existing `BakeOverworld` is untouched.** Ruinborne and every
   other consumer keep calling `BakeOverworld(terrain, colliders, ...)` with identical behavior. The
   step-surface path is a new method (`BakeOverworldSteps`) plus a new `NavGrid` factory
   (`FromSurfaces`), never a change to the existing signatures. A terrain-only provider with no props
   produces a mask that matches the old bake save for the step test, which on smooth analytic terrain
   almost never fires (adjacent terrain cells differ by far less than a sane `StepHeight`).

6. **Per-cell surface height is stored on `NavGrid`, but optionally.** The step test only needs the
   heights during the bake, and the planner never reads them. Persisting them anyway (a nullable
   `float[]`) is cheap groundwork the extensibility preference favors: phase 2 reads it, and a game
   can resolve an agent's standing Y or a debug overlay from it today. The existing `FromWalkable`
   path leaves the field null, so old bakes pay nothing and `SurfaceHeightAt` returns null there.

## Architecture

No new package, no new dependency edge. Everything lands in `KhaozEngine.Navigation`, whose deps stay
`Primitives`, `Collision`, `Terrain`.

### Dependency-layering decision (recorded for DEPENDENCY-SEAMS.md)

Full reasoning for the `Navigation -> Physics` non-edge and the `INavSurfaceProvider` inversion (the
GAME implements the provider over its own `IPhysicsWorld` and hands it to `BakeOverworldSteps`) is
canonical in `docs/DEPENDENCY-SEAMS.md`'s "Surface-source seam" section, not here. Two points that
section does not carry: the edge would have been LEGAL in the layering (Physics is a dependency-free
Foundation leaf, would not drag in the opt-in `Physics.Bepu`), just unnecessary, and the shape mirrors
the existing Navigation stance of taking agent radius and slope as plain floats rather than
referencing `Locomotion`.

### New public surface

- **`INavSurfaceProvider`**: the height source seam.
  `bool TrySample(float x, float z, out float height, out float headroom)`. Returns false where there
  is no standable surface (a hole, out of bounds, or a solid obstacle with no standable top), so the
  cell bakes blocked. On true, `height` is the surface top the agent stands on and `headroom` is the
  clear vertical space above it (`PositiveInfinity` for open sky).
- **`TerrainSurfaceProvider : INavSurfaceProvider`**: the default overworld source. Analytic terrain
  as the base standable height (`TerrainCollision.GroundHeight`), raised to any `WorldSurfaces` prop
  top covering the point. A cell fails (returns false) when the terrain slope exceeds the configured
  max AND no surface covers it (a prop top rescues slope-blocked ground, since the agent stands on the
  prop, not the hillside), or when a solid `WorldCollider` with no covering surface overlaps it.
  Reports open-sky headroom.
- **`DelegateSurfaceProvider : INavSurfaceProvider`**: a turn-key wrapper over a
  `TrySample`-shaped delegate, so a game can supply a physics probe or a scripted source without a
  named class. Convenience only.
- **`NavGridBaker.BakeOverworldSteps(provider, minX, minZ, maxX, maxZ, cellSize, stepHeight,
  agentHeight, extraBlocked, yMin, yMax)`**: the step-surface bake. Samples the provider at each cell
  center, applies `extraBlocked`, then delegates the two-pass step + headroom + clearance work to
  `NavGrid.FromSurfaces`.
- **`NavGrid.FromSurfaces(...)`**: a new factory alongside `FromWalkable` that takes a per-cell
  surface sample, runs the step-reachability + headroom mask, records the height field, and bakes the
  clearance transform once.
- **`NavGrid.SurfaceHeightAt(cx, cz) -> float?`** and **`NavGrid.HasSurfaceHeights`**: read the
  optional per-cell height field, null when the grid was baked without one.

### New internal

- **`StepMask`**: the step-reachability + headroom pass. Given per-cell `standable` / `height` /
  `headroom` arrays, `stepHeight`, and `agentHeight`, produces the boolean blocked mask. Separately
  unit-tested, mirroring how `ClearanceTransform` is an internal tested in isolation.

## Data model

`NavGrid` keeps its one clearance byte per cell (walkable-for-radius r is `clearance >= r`, one bake
serves every agent size, unchanged). It gains an optional parallel `float[] _heights`, one surface
height per cell, populated only by `FromSurfaces`. `HasSurfaceHeights` reports whether it is present.
`SurfaceHeightAt` returns the stored height, or null for a blocked cell or a grid with no height
field. Memory: a 520x520 grid (Ruinborne's zone at 0.5 m cells) adds ~1.08 MB of float heights on top
of the ~270 KB clearance grid, paid only on the step path.

## Baking (step-surface path)

`BakeOverworldSteps` derives `width` and `height` the same way `BakeOverworld` does
(`(int)MathF.Ceiling((max - min) / cellSize)` per axis), then for each cell:

1. Sample the provider at the cell center `(minX + (cx + 0.5) * cellSize, minZ + (cz + 0.5) *
   cellSize)`. A false result marks the cell not-standable.
2. `extraBlocked(x, z)` (unchanged semantics) also marks it not-standable.
3. Record the returned surface height (only meaningful for standable cells).

Then `StepMask.Compute` produces the blocked mask. A cell is blocked when any of:

- Not standable (provider returned false, or `extraBlocked`).
- Headroom below the agent: `headroom < agentHeight`.
- Too-tall drop: for any in-bounds 8-neighbor that is itself standable, `H(cell) - H(neighbor) >
  stepHeight`. Neighbors that are not standable (walls, holes) are skipped, so standing next to a wall
  never erodes a cell, only standing on the high side of a real drop does.

The mask feeds `ClearanceTransform.Compute` exactly as `FromWalkable` does, so the clearance grid the
planner consumes is produced by the identical path. The height field is stored alongside.

Determinism: fixed cell scan order, fixed 8-neighbor order, float compares only, no time or
randomness. Deterministic given a deterministic provider (both shipped providers are). A physics-probe
provider's determinism is the game's responsibility, documented on the seam.

### Worked cases

- **Low rock, top 0.4 m, StepHeight 0.5 m.** Rock-top cells drop to their ground neighbors by 0.4
  (within budget, not eroded). Ground cells beside the rock see only a rise to the rock (a negative
  drop, ignored) and flat ground elsewhere, so they are not eroded either. Rock and ground are both
  passable and adjacent, the planner routes straight over the rock. This is the headline fix.
- **Ramp / staircase, per-cell rise within StepHeight.** Every neighbor drop stays within budget, no
  erosion, the whole ramp is walkable. A staircase baked as a prop or as physics geometry both work
  through the provider.
- **Tall box, top 10 m, StepHeight 0.5 m.** The box-top rim cells drop 10 m to the ground and bake
  blocked, so a box three-or-more cells wide keeps a walkable interior fenced off by a blocked rim,
  and the planner cannot route up onto it. Ground beside the box stays walkable. A box under three
  cells wide bakes fully blocked (no standable interior survives the rim), matching today's behavior
  for something too tall and thin to stand on.
- **Natural cliff in analytic terrain.** The steep face already fails the slope gate and blocks, as
  today. The step test adds a one-cell safety rim at the cliff top, which is harmless and arguably
  desirable (NPCs do not toe the edge).

## Consumer wiring (Ruinborne, game-side, separate work)

After the engine release ships, Ruinborne swaps its `NavGridBaker.BakeOverworld` call at server boot
for `BakeOverworldSteps`, passing a `TerrainSurfaceProvider` built from its terrain plus the
`WorldSurfaces` it already builds via `PropSurfaces.FromScatter`, with `stepHeight` and `agentHeight`
from its wolf `MoveTuning`. If it prefers the unified physics path, it implements `INavSurfaceProvider`
(or uses `DelegateSurfaceProvider`) over `PhysicsGroundProbe.Height` for the surface and a short up-ray
for headroom. The planner, follower, `NpcStepContext`, and `NpcChaseBrain` are all unchanged: a wolf
now paths onto rocks and up stairs with no brain change. Windowed playtest of wolf-chases-player-onto-a
rock is the final gate.

## Testing

All headless xUnit in `KhaozEngine.Tests`, no GPU or device:

- **StepMask (internal):** hand-built height/standable/headroom arrays on small grids, asserting the
  exact blocked cells for a low step (no erosion), a too-tall step (higher side eroded), a headroom
  violation, a non-standable neighbor (no erosion), and diagonal drops.
- **NavGrid.FromSurfaces:** the height field round-trips (`SurfaceHeightAt`, `HasSurfaceHeights`), a
  blocked cell reports null height, and the clearance transform still runs (passable interior).
- **TerrainSurfaceProvider:** terrain base returned on flat ground, prop top raises the height where a
  `WorldSurface` covers, slope-blocked ground returns false, a covering surface rescues slope-blocked
  ground, a solid collider with no surface returns false.
- **BakeOverworldSteps:** a low standable prop between two open cells is now passable and the planner
  routes over it (vs `BakeOverworld` on the same collider routing around), a tall prop still routes
  around, `extraBlocked` still blocks, terrain-only provider matches `BakeOverworld` on smooth terrain.
- **Determinism:** the same provider and region baked twice give byte-identical clearance and heights.
- **Backward compatibility:** existing `NavGridBakerTests` and `FromWalkable` behavior are unchanged
  (the old grids carry no height field, `HasSurfaceHeights` is false).

## Non-goals (phase 1)

- Multi-level surfaces at one XZ (bridges, overhangs, roofed interiors). Phase 2, via a layered
  extractor behind the same provider concept feeding `NavSpace` layers and `NavLink`s.
- Automatic inter-layer link generation at climbable transitions. Phase 2.
- Per-cell traversal cost from surface material. Still the recorded follow-up parallel array.
- A physics-probe provider shipped inside Navigation. The seam is here, the physics wrapper is the
  game's (keeps Navigation off the Physics dependency).
- Removing the one-cell rim erosion on tall standable tops. Inherent to a single grid, gone in phase 2.

## Deferred follow-ups

Items surfaced during implementation and review append here, same convention as the roadmap: delete an
item once it ships, the detail moves to `CHANGELOG.md`.
