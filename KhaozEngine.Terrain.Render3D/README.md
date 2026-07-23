# KhaozEngine.Terrain.Render3D

The render arm of [KhaozEngine.Terrain](../KhaozEngine.Terrain): meshes chunks off the analytic field,
streams an endless ring of them around the player, and renders the PBR splat-material layer. Kept
separate from the render-free field so a server/sim never drags in `Render3D`. In the `Game3D` umbrella.

## Types

- **`TerrainChunkBuilder.Build(field, region, lod[, lodConfig])`** -> **`TerrainChunkMesh`** - samples the
  field on a LOD-chosen grid into a `Render3D` `GltfMesh` with ~0.3 m edge skirts (mismatched-LOD neighbours
  stay crack-free), a per-vertex splat-weight array (grass/dirt/rock/sand/snow), a height/slope vertex-colour
  ramp, and an AABB (`TerrainChunkBounds`) for culling. CPU only, no GPU device. The `lodConfig` overload
  resolves the tier's resolution through a custom table; the plain overload uses `TerrainLodConfig.Default`.
- **`TerrainLodConfig`** + **`TerrainLodTier`** - data-driven LOD tiers: an ordered list of
  `(Resolution, MaxDistance)` tiers, validated (strictly descending resolutions, strictly ascending distances,
  the coarsest at `float.PositiveInfinity`). `PickLod(distance)` -> tier index, `ResolutionFor(lod)` -> grid
  resolution. `TerrainLodConfig.Default` reproduces the legacy 64/32/16 at 80 m/200 m byte-for-byte and adds
  coarser 8- and 4-segment far tiers so a distant chunk costs a few hundred triangles. **`TerrainLod`** is a
  thin facade over `Default` (`PickLod`/`ResolutionFor`). **`TerrainChunkRegion`** is the square world tile to
  mesh (default 60 m).
- **`TerrainScene3D`** extensions - `Scene3D.LoadTerrainChunk` / `DrawTerrainChunk` (world-space
  vertices, identity transform) and `LoadTerrainMaterial` (realize a layered material once, share the
  handle across every chunk).
