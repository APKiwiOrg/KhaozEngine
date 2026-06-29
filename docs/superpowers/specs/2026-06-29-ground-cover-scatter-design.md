# Ground-cover scatter + understory companions (7.71.0)

Two additive capabilities on the existing streamed opaque-instanced prop path, so a dense short-radius
ground-cover layer rides alongside the sparse tree layer and trees are dressed at the base instead of
standing on bare ground. Generic terrain dressing every game wants, so it lives in the engine, not in
bespoke game code.

Origin handoff: `Ruinborne/docs/superpowers/specs/2026-06-29-engine-ground-cover-scatter-handoff.md`.
This is the engine-side design that makes that handoff concrete.

## Scope and constraints

- **Additive minor: 7.71.0** (current line 7.70.0). The concurrent physics worktree plans a **major
  8.0.0**, so the version lines do not collide. Ordering rule: this lands before physics's 8.0.0;
  if physics merges first, rebase this to 8.1.0 (the second merge must not regress the shared
  `<KhaozEngineVersion>` line).
- Touches **`KhaozEngine.Terrain`** (the companion primitive) and **`KhaozEngine.Terrain.Render3D`**
  (the multi-layer sink). Both ship in the `Game3D` umbrella.
- **No new material.** Foliage is solid low-poly geometry with a flat `baseColorFactor`, drawn on the
  opaque-only prop path. No alpha-cutout, no transparency pass.
- **No protocol change. No collision change.** Foliage is render-only: any foliage id with no collider
  (consumers never add one) never reaches the server, client prediction, or collision. Zero netcode
  risk by construction.

## Non-goals (v1)

- Alpha-cutout grass cards / billboards (needs a transparent/cutout material pass).
- Wind sway (vertex-animation shader).
- Distance alpha-fade at the cull boundary (needs per-instance alpha in the instanced path). v1 relies
  on a modest draw radius + small props.

## Current state (confirmed from engine code)

- `Scene3DChunkSink` (`KhaozEngine.Terrain.Render3D/Scene3DChunkSink.cs`) holds exactly ONE
  `ScatterConfig`, ONE `_propMeshes` dict, ONE `_propDrawRadius`. `Load` builds the chunk mesh and sets
  `Props = ScatterFor(coord) = PropScatter.Generate(field, scatter, ChunkGrid.AreaOf(coord, chunkSize))`.
  `Draw` does `_scene.DrawProps(load.Props, _propMeshes, focus, _propDrawRadius)` per loaded chunk.
  Props are LOD-independent (kept across `ReLod`). `ChunkLoad.Props` is read by nobody outside the sink.
- `PropScatter.Generate(field, config, area)` (`KhaozEngine.Terrain/PropScatter.cs`) is a pure,
  render-free, deterministic coordinate-hash scatter: every placement for a grid cell depends only on
  `(cell, seed)`; cells are included by their un-jittered centre on half-open `[Min, Max)` intervals,
  so `Generate` over a large area equals the union over its tiles (streaming-ready).
- `PropRenderer.DrawProps`/`Queue` (`KhaozEngine.Terrain.Render3D/PropRenderer.cs`) is the instanced
  draw: horizontal (XZ) distance cull to `drawRadius`, world matrix `Scale * RotY * Translate`. No
  distance fade, no per-instance alpha.
- The hash primitive is `TerrainNoise.Hash2(int gx, int gz, int seed) -> [-1, 1)`. `PropScatter` maps
  it to `[0, 1)` via `Hash01` with per-channel XOR salts.

## Feature 1: multi-layer scatter in the chunk sink

Generalize `Scene3DChunkSink` to N prop layers, each with its own scatter config, mesh set, and draw
radius. The short per-layer draw radius is the point: ground cover only needs ~35-45 m (you only see
grass up close), trees draw out to ~90 m; the short radius is what keeps a dense layer affordable.

```csharp
public readonly struct PropLayer
{
    public ScatterConfig? Scatter { get; }            // set for a scatter layer
    public CompanionConfig? Companions { get; }        // set for a companion layer
    public int HostLayerIndex { get; }                 // companion layer: index of its host scatter layer
    public IReadOnlyDictionary<string, MeshHandle> Meshes { get; }
    public float DrawRadius { get; }
    public bool IsCompanion => Companions != null;

    public static PropLayer Scatter(ScatterConfig scatter,
        IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius);
    public static PropLayer Companions(int hostLayerIndex, CompanionConfig companions,
        IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius);
}
```

