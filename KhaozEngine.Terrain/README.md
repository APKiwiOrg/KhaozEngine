# KhaozEngine.Terrain

Render-free analytic terrain. `TerrainField` is the single source of truth for ground height, and it is
stateless: the height at `(x, z)` depends only on `(x, z, seed)`, never on which neighbour chunks are
loaded, so the authoritative server and the visual client sample the same field and streamed chunks line
up regardless of load order. Plain `float` math throughout.

## Types

- **`TerrainField`** - `SampleHeight` folds the analytic layers in order (biome-band shaping, smoothstep
  blended, base coordinate-hash fractal noise, then an ordered feature list), then adds the authored
  sculpt delta when a `TerrainSculpt` is attached. Also `SampleNormal` (central finite difference over
  the composited height, at the sculpt cell size when sculpted), `SampleBiome` (dominant band), and
  `WaterLevel`. The `TerrainField(TerrainConfig, TerrainSculpt?)` constructor takes the sculpt layer; a
  null or empty one keeps the exact pure-analytic fast path. **`SetSculpt(TerrainSculpt?)`** swaps that
  layer at runtime by an atomic reference exchange, for a game whose authored sculpt streams in and out
  around the player. A sampler running concurrently on a worker thread sees the old snapshot or the new
  one and never a torn state: the field is `volatile`, and every public sampler reads it exactly once per
  call, including `SampleNormal`, whose four height taps and epsilon all come from that one snapshot.
  `SetSculpt` applies the same normalization the constructor does, so a null or empty sculpt restores both
  the analytic fast path and the 1 m normal epsilon.
- **`TerrainSculpt`** (+ **`TerrainSculptTile`**) - runtime composition of hand-authored height deltas
  over the analytic base: a sparse map of 32x32 (`TerrainSculpt.TileSize`) delta tiles at a fixed cell
  size. `SampleDelta(x, z)` bilinearly interpolates the authored deltas between cell centers and returns
  0 outside every stored tile. Read-only and deterministic, so composited terrain stays stateless.
  Documents author the tiles as the `terrainOverrides` block (`KhaozEngine.MapDoc`), and
  `MapRuntime.BuildField` builds and attaches this. **`With(add, remove)`** returns a new snapshot sharing
  every unchanged tile's delta array by reference, `O(tile count)` rather than `O(cell count)`, which is
  what makes rebuilding the layer on every streamed arrival cheap enough to do inline. It takes OWNERSHIP
  of each added array, matching the constructor: clone first if you intend to keep editing it, exactly as
  the editor's sculpt stroke already does. Removals apply before additions.
- **`TerrainConfig`** / **`BiomeBand`** / **`BiomeId`** - authoring inputs. Defaults give a single
  gentle meadow band, supply `Biomes` (designed regions along world Z) and `Features` for more.
- **`TerrainNoise`** - stateless coordinate-hash noise (`Hash2`, `ValueNoise`, `Fbm`, `Turbulence`,
  `SmoothStep`). Every function depends only on its arguments plus the seed, no `Random`.
- **`ITerrainFeature`** - a pure, composable height modifier applied in list order. Ready-made:
  **`LakeFeature`** (carved basin), **`RidgeFeature`** (gaussian wall, optionally pierced by a pass gap),
  **`FlattenFeature`** (levelled hub pad), **`RimFeature`** + `RimPass` (enclosing mountain wall with
  corridors out, the diegetic world border).
- **`TerrainCollision`** - ground-follow over a field: `GroundHeight`, `GroundNormal` (feed both to
  `CharacterMovement.Step` so steep terrain gates movement), and `IsWalkable(x, z, maxSlope)`.
- **`TerrainRaycast`** - GPU-free ray vs terrain intersection for editor and gameplay picking:
  `Raycast(field, origin, direction, maxDistance, out hit, step)` marches `step` in units of the direction's
  length (t units, so pass a normalized direction to march in world units) until the ray
  crosses the analytic surface, then bisects 24 times for a converged hit point. Endpoint-inclusive (a crossing
  inside the final partial step is still found), a ray starting below the surface returns the origin, and
  `step` must be positive (`ArgumentOutOfRangeException` otherwise). A NaN `step` or `maxDistance` throws the
  same exception rather than silently NaN-poisoning the march into a guaranteed miss.
  Deterministic: the same field, ray, and parameters always give the same hit.
  `Vector3` in/out, so `Terrain` stays render-free (build the ray from a camera's `ScreenToRay` and pass its
  `Origin`/`Direction`).
