# Terrain system design (`KhaozEngine.Terrain` + `KhaozEngine.Terrain.Render3D`)

Date: 2026-06-27
Status: approved design, ready for implementation plan
Program: MMO overworld render-scale track, sub-project 1 of 6 (the terrain foundation)

## Context

At `7.42.0` the engine's **server/authoritative-multiplayer track is complete**: sim,
replication, interest management, and the sharded cell grid all shipped (MMO Phases 0-3,
`KhaozEngine.Sharding` + `MmoServerSample`). What the MMO still lacks is the entire
**client-side world / render-scale track**. The roadmap already draws this line
(`docs/ROADMAP.md`): *"The render-scale track (frustum culling, world streaming, LOD,
tilemap) is a separate plan, not part of this [netcode] program."* That plan is unstarted.

`Render3D` today has glTF load (incl. skinned), basic instancing (`SceneInstances`), an iso
camera, ground decals, and the procedural chain solver. It has **no** terrain, LOD, world
streaming, prop scatter, or scale-normalizing asset pipeline.

This program builds the overworld vertical slice. The build spine is
**Terrain → Character + Camera → World streaming → Prop scatter/LOD**, with an asset-normalize
pipeline underneath the props. **This spec is the first sub-project: the terrain system.**
Everything else stands on it (streaming streams terrain chunks, props scatter onto terrain, the
character walks on terrain, dungeons are a flat-floor variant).

### Reference implementation

`https://github.com/levy-street/world-of-claudecraft` — a working, open-source, AI-built
MMO-like. It is Three.js, but its **world generation is engine-agnostic pure math** and is the
pattern we mine throughout this spec. Keep this link for future reference. Key files studied:
`src/sim/world.ts` (`terrainHeight`/`baseHeight`/`shapeAt`, lake/ridge features, the
coordinate-hash `generateDecorations` scatter), `src/render/terrain.ts` (chunked terrain, LOD,
0.3 u skirts, PBR splat material), `src/ui/map_terrain.ts` (the same height function reused for
the minimap). Its art is all CC0 kits (Quaternius, Kenney, KayKit, ambientCG, Poly Haven).

### Locked decisions (from brainstorming)

1. **Analytic field, not baked heightmaps.** Ground height comes from a deterministic
   `terrainHeight(x, z, seed)` function evaluated at runtime by both server and client. No
   terrain assets to stream, automatic server/client agreement. Authored heightmap patches (a
   hybrid model) are explicitly deferred.
2. **Authoritative server, visual client.** The server samples the field for collision/truth;
   the client evaluates the same field in plain `float` to render and predict. Tiny
   cross-platform float differences are invisible and the existing replication corrects them.
   No fixed-point / `DeterministicFp` constraint on terrain math.

## Architecture

Two packages, following the existing **render-free leaf + `.Render3D` companion** pattern
(as used by `Snapshot`/`Snapshot.Render3D` and `Telegraphs`/`Telegraphs.Render3D`). This keeps
the server headless: the sim references the leaf and never pulls in `Render3D`.

```
Primitives (leaf)
   └── KhaozEngine.Terrain            (field + Sample + noise + collision)  ← server/sim, Collision
          └── KhaozEngine.Terrain.Render3D   (chunked-LOD mesh + splat material)  ← client
                 └── Render3D
```

- `KhaozEngine.Terrain` → **Foundation** umbrella (render-free, broadly useful like `Collision`/`Simulation`).
- `KhaozEngine.Terrain.Render3D` → **Game3D** umbrella.

Rejected alternatives: a single `KhaozEngine.Terrain` that references `Render3D` (drags render
deps into the server, breaks the render-free-leaf rule); folding terrain into `Collision` or
`Render3D` (tangles boundaries).

## `KhaozEngine.Terrain` — the analytic field

Single entry point, composable internally. Modeled on Claudecraft's
`terrainHeight`/`baseHeight`/`shapeAt`.

