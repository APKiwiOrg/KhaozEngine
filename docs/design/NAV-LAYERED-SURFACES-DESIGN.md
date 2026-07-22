# Layered nav surfaces: multi-level overworld extraction (phase 2)

Design approved 2026-07-22. Roadmap item "NPC navigation: vertical worlds (multi-level overworld)"
(issue #30), phase 2 of 2, completing the item. Phase 1 (step-aware single-layer bake,
`NAV-STEP-SURFACES-DESIGN.md`) and the same-grid hop slice (`NAV-HOP-LINKS-DESIGN.md`) both shipped.
This phase delivers the two remaining halves: the layered many-surfaces-per-column extraction bake,
and link generation at climbable transitions beyond same-grid hops.

## Problem

One `NavGrid` stores at most one surface height per cell, so two walkable surfaces at the same XZ
(a bridge deck over a path, a roofed interior under its roof, an overhang above a trail) cannot
coexist: the phase-1 provider reports whichever surface it considers topmost and the other is
invisible to navigation. `NavSpace` already carries multiple layers joined by directed `NavLink`s
and the planner already routes across them (proven by the dungeon adapter and the hop slice), so
the missing piece is purely the bake: extracting the layers and their links from the world.

## Decisions made (with rationale)

1. **The many-surfaces source is a new provider seam, `INavColumnProvider`, mirroring the phase-1
   inversion.** `SampleColumn(x, z, Span<NavSurfaceSample>)` writes every standable surface in the
   column bottom-up (ascending height), each with its own headroom, and returns the count. Navigation
   stays physics-free exactly as `docs/DEPENDENCY-SEAMS.md`'s surface-source seam records: the GAME
   implements the provider over its physics world and hands it to the bake. The single-surface
   `INavSurfaceProvider` stays the phase-1 read, and `SurfaceColumnAdapter` wraps one so a
   single-surface world can use the layered bake unchanged. `DelegateColumnProvider` mirrors
   `DelegateSurfaceProvider` for tests and scripted fields.

2. **The physics half ships as `PhysicsColumnProbe` in KhaozEngine.Physics.** A repeated downward
   raycast sweep: cast from `ProbeHeight`, record the hit, re-cast from just below it, until the
   range is spent. A hit is standable when its surface normal passes the walkable-slope gate; every
   hit (standable or not) is a ceiling for the surface below it, so per-surface headroom is the gap
   to the hit above (`PositiveInfinity` for the topmost). Statics-only by default, the same stance as
   `PhysicsGroundProbe`. Physics cannot reference Navigation (the layering forbids the reverse edge
   too), so the game glues the probe to `INavColumnProvider` with a one-line delegate. That keeps the
   roadmap phrase "auto-extract from the physics world" turnkey without a new dependency edge.

3. **Extraction decomposes columns into regions that are single-valued per column by construction
   (the Recast heightfield-layers shape), and growth holds a hard no-too-tall-contact invariant.**
   Nodes are (cell, surface) pairs. Region growing connects 8-adjacent surfaces whose rise is within
   `stepHeight` (both ends standable after the headroom gate), but a region never claims two surfaces
   in one column (a bridge-to-ground continuum or a spiral ramp splits exactly where it would overlap
   itself), and never claims a surface that would sit 8-adjacent to an already-claimed surface of the
   same region with a rise beyond `stepHeight`. That second constraint was added as a correction
   during implementation: without it, a gradual ramp climbed step by step folds back into the same
   region as the flat ground beside it, the ramp top and the under-bridge ground become same-region
   neighbors, and `StepMask` erodes the ramp top, silently breaking the ordinary open-field
   bridge-plus-ramps layout (found by the natural-geometry regression test, which is kept). A merge
   pass then joins step-adjacent regions whose cell sets do not overlap AND that share no too-tall
   contact anywhere (a pair meeting within step at one spot but towering at another is recorded as
   forbidden and never merged, so the invariant survives merging). All scan orders are fixed (z, then
   x, then surface index), so the decomposition is deterministic.

4. **Two regions share a layer only when they have no column overlap AND no 8-adjacency at all.
   This is what deletes the phase-1 rim erosion.** After merging, any adjacency between two distinct
   regions is provably a too-tall rise (a within-step adjacency either merged or was blocked by
   overlap, and overlap already forces separate layers). Together with decision 3's growth invariant
   and merge guard, no region ever contains an internal too-tall contact and no layer ever holds one
   across regions, so `StepMask` provably never fires on a layered-bake layer (it still runs as belt
   and braces). Erosion is fully eliminated: a plateau rim, a rock top edge, a deck edge, and a
   switchback ramp's legs all bake standable to their true boundary. Phase 1's one-cell erosion
   (`NAV-STEP-SURFACES-DESIGN.md` decision 3) was the single-grid encoding of "do not walk off this
   edge". With layers, that separation is structural (layers only connect via links): every too-tall
   contact becomes a layer boundary, and because a refusal boundary always leaves two standable cells
   within `stepHeight` on different layers along the walk direction, `NavLayerLinks` seams it with
   directed Stair pairs, so walk connectivity across every boundary is preserved by construction
   (and hop links bridge the jumpable rises). Assignment is greedy over deterministically ordered
   regions: lowest layer index with no conflict.

