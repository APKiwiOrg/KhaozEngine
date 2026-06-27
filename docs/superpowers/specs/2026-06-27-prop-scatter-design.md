# Prop scatter + asset pipeline design (`AssetManifest` + `PropScatter` + instanced props)

Date: 2026-06-27
Status: approved design, ready for implementation plan
Program: MMO overworld render-scale track, sub-project 4 (the forest)

## Context

Terrain shipped at `7.43.0` and the walkable slice at `7.44.0` — you can walk a 1.8 m capsule across
bare terrain in a window. This sub-project populates it: deterministic coordinate-hash **prop
scatter** + a glTF **asset pipeline** (scale-normalize + validate, the 1.8 m rule) + **GPU
instancing**, so you walk through the forested clearing we prototyped in Blender. It also banks the
reusable asset foundation and proves instancing handles the poly count (a ~275-tree forest renders in
a handful of draws, not 275).

Specs for the prior sub-projects: `docs/superpowers/specs/2026-06-27-terrain-system-design.md`,
`docs/superpowers/specs/2026-06-27-walkable-slice-design.md`.
Reference repo for the program: `https://github.com/levy-street/world-of-claudecraft` — its
`src/sim/world.ts` `generateDecorations` is the scatter pattern; its props are CC0 kit glTF instanced
off a placement list.

### Existing engine pieces (reused, not rebuilt)

- **Instancing**: `Scene3D` uploads a mesh once → `MeshHandle`; `SceneInstances` queues many
  instances (`Begin`/`Add(handle, world, tint)`) into the hardware instance-data path. A forest =
  upload each tree once, queue N matrices. No new GPU work needed.
- **glTF loading**: `GltfLoader.Load` / `LoadWithMaterial` (SharpGLTF). **No meshopt support** — the
  Quaternius kit is meshopt-compressed and will not load raw. Consequence below.
- **Coordinate-hash noise**: `TerrainNoise.Hash2` (shipped in `KhaozEngine.Terrain`) is the
  deterministic hash the scatter reuses.

### Locked decisions (from brainstorming)

1. **Asset manifest** drives scale + provenance. A JSON file per kit, `{ id, file, heightMeters,
   source, license }`. The loader normalizes each asset to its declared height and validates it
   against the 1.8 m reference. The manifest is also the CC0 attribution/provenance record.
2. **Require pre-decompressed glTF.** The engine does not decode `EXT_meshopt_compression`; kit
   assets are decompressed offline (`gltf-transform`) as an ingest step. A meshopt decoder in the
   engine is deferred (YAGNI).
3. **Scatter is render-free placement data** in the `Terrain` leaf (it only needs the field + the
   coordinate hash), so it is server-usable later and streaming-ready.
4. **Reuse existing instancing**; distance-cull only. Mesh-LOD / impostors deferred.
5. **Commit a tiny CC0 asset set** into the sample (with manifest + CREDITS) so the forest runs
   out-of-the-box. ~1-2 MB of decompressed `.glb`; small enough for a plain commit (no LFS).
6. **Engine-first placement**: asset pipeline in `Render3D`, scatter in `KhaozEngine.Terrain`,
   instanced render helper in `KhaozEngine.Terrain.Render3D`; only the sample forest is throwaway.

## Components

### 1. Asset pipeline — `KhaozEngine.Render3D` (beside `GltfLoader`)

- **`AssetManifest`** — loads/parses the JSON manifest into entries
  `{ string Id, string File, float HeightMeters, string Source, string License }`.
- **`LoadProp(entry)`** — `GltfLoader.Load(entry.File)` (decompressed glTF) → measure the mesh
  bounding height → scale uniformly so height == `entry.HeightMeters` → set origin to the base (feet
  on the ground, the `transform_apply` fix from the Blender work). Returns the normalized mesh.
- **Validation** — throw (or warn) if a normalized asset's height is implausible relative to the
  1.8 m reference (e.g. outside a configurable plausible band). A mis-scaled asset fails loudly, not
  silently — the documented rule.

### 2. Prop scatter — `KhaozEngine.Terrain` (leaf, render-free)

```csharp
public readonly struct PropPlacement { public string Id; public float X, Z, Y, Scale, Yaw; public int Variant; }

public static class PropScatter
{
    public static IReadOnlyList<PropPlacement> Generate(TerrainField field, ScatterConfig config, RectArea area);
}
```

- Deterministic **coordinate-hash** (reuses `TerrainNoise.Hash2`): a grid + per-cell jitter,
  per-biome density + kind mix, exclusions (below `WaterLevel`, inside a clearing/road radius),
  per-instance `Scale`/`Yaw`/`Variant` from independent hashes. Exactly Claudecraft
  `generateDecorations`.