- **`TerrainStreamer`** + **`StreamerConfig`** - keeps the world loaded in a ring around the player:
  hysteresis unload band (`UnloadRadius` greater than the outer load radius stops boundary churn), re-LOD when a
  loaded chunk's tier OR residency ring changes, nearest-first ordering. Pure bookkeeping over
  **`ChunkCoord`**/**`ChunkGrid`** driving an injected **`IChunkSink`**, so it is headless-testable with a fake
  sink. `UnloadAll`/`Dispose` free the loaded ring instead of leaking it.
  - **Decor ring / far field.** `StreamerConfig` carries a **`DecorRadius`** (chunk units, default 0 = off) and
    a **`LodConfig`**. `LoadRadius` is the gameplay radius; chunks between it and `DecorRadius` load as
    render-only **`ChunkRing.Decor`** chunks (coarse mesh, no scatter or physics), so a real horizon costs mesh
    only. A decor chunk upgrades to gameplay on approach (gaining scatter + colliders) and downgrades on
    retreat, via the same re-LOD path a tier change uses. **`RingOf(coord)`** reports a loaded chunk's ring. The
    same `LodConfig` must be wired to the sink; both default to `TerrainLodConfig.Default`.
  - **Async build (default).** With `StreamerConfig.Async` set (it is by default) and an `IAsyncChunkSink`
    sink, each chunk's CPU mesh build runs on a background thread and only the GPU upload happens on the
    frame thread, so a streamed chunk is no longer a full CPU-mesh-build hitch. `MaxLoadsPerFrame` then caps
    how many completed builds are APPLIED (GPU upload + handle swap) per `Update`. The builds themselves are
    unbudgeted (they run in parallel off the frame thread). `StreamerConfig.Synchronous()` opts back into the
    old inline build+upload path (blocking, deterministic - what editors/tools want), and a sink that is not
    an `IAsyncChunkSink` always runs synchronously regardless of the flag.
  - **`FlushPendingBuilds()`** forces every outstanding async build to complete + apply now (deterministic
    drain, ignores the budget). **`PrimeAround(playerPos)`** fills the whole ring around a point right away
    (a loading moment): use it to prime the first ring before the first frame. Both work in either mode.
  - **`Invalidate(RectArea)`** / **`Invalidate(ChunkCoord)`** rebuild every currently loaded chunk the rect
    (or the single coord) touches, in place, at its CURRENT LOD tier (no tier change, no ring reshuffle). This
    is the partial-invalidation seam an editor uses: a bounded edit re-meshes only the chunks it actually
    overlaps instead of the whole streamed ring. A chunk not currently loaded is left alone and picks up the
    change the next time it loads naturally. Both overloads flush any in-flight async builds first, so a
    build already running against the old state cannot land after the invalidation and overwrite it. Pair
    with `Scene3DChunkSink.UpdateField` below: swap the field, then invalidate the touched area.
- **`IChunkSink`** / **`IAsyncChunkSink`** - the load/unload seam the streamer drives. Every build call carries
  a **`ChunkRing`** (`Gameplay` / `Decor`) so the sink knows how much of a chunk to build:
  **`Load(coord, lod, ring)`** / **`ReLod(coord, handle, lod, ring)`**, and the async split
  **`BuildCpu(coord, lod, ring)`** (mesh + scatter, no GPU, safe on a worker thread) +
  **`Apply(coord, lod, ring, cpuBuild, existing)`** (GPU buffers + physics on the frame thread).
  `Scene3DChunkSink` implements `IAsyncChunkSink` and defaults the ring to `Gameplay` for direct callers; a
  custom sink implementing only `IChunkSink` still streams (synchronously).
- **`ChunkBuildScheduler<T>`** + **`ChunkBuild<T>`** - the GPU-free heart of async streaming: per-chunk
  generation tokens dispatch each build, collect the finished ones, and drop the superseded (a newer re-LOD)
  or cancelled (left the ring) results before they can be applied (last request wins). Pure `ChunkCoord`
  bookkeeping with no device, so it is fully headless-testable. **`IChunkBuildDispatcher`** chooses how build
  bodies run: **`TaskChunkBuildDispatcher`** (the default) fans them onto the thread pool, and a test dispatcher
  queues them to control completion order. A faulted build surfaces as a **`ChunkBuildException`** on the
  frame thread (during `Pump`/`Flush`), never a silent stuck chunk.
- **`Scene3DChunkSink`** - the production sink: builds each chunk's mesh + scatters **`PropLayer`**s
  (each layer with its own config, mesh set, and draw radius) for a `Gameplay` chunk, re-LODs meshes AND
  re-adopts the freshly scattered props in place (byte-identical after a pure LOD change, freshly correct
  after a field swap plus invalidate), draws every loaded chunk + in-range props per frame, and optionally
  adds baked prop collision statics to an `IPhysicsWorld` (the `physics` + `collisionShapes` ctor params). A
  **`Decor`**-ring chunk is render-only: the sink skips scatter, prop colliders, dynamics, and terrain
  collision for it. The `lodConfig` ctor param sets the tier table it meshes with (must match the streamer's).
  A game may also
  pass an **`IChunkDynamicsSource`** (`dynamicsSource` ctor param, requires `physics`) to spawn dynamic
  bodies per chunk: the source yields **`DynamicSpawn`**s (shape + pose + `DynamicBodyDescription`) for a
  chunk, the sink registers them on load and removes them on unload. Mechanism only - the game decides what
  spawns where; the engine just registers what the source returns. The **`collideTerrain`** ctor flag
  (opt-in, requires `physics`) additionally registers each gameplay chunk's SURFACE as a static triangle-mesh
  body, so the terrain surface is part of the unified physics query path (raycasts, capsule sweeps,
  dynamic-body rest all see it) instead of only the analytic `TerrainCollision` ground-follow delegate. Off by
  default: a game keeps the analytic delegate path exactly as before.
  - **Collision LOD is decoupled from render LOD.** The terrain collider registers at a FIXED tier
    (`collisionLod` ctor param, default 0 = densest), so a render re-LOD never rebuilds the physics
    triangle-mesh body. Prop static bodies are likewise KEPT across a pure tier re-LOD (placements are
    LOD-independent); both rebuild only on load, unload, a ring change, or an editor invalidate (field swap).
  - **`UpdateField(field)`** swaps the field every FUTURE chunk build reads (mesh height/splat plus prop
    scatter). An already-loaded chunk keeps its OLD field's shape until the caller invalidates or re-LODs it
    (`TerrainStreamer.Invalidate`); this call only changes what a build starting after it reads. In async mode
    the caller must flush in-flight builds first (`TerrainStreamer.FlushPendingBuilds`) before swapping, so a
    build already running against the old field cannot land after the swap. The map editor runs its streamer
    synchronously, so that ordering concern does not apply there.
- **`PropLayer`** - one scatter or companion layer's config + mesh set + draw radius, plus its dissolve fade
  band and optional far LOD variants. `PropLayer.ScatterLayer(scatter, meshes, drawRadius, fadeBandWidth = 0,
  lodMeshes = null, lodDistance = 0)` / `CompanionLayer(hostLayerIndex, companions, meshes, drawRadius, ...)`
  each have two overloads: the original `IReadOnlyDictionary<string, MeshHandle>` (one mesh per kit id) and a
  multi-part `IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>` (one-or-many mesh PARTS per kit id - a
  multi-material textured prop split into one textured sub-mesh per source material, from
  `Scene3D.LoadPropMeshes`). Exactly one of a layer's `Meshes`/`PartMeshes` is set, and its LOD set matches that
  representation (`LodMeshes`/`LodPartMeshes`). `FadeBandWidth` (default 0 = hard cut) dissolves props across the
  band just inside `DrawRadius`; a positive `LodDistance` with LOD variants swaps a kit to its far mesh past that
  distance. `Scene3DChunkSink.Draw` reads whichever mesh set is set and threads the fade band + LOD through.
  `layer.WithHlod(sourceMeshes, hlodDistance, weldCell, crossfadeWidth = 0)` returns a copy with **HLOD** on: past
  `HlodDistance` the chunk cluster swaps its individual props for one merged coarse mesh (baked from `sourceMeshes`,
  see `PropHlod`), crossfading the two across `HlodCrossfadeWidth`. Defaults off, so a layer without `WithHlod`
  always draws its props.
- **`PropRenderer`** - `Queue` (against a raw `SceneInstances`, headless-testable) and the `Scene3D.DrawProps`
  extension instance every placement within a draw radius of a focus point, distance-culling the rest. Both
  overload the same way as `PropLayer`: a single-handle map queues one instance per in-range placement, and a
  multi-part map (`IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>`) queues every part of an id at the
  placement's shared scale/yaw/translation transform, so the whole prop instances as a unit. A single-part
  list produces byte-identical submissions to the single-handle form (same `SceneInstances` path, no new
  per-instance shader indexing), so adopting the multi-part overload costs nothing for an untextured/flat
  prop. Two optional presentation knobs (both defaulting to today's exact behaviour): `fadeBandWidth` turns
  the hard cut at the draw radius into a **dissolve fade band** (issue #44) - each prop's rigid dissolve
  (the 14.5.0 opaque-noise-discard primitive, so overlapping fades never sort-fight) ramps deterministically
  0..1 by horizontal distance over `[drawRadius - fadeBandWidth, drawRadius]`, so props thin out instead of
  popping; and `lodMeshes`/`lodParts` + `lodDistance` swap a kit to an author-supplied far LOD mesh past
  `lodDistance` (per-kit opt-in, an id with no variant keeps its full mesh). See
  `KhaozEngine.Render3D/README.md` ("Manifest-driven textured opt-in", "Far LOD variants") for `LoadPropAuto` /
  `LoadPropMeshes` / `LoadPropLodAuto`, the load-side half of this seam. Alpha-cutout foliage needs nothing here: the cutoff
  rides each part's material state (set at load via `LoadPropMeshes`), so a MASK leaf-card kit scatters and
  draws through this path with its silhouette carved, exactly like an opaque prop (see the Render3D README's
  "Alpha cutout" bullet).
- **`PropHlod`** - author-agnostic HLOD (hierarchical LOD) merge+weld for a chunk cluster's props.
  `PropHlod.Merge(placements, sourceMeshes)` transforms each placement's flat source mesh to world space and
  concatenates into one `GltfMesh` (per-kit opt-in, an id with no source mesh contributes nothing).
  `PropHlod.Weld(mesh, cellSize)` is a vertex-cluster decimator: vertices in the same cubic cell collapse to one
  averaged vertex (position, normal, colour) and degenerate triangles drop, cutting the triangle count while
  silhouettes and canopy colour hold at range (the spike measured a 41-prop cluster 139,608 -> 16,178 tris at a
  1.5 m cell). `PropHlod.BuildMergedMesh(placements, sourceMeshes, weldCellSize)` is the one-call bake (merge, then
  weld when the cell is positive), and `PropHlod.CrossfadeAt(distance, hlodDistance, crossfadeWidth)` is the 0..1
  distance crossfade curve. All pure and deterministic, so the bake reproduces byte-for-byte - `Scene3DChunkSink`
  runs it as a RUNTIME bake at chunk load (cached per cluster in the chunk handle, freed on unload, rebuilt only
  on an Invalidate field rebuild), and the same function is offline-ready if a future artifact bake wants it. The
  merged mesh keeps flat **vertex-colour** albedo (from the `PropLoader.LoadProp` source form), so it renders
  through the existing untextured `Scene3D.Draw` path with no atlas, no impostor card, and no new shader.
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
var streamer = new TerrainStreamer(StreamerConfig.Default, sink);   // async build by default

streamer.PrimeAround(player.Position);  // loading moment: fill the first ring before the first frame

// per frame:
streamer.Update(player.Position, dt);   // request builds (off-thread) + apply up to MaxLoadsPerFrame
sink.Draw(player.Position);             // queue chunks + in-range props

streamer.Dispose();                     // teardown: frees the ring, the in-flight builds, and the sink
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
