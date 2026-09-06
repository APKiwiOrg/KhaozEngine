# Authored ground cover

Status: in flight. Grimhollow is waiting for this engine capability.

## Problem

Grimhollow's short grass is currently a tile object. Varying the meshes improves the silhouette, but
the one-metre placement grid still controls the planting and makes broad lawns look sparse and regular.
Ground cover is cosmetic. It must not create gameplay objects, collision, picking targets or replication.
The owner approved an engine implementation with painted density, positions between tiles, surface
alignment and distance thinning. Models remain hand authored in Blender MCP on the game side.

## Existing pieces

`KhaozEngine.Terrain.PropScatter` already generates stable placements on an analytic `TerrainField`.
`KhaozEngine.Terrain.Render3D.PropRenderer` already feeds instanced meshes and rigid dissolve fading.
`TileWorldView` owns region residency and a headless scene seam. `TileEditSession` already provides
validated, undoable MCP authoring. These are the integration points, rather than a new shader pipeline.

## Decisions

- Put a generic surface-sampled ground-cover distribution in `KhaozEngine.Terrain`. A surface callback
  supplies height, normal and density. Analytic terrain and tile worlds can use the same distribution.
  Independent coordinate hash channels select position, model, scale, yaw and thinning rank. Querying
  adjacent rectangles produces the same result as one combined rectangle, without seam duplicates.
- Keep authored tile-world foliage layers separate from `TileObject`. A layer carries a bounded density
  raster in world metres, a seed, spacing, weighted archetypes and placement rules. Its raster resolution
  is independent of gameplay tile size. Density is sampled continuously between raster samples.
- Persist optional foliage layers with the world manifest and include them in its world hash. Empty
  foliage remains absent from serialization, preserving existing world bytes and hashes. Loading an old
  world yields no foliage. Region streaming and save must retain the layers without materializing every
  region. A malformed layer fails validation before mutation or save.
- An engine tile-world surface adapter samples the document's ground and rejects absent regions,
  disallowed underlays, water, room footprints, solid objects and configured door clearances. These
  exclusions apply to the final jittered position. Density fades at allowed-ground edges so the grass
  meets paths softly. The existing world coordinate conversion governs negative tile and world Z.
- Cache ground-cover instances per resident region. Each instance carries its surface-aligned transform
  and stable thinning rank. Terrain and object edits invalidate affected caches. Unloading a region
  drops its cover. Old worlds incur no distribution work.
- Draw through the existing rigid instance path. Reuse distance dissolve, with no glowing edge.
  Quality and distance choose stable subsets, and the distance transition fades before an instance is
  culled. Short cover receives lighting and defaults to no cast shadows. No new shader or renderer-wide
  behavior change is required. Existing prop transforms and APIs stay compatible.
- Add MCP configure, read, density write, brush paint and remove operations. Each is one undo step,
  including configuration replacement. Query results are detached. Save, undo, redo and headless render
  see the same content. No game-specific grass placement code is needed.

## Boundaries

This release provides static mesh ground cover. Wind animation, simulation of bent grass, interaction
with blades and automatic changes to any other game's content are outside this request.
Grimhollow's art, density choices, eligible grass materials and weeds remain game content. The engine
must not mention Hollowmere archetype ids in runtime code. A tile's movement and collision state remain
unchanged when foliage is enabled, painted or removed.

## Acceptance

- Two surface implementations can use the generic distribution, including a tile-world adapter.
- Density zero produces no instances. Scale, yaw and positions vary between tile centres. Identical
  input and seed produce identical instances across repeat calls and differently partitioned queries.
- Surface normals orient the blades correctly, including on sloping terrain and negative coordinates.
- Bad dimensions, lengths, non-finite values, weights and excessive candidate budgets fail clearly.
- Old-world save/load and hashes remain unchanged. New layers round-trip and affect only cosmetic
  content and the document hash. Rejected edits leave the document and history unchanged.
- No foliage occupies roads, water, building interiors, solid footprints or protected door approaches.
- Mesh submission remains instanced. Zero quality submits nothing. Distance and quality thinning are
  deterministic and nested, with a fade before distance culling and no per-frame allocations after warmup.
- Headless tests cover distribution, persistence, exclusions, region invalidation, render submission,
  MCP discovery, validation, undo and redo. A GPU smoke capture uses the existing instanced path.
- Package READMEs, usage and dependency seams describe the shipped capability. No frozen source-file
  baseline is raised. The parent coordinates versioning, packing, release and Grimhollow adoption.
