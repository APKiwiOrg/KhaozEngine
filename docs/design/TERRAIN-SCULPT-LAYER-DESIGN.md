# Terrain sculpt layer: authored height deltas over the analytic base

Design approved 2026-07-23. Roadmap item #271, part of the authored-world program
(https://github.com/APKiwiOrg/Ruinborne/issues/169). Implements the seam map format v1 reserved:
`MapDocument.TerrainOverrides`, null-only until now, becomes the sculpt-delta layer in format v2.

## Problem

The map document authors terrain only parametrically: seed, biome bands, and lake/flatten/ridge/rim
features over an analytic noise field. That shapes a valley but cannot hand-author topology
(canyons, custom cliff lines, non-parametric landforms). The authored-world direction needs
hand-sculpted terrain without giving up the pipeline that everything else rides.

## Decision: composite authored deltas inside TerrainField

Three approaches were weighed with the user on 2026-07-23: a sculpt-delta layer over the analytic
base (approved), authored Blender mesh terrain replacing the field (declined), and staying
parametric (declined).

The decisive argument for the delta layer is the composition point. Every terrain consumer reads
`TerrainField.SampleHeight`/`SampleNormal`: the chunk streamer, the physics terrain mesh
(`TerrainChunkCollision`), the nav bakes, scatter ground-snap, placement ground-snap, and the
editor's live viewport. Compositing sculpt deltas inside the field means every one of those
inherits authored terrain with zero downstream changes, and determinism is preserved trivially
because the composition is pure data over a pure function, no seeds involved.

Mesh terrain was declined for now, with the reasoning recorded: it replaces the whole ground stack
(streaming, snapping, physics, editor picking), demotes the map editor to a viewer for terrain, and
forecloses nothing by waiting, since a future mesh-terrain program could still land behind the same
`TerrainField` seam or beside it. Staying parametric was declined because it caps authoring at
procedural-shaped topology, which contradicts the program's goal.

## Format (v2)

- `terrainOverrides` becomes a sparse tile map of height deltas. Tile: 32 x 32 cells at the
  document's sculpt cell size (default 0.5 world units, stored in the block header so a zone can
  choose coarser). Only touched tiles are stored. Delta values are float meters added to the
  analytic height, bilinearly sampled between cell centers so sculpts stay smooth at any query
  resolution.
- `formatVersion` bumps to 2. Migration: a v1 document (null overrides) loads as v2 with an empty
  tile map, byte-identical terrain. The validating writer refuses tiles whose extent leaves the
  document bounds.
- The schema, `MapDocumentValidator`, and `MapDocumentSchema` gain the block. `MapRuntime.BuildField`
  attaches the tile map to the `TerrainField` it builds. No consumer signature changes.

## Sampling and normals

`TerrainField.SampleHeight(x, z)` returns analytic height plus the bilinear delta (zero where no
tile). `SampleNormal` is recomputed from the composited heights by central differences at the
sculpt cell size, not from the analytic normal, so slope gates (movement, scatter, nav bake
standability) see the sculpted surface. The existing analytic fast path stays when the tile map is
empty, so unsculpted zones pay nothing.

## Editor

- Brush tools on a new terrain-sculpt toolbar mode: raise, lower, smooth, flatten, set-height.
  Radius and strength in the inspector. Brushes edit delta tiles through the undoable command
  layer (capture touched tiles per stroke, one undo step per stroke).
- The viewport rebuild path already re-meshes terrain chunks on terrain edits. Sculpt strokes
  invalidate only the chunks intersecting the stroke's tile set (the dirty-region path, not full
  rebuild).
- `ke-mapedit` verbs mirror the GUI per the editor's dual-surface convention: `sculpt_apply`
  (brush op at a point with radius/strength), `sculpt_flatten_region` (shape to height),
  `sculpt_clear` (drop tiles back to analytic), plus read verbs for tile stats.

## Phasing (each phase releasable)

- **T1, format and field.** `terrainOverrides` v2 block, migration, validator, schema,
  `TerrainField` composition and normals, `MapRuntime` wiring, headless determinism and migration
  tests. Ships with no editor surface, the layer is MCP- and code-authorable only.
- **T2, editor brushes.** Toolbar mode, brush commands, stroke undo, dirty-region viewport rebuild.
- **T3, verbs and adoption.** `sculpt_*` MCP verbs, then Ruinborne repin and first sculpt pass on
  the valley (program issue tracks the game side).

## Non-goals

- Authored mesh terrain (declined above, revisit only if delta sculpting proves insufficient for a
  real zone).
- Live sculpting of a running server (the map editor program's standing non-goal).
- Erosion/hydraulic simulation brushes. Pure manual sculpting first.
- Texture/material painting. This layer is height only.