5. **Links reuse the shipped kinds and machinery; nothing in the planner or follower changes for
   them.** `NavLayerLinks.Generate` emits, for every Chebyshev-distance-1 cross-layer pair of
   passable cells: a directed `Stair` pair when the rise is within `stepHeight` (a walked seam, the
   bridge deck meeting its abutment), and a directed `Hop` pair when the rise is in
   (`stepHeight`, `jumpHeight`] (a cliff edge, a rock top). Stair links cost one source cell and are
   walked with no special state, exactly the dungeon semantics; Hop links cost `hopCostCells` and
   surface the follower's `Hopping` seam, exactly the hop-slice semantics. Same-layer islands keep
   `NavHopLinks.Generate` per layer. Same-column (Chebyshev 0) cross-layer links are deliberately not
   emitted: standing under a ledge does not make it jumpable straight up, and a two-surfaces-within-
   step column is a degenerate authoring case, not a transition.

6. **Position-to-layer resolution becomes surface-aware: `NavSpace.LayerAt(Vector3)`.** `LayerOf(y)`
   picks by Y band, which is ambiguous once bands overlap (a bridge deck's Y sits inside the ground
   layer's band whenever the ground spans valleys to hills). `LayerAt` resolves the position's cell
   per layer and picks the passable surface nearest in Y, falling back to `LayerOf(y)` when no layer
   has a surface there (or none has heights at all). `GridPathPlanner.FindPath` switches to `LayerAt`
   for its endpoints. Single-layer spaces short-circuit to 0 and height-less multi-layer spaces (the
   dungeon adapter's `FromWalkable` grids) hit the fallback, so every shipped consumer resolves
   byte-identically.

7. **Full backward compatibility.** `BakeOverworld`, `BakeOverworldSteps`, and `BakeOverworldHops`
   are untouched. The layered path is a new entry point, `NavLayerBaker.BakeOverworldLayered`,
   returning a `NavSpace`. A world whose columns all carry one surface produces layer 0 equal to the
   `BakeOverworldSteps` grid minus the erosion rule (per decision 4) plus the same hop links, so
   adopting the layered bake on a flat world is behavior-preserving where phase 1 was already
   correct.

## Architecture

No new package, no new dependency edge. Navigation gains `INavColumnProvider.cs` (seam + delegate +
adapter), `NavLayerExtractor.cs` (internal decomposition), `NavLayerBaker.cs` (public bake),
`NavLayerLinks.cs` (public cross-layer link generation); `NavSpace` gains `LayerAt`;
`GridPathPlanner.FindPath` calls it. Physics gains `PhysicsColumnProbe.cs`. Each new concern is its
own type per the KESIZE rule.

### Bake pipeline

```
INavColumnProvider
  -> sample W x H columns (maxSurfacesPerColumn cap, extraBlocked exclusion)
  -> drop surfaces with headroom < agentHeight
  -> region grow (8-adjacent, rise <= stepHeight, single-valued per column, no-too-tall-contact invariant)
  -> merge non-overlapping step-adjacent regions (too-tall-contact pairs forbidden)
  -> assign regions to layers (no overlap, no adjacency)
  -> per layer: NavGrid.FromSurfaces (StepMask provably idle, belt and braces, clearance as always)
  -> links: NavHopLinks per layer + NavLayerLinks across layers
  -> NavSpace(layers, links)
```

Per-layer `YMin`/`YMax` are the layer's surface-height min/max, so `LayerOf` stays meaningful as the
fallback. Layer 0 is the first-assigned (deterministically the region containing the first-scanned
standable surface, in practice the ground).

## Non-goals

- Hop links across a blocked run to a DIFFERENT layer (the cross-layer analogue of
  `maxHopCells` > 1). Erosion-free layers meet at their true boundaries, so Chebyshev-1 covers the
  cliff/rock/deck transitions this item names. Widen `NavLayerLinks` if a real consumer case appears.
- Cropping sparse upper layers to their occupied bounding rectangle. Layers currently span the full
  bake extent; memory is width x height x (1 byte clearance + 4 bytes height) per layer. Revisit if a
  consumer bakes a large-extent world where this shows up.
- Rebake-region invalidation (unchanged from phase 1's non-goal).
- Bounding layer count on pathological terrain. Layer count is the greedy coloring of the region
  conflict graph, so a noisy world of many mutually adjacent too-tall plates could produce more
  layers (each a full-extent grid) than the authored worlds this targets ever will. Revisit together
  with layer cropping if a real bake shows it.
- Engine-side multi-surface extraction from `WorldSurfaces` (prop tops carry no undersides, so honest
  multi-surface columns cannot be derived from them; the physics probe is the multi-surface source).