A companion layer needs its OWN meshes and a SHORT draw radius distinct from its host trees, so it must
be its own layer (not a flag on the host layer). It is a tagged `PropLayer`: a *scatter* layer
(`Scatter` set) or a *companion* layer (`Companions` + `HostLayerIndex` set), built via the two
factories. One list, one iteration in `Load`/`Draw`; the host reference is explicit.

```csharp
// New ctor (additive).
public Scene3DChunkSink(Scene3D scene, TerrainField field, IReadOnlyList<PropLayer> layers,
                        float chunkSize, Scene3D.SplatMaterialHandle material = default)

// Existing ctor stays, byte-identical, delegating to a single scatter PropLayer:
public Scene3DChunkSink(Scene3D scene, TerrainField field, ScatterConfig scatter,
                        IReadOnlyDictionary<string, MeshHandle> propMeshes, float chunkSize,
                        float propDrawRadius, Scene3D.SplatMaterialHandle material = default)
```

- `ChunkLoad` carries `IReadOnlyList<PropPlacement>[] LayerProps` (one entry per layer). `Props` stays
  as a read-only alias for `LayerProps[0]` so nothing breaks (no external reader exists anyway).
- `Load`: build + upload the chunk mesh (unchanged), then compute `LayerProps` via `ScatterLayersFor`.
- `ReLod`: keep `LayerProps` (LOD-independent, unchanged). `Unload`: unchanged.
- `Draw(focus)` loops layers: `scene.DrawProps(load.LayerProps[i], layers[i].Meshes, focus, layers[i].DrawRadius)`.

### Companion wiring + the exactly-once property

In `Load`, after scattering the scatter-layers for the chunk, each companion layer is derived from its
host layer's placements *for that same chunk* via `PropScatter.GenerateCompanions`, and stored as its
own entry in `LayerProps`. Because each host lives in exactly one chunk (half-open cell intervals), its
companions are emitted exactly once, even when they spill geometrically into a neighbour chunk (fine:
`DrawProps` culls by distance from focus, not by chunk bounds). Companions are NOT re-derived per query
area (that would double-emit at seams).

## Feature 2: understory companions primitive

A pure, render-free, headless-testable primitive in `KhaozEngine.Terrain`.

```csharp
public sealed class CompanionConfig
{
    public int Seed;
    public string[] HostKinds = Array.Empty<string>();   // host ids that spawn companions (the tree ids)
    public PropKind[] Kinds = Array.Empty<PropKind>();    // weighted companion kit ids (bush/fern/...)
    public int CountMin = 2, CountMax = 4;
    public float RadiusMin = 0.6f, RadiusMax = 1.8f;      // ring offset from host base (metres)
    public float ScaleMin = 0.7f, ScaleMax = 1.1f;
    public float? MaxHeight;                              // same off-mountain exclusion as the host layer
}

// For each host whose Id is in HostKinds, emit Count companions in a jittered ring around (X,Z),
// Y resampled from the field, count/kind/angle/radius/scale/yaw from independent salted hashes.
public static IReadOnlyList<PropPlacement> GenerateCompanions(
    TerrainField field, IReadOnlyList<PropPlacement> hosts, CompanionConfig config);
```

### The per-host hash key (the one place the handoff was loose)

The handoff says companions hash "off the host's cell," but `GenerateCompanions` only receives
`PropPlacement`s: no cell coords, and the jittered host position is not cleanly invertible to a cell
without coupling to the host scatter's grid params. **Resolution: key each host's hashes off its
quantized world XZ (centimetre integers), never its list index.**

```csharp
int hx = (int)MathF.Round(host.X * 100f);
int hz = (int)MathF.Round(host.Z * 100f);
// per channel: Hash01(hx, hz, seed ^ salt, companionIndex)
```

This is:
- **Deterministic** - a pure function of the host's position, so the same host always yields the same
  companions across calls.