- `Y` comes from `field.SampleHeight`. Generating over a `RectArea` makes it **streaming-ready**:
  the placement for a coordinate is identical regardless of which neighbours are loaded.
- `ScatterConfig` is data-driven (per-biome rules, kind→id mix, density, exclusion radii).

### 3. Instanced render helper — `KhaozEngine.Terrain.Render3D`

- Given placements + a `Dictionary<id, MeshHandle>` + a draw radius around a focus point, queue
  `SceneInstances.Add(handle, TRS(placement), tint)` for in-range placements and **skip out-of-range**
  (distance cull). Pure use of existing instancing; testable headless (assert the queued set).

### 4. Sample — extend `TerrainWalkSample`

- Add a tiny committed CC0 kit (a few decompressed pine/oak/rock `.glb` + `props.manifest.json` +
  `CREDITS.md`) under the sample. Load via the pipeline (one `MeshHandle` per id).
- `PropScatter.Generate` over the clearing field; each frame queue instances within a draw radius of
  the player. Walk through the forest.

## Data flow

```
TerrainField + ScatterConfig → PropScatter.Generate → PropPlacement[]   (deterministic, render-free)
AssetManifest → LoadProp (normalize + validate)     → MeshHandle per id
per frame: placements within draw radius → SceneInstances.Add(handle, TRS) → Scene3D draws instanced
```

## Testing (headless, in `KhaozEngine.Tests`)

Build test glTF in-process via SharpGLTF (as the existing `GltfLoader` tests do) — no real kit asset
in unit tests.

- **AssetManifest** — parses entries; missing/garbled file errors cleanly.
- **LoadProp** — a known-height test mesh normalizes to its declared `HeightMeters`; origin ends at
  the base; **validation throws on an implausible declared-vs-actual size** (the 1.8 m guard).
- **PropScatter** — determinism: same `(seed, area)` → identical placements regardless of query
  order or area tiling (the coordinate-hash property); density within tolerance for a biome;
  exclusions respected (nothing below water / inside the clearing radius); `placement.Y` ==
  `field.SampleHeight`.
- **Render helper** — in-range placements queued, out-of-range culled (the `SceneInstances` queue is
  GPU-free testable).

## Scope

### In scope

- `AssetManifest` + `LoadProp` + validation (`Render3D`).
- `PropScatter` + `PropPlacement` + `ScatterConfig` (`KhaozEngine.Terrain`).
- Instanced render helper + distance cull (`KhaozEngine.Terrain.Render3D`).
- Tiny committed CC0 kit + `props.manifest.json` + `CREDITS.md` in the sample; forest in
  `TerrainWalkSample`.
- Headless tests.
- Release: **minor** bump (additive API in three existing packages — no new *package*). Update
  `Directory.Build.props`, `CHANGELOG.md` + `CHANGENOTES.md`, the 3 guard declarations,
  `docs/USING-KHAOZENGINE.md` (usage section for the manifest/scatter/instanced-props), and an
  ingest note documenting the offline `gltf-transform` decompress step. End with the sample boot
  command.

### Out of scope (named so they are not forgotten)

- **meshopt decoder** in the engine — require decompressed glTF (offline `gltf-transform`).
- **Mesh-LOD / impostors** for distant props — distance-cull only here.
- **PBR splat textures** for terrain (the separate terrain-material upgrade).
- **Prop / obstacle collision** — props are visual; terrain ground-clamp only.
- **Chunk streaming** — props ride the fixed grid; `PropScatter` is already streaming-ready for when
  streaming lands (sub-project: world streaming).
- **Animated props / creatures** — needs glTF animation-clip playback (a future feature).

## Open items to confirm during implementation

- Manifest format details (JSON shape, relative-path resolution from the manifest location).
- The plausible-size band for validation (e.g. 0.3 m – 80 m, configurable per category).
- `ScatterConfig` defaults that reproduce the greybox clearing's forest ring (density, clearing
  radius, kind mix) — parity with `tools/blender/make_clearing_greybox.py` is the visual target.
- Draw radius + how many instances that implies; confirm the instanced draw path batches by mesh.
- Exact CC0 assets to commit (a few Quaternius pines + oaks + rocks, decompressed) and their
  declared `heightMeters`.

## The overworld program (for orientation)

1. Asset/render foundation — folded into this sub-project (manifest + normalize + instancing).
2. ✅ Terrain — `7.43.0`.
3. ✅ Walkable slice — `7.44.0`.
4. **Prop scatter + asset pipeline — this spec.**
5. World streaming / culling — load/unload chunks (and scatter per cell) around the player, wired to `Sharding`.
6. Procedural dungeon generator — parallel track.

(Animated characters/creatures need a glTF animation-clip-playback feature, surfaced during the walkable slice.)
