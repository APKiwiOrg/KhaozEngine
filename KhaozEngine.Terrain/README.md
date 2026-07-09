# KhaozEngine.Terrain

Render-free analytic terrain. `TerrainField` is the single source of truth for ground height, and it is
stateless: the height at `(x, z)` depends only on `(x, z, seed)`, never on which neighbour chunks are
loaded, so the authoritative server and the visual client sample the same field and streamed chunks line
up regardless of load order. Plain `float` math throughout.

## Types

- **`TerrainField`** - `SampleHeight` folds three layers in order: biome-band shaping (smoothstep
  blended), base coordinate-hash fractal noise, then an ordered feature list. Also `SampleNormal`
  (central finite difference), `SampleBiome` (dominant band), and `WaterLevel`.
- **`TerrainConfig`** / **`BiomeBand`** / **`BiomeId`** - authoring inputs. Defaults give a single
  gentle meadow band, supply `Biomes` (designed regions along world Z) and `Features` for more.
- **`TerrainNoise`** - stateless coordinate-hash noise (`Hash2`, `ValueNoise`, `Fbm`, `Turbulence`,
  `SmoothStep`). Every function depends only on its arguments plus the seed, no `Random`.
- **`ITerrainFeature`** - a pure, composable height modifier applied in list order. Ready-made:
  **`LakeFeature`** (carved basin), **`RidgeFeature`** (gaussian wall with a pass gap),
  **`FlattenFeature`** (levelled hub pad), **`RimFeature`** + `RimPass` (enclosing mountain wall with
  corridors out, the diegetic world border).
- **`TerrainCollision`** - ground-follow over a field: `GroundHeight`, `GroundNormal` (feed both to
  `CharacterMovement.Step` so steep terrain gates movement), and `IsWalkable(x, z, maxSlope)`.
- **`TerrainPresets`** - `Clearing()` (meadow + mountains + lake) and `BoundedClearing()` (meadow
  ringed by a rim wall with one pass out).
- **`PropScatter`** (+ `PropPlacement`) - deterministic coordinate-hash prop placement, and
  **`PropColliders`** / **`PropSurfaces`** turn those placements into `Collision` collider/surface
  sets that line up exactly with the rendered props (tiled build equals whole-area build).

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

Depends on `KhaozEngine.Primitives` and `KhaozEngine.Collision`. No render dependency: add
[KhaozEngine.Terrain.Render3D](../KhaozEngine.Terrain.Render3D) to mesh and stream it. In the
`Foundation` umbrella metapackage.

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