- **`TerrainPresets`** - `Clearing()` (meadow + mountains + lake) and `BoundedClearing()` (meadow
  ringed by a rim wall with one pass out).
- **`PropScatter`** (+ `PropPlacement`) - deterministic coordinate-hash prop placement, and
  **`PropColliders`** / **`PropSurfaces`** turn those placements into `Collision` collider/surface
  sets that line up exactly with the rendered props (tiled build equals whole-area build).
- **`TerrainStreamer`** + **`StreamerConfig`** - keeps the world loaded in a ring around a viewpoint:
  hysteresis unload band (`UnloadRadius` greater than the outer load radius stops boundary churn), re-LOD
  when a loaded chunk's tier OR residency ring changes, nearest-first ordering. Pure bookkeeping over
  **`ChunkCoord`**/**`ChunkGrid`** driving an injected **`IChunkSink`**, so it is headless-testable with a
  fake sink and just as usable on a dedicated server (no chunk mesh, no GPU) as on a client.
  `StreamerConfig` also carries an optional `DecorRadius` (chunk units, default 0 = off) for a farther,
  coarser decor-only ring tagged with **`ChunkRing`** (`Gameplay` / `Decor`), a `MaxUnloadsPerFrame`
  budget (default 8, farthest first, 0 or less frees everything at once) so a ring shift spreads its GPU
  frees over frames instead of bursting them onto one, and a `LodHysteresis` dead zone (metres, default
  10, 0 opts out) so a chunk parked on a tier boundary does not re-tier and rebuild its mesh on every
  small move. `UnloadAll`/`Dispose` free the loaded ring instead of leaking it, budget or no budget, and
  `PrimeAround` settles on work done rather than on the resident count, so neither budget can leave it
  returning with a half-filled ring.
- **`RenderDistanceProfile`** (+ **`RenderDistanceTier`**) - the view-distance radii as one coherent set
  rather than four independent knobs: `GameplayLoadRadiusChunks`/`DecorRadiusChunks`/`UnloadRadiusChunks`
  (chunk units, feeding `StreamerConfig` via `ToStreamerConfig()` / `ToStreamerConfig(baseConfig)`), plus
  `PropDrawRadius`, `FarClip` and `OceanHalfExtent` (metres) for the caller's own prop cull, camera and
  water plane, since this type names no renderer and stays GPU-free. `For(tier)` maps the built-in
  `Near`/`Medium`/`Far`/`Ultra` tiers (`Default` is `Far`, the engine's stock 500 m far clip), each pinned
  to the same 4-chunk gameplay ring so buying horizon scales mesh cost, not simulation footprint.
  `ChunkMeters` reads `TerrainChunkRegion.DefaultSize` directly (never a copied literal), so the chunk-unit
  and metre radii cannot drift apart if the chunk size ever changes. **Why one type instead of four
  sliders:** the radii only read as a single horizon when chosen together - raise the far clip alone and
  the frustum outruns terrain residency (a black void with props still drawn over it), raise the ocean
  extent alone and its rim sits inside the frustum as a visible wall of water, or past residency as a sea
  floating over nothing. **`Validate(paramName?)`** throws `ArgumentOutOfRangeException` unless the set is
  coherent: every radius positive, the unload band beyond both load radii (hysteresis), the ocean rim past
  `FarClip` but within `DecorRadiusMeters`, and `PropDrawRadius` at or inside `FarClip` (past it is never
  visible). Call it once where a caller-supplied profile is consumed, because a `readonly record struct`
  always has a zero-initialised `default` no constructor can intercept, so validating in the constructor
  would only give the illusion of an always-valid value. Every built-in `For` tier passes.
  **`Scaled(multiplier)`** (17.6.0) returns this profile scaled up as one coherent set, for a settings UI that
  offers Base/2x/4x rather than the fixed `For` tiers. `FarClip`, `OceanHalfExtent` and `PropDrawRadius` scale
  linearly. `DecorRadiusChunks` and `UnloadRadiusChunks` scale in whole chunks, rounded UP, because a linear
  scale of a chunk count is usually fractional and rounding up only ever grows residency, so it cannot break
  the rim rules `Validate` enforces. The unload radius is then clamped above the scaled decor radius and the
  unchanged `GameplayLoadRadiusChunks` in case rounding lands exactly on the boundary, so the hysteresis
  invariant holds regardless. `GameplayLoadRadiusChunks` itself does not scale: the gameplay ring is a
  simulation footprint, not a view distance, the same reason every `For` tier pins it. `Scaled(1f)` is the
  identity, a multiplier below 1 (or NaN, or infinity) throws `ArgumentOutOfRangeException` (scale DOWN with a
  smaller `For` tier), and every built-in tier still passes `Validate` after scaling by any factor at or above
  1. A blind per-field multiply does not: that is why the scaling lives on the type that owns the invariants.
- **`IChunkBuildGate`** + **`TerrainStreamer.BuildGate`** - an optional veto on which chunks the streamer
  may build, null by default (every chunk in the ring is eligible, the pre-gate behaviour exactly). A
  refused chunk is DEFERRED: not requested, not marked loaded, reconsidered next `Update`. It exists for a
  streamer composed with an asynchronous data layer, where continuous motion can outrun the data and no
  ordering rule fixes it: `MapTileResidency.GateFor` (`KhaozEngine.MapDoc`) returns the implementation
  that holds a chunk until every document tile its footprint touches is resident or unoccupied. The gate
  governs the ring scan only, never unloads and never `Invalidate`, which is the explicit "this data just
  arrived, rebuild it" call the arrival itself makes.
- **`IPlacementSource`** - a live source of a placement layer's props, queried at every chunk build
  instead of bucketed once at sink construction, so content that arrives after the sink was built reaches
  the renderer at all. `PlacementsIn(RectArea, List<PropPlacement>)` appends into a caller-owned list, and
  is called on the BUILD thread, so an implementation publishes an immutable snapshot and reads it once.
  `PropLayer.PlacementLayer` in `KhaozEngine.Terrain.Render3D` takes one, and `MapTileResidency` is one.
- **`IChunkSink`** / **`IAsyncChunkSink`** - the load/unload seam the streamer drives, GPU-free at this
  layer: **`Load(coord, lod, ring)`** / **`ReLod(coord, handle, lod, ring)`**, and the async split
  **`BuildCpu(coord, lod, ring)`** (mesh + scatter, no GPU, safe on a worker thread) /
  **`Apply(coord, lod, ring, cpuBuild, existing)`** (GPU buffers + physics on the frame thread, implemented
  by a render-side sink such as `Scene3DChunkSink` in `KhaozEngine.Terrain.Render3D`). A sink implementing
  only `IChunkSink` streams synchronously, no async split needed.
- **`ChunkBuildScheduler<T>`** + **`ChunkBuild<T>`** - the GPU-free heart of async streaming: per-chunk
  generation tokens dispatch each build, collect the finished ones, and drop the superseded (a newer
  re-LOD) or cancelled (left the ring) results before they can be applied (last request wins). Pure
  `ChunkCoord` bookkeeping with no device, so it is fully headless-testable. **`IChunkBuildDispatcher`**
  chooses how build bodies run: **`TaskChunkBuildDispatcher`** (the default) fans them onto the thread
  pool, and a test dispatcher queues them to control completion order. A faulted build surfaces as a
  **`ChunkBuildException`** on the frame thread (during `Pump`/`Flush`), never a silent stuck chunk.
- **`TerrainLodConfig`** + **`TerrainLodTier`** - data-driven LOD tiers: an ordered list of
  `(Resolution, MaxDistance)` tiers, validated (strictly descending resolutions, strictly ascending
  distances, the coarsest at `float.PositiveInfinity`). `PickLod(distance)` -> tier index,
  `PickLod(distance, currentLod, hysteresis)` the same with a dead zone around the boundary the chunk
  would cross (`currentLod` -1 = not built yet, a hysteresis of 0 or less is the undamped pick, and
  `DefaultHysteresis` is the 10 m `StreamerConfig` default), `ResolutionFor(lod)` -> grid resolution.
  `TerrainLodConfig.Default` gives 64/32/16 at 80 m/200 m plus
  coarser 8- and 4-segment far tiers. The table itself is chunk-size agnostic, so the coupling check lives
  in the `TerrainStreamer` constructor: a finite tier distance under a chunk's half-diagonal
  (`ChunkSize * sqrt(2) / 2`) is rejected, since a boundary inside one chunk's own footprint would re-tier
  the chunk the viewer stands in on sub-chunk movement. A tier past `OuterRadius * ChunkSize` is legal and
  simply never engages, which is how the default far tiers wait for a `DecorRadius`.
  **`TerrainLod`** is a thin facade over `Default`
  (`PickLod`/`ResolutionFor`). **`TerrainChunkRegion`** is the square world tile a mesh builder chunks the
  field into (default 60 m).

All of the above are plain data and interfaces with no GPU or render dependency, so a dedicated server
streams and re-LODs the same world a client renders, over the same `IChunkSink` seam a headless test
fakes. `KhaozEngine.Terrain.Render3D` supplies the one GPU-bound sink (`Scene3DChunkSink`) and mesh
builder that turns a chunk into drawable geometry. Everything above works without it.

## Usage

```csharp
using KhaozEngine.Terrain;

var field = new TerrainField(TerrainPresets.Clearing(seed: 5));
float h = field.SampleHeight(x, z);            // same answer on server and client
BiomeId biome = field.SampleBiome(x, z);

var ground = new TerrainCollision(field);
state = CharacterMovement.Step(state, cmd, dt, ground.GroundHeight, MoveTuning.Default,
                               groundNormal: ground.GroundNormal);
```

## Picking

```csharp
// ray from a camera's ScreenToRay (KhaozEngine.Render3D), normalize so step and maxDistance are world units
var dir = Vector3.Normalize(ray.Direction);
if (TerrainRaycast.Raycast(field, ray.Origin, dir, 200f, out Vector3 hit))
    PlaceAt(hit);   // a marched hit lies on the surface (a ray starting below ground returns its origin as-is)
```

## Scatter exclusions and overrides

`PropScatter.Generate` takes generalized region shapes alongside the legacy single clearing disc:

- **`IArea2D`** (`DiscArea2D`, `BoxArea2D`, `PolygonArea2D`) - a pure, stateless XZ-plane region test
  (`Contains(x, z)`), so a candidate's exclusion/override status depends only on the shape's own
  construction values, never on call order or which chunk asked.
- **`ScatterConfig.Exclusions`** (`IArea2D[]`) - a candidate inside ANY exclusion is skipped, on top of
  the legacy `ClearingRadius` disc (which still works unchanged, a document-driven config just zeroes it
  and expresses clearings as exclusion shapes instead).
- **`ScatterConfig.Overrides`** (`ScatterOverride[]`) - the first override (list order) whose `Area`
  contains the candidate wins: its `DensityMultiplier` scales the biome rule's density up or down (a
  multiplier above 1 boosts spawns, 0 suppresses them, and the product is clamped to the 0..1 keep
  probability range), and a non-empty `Kinds` replaces the rule's weighted kind mix inside the area.
  An override can only adjust a biome that already has a scatter rule with at least one kind, since
  candidates in a biome with no rule are skipped before overrides are consulted, so overrides cannot
  inject props into an otherwise empty biome.

Both arrays default empty (no behaviour change) and must be the same set on every `Generate` call over the
same world for tiling invariance to hold, exactly like every other input `PropScatter` reads.

## Companion foliage

`PropScatter.GenerateCompanions(field, hosts, config)` rings each matching host placement (a tree, say)
with a few small-foliage instances (a fern, a bush) in a jittered ring, Y resampled from the field.
Pure per-host: every value (count, ring angle/radius, kind, scale, yaw) hashes off the host's
centimetre-quantized world XZ plus per-channel salts, never the host's list index, so the result is
deterministic and tiling-invariant.

`CompanionConfig.HostKinds` selects which hosts grow companions: a host matches when its
`PropPlacement.Id` is listed in `HostKinds`, or when `HostKinds` is empty or absent, which now matches
every host placement. **This is a behavior-visible contract change**: earlier builds treated an empty
`HostKinds` as matching no host, so a companion layer left with an empty list silently grew nothing. A
document authored against the old behaviour that relied on an empty `HostKinds` staying inert now grows
companions on every host in the layer, so re-check any existing companion config with an empty
`HostKinds` before upgrading. A populated `HostKinds` list is unaffected: it still filters by exact
ordinal match against `PropPlacement.Id`.

Depends on `KhaozEngine.Primitives` and `KhaozEngine.Collision`. No render dependency, and that includes
the streamer: `TerrainStreamer`/`IChunkSink`/`ChunkBuildScheduler` are GPU-free and server-usable as they
stand. Add [KhaozEngine.Terrain.Render3D](../KhaozEngine.Terrain.Render3D) only for `Scene3DChunkSink`
(the GPU sink) and the chunk mesh builder. In the `Foundation` umbrella metapackage.

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
