# KhaozEngine.Terrain.Render3D

The render arm of [KhaozEngine.Terrain](../KhaozEngine.Terrain): meshes chunks off the analytic field,
streams an endless ring of them around the player, and renders the PBR splat-material layer. Kept
separate from the render-free field so a server/sim never drags in `Render3D`. In the `Game3D` umbrella.

## Types

- **`TerrainChunkBuilder.Build(field, region, lod)`** -> **`TerrainChunkMesh`** - samples the field on
  a LOD-chosen grid into a `Render3D` `GltfMesh` with ~0.3 m edge skirts (mismatched-LOD neighbours stay
  crack-free), a per-vertex splat-weight array (grass/dirt/rock/sand/snow), a height/slope vertex-colour
  ramp, and an AABB (`TerrainChunkBounds`) for culling. CPU only, no GPU device.
- **`TerrainLod`** - `PickLod(distance)` maps camera distance to 3 tiers, `ResolutionFor(lod)` gives
  the grid resolution. **`TerrainChunkRegion`** is the square world tile to mesh (default 60 m).
- **`TerrainScene3D`** extensions - `Scene3D.LoadTerrainChunk` / `DrawTerrainChunk` (world-space
  vertices, identity transform) and `LoadTerrainMaterial` (realize a layered material once, share the
  handle across every chunk).