- **Tiling-invariant** - host positions are bit-identical across tilings (`PropScatter` guarantees a
  cell's placement is computed identically regardless of query area), and each host maps independently
  to its companions, so the union of companions over sub-tiles equals companions over the whole.
  Using the list index would break this (a host's index differs between a tile and the whole), so the
  index is never used.
- **Decoupled** - needs no knowledge of the host scatter's `CellSize`/`Jitter`. Centimetre quantization
  is collision-free for any grid scatter (distinct hosts are always >> 1 cm apart) and safe in `int`
  range for any reasonable world.

### Per-host emission

For each host with `Id` in `HostKinds`:
1. `count = CountMin + floor(Hash01(count-channel) * (CountMax - CountMin + 1))`, giving an integer in
   `[CountMin, CountMax]` (Hash01 is `[0, 1)` so the top of the range is never overshot). If
   `CountMax < CountMin` or `Kinds` is empty, emit none.
2. For each companion `j` in `[0, count)`:
   - `angle = Hash01(angle, j) * Tau`
   - `radius = RadiusMin + Hash01(radius, j) * (RadiusMax - RadiusMin)`
   - `x = host.X + radius * cos(angle)`, `z = host.Z + radius * sin(angle)`
   - `variant = weighted pick over Kinds with Hash01(kind, j)`; `id = Kinds[variant].Id`
   - `scale = ScaleMin + Hash01(scale, j) * (ScaleMax - ScaleMin)`
   - `yaw = Hash01(yaw, j) * Tau`
   - `y = field.SampleHeight(x, z)`; if `MaxHeight is cap && y > cap`, skip this companion.
   - emit `PropPlacement(id, x, y, z, scale, yaw, variant)`.

Each channel has its own XOR salt; the companion index `j` is mixed into the hash so the N companions
of one host are uncorrelated. Hosts whose `Id` is not in `HostKinds` emit nothing. The weighted pick
reuses `PropScatter`'s existing weighted-pick logic (shared, not duplicated).

## Headless test seam

`Draw` needs a GPU `Scene3D`, but the existing streamer test already builds the sink with `scene: null!`
and calls the internal `ScatterFor(coord)`. Generalize:

- `internal IReadOnlyList<PropPlacement>[] ScatterLayersFor(ChunkCoord coord)` - scatters all scatter
  layers, then derives all companion layers from their host layer's placements; no GPU.
- `internal IReadOnlyList<PropPlacement> ScatterFor(ChunkCoord coord)` stays as `ScatterLayersFor(coord)[0]`
  (back-compat for the existing test).

All Feature-1/Feature-2 tests run headless against these and against `PropScatter.GenerateCompanions`.

## Tests (headless, `KhaozEngine.Tests/Terrain`)

1. **Multi-layer independence** - a sink with two scatter layers (distinct configs + meshes) produces
   per-layer placements matching `PropScatter.Generate` for each config over the chunk area.
2. **Single-layer back-compat** - the existing single-layer ctor yields `ScatterFor(coord)` identical
   to the pre-change result (and to `PropScatter.Generate` for that config).
3. **Companion determinism** - `GenerateCompanions` over the same hosts returns identical placements
   across calls (id, X/Z, Y, scale, yaw, variant).
4. **Companion tiling-invariance (core property)** - companions derived from hosts scattered over an
   area equal the union of companions derived per sub-tile (re-tiling the host query does not change
   the companion set). Driven through the sink: companions from `ScatterLayersFor` over a block of
   chunks equal companions from each chunk, with the host set coming from `PropScatter`.
5. **Ring + count bounds** - each companion sits within `[RadiusMin, RadiusMax]` of its host (XZ) and
   per-host count is within `[CountMin, CountMax]`.
6. **Host-kind filter** - only hosts whose `Id` is in `HostKinds` spawn companions; others spawn none.
7. **Kind membership + MaxHeight** - companion ids are all in `Kinds`; with a `MaxHeight` cap on a
   sloped field, no companion sits above the cap.
8. **No collision coupling** - companion/ground-cover placements are render-only; the sink never feeds
   them to any collider path (assert by construction: the sink has no collision surface; companions are
   plain `PropPlacement`s like any cosmetic prop).

## Docs to sweep on release

Per the engine ritual, after the code + version bump + CHANGELOG:

- `CHANGELOG.md` - the two additions + the non-goals.
- `Directory.Build.props` `<KhaozEngineVersion>` -> 7.71.0; the three guard-checked declarations
  (`docs/CONSUMERS.md` engine current version, `docs/ROADMAP.md` current released version, the
  `README.md` `<PackageReference>` example).
- `CLAUDE.md` package map - the `Terrain`/`Terrain.Render3D` descriptions gain `GenerateCompanions`/
  `CompanionConfig` and the multi-layer `Scene3DChunkSink` + `PropLayer`.
- `docs/USING-KHAOZENGINE.md` - a usage note for the multi-layer sink + companions (new public API).
- `README.md` package catalog if any package summary references the sink/scatter.

Pack to `~/KhaozEngine/local-feed`; commit; `git tag v7.71.0`; hold/batch the push per the engine
policy (confirm before pushing). Ruinborne adopts afterward per the handoff's adoption section.
