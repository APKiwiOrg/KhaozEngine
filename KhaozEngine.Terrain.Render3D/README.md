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
  spawns where; the engine just registers what the source returns.
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

Depends on `KhaozEngine.Terrain`, `KhaozEngine.Render3D`, and `KhaozEngine.Physics` (chunk collision
statics). See the 3D World room (`Room3D`) in `KhaozEngine.Showcase` for the walkable streamed overworld.

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