```csharp
public sealed class TerrainField
{
    public TerrainField(TerrainConfig config);
    public float   SampleHeight(float x, float z);   // the one source of truth
    public Vector3 SampleNormal(float x, float z);    // finite-difference, for lighting/slope
    public BiomeId SampleBiome(float x, float z);     // for splat material + gameplay
    public float   WaterLevel { get; }
}

public sealed class TerrainConfig
{
    public int Seed;
    public float WaterLevel;
    public BiomeBand[] Biomes;          // designed regions along an axis
    public ITerrainFeature[] Features;  // lakes, ridges, flatten, ...
}
```

`SampleHeight` folds three layers in order (the Claudecraft pipeline):

1. **Biome shape** — `BiomeBand`s along the world give per-region hill-amplitude / base-height /
   biome id, **smoothstep-blended at boundaries** (their `shapeAt`). This is what produces
   designed regions (meadow → marsh → peaks) rather than uniform noise.
2. **Base noise** — fractal noise (`fbm`) × amplitude + base, plus a finer detail octave
   (their `baseHeight`).
3. **Features** — a list of `ITerrainFeature`, each `float Apply(float x, float z, float h)`:
   - `LakeFeature` — carves a basin toward water level inside a radius (the trick from the
     greybox clearing).
   - `RidgeFeature` — raises a gaussian wall along a line, **pierced by a pass** (smoothstep gap)
     so the wall reads as mountains, not a berm. The MMO's zone borders.
   - `FlattenFeature` — levels a hub/landmark region toward a target height.

```csharp
public interface ITerrainFeature { float Apply(float x, float z, float h); }
```

Two deliberate properties:

- **Stateless coordinate-hash noise**, not a sequential RNG. Height at `(x,z)` depends only on
  `(x, z, seed)`, so it is identical whether or not neighbouring cells are loaded — essential
  for the sharded streaming world, and the key refinement learned from Claudecraft (it uses
  `hash2(gx, gz, seed)`, not a per-iteration RNG).
- **Plain `float` math** per the authoritative-server decision. Noise/hash helpers live in
  `Terrain` for now (a value/Perlin-noise + `fbm` + coordinate-hash static); they could be
  promoted to `Primitives` later if reused — YAGNI for now.

### Collision wrapper

```csharp
public sealed class TerrainCollision
{
    public TerrainCollision(TerrainField field);
    public float GroundHeight(float x, float z);                 // = field.SampleHeight
    public bool  IsWalkable(float x, float z, float maxSlope);   // from SampleNormal
}
```

`Sharding`'s `CellSim` uses this each tick to keep entities on the ground and reject
out-of-bounds / too-steep moves. No new netcode — terrain feeds the sim that already exists.
`KhaozEngine.Collision` may reference `Terrain` for a terrain-aware collider; if that dependency
is awkward, the collider stays in `Terrain`.

## `KhaozEngine.Terrain.Render3D` — chunked-LOD mesh builder

The field is infinite and analytic; the renderer needs finite meshes that cull and scale.

- **Chunks** — meshed in fixed-size tiles (≈ 60 m, aligned so a `Sharding` `CellCoord` maps to a
  whole number of chunks). Each chunk is its own mesh **with a bounding volume** so frustum
  culling drops off-screen terrain.
- **LOD by distance** — chunk vertex density comes from distance to the camera (near dense, far
  coarse). The builder takes `(region, lodLevel)` and samples `TerrainField.SampleHeight` on
  that grid. A `PickLod(distance)` helper maps distance → tier.
- **Skirts** — each chunk drops a short vertical skirt (~0.3 m) at its edges to hide cracks where
  a dense chunk meets a coarse neighbour. This is what makes mismatched LODs seamless.
- **Material** — the builder bakes splat weights (grass/dirt/rock/sand/snow) per vertex from
  height + slope + biome into a vertex attribute. For this slice the material renders a
  **height/slope vertex-color ramp** (like the greybox Blender ramp). The **PBR splat *textures***
  (ambientCG albedo + normal) are a deliberately separate later sub-project; the weight attribute
  is plumbed now so that upgrade is drop-in.

Output is standard `Render3D` mesh data (`ModelVertex`/`MeshBuilder`) registered with `Scene3D`.

**Scope line:** this package delivers "build one chunk mesh at a given LOD, with skirts and
splat-weight vertices, plus a distance→LOD helper." *Which* chunks exist and *when* they rebuild
is the **World streaming** sub-project, not this one.

