# KhaozEngine.Terrain.Render3D

The render arm of [KhaozEngine.Terrain](../KhaozEngine.Terrain): meshes chunks off the analytic field and
renders the PBR splat-material layer, over the headless streaming core that now lives in `KhaozEngine.Terrain`
itself (`TerrainStreamer`, `IChunkSink`, `ChunkBuildScheduler`, and friends - see that package's README).
Kept separate from the render-free field so a server/sim never drags in `Render3D`. In the `Game3D` umbrella.

## Types

- **`TerrainChunkBuilder.Build(field, region, lod[, lodConfig][, skirtDepth][, splatRule])`** -> **`TerrainChunkMesh`** - samples the
  field on a LOD-chosen grid into a `Render3D` `GltfMesh` with edge skirts (mismatched-LOD neighbours
  stay crack-free), a per-vertex splat-weight array (grass/dirt/rock/sand/snow), a height/slope vertex-colour
  ramp, and an AABB (`TerrainChunkBounds`) for culling. CPU only, no GPU device. The `lodConfig` overload
  resolves the tier's resolution through a custom table; the plain overload uses `TerrainLodConfig.Default`.
  - **`skirtDepth`** defaults to a flat 0.3 m, which is only right for the densest tier. The slit a skirt hides is
    the coarse neighbour's, bounded by how far the field departs from that side's chord across ONE of its cells, so
    it grows with the tier: on the default table's far tiers a 0.3 m skirt leaves daylight at the seam (issue #100).
    Anything streaming more than one tier passes `TerrainLodConfig.SkirtDepthFor(lod, chunkSize)`, which is what
    `Scene3DChunkSink` does for every chunk it builds. The parameter's default stays flat so a direct caller keeps
    meshing exactly what it did before.
  **Vertices are CHUNK-LOCAL in X/Z (absolute in Y), and so is `TerrainChunkBounds`.** The field is still sampled
  at the ABSOLUTE coordinate (it is authored in world space and stays that way), but what is stored is
  `x - region.OriginX`, so a chunk 100 km out has vertices of magnitude at most its own size instead of being
  quantized to that magnitude's 7.8 mm float32 lattice at bake time. The placement travels in the draw transform
  and in the collision static's pose. Offset the bounds by the region origin for a world-space box.
  - **`splatRule`** (optional, default null) is the consumer hook on the per-vertex material mix. Null bakes
    exactly what `TerrainSplatWeights.From` produces, so an existing caller is byte-identical. The rule is handed a
    **`TerrainSplatContext`** (`Height`, `Slope01`, `Biome`, ABSOLUTE `WorldX`/`WorldZ`, and `Default` = the
    engine's own weights for that vertex) and returns the weights to bake. This is how a world with a SECOND body
    of water gets a shoreline at all: `From` derives its sand band from the field's single `WaterLevel`, which is
    the sea, so a lake edge otherwise bakes as grass meeting water. It is equally the seam for paths, trampled
    ground, and biome-specific dirt. Handing over `Default` is what keeps a rule from reimplementing (and then
    drifting from) the engine's mix. Three constraints, spelled out on `TerrainSplatContext`: the rule must be
    **pure** (chunk meshes are cached per region + LOD and built off the frame thread, so an impure rule bakes
    neighbours that disagree at their shared edge), it is a **hot path** (once per vertex of every streamed chunk),
    and splat is **presentation only** (no field, collision, document, or world-identity impact, so a client may
    adopt a rule against a server that has never heard of it). **`TerrainSplatWeights.Normalized()`** restores the
    sum-to-1 invariant a rule that adjusts a weight has to hand back, since the splat shader reconstructs snow as
    `1 - sum`.
- **`TerrainScene3D`** extensions - `Scene3D.LoadTerrainChunk` / `DrawTerrainChunk(handle, region)` (chunk-local
  vertices placed by the region origin, a pure translation) and `LoadTerrainMaterial` (realize a layered material
  once, share the handle across every chunk). The old parameterless `DrawTerrainChunk(handle)` is obsolete: it
  stays correct only for a chunk whose region origin is (0, 0), and draws every other chunk at the world origin.
  A chunk drawn this way RECEIVES the key light's shadow and, by default, casts none. A sculpted world with real
  hills wants `scene.TerrainCastsShadows = true`, which puts chunk meshes into the shadow depth pass through the
  same per-cascade cull everything else uses (see the Render3D README, and the terrain-casting note in
  `docs/USING-KHAOZENGINE.md`).
- **Terrain collision placement.** A chunk's collision mesh is built from those same chunk-local vertices
  (`TerrainChunkCollision.Build`), so it is registered at the chunk's REGION ORIGIN rather than `Pose.Identity`.
  Bepu transforms a query into a mesh's local space using the static's pose, so every triangle test runs at chunk
  magnitude however far out the chunk sits. Both halves of the placement are reduced by `IPhysicsWorld.Origin`, so
  streaming into a rebased physics world lands in that world's space.
- **The streaming core lives in `KhaozEngine.Terrain` now.** `TerrainStreamer`, `StreamerConfig`,
  `IChunkSink`/`IAsyncChunkSink`, `ChunkCoord`/`ChunkGrid`/`ChunkRing`, `ChunkBuildScheduler<T>`/
  `ChunkBuild<T>`/`ChunkBuildException`/`IChunkBuildDispatcher`/`TaskChunkBuildDispatcher`, and
  `TerrainLod`/`TerrainLodTier`/`TerrainLodConfig`/`TerrainChunkRegion` all moved to
  [KhaozEngine.Terrain](../KhaozEngine.Terrain) (headless and server-usable, no GPU dependency) - see
  that package's README for the full API (hysteresis unload band, decor ring, async build budget,
  `Invalidate`/`FlushPendingBuilds`/`PrimeAround`, and the build-scheduler internals). This package keeps
  binary-compat type forwarders (`AssemblyForwarders.cs`) so an existing `using KhaozEngine.Terrain.Render3D;`
  reference to any of them still compiles. What stays here is the render side that actually implements and
  drives the seam, below.
- **`Scene3DChunkSink`** - the production `IAsyncChunkSink`: builds each chunk's mesh + scatters **`PropLayer`**s
  (each layer with its own config, mesh set, and draw radius) for a `Gameplay` chunk, re-LODs meshes AND
  re-adopts the freshly scattered props in place (byte-identical after a pure LOD change, freshly correct
  after a field swap plus invalidate), draws every loaded chunk + in-range props per frame, and optionally
  adds baked prop collision statics to an `IPhysicsWorld` (the `physics` + `collisionShapes` ctor params). A
  **`Decor`**-ring chunk is render-only: the sink skips scatter, prop colliders, dynamics, and terrain
  collision for it. The `lodConfig` ctor param sets the tier table it meshes with (must match the streamer's),
  and the `splatRule` ctor param (both ctors, default null) threads a consumer splat rule into every chunk the
  sink builds - this is the seam a game configures, since games drive the streamer rather than calling
  `TerrainChunkBuilder.Build` themselves. See the builder bullet above for the contract. The rule is fixed for
  the sink's lifetime, matching how the mesh cache is keyed.
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
    synchronously, so that ordering concern does not apply there. Swapping while a build is EXECUTING throws
    `InvalidOperationException` naming the number of builds it caught (issue #105), because that build reads the
    field at several points and would otherwise mesh one chunk from two fields. A build that has already returned
    and is waiting to apply is the half `FlushPendingBuilds` covers, so the fix for that exception is to flush,
    never to retry.
- **`PropLayer`** - one scatter, companion, or placement layer's config + mesh set + draw radius, plus its
  dissolve fade band and optional far LOD variants. `PropLayer.ScatterLayer(scatter, meshes, drawRadius,
  fadeBandWidth = 0, lodMeshes = null, lodDistance = 0)` / `CompanionLayer(hostLayerIndex, companions, meshes,
  drawRadius, ...)` / `PlacementLayer(placements, meshes, drawRadius, fadeBandWidth = 0, lodMeshes = null,
  lodDistance = 0, colliders = true)` (issue #286) each have two overloads: the original
  `IReadOnlyDictionary<string, MeshHandle>` (one mesh per kit id) and a multi-part
  `IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>` (one-or-many mesh PARTS per kit id - a multi-material
  textured prop split into one textured sub-mesh per source material, from `Scene3D.LoadPropMeshes`). Exactly
  one of a layer's `Meshes`/`PartMeshes` is set, and its LOD set matches that representation
  (`LodMeshes`/`LodPartMeshes`). `FadeBandWidth` (default 0 = hard cut) dissolves props across the band just
  inside `DrawRadius`. A positive `LodDistance` with LOD variants swaps a kit to its far mesh past that
  distance. `CastsShadows` (default true) is the layer's shadow policy: pass `castsShadows: false` to ANY of the
  factories and the layer's props (and its merged HLOD mesh) stop writing into the key light's shadow depth pass
  while still drawing and still RECEIVING shadows - what a dense short-radius ground-cover or understory layer wants
  when hundreds of small casters pop on the draw-radius circle (issue #287). `Scene3DChunkSink.Draw` reads whichever
  mesh set is set and threads the fade band + LOD + the shadow policy through.
  `layer.WithHlod(sourceMeshes, hlodDistance, weldCell, crossfadeWidth = 0)` returns a copy with **HLOD** on: past
  `HlodDistance` the chunk cluster swaps its individual props for one merged coarse mesh (baked from `sourceMeshes`,
  see `PropHlod`), crossfading the two across `HlodCrossfadeWidth`. Defaults off, so a layer without `WithHlod`
  always draws its props. A placement layer carries a frozen, author-supplied `IReadOnlyList<PropPlacement>`
  instead of a `ScatterConfig`/`CompanionConfig` - `Scene3DChunkSink` buckets it by chunk coord once at
  construction and streams it through the exact same per-chunk path, so every knob above applies unchanged.
  `colliders` (default true) registers a static body for the layer's placements at ANY layer index. Pass
  `colliders: false` to keep it render-only when the game registers that zone's physics itself outside the
  sink. Scatter and companion layers keep the older layer-0-only collider rule (issue #288): only a layer at
  index 0 registers colliders. A placement layer instead follows its `colliders` flag regardless of where it
  sits in the list.
  - **`PlacementLayer(source, meshes, drawRadius, ...)`** (both overloads) takes an
    `IPlacementSource` (`KhaozEngine.Terrain`) instead of a frozen list, exposed as `PropLayer.PlacementSource`.
    The sink then queries the source at EVERY chunk build rather than bucketing once at construction, which is
    what lets content arriving later reach the renderer at all: a frozen list is split into per-chunk buckets
    when the sink is built, so a placement added afterwards would never draw no matter how correct the layer
    above it is. Exactly one of `Placements` and `PlacementSource` is set, `IsPlacement` covers both, and every
    knob (fade band, LOD variants, `WithHlod`, `colliders`) applies unchanged. The query runs on the build
    thread. `MapTileResidency` (`KhaozEngine.MapDoc`) is one, so a game streaming a tiled map document writes
    `PropLayer.PlacementLayer(residency, meshes, drawRadius)` and no glue. A frozen-list layer is untouched by
    any of this.
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
  `lodDistance` (per-kit opt-in, an id with no variant keeps its full mesh). A third knob, `castsShadows`
  (default true), is policy rather than presentation: false stamps every queued prop as a non-caster (issue #287).
  Left true, a prop inside the fade band now also thins its SHADOW as it dissolves, instead of casting solid up to
  the cull radius. See
  `KhaozEngine.Render3D/README.md` ("Manifest-driven textured opt-in", "Far LOD variants") for `LoadPropAuto` /
  `LoadPropMeshes` / `LoadPropLodAuto`, the load-side half of this seam. Alpha-cutout foliage needs nothing here: the cutoff
  rides each part's material state (set at load via `LoadPropMeshes`), so a MASK leaf-card kit scatters and
  draws through this path with its silhouette carved, exactly like an opaque prop (see the Render3D README's
  "Alpha cutout" bullet).
- **`GroundCoverRenderer`** - queues precomputed `GroundCoverInstance` transforms through `SceneInstances`,
  with a `Scene3D.DrawGroundCover` convenience overload. `GroundCoverRenderOptions` carries draw radius, fade
  band, quality density, distant density and shadow policy. Quality and distance compare against each
  instance's stable thinning rank, so lower settings select nested subsets. Distance dissolve starts before
  the final cull and uses no emissive edge. Multi-part meshes share one transform, shadows default off, and the
  warmed queue path allocates nothing per frame.
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