- **`TerrainStreamer`** + **`StreamerConfig`** - keeps the world loaded in a ring around the player:
  hysteresis unload band (`UnloadRadius > LoadRadius` stops boundary churn), re-LOD when a loaded
  chunk's tier changes, at most `MaxLoadsPerFrame` builds per update (nearest first). Pure bookkeeping
  over **`ChunkCoord`**/**`ChunkGrid`** driving an injected **`IChunkSink`**, so it is headless-testable
  with a fake sink. `UnloadAll`/`Dispose` free the loaded ring instead of leaking it.
- **`Scene3DChunkSink`** - the production sink: builds each chunk's mesh + scatters **`PropLayer`**s
  (each layer with its own config, mesh set, and draw radius), re-LODs meshes in place, draws every
  loaded chunk + in-range props per frame, and optionally adds baked prop collision statics to an
  `IPhysicsWorld` (the `physics` + `collisionShapes` ctor params), removed again on unload. A game may also
  pass an **`IChunkDynamicsSource`** (`dynamicsSource` ctor param, requires `physics`) to spawn dynamic
  bodies per chunk: the source yields **`DynamicSpawn`**s (shape + pose + `DynamicBodyDescription`) for a
  chunk, the sink registers them on load and removes them on unload. Mechanism only - the game decides what
  spawns where; the engine just registers what the source returns. The **`collideTerrain`** ctor flag
  (opt-in, requires `physics`) additionally registers each chunk's SURFACE as a static triangle-mesh body on
  load (rebuilt on re-LOD, removed on unload), so the terrain surface is part of the unified physics query
  path (raycasts, capsule sweeps, dynamic-body rest all see it) instead of only the analytic
  `TerrainCollision` ground-follow delegate. Off by default: a game keeps the analytic delegate path exactly
  as before.
- **`PropLayer`** - one scatter or companion layer's config + mesh set + draw radius.
  `PropLayer.ScatterLayer(scatter, meshes, drawRadius)` / `CompanionLayer(hostLayerIndex, companions, meshes,
  drawRadius)` each have two overloads: the original `IReadOnlyDictionary<string, MeshHandle>` (one mesh per
  kit id) and a multi-part `IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>` (one-or-many mesh PARTS
  per kit id - a multi-material textured prop split into one textured sub-mesh per source material, from
  `Scene3D.LoadPropMeshes`). Exactly one of a layer's `Meshes`/`PartMeshes` is set. `Scene3DChunkSink.Draw`
  reads whichever is set.
- **`PropRenderer`** - `Queue` (against a raw `SceneInstances`, headless-testable) and the `Scene3D.DrawProps`
  extension instance every placement within a draw radius of a focus point, distance-culling the rest. Both
  overload the same way as `PropLayer`: a single-handle map queues one instance per in-range placement, and a
  multi-part map (`IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>`) queues every part of an id at the
  placement's shared scale/yaw/translation transform, so the whole prop instances as a unit. A single-part
  list produces byte-identical submissions to the single-handle form (same `SceneInstances` path, no new
  per-instance shader indexing), so adopting the multi-part overload costs nothing for an untextured/flat
  prop. See `KhaozEngine.Render3D/README.md` ("Manifest-driven textured opt-in") for `LoadPropAuto` /
  `LoadPropMeshes`, the load-side half of this seam. Alpha-cutout foliage needs nothing here: the cutoff
  rides each part's material state (set at load via `LoadPropMeshes`), so a MASK leaf-card kit scatters and
  draws through this path with its silhouette carved, exactly like an opaque prop (see the Render3D README's
  "Alpha cutout" bullet).
- **`TerrainChunkCollision`** - extracts a chunk's SURFACE triangles (skirts excluded, winding flipped so
  the collidable face points up) into a static `TriangleMeshShape`. `Build(TerrainChunkMesh)` or
  `Build(GltfMesh, surfaceVertexCount)`; returns null for an empty chunk. Render-free (no GPU), so terrain
  collision is headless-testable. A Bepu mesh is not recentered, so the shape uses `Pose.Identity` (the
  vertices carry world position). This is what `collideTerrain` uses per chunk.
- **Splat materials** - **`TerrainMaterialLayer`** / **`TerrainLayeredMaterial`** (five tileable
  albedo + normal layers blended by the baked splat weights) and **`TerrainMaterialPresets.Procedural()`**
  (deterministic placeholder textures). Omit the material for the vertex-colour ramp fallback.
  **`TerrainLayeredMaterial.Sampler`** (`TerrainSamplerConfig?`, opt-in, default null) overrides how the ground
  filters its detail textures at a distance - anisotropy level, filter, mip LOD bias - to trade grazing sharpness
  for less distance "fuzz" from a high-frequency tiling albedo. Null keeps the tuned default (anisotropic 16x +
  a +1 bias).

## Usage

```csharp
using KhaozEngine.Terrain;

var field = new TerrainField(TerrainPresets.Clearing());
var material = scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural());
var sink = new Scene3DChunkSink(scene, field, scatter, propMeshes,
                                chunkSize: StreamerConfig.Default.ChunkSize,
                                propDrawRadius: 120f, material: material);
var streamer = new TerrainStreamer(StreamerConfig.Default, sink);

// per frame:
streamer.Update(player.Position, dt);   // load/unload/re-LOD, amortized
sink.Draw(player.Position);             // queue chunks + in-range props

streamer.Dispose();                     // teardown: frees the ring and the sink
```

For a scatter layer of multi-material textured props (e.g. a re-baked pine/oak kit with a separate bark and
leaf material), pass `Scene3D.LoadPropMeshes` output through `PropLayer.ScatterLayer` instead of the plain
`propMeshes` dictionary above:

```csharp
var partMeshes = new Dictionary<string, IReadOnlyList<MeshHandle>>();
foreach (AssetEntry e in manifest.Props)
    partMeshes[e.Id] = scene.LoadPropMeshes(PropLoader.LoadPropAuto(e));
var layers = new[] { PropLayer.ScatterLayer(scatter, partMeshes, drawRadius: 90f) };
var sink = new Scene3DChunkSink(scene, field, layers, chunkSize: StreamerConfig.Default.ChunkSize, material: material);
```

Depends on `KhaozEngine.Terrain`, `KhaozEngine.Render3D`, and `KhaozEngine.Physics` (chunk collision
statics). See the 3D World room (`Room3D`) in `KhaozEngine.Showcase` for the walkable streamed overworld
(its foliage scatter uses this multi-part form so the re-baked textured pine/oak/rock kit renders textured).

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