## Testing (all headless, per the engine's "new behaviour ships with a test" rule)

In `KhaozEngine.Tests`:

- **Field determinism** — same `(x, z, seed)` → same height across runs; x64 CI is the
  cross-platform net.
- **Composition** — biome blend is continuous across a boundary; `LakeFeature` lowers height
  toward water level inside its radius; `RidgeFeature` raises along the line but the pass dips;
  `FlattenFeature` levels its region.
- **Normals** — flat ground → up; a slope tilts correctly.
- **Mesh builder** (CPU geometry, no GPU device — per "no real device in unit tests") — vertex
  count per LOD tier, skirt present, chunk bounds correct, `PickLod` monotonic, mesh vertex
  heights equal `field.SampleHeight` at those points.
- **Collision** — `GroundHeight` equals the field; `IsWalkable` flips at the slope threshold.

## Scope

### In scope (this spec)

- `KhaozEngine.Terrain` (leaf): `TerrainField`, `TerrainConfig`, `BiomeBand`, `ITerrainFeature`
  + `LakeFeature`/`RidgeFeature`/`FlattenFeature`, `SampleHeight`/`SampleNormal`/`SampleBiome`,
  `WaterLevel`, stateless coordinate-hash noise/fbm helpers, `TerrainCollision`. Deterministic
  `float`.
- `KhaozEngine.Terrain.Render3D` (companion): chunked mesh builder + bounds, distance LOD +
  `PickLod`, skirts, per-vertex splat weights, height/slope vertex-color ramp material, `Scene3D`
  integration.
- Headless tests for both.
- Full release ritual: version bump, `CHANGELOG.md` + `CHANGENOTES.md`, README package
  catalog + repo-layout, `CLAUDE.md` package map + umbrella descriptions (`Terrain` → Foundation,
  `Terrain.Render3D` → Game3D), `docs/CONSUMERS.md` table, `docs/USING-KHAOZENGINE.md` usage
  section, `check-doc-versions.sh` declarations, `dotnet pack` to `local-feed`, tag.

### Out of scope (named so they are not forgotten — later sub-projects)

- **World streaming / culling** — which chunks load/unload, rebuild cadence, tie-in to `Sharding`
  cell load. (sub-project 3)
- **Prop scatter** (coordinate-hash) + **GPU instancing** + **LOD / impostors** + **asset
  scale-normalize/validate pipeline** (the 1.8 m rule — kit glTF assets bake their own object
  scale; `transform_apply` + normalize to metres + validate against a 1.8 m reference). (sub-projects 1 + 4)
- **Character controller + camera follow.** (sub-project 6)
- **PBR splat textures** (ambientCG) + normal maps — terrain *material* upgrade.
- **Water rendering** — the slice uses a flat plane at `WaterLevel`; a real water shader later.
- **Authored heightmap patches** (the hybrid model) — deferred per the analytic decision.

## The overworld program (for orientation)

1. Asset/render foundation — glTF kit ingest + scale-normalize & validate + GPU instancing + LOD/impostors.
2. **Terrain — this spec.**
3. World streaming / culling — load/unload chunks around the player, frustum + distance cull, wired to `Sharding`.
4. Prop scatter — deterministic coordinate-hash scatter onto terrain, instanced.
5. Procedural dungeon generator — modular kit + rooms/corridors/BSP, deterministic via `DeterministicRng`, instanced (flat-floor variant; a parallel track once terrain lands).
6. World-client glue — character controller + camera follow + render terrain/props/replicated entities.

## Open items to confirm during implementation

- Exact chunk size and `CellCoord`-to-chunk ratio (≈ 60 m is a starting point; align to the
  `Sharding` cell size in use).
- Number of LOD tiers and the distance thresholds (start with 3 tiers; tune against the headless
  vertex-count tests, not by eye).
- Whether `TerrainCollision` lives in `Terrain` or `Collision` (decide by which dependency edge
  is cleaner).
- Greybox parity: the analytic field should be able to reproduce the
  `tools/blender/make_clearing_greybox.py` clearing (gentle mountains + lake basin) as a sanity
  check that the C# field matches the prototype.
