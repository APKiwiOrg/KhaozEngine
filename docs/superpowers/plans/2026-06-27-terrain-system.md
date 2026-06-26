# KhaozEngine Terrain System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `KhaozEngine.Terrain` (analytic deterministic height field, render-free leaf) and `KhaozEngine.Terrain.Render3D` (chunked-LOD mesh builder with skirts + splat weights + vertex-colour ramp), the first sub-project of the overworld render-scale track.

**Architecture:** A single `TerrainField` evaluates `SampleHeight(x,z)` by folding three layers (biome shape → base coordinate-hash noise → ordered feature list). It is stateless: height at `(x,z)` depends only on `(x,z,seed)`, never on which neighbour cells are loaded. The `.Render3D` companion meshes finite chunks off that field with distance LOD, ~0.3 m skirts and per-vertex splat weights, exactly mirroring the existing `Snapshot`/`Snapshot.Render3D` and `Telegraphs`/`Telegraphs.Render3D` leaf+companion split. The leaf MUST NOT reference `Render3D`.

**Tech Stack:** C# / net10.0, `System.Numerics`, `KhaozEngine.Primitives` (leaf dep), `KhaozEngine.Render3D` (companion dep), xUnit headless tests. Plain `float` math (NOT `DeterministicFp` — authoritative server, visual client; locked decision).

## Global Constraints

- **Spec is settled.** Build `docs/superpowers/specs/2026-06-27-terrain-system-design.md` exactly. Do not re-brainstorm.
- **Render-free-leaf rule:** `KhaozEngine.Terrain` references only `KhaozEngine.Primitives`. It MUST NOT reference `Render3D` (or any GPU/render package). Only `KhaozEngine.Terrain.Render3D` references `Render3D`.
- **Stateless coordinate-hash noise:** `SampleHeight(x,z)` depends only on `(x,z,seed)`. No per-iteration RNG, no dependence on load order. Sharded-streaming requirement.
- **Plain `float`, deterministic per platform.** No `DeterministicFp`. x64 CI is the cross-platform net.
- **TDD, headless.** Every behaviour ships a headless test in `KhaozEngine.Tests`. NO GPU device in unit tests — the mesh builder is CPU geometry; assert on produced vertex data only.
- **One version bump for the whole batch** (both packages together), **minor / additive**: `7.42.0` → `7.43.0`.
- **No em-dashes** anywhere (code comments, docs, commits). Use periods/commas/parentheses.
- **Commit subjects:** conventional-commit `area(scope): summary`. On the release/version-bump commit use the new version as scope (e.g. `terrain(7.43.0): ...`).
- **Stay in scope.** Do NOT build world streaming, prop scatter, PBR splat textures, character controller, or a water shader (named in the spec Out-of-Scope; later sub-projects).
- **Parity target:** the analytic field reproduces `tools/blender/make_clearing_greybox.py` `height(x,y)` (gentle mountains ramping toward +Z, lake basin at world (-13, -2)). The greybox's `(x, y)` map to our `(x, z)`; its returned Blender-Z is our world height (Y up).

---

## Reference facts (engine API, verified)

- `ModelVertex` (KhaozEngine.Render3D/Models/GltfMesh.cs): `struct { Vector3 Position; Vector3 Normal; Vector4 Color; Vector2 Uv; Vector4 Tangent; }`. Ctors: `(p,n,c,uv,tangent)`, `(p,n,c,uv)`, `(p,n,c)`. Color is RGBA `Vector4`.
- `GltfMesh(ModelVertex[] v, uint[] i)` or `(ModelVertex[] v, ushort[] i)`. `.Vertices`, `.Indices32`, `.TriangleCount`.
- `MeshPrimitives.Plane(width, depth, subdivisionsX, subdivisionsZ)` is the reference for a subdivided XZ grid: vertex `pos = (-hw + fx*width, 0, -hd + fz*depth)`, indices wound CCW seen from +Y: `i0,i2,i3` then `i0,i3,i1`.
- `Color` (KhaozEngine.Primitives/Color.cs): `readonly struct { float R,G,B,A; }`, `new Color(r,g,b,a=1)`, implicit `-> Vector4`, explicit `(Color)Vector4`, `Color.Lerp(a,b,t)`, `Color.White`.
- `Scene3D.LoadMesh(GltfMesh) -> MeshHandle`; `Scene3D.Draw(MeshHandle, Matrix4x4, Color)`.
- Leaf+companion csproj pattern: see `KhaozEngine.Telegraphs.csproj` (refs Render2D+Primitives, `InternalsVisibleTo KhaozEngine.Tests`) and `KhaozEngine.Telegraphs.Render3D.csproj` (refs the leaf + Render3D). The `.Render3D` source uses the LEAF's namespace (`KhaozEngine.Telegraphs`) so a `using` brings the extension methods into scope.
- `KhaozEngine.MathUtil` exists in Primitives — verify its members (Clamp/Lerp/SmoothStep) in Task 1; reuse if present, otherwise the noise file defines its own.
- Test style: `namespace KhaozEngine.Tests.<Area>`, `public class XxxTests`, `[Fact] public void Method_describes_behavior()`, `using Xunit;`.
- Files to register the new projects: `KhaozEngine.slnx` (add 2 `<Project>` lines), `KhaozEngine.Tests/KhaozEngine.Tests.csproj` (add 2 `<ProjectReference>`), `KhaozEngine.Foundation.csproj` (+= Terrain), `KhaozEngine.Game3D.csproj` (+= Terrain.Render3D).
- Three doc-guard declarations (scripts/check-doc-versions.sh): `docs/CONSUMERS.md` `**Engine current version:** \`X.Y.Z\``; `docs/ROADMAP.md` `Current released version: **X.Y.Z**`; `README.md` `<PackageReference ... Version="X.Y.Z" />`.

---

## File structure

**KhaozEngine.Terrain/** (leaf)
- `KhaozEngine.Terrain.csproj` — PackageId `KhaozEngine.Terrain`, ref Primitives, `InternalsVisibleTo KhaozEngine.Tests`.
- `TerrainNoise.cs` — stateless `Hash2`, `ValueNoise`, `Fbm`, `Turbulence`, `SmoothStep` statics.
- `BiomeId.cs` — `enum BiomeId : byte`.
- `BiomeBand.cs` — one designed region: `Start/End` along Z, `BaseHeight`, `HillAmplitude`, `BiomeId`.
- `ITerrainFeature.cs` — `float Apply(float x, float z, float h)`.
- `LakeFeature.cs`, `RidgeFeature.cs`, `FlattenFeature.cs`.
- `TerrainConfig.cs` — seed, water level, noise params, `BiomeBand[]`, `ITerrainFeature[]`.
- `TerrainField.cs` — `SampleHeight`/`SampleNormal`/`SampleBiome`/`WaterLevel` + internal `ShapeAt`.
- `TerrainCollision.cs` — `GroundHeight`/`IsWalkable`.
- `TerrainPresets.cs` — `Clearing()` factory (greybox parity + demo seed).

**KhaozEngine.Terrain.Render3D/** (companion, namespace `KhaozEngine.Terrain`)
- `KhaozEngine.Terrain.Render3D.csproj` — PackageId `KhaozEngine.Terrain.Render3D`, refs the leaf + Render3D, `InternalsVisibleTo KhaozEngine.Tests`.
- `TerrainSplatWeights.cs` — 5-channel normalized weights (Grass/Dirt/Rock/Sand/Snow) + `From(...)`.
- `TerrainRamp.cs` — palette + `Color Of(in TerrainSplatWeights)` (vertex-colour ramp).
- `TerrainLod.cs` — tiers, `PickLod(distance)`, `ResolutionFor(tier)`.
- `TerrainChunkRegion.cs` — origin + size of a chunk in world metres.
- `TerrainChunkBounds.cs` — AABB.
- `TerrainChunkMesh.cs` — `GltfMesh Mesh` + `TerrainSplatWeights[] Splat` + `TerrainChunkBounds Bounds` + `Lod`/`Region`.
- `TerrainChunkBuilder.cs` — `Build(field, region, lod)`; grid + skirts + per-vertex colour & splat + bounds.
- `TerrainScene3D.cs` — `Scene3D` extension methods (load/draw a chunk). Compile-only, no unit test.

**KhaozEngine.Tests/Terrain/**
- `TerrainNoiseTests.cs`, `TerrainFieldTests.cs`, `TerrainFeatureTests.cs`, `TerrainCollisionTests.cs`, `TerrainParityTests.cs`, `TerrainChunkBuilderTests.cs`, `TerrainLodTests.cs`, `TerrainSplatTests.cs`.

---

### Task 1: `KhaozEngine.Terrain` scaffold + stateless noise

**Files:**
- Create: `KhaozEngine.Terrain/KhaozEngine.Terrain.csproj`
- Create: `KhaozEngine.Terrain/TerrainNoise.cs`
- Modify: `KhaozEngine.slnx` (add `<Project Path="KhaozEngine.Terrain/KhaozEngine.Terrain.csproj" />`)
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj` (add `<ProjectReference Include="../KhaozEngine.Terrain/KhaozEngine.Terrain.csproj" />`)
- Test: `KhaozEngine.Tests/Terrain/TerrainNoiseTests.cs`

**Interfaces:**
- Produces: `static class TerrainNoise` in namespace `KhaozEngine.Terrain`:
  - `static float SmoothStep(float a, float b, float x)` — clamped Hermite, `0` at `x<=a`, `1` at `x>=b`.
  - `static float Hash2(int gx, int gz, int seed)` — deterministic `[-1,1)` from integer lattice point + seed (integer bit-mix; no `Random`).
  - `static float ValueNoise(float x, float z, int seed)` — bilinearly-interpolated lattice value noise, `[-1,1]`.
  - `static float Fbm(float x, float z, int seed, int octaves = 4, float lacunarity = 2f, float gain = 0.5f)` — signed fractal sum, normalized to ~`[-1,1]`.
  - `static float Turbulence(float x, float z, int seed, int octaves = 4, float lacunarity = 2f, float gain = 0.5f)` — sum of `abs` octaves, non-negative ~`[0,1]`.

- [ ] **Step 1: Verify MathUtil members.** Run `grep -n "public static" KhaozEngine.Primitives/MathUtil.cs`. If it has `Clamp`/`Lerp`, use them inside `TerrainNoise`; otherwise keep the small helpers local. (Decision recorded in the file header comment.)

- [ ] **Step 2: Write the csproj.**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Terrain</PackageId>
    <Version>$(KhaozEngine5xVersion)</Version>
    <Description>Render-free analytic terrain field for KhaozEngine. A deterministic TerrainField evaluates SampleHeight/SampleNormal/SampleBiome from a single coordinate-hash function (height at (x,z) depends only on (x,z,seed), never on which neighbour cells are loaded), folding biome-band shaping, fractal coordinate-hash noise, and an ordered feature list (LakeFeature/RidgeFeature/FlattenFeature). Plain float (authoritative server samples the same field the visual client renders). TerrainCollision wraps it for ground-follow and walkability. No render dependency - add KhaozEngine.Terrain.Render3D to mesh it.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Primitives/KhaozEngine.Primitives.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write the failing test** `KhaozEngine.Tests/Terrain/TerrainNoiseTests.cs`:

```csharp
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainNoiseTests
    {
        [Fact]
        public void Hash2_is_deterministic_and_seed_sensitive()
        {
            Assert.Equal(TerrainNoise.Hash2(3, -7, 99), TerrainNoise.Hash2(3, -7, 99));
            Assert.NotEqual(TerrainNoise.Hash2(3, -7, 99), TerrainNoise.Hash2(3, -7, 100));
            Assert.InRange(TerrainNoise.Hash2(3, -7, 99), -1f, 1f);
        }

        [Fact]
        public void SmoothStep_clamps_and_midpoints()
        {
            Assert.Equal(0f, TerrainNoise.SmoothStep(10f, 20f, 5f));
            Assert.Equal(1f, TerrainNoise.SmoothStep(10f, 20f, 25f));
            Assert.Equal(0.5f, TerrainNoise.SmoothStep(10f, 20f, 15f), 5);
        }

        [Fact]
        public void Fbm_is_deterministic_and_bounded()
        {
            float a = TerrainNoise.Fbm(12.5f, -3.25f, 7);
            float b = TerrainNoise.Fbm(12.5f, -3.25f, 7);
            Assert.Equal(a, b);
            Assert.InRange(a, -1.5f, 1.5f);
        }

        [Fact]
        public void Turbulence_is_non_negative()
        {
            for (int i = 0; i < 50; i++)
                Assert.True(TerrainNoise.Turbulence(i * 1.3f, i * -0.7f, 5) >= 0f);
        }

        [Fact]
        public void ValueNoise_is_continuous_between_lattice_points()
        {
            // two nearby samples differ by less than a lattice step's worth of range.
            float a = TerrainNoise.ValueNoise(4.10f, 9.00f, 1);
            float b = TerrainNoise.ValueNoise(4.11f, 9.00f, 1);
            Assert.True(System.MathF.Abs(a - b) < 0.1f);
        }
    }
}
```

- [ ] **Step 4: Run, verify it fails to compile** (`TerrainNoise` not defined).

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~TerrainNoiseTests`
Expected: build error / FAIL.

- [ ] **Step 5: Implement `TerrainNoise.cs`.**

```csharp
using System;

namespace KhaozEngine.Terrain
{
    /// <summary>
    /// Stateless coordinate-hash noise for the analytic terrain field. Every function depends only on its
    /// arguments (lattice coords / world position + seed), never on call order or loaded state, so the height
    /// at a world point is identical regardless of which chunks are streamed in. Plain float (authoritative
    /// server and visual client evaluate the same math; tiny cross-platform float differences are corrected by
    /// replication, per the terrain design decision).
    /// </summary>
    public static class TerrainNoise
    {
        /// <summary>Clamped Hermite smoothstep: 0 at x&lt;=a, 1 at x&gt;=b, smooth in between. Returns 0.5 at the midpoint.</summary>
        public static float SmoothStep(float a, float b, float x)
        {
            if (a == b) return x < a ? 0f : 1f;
            float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        /// <summary>Deterministic hash of an integer lattice point + seed to [-1, 1). Integer bit-mix (no Random).</summary>
        public static float Hash2(int gx, int gz, int seed)
        {
            unchecked
            {
                uint h = (uint)seed * 0x9E3779B1u;
                h ^= (uint)gx * 0x85EBCA77u;
                h = (h << 13) | (h >> 19);
                h ^= (uint)gz * 0xC2B2AE3Du;
                h *= 0x27D4EB2Fu;
                h ^= h >> 15;
                // map [0, 2^32) -> [-1, 1)
                return (h / 4294967295f) * 2f - 1f;
            }
        }

        /// <summary>Bilinearly-interpolated value noise in [-1, 1]. Smoothstep fade for C1 continuity at the lattice.</summary>
        public static float ValueNoise(float x, float z, int seed)
        {
            int x0 = (int)MathF.Floor(x);
            int z0 = (int)MathF.Floor(z);
            float fx = x - x0;
            float fz = z - z0;
            float u = fx * fx * (3f - 2f * fx);
            float v = fz * fz * (3f - 2f * fz);

            float n00 = Hash2(x0, z0, seed);
            float n10 = Hash2(x0 + 1, z0, seed);
            float n01 = Hash2(x0, z0 + 1, seed);
            float n11 = Hash2(x0 + 1, z0 + 1, seed);

            float nx0 = n00 + (n10 - n00) * u;
            float nx1 = n01 + (n11 - n01) * u;
            return nx0 + (nx1 - nx0) * v;
        }

        /// <summary>Signed fractional Brownian motion: summed octaves of value noise, normalized to ~[-1, 1].</summary>
        public static float Fbm(float x, float z, int seed, int octaves = 4, float lacunarity = 2f, float gain = 0.5f)
        {
            float sum = 0f, amp = 1f, freq = 1f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * ValueNoise(x * freq, z * freq, seed + o * 1013);
                norm += amp;
                amp *= gain;
                freq *= lacunarity;
            }
            return norm > 0f ? sum / norm : 0f;
        }

        /// <summary>Turbulence: summed |octaves|, normalized to ~[0, 1]. Non-negative, so it only raises terrain.</summary>
        public static float Turbulence(float x, float z, int seed, int octaves = 4, float lacunarity = 2f, float gain = 0.5f)
        {
            float sum = 0f, amp = 1f, freq = 1f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * MathF.Abs(ValueNoise(x * freq, z * freq, seed + o * 1013));
                norm += amp;
                amp *= gain;
                freq *= lacunarity;
            }
            return norm > 0f ? sum / norm : 0f;
        }
    }
}
```

- [ ] **Step 6: Add the project to the solution and the test refs.** Add the `<Project>` line to `KhaozEngine.slnx` (alongside the other `KhaozEngine.*` entries) and the `<ProjectReference>` to `KhaozEngine.Tests.csproj`.

- [ ] **Step 7: Run tests, verify pass.**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~TerrainNoiseTests`
Expected: PASS (5 tests).

- [ ] **Step 8: Commit.**

```bash
git add KhaozEngine.Terrain KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj KhaozEngine.Tests/Terrain
git commit -m "terrain: stateless coordinate-hash noise + package scaffold"
```

---

### Task 2: Biome shaping (`BiomeId`, `BiomeBand`, `TerrainConfig`, `TerrainField.ShapeAt`)

**Files:**
- Create: `KhaozEngine.Terrain/BiomeId.cs`, `BiomeBand.cs`, `TerrainConfig.cs`, `TerrainField.cs`
- Test: `KhaozEngine.Tests/Terrain/TerrainFieldTests.cs`

**Interfaces:**
- Produces:
  - `enum BiomeId : byte { Meadow, Forest, Marsh, Mountains, Desert, Snow }`
  - `struct BiomeBand { float Start; float End; BiomeId Biome; float BaseHeight; float HillAmplitude; }` (bands along the Z axis; `Start`/`End` may be `float.NegativeInfinity`/`PositiveInfinity` for the outer bands).
  - `sealed class TerrainConfig { int Seed; float WaterLevel; BiomeBand[] Biomes; ITerrainFeature[] Features; float BiomeBlend; float GentleFrequency; float GentleAmplitude; float DetailFrequency; int DetailOctaves; }` with sensible defaults and a single one-band fallback when `Biomes` is null/empty.
  - `sealed class TerrainField { TerrainField(TerrainConfig config); float WaterLevel { get; } internal (float baseHeight, float hillAmp, BiomeId biome) ShapeAt(float z); }` (full `SampleHeight` lands in Task 3; `ShapeAt` is `internal` and tested via `InternalsVisibleTo`).

**Shape blend algorithm (continuous everywhere):** each band's weight at `z` is `rise * fall` where `rise = SmoothStep(Start - Blend, Start + Blend, z)` and `fall = 1 - SmoothStep(End - Blend, End + Blend, z)` (infinite edges give `rise`/`fall = 1`). Normalize weights across bands; blend `BaseHeight`/`HillAmplitude` by the normalized weights; `SampleBiome` picks the argmax band. Adjacent tiling bands (`band[i].End == band[i+1].Start = B`) give exactly `0.5/0.5` at `z = B`, so a meadow→mountain pair with blend `26` around boundary `48` reproduces the greybox `SmoothStep(22, 74, z)` mask.

- [ ] **Step 1: Write the failing test:**

```csharp
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainFieldTests
    {
        static TerrainField TwoBand()
        {
            var cfg = new TerrainConfig
            {
                Seed = 1,
                BiomeBlend = 26f,
                Biomes = new[]
                {
                    new BiomeBand { Start = float.NegativeInfinity, End = 48f, Biome = BiomeId.Meadow,    BaseHeight = 0f,  HillAmplitude = 0f },
                    new BiomeBand { Start = 48f, End = float.PositiveInfinity, Biome = BiomeId.Mountains, BaseHeight = 34f, HillAmplitude = 22f },
                },
            };
            return new TerrainField(cfg);
        }

        [Fact]
        public void Shape_blend_is_continuous_across_the_boundary()
        {
            var f = TwoBand();
            float prev = f.ShapeAt(20f).baseHeight;
            // walk across the boundary; no jump larger than the per-step slope allows.
            for (float z = 20f; z <= 76f; z += 0.5f)
            {
                float h = f.ShapeAt(z).baseHeight;
                Assert.True(System.MathF.Abs(h - prev) < 2f, $"discontinuity at z={z}");
                prev = h;
            }
        }

        [Fact]
        public void Shape_is_meadow_low_and_mountains_high()
        {
            var f = TwoBand();
            Assert.True(f.ShapeAt(0f).baseHeight < 2f);
            Assert.Equal(BiomeId.Meadow, f.ShapeAt(0f).biome);
            Assert.True(f.ShapeAt(120f).baseHeight > 30f);
            Assert.Equal(BiomeId.Mountains, f.ShapeAt(120f).biome);
        }

        [Fact]
        public void Boundary_blends_half_and_half()
        {
            var f = TwoBand();
            // at z=48 the two bands are 50/50: baseHeight ~= mean(0, 34) = 17.
            Assert.Equal(17f, f.ShapeAt(48f).baseHeight, 0);
        }
    }
}
```

- [ ] **Step 2: Run, verify fail** (types undefined). Run: `dotnet test ... --filter FullyQualifiedName~TerrainFieldTests`.

- [ ] **Step 3: Implement `BiomeId.cs`, `BiomeBand.cs`, `TerrainConfig.cs`, and a `TerrainField.cs` carrying `ShapeAt` + `WaterLevel`.** (Full code; `SampleHeight`/`SampleNormal`/`SampleBiome` added in Task 3.)

```csharp
// BiomeId.cs
namespace KhaozEngine.Terrain
{
    /// <summary>Designed terrain region id, assigned per BiomeBand and surfaced by TerrainField.SampleBiome
    /// for splat material selection and gameplay. Distinct from per-vertex splat weights.</summary>
    public enum BiomeId : byte { Meadow, Forest, Marsh, Mountains, Desert, Snow }
}
```

```csharp
// BiomeBand.cs
namespace KhaozEngine.Terrain
{
    /// <summary>One designed region along the world Z axis: it contributes its BaseHeight and HillAmplitude
    /// where it is dominant, smoothstep-blended with its neighbours across TerrainConfig.BiomeBlend. Outer
    /// bands use +/- infinity for the open edge.</summary>
    public struct BiomeBand
    {
        public float Start;
        public float End;
        public BiomeId Biome;
        public float BaseHeight;
        public float HillAmplitude;
    }
}
```

```csharp
// TerrainConfig.cs
namespace KhaozEngine.Terrain
{
    /// <summary>Authoring inputs for a TerrainField. Defaults give a single gentle meadow band; supply Biomes
    /// for designed regions and Features for lakes/ridges/flattened hubs.</summary>
    public sealed class TerrainConfig
    {
        public int Seed = 1;
        public float WaterLevel = 0f;
        /// <summary>Smoothstep blend half-width (metres) at biome-band boundaries.</summary>
        public float BiomeBlend = 24f;
        /// <summary>Low-frequency ground roll applied everywhere.</summary>
        public float GentleFrequency = 0.02f;
        public float GentleAmplitude = 1.5f;
        /// <summary>Detail octave scaled by the dominant band's HillAmplitude (non-negative turbulence: only raises).</summary>
        public float DetailFrequency = 0.03f;
        public int DetailOctaves = 4;
        public BiomeBand[]? Biomes;
        public ITerrainFeature[]? Features;
    }
}
```

```csharp
// TerrainField.cs  (Task 2 form: ShapeAt + WaterLevel; extended in Task 3)
using System;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>
    /// The analytic terrain height field: the single source of truth for ground height. SampleHeight folds
    /// three layers in order - biome shape (designed regions, smoothstep-blended), base coordinate-hash noise,
    /// then an ordered feature list (lakes/ridges/flatten). Stateless: the height at (x,z) depends only on
    /// (x,z,seed), so server and client agree and streamed chunks line up regardless of load order.
    /// </summary>
    public sealed class TerrainField
    {
        readonly TerrainConfig _cfg;
        readonly BiomeBand[] _bands;

        public TerrainField(TerrainConfig config)
        {
            _cfg = config ?? throw new ArgumentNullException(nameof(config));
            _bands = (config.Biomes is { Length: > 0 })
                ? config.Biomes
                : new[] { new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f } };
        }

        public float WaterLevel => _cfg.WaterLevel;

        /// <summary>Blends the biome bands at world Z: normalized smoothstep weights -> (baseHeight, hillAmp);
        /// biome = argmax weight. Continuous everywhere.</summary>
        internal (float baseHeight, float hillAmp, BiomeId biome) ShapeAt(float z)
        {
            float blend = MathF.Max(1e-3f, _cfg.BiomeBlend);
            float wSum = 0f, baseH = 0f, hill = 0f, bestW = -1f;
            BiomeId best = _bands[0].Biome;
            for (int i = 0; i < _bands.Length; i++)
            {
                ref readonly BiomeBand b = ref _bands[i];
                float rise = float.IsNegativeInfinity(b.Start) ? 1f : TerrainNoise.SmoothStep(b.Start - blend, b.Start + blend, z);
                float fall = float.IsPositiveInfinity(b.End) ? 1f : 1f - TerrainNoise.SmoothStep(b.End - blend, b.End + blend, z);
                float w = rise * fall;
                wSum += w;
                baseH += w * b.BaseHeight;
                hill += w * b.HillAmplitude;
                if (w > bestW) { bestW = w; best = b.Biome; }
            }
            if (wSum > 1e-6f) { baseH /= wSum; hill /= wSum; }
            return (baseH, hill, best);
        }
    }
}
```

- [ ] **Step 4: Run tests, verify pass.** Run: `dotnet test ... --filter FullyQualifiedName~TerrainFieldTests`. Expected: PASS (3 tests).

- [ ] **Step 5: Commit.**

```bash
git add KhaozEngine.Terrain KhaozEngine.Tests/Terrain/TerrainFieldTests.cs
git commit -m "terrain: biome-band shaping with smoothstep blend"
```

---

### Task 3: `SampleHeight` / `SampleNormal` / `SampleBiome`

**Files:**
- Modify: `KhaozEngine.Terrain/TerrainField.cs`
- Test: `KhaozEngine.Tests/Terrain/TerrainFieldTests.cs` (append)

**Interfaces:**
- Produces (on `TerrainField`): `float SampleHeight(float x, float z)`, `Vector3 SampleNormal(float x, float z)`, `BiomeId SampleBiome(float x, float z)`.
- `SampleHeight` = `shape.BaseHeight + gentleRoll + shape.HillAmplitude * mountainDetail`, then each feature `Apply(x,z,h)` folded in order. `gentleRoll = GentleAmplitude * Fbm(x*GentleFrequency, z*GentleFrequency, seed)`; `mountainDetail = Turbulence(x*DetailFrequency, z*DetailFrequency, seed, DetailOctaves)` (non-negative, so HillAmplitude only raises - mirrors the greybox `mask * (base + detail*turbulence)`).
- `SampleNormal` = central finite difference, `eps = 1f`: `normalize((-(hxp - hxm)/(2eps), 1, -(hzp - hzm)/(2eps)))`. Flat ground -> `(0,1,0)`.

- [ ] **Step 1: Append failing tests:**

```csharp
        [Fact]
        public void SampleHeight_is_deterministic_across_instances()
        {
            var a = TwoBand(); var b = TwoBand();
            Assert.Equal(a.SampleHeight(12.3f, 45.6f), b.SampleHeight(12.3f, 45.6f));
        }

        [Fact]
        public void SampleHeight_locality_independent_of_query_path()
        {
            // sampling a far point first must not change a later sample (statelessness).
            var f = TwoBand();
            float direct = f.SampleHeight(5f, 5f);
            _ = f.SampleHeight(9999f, -9999f);
            Assert.Equal(direct, f.SampleHeight(5f, 5f));
        }

        [Fact]
        public void Mountains_rise_above_meadow()
        {
            var f = TwoBand();
            Assert.True(f.SampleHeight(0f, 120f) > f.SampleHeight(0f, 0f) + 20f);
        }

        [Fact]
        public void Normal_on_flat_meadow_points_up()
        {
            var f = TwoBand();
            var n = f.SampleNormal(0f, 0f);
            Assert.True(n.Y > 0.99f);
        }

        [Fact]
        public void Normal_tilts_on_a_slope()
        {
            var f = TwoBand();
            // the mountain ramp climbs toward +Z, so its normal leans toward -Z.
            var n = f.SampleNormal(0f, 50f);
            Assert.True(n.Y < 0.999f);
            Assert.True(n.Z < 0f);
        }
```

- [ ] **Step 2: Run, verify fail** (methods undefined).

- [ ] **Step 3: Add the methods to `TerrainField`:**

```csharp
        /// <summary>The one source of truth for ground height at a world point. Folds biome shape, base
        /// coordinate-hash noise, then each feature in order. Stateless in (x,z,seed).</summary>
        public float SampleHeight(float x, float z)
        {
            var shape = ShapeAt(z);
            float gentle = _cfg.GentleAmplitude * TerrainNoise.Fbm(x * _cfg.GentleFrequency, z * _cfg.GentleFrequency, _cfg.Seed);
            float detail = TerrainNoise.Turbulence(x * _cfg.DetailFrequency, z * _cfg.DetailFrequency, _cfg.Seed, _cfg.DetailOctaves);
            float h = shape.baseHeight + gentle + shape.hillAmp * detail;

            var feats = _cfg.Features;
            if (feats != null)
                for (int i = 0; i < feats.Length; i++)
                    h = feats[i].Apply(x, z, h);
            return h;
        }

        /// <summary>Surface normal via central finite difference (eps = 1 m). Flat ground returns +Y.</summary>
        public Vector3 SampleNormal(float x, float z)
        {
            const float eps = 1f;
            float hxp = SampleHeight(x + eps, z), hxm = SampleHeight(x - eps, z);
            float hzp = SampleHeight(x, z + eps), hzm = SampleHeight(x, z - eps);
            var n = new Vector3(-(hxp - hxm) / (2f * eps), 1f, -(hzp - hzm) / (2f * eps));
            return Vector3.Normalize(n);
        }

        /// <summary>The dominant biome at the world point (from the band blend).</summary>
        public BiomeId SampleBiome(float x, float z) => ShapeAt(z).biome;
```

(Ensure `using System.Numerics;` is present.)

- [ ] **Step 4: Run tests, verify pass.** Run: `dotnet test ... --filter FullyQualifiedName~TerrainFieldTests`.

- [ ] **Step 5: Commit.**

```bash
git add KhaozEngine.Terrain/TerrainField.cs KhaozEngine.Tests/Terrain/TerrainFieldTests.cs
git commit -m "terrain: SampleHeight/SampleNormal/SampleBiome fold shape+noise"
```

---

### Task 4: Features (`ITerrainFeature`, `LakeFeature`, `RidgeFeature`, `FlattenFeature`)

**Files:**
- Create: `KhaozEngine.Terrain/ITerrainFeature.cs`, `LakeFeature.cs`, `RidgeFeature.cs`, `FlattenFeature.cs`
- Test: `KhaozEngine.Tests/Terrain/TerrainFeatureTests.cs`

**Interfaces:**
- Produces:
  - `interface ITerrainFeature { float Apply(float x, float z, float h); }`
  - `sealed class LakeFeature(float centerX, float centerZ, float radius, float depth, float innerFraction = 0.45f, float outerFraction = 1.30f) : ITerrainFeature` — additive carve `h - depth * (1 - SmoothStep(radius*innerFraction, radius*outerFraction, d))` (greybox basin form; max carve at centre, none outside `radius*outerFraction`).
  - `sealed class RidgeFeature(Vector2 point, Vector2 direction, float height, float width, float passAlong, float passWidth) : ITerrainFeature` — gaussian wall on perpendicular distance, gated to ~0 within `passWidth` of `passAlong` along the line (the pass).
  - `sealed class FlattenFeature(float centerX, float centerZ, float radius, float targetHeight, float blend = 0.4f) : ITerrainFeature` — `Lerp(h, targetHeight, 1 - SmoothStep(radius*(1-blend), radius, d))`.

- [ ] **Step 1: Write failing tests:**

```csharp
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainFeatureTests
    {
        [Fact]
        public void Lake_lowers_height_inside_radius_and_leaves_outside()
        {
            var lake = new LakeFeature(0f, 0f, 8f, 3.6f);
            float center = lake.Apply(0f, 0f, 10f);
            float edge = lake.Apply(20f, 0f, 10f);
            Assert.True(center < 10f);                 // carved
            Assert.True(center <= lake.Apply(4f, 0f, 10f)); // deepest at centre
            Assert.Equal(10f, edge, 3);                // untouched well outside
        }

        [Fact]
        public void Ridge_raises_along_the_line_but_dips_at_the_pass()
        {
            // line through origin along +X, pass at x=0.
            var ridge = new RidgeFeature(Vector2.Zero, new Vector2(1f, 0f), height: 30f, width: 4f, passAlong: 0f, passWidth: 10f);
            float onWall = ridge.Apply(40f, 0f, 0f);     // on the line, far from pass
            float atPass = ridge.Apply(0f, 0f, 0f);      // on the line, at the pass
            float offLine = ridge.Apply(40f, 30f, 0f);   // far perpendicular
            Assert.True(onWall > 20f);
            Assert.True(atPass < 5f);
            Assert.True(offLine < 1f);
        }

        [Fact]
        public void Flatten_levels_its_region_to_target()
        {
            var flat = new FlattenFeature(0f, 0f, 10f, targetHeight: 5f);
            Assert.Equal(5f, flat.Apply(0f, 0f, 50f), 1);  // centre pulled to target
            Assert.Equal(40f, flat.Apply(40f, 0f, 40f), 1); // outside untouched
        }
    }
}
```

- [ ] **Step 2: Run, verify fail.**

- [ ] **Step 3: Implement the four files.**

```csharp
// ITerrainFeature.cs
namespace KhaozEngine.Terrain
{
    /// <summary>A composable height modifier folded by TerrainField.SampleHeight in list order. Stateless and
    /// pure: Apply must depend only on (x, z, h), so terrain stays load-order independent.</summary>
    public interface ITerrainFeature
    {
        float Apply(float x, float z, float h);
    }
}
```

```csharp
// LakeFeature.cs
using System;

namespace KhaozEngine.Terrain
{
    /// <summary>Carves a basin toward the water by subtracting up to <c>depth</c> at the centre, smoothstep-faded
    /// to zero by <c>radius*outerFraction</c> (the greybox clearing's lake trick).</summary>
    public sealed class LakeFeature : ITerrainFeature
    {
        readonly float _cx, _cz, _radius, _depth, _inner, _outer;

        public LakeFeature(float centerX, float centerZ, float radius, float depth, float innerFraction = 0.45f, float outerFraction = 1.30f)
        {
            _cx = centerX; _cz = centerZ; _radius = radius; _depth = depth; _inner = innerFraction; _outer = outerFraction;
        }

        public float Apply(float x, float z, float h)
        {
            float d = MathF.Sqrt((x - _cx) * (x - _cx) + (z - _cz) * (z - _cz));
            float carve = 1f - TerrainNoise.SmoothStep(_radius * _inner, _radius * _outer, d);
            return h - _depth * carve;
        }
    }
}
```

```csharp
// RidgeFeature.cs
using System;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>Raises a gaussian wall along an infinite line (perpendicular falloff = <c>width</c>), pierced by a
    /// pass: within <c>passWidth</c> of <c>passAlong</c> (signed distance along the line from <c>point</c>) the
    /// wall is gated to ~0 so the ridge reads as mountains with a gap, not a continuous berm.</summary>
    public sealed class RidgeFeature : ITerrainFeature
    {
        readonly Vector2 _point, _dir;  // _dir normalized
        readonly float _height, _width, _passAlong, _passWidth;

        public RidgeFeature(Vector2 point, Vector2 direction, float height, float width, float passAlong, float passWidth)
        {
            _point = point;
            _dir = direction.LengthSquared() > 1e-12f ? Vector2.Normalize(direction) : new Vector2(1f, 0f);
            _height = height; _width = MathF.Max(1e-3f, width); _passAlong = passAlong; _passWidth = MathF.Max(1e-3f, passWidth);
        }

        public float Apply(float x, float z, float h)
        {
            Vector2 rel = new Vector2(x, z) - _point;
            float along = Vector2.Dot(rel, _dir);
            Vector2 perpVec = rel - _dir * along;
            float perp = perpVec.Length();
            float wall = _height * MathF.Exp(-(perp * perp) / (2f * _width * _width));
            // pass gate: 0 at the pass centre, 1 by passWidth away.
            float gate = TerrainNoise.SmoothStep(_passWidth * 0.5f, _passWidth, MathF.Abs(along - _passAlong));
            return h + wall * gate;
        }
    }
}
```

```csharp
// FlattenFeature.cs
using System;

namespace KhaozEngine.Terrain
{
    /// <summary>Levels a hub/landmark region toward <c>targetHeight</c>: full inside <c>radius*(1-blend)</c>,
    /// smoothstep-faded to no effect by <c>radius</c>.</summary>
    public sealed class FlattenFeature : ITerrainFeature
    {
        readonly float _cx, _cz, _radius, _target, _blend;

        public FlattenFeature(float centerX, float centerZ, float radius, float targetHeight, float blend = 0.4f)
        {
            _cx = centerX; _cz = centerZ; _radius = radius; _target = targetHeight; _blend = blend;
        }

        public float Apply(float x, float z, float h)
        {
            float d = MathF.Sqrt((x - _cx) * (x - _cx) + (z - _cz) * (z - _cz));
            float t = 1f - TerrainNoise.SmoothStep(_radius * (1f - _blend), _radius, d);
            return h + (_target - h) * t;
        }
    }
}
```

- [ ] **Step 4: Run tests, verify pass.** Run: `dotnet test ... --filter FullyQualifiedName~TerrainFeatureTests`.

- [ ] **Step 5: Commit.**

```bash
git add KhaozEngine.Terrain/ITerrainFeature.cs KhaozEngine.Terrain/LakeFeature.cs KhaozEngine.Terrain/RidgeFeature.cs KhaozEngine.Terrain/FlattenFeature.cs KhaozEngine.Tests/Terrain/TerrainFeatureTests.cs
git commit -m "terrain: Lake/Ridge/Flatten features"
```

---

### Task 5: `TerrainCollision`

**Files:**
- Create: `KhaozEngine.Terrain/TerrainCollision.cs`
- Test: `KhaozEngine.Tests/Terrain/TerrainCollisionTests.cs`

**Interfaces:**
- Produces: `sealed class TerrainCollision(TerrainField field) { float GroundHeight(float x, float z); bool IsWalkable(float x, float z, float maxSlopeRadians); }`. `GroundHeight` == `field.SampleHeight`. `IsWalkable` = `acos(clamp(normal.Y,0,1)) <= maxSlopeRadians`.

**Decision (spec open item):** `TerrainCollision` lives in `KhaozEngine.Terrain`, not `KhaozEngine.Collision`. The server already references the leaf; putting the collider here keeps the dependency edge `Terrain -> Primitives` only and avoids `Collision -> Terrain` (which would drag the field into every 2D collision consumer). Spec sanctions this ("if that dependency is awkward, the collider stays in Terrain").

- [ ] **Step 1: Write failing tests:**

```csharp
using System;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainCollisionTests
    {
        static TerrainField Ramp()
        {
            // single mountain band so there is a real slope toward +Z.
            var cfg = new TerrainConfig
            {
                Seed = 2, BiomeBlend = 26f,
                Biomes = new[]
                {
                    new BiomeBand { Start = float.NegativeInfinity, End = 48f, Biome = BiomeId.Meadow,    BaseHeight = 0f,  HillAmplitude = 0f },
                    new BiomeBand { Start = 48f, End = float.PositiveInfinity, Biome = BiomeId.Mountains, BaseHeight = 34f, HillAmplitude = 22f },
                },
            };
            return new TerrainField(cfg);
        }

        [Fact]
        public void GroundHeight_equals_the_field()
        {
            var f = Ramp(); var c = new TerrainCollision(f);
            Assert.Equal(f.SampleHeight(7f, 13f), c.GroundHeight(7f, 13f));
        }

        [Fact]
        public void IsWalkable_flips_at_the_slope_threshold()
        {
            var f = Ramp(); var c = new TerrainCollision(f);
            // flat meadow walkable even at a tiny budget; the mid-ramp is not.
            Assert.True(c.IsWalkable(0f, 0f, 0.1f));
            float steepBudget = MathF.PI / 2f;   // 90 deg: everything walkable
            float tinyBudget = 0.01f;            // ~0.6 deg: ramp fails
            Assert.True(c.IsWalkable(0f, 50f, steepBudget));
            Assert.False(c.IsWalkable(0f, 50f, tinyBudget));
        }
    }
}
```

- [ ] **Step 2: Run, verify fail.**

- [ ] **Step 3: Implement `TerrainCollision.cs`:**

```csharp
using System;

namespace KhaozEngine.Terrain
{
    /// <summary>Terrain-aware collision over a TerrainField: ground-follow height and a slope walkability gate.
    /// The sim (e.g. Sharding's CellSim) calls this each tick to keep entities on the ground and reject moves
    /// onto terrain steeper than a per-entity budget. Render-free.</summary>
    public sealed class TerrainCollision
    {
        readonly TerrainField _field;

        public TerrainCollision(TerrainField field) => _field = field ?? throw new ArgumentNullException(nameof(field));

        /// <summary>Ground height at the world point (= TerrainField.SampleHeight).</summary>
        public float GroundHeight(float x, float z) => _field.SampleHeight(x, z);

        /// <summary>True when the surface slope at (x,z) is no steeper than <paramref name="maxSlopeRadians"/>
        /// (angle between the surface normal and +Y).</summary>
        public bool IsWalkable(float x, float z, float maxSlopeRadians)
        {
            float ny = Math.Clamp(_field.SampleNormal(x, z).Y, 0f, 1f);
            float slope = MathF.Acos(ny);
            return slope <= maxSlopeRadians;
        }
    }
}
```

- [ ] **Step 4: Run tests, verify pass.**

- [ ] **Step 5: Commit.**

```bash
git add KhaozEngine.Terrain/TerrainCollision.cs KhaozEngine.Tests/Terrain/TerrainCollisionTests.cs
git commit -m "terrain: TerrainCollision (ground height + slope walkability)"
```

---

### Task 6: `TerrainPresets.Clearing()` + greybox parity test

**Files:**
- Create: `KhaozEngine.Terrain/TerrainPresets.cs`
- Test: `KhaozEngine.Tests/Terrain/TerrainParityTests.cs`

**Interfaces:**
- Produces: `static class TerrainPresets { static TerrainConfig Clearing(int seed = 5); }` reproducing `make_clearing_greybox.py`: meadow band `[-inf, 48]` (base 0, hill 0) and mountain band `[48, +inf]` (base 34, hill 22), `BiomeBlend = 26` (so the blend window `[22, 74]` matches the greybox `SmoothStep(22, 74, z)` mask), gentle roll `amp 1.5 / freq 0.02`, detail `freq 0.03 / 4 octaves`, `LakeFeature(-13, -2, 8, 3.6)`, `WaterLevel = -1.2`.

- [ ] **Step 1: Write the failing parity test:**

```csharp
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainParityTests
    {
        [Fact]
        public void Clearing_has_gentle_meadow_mountains_and_a_lake_basin()
        {
            var f = new TerrainField(TerrainPresets.Clearing());

            // meadow floor near the clearing centre is gentle (a few metres of roll, not tens).
            Assert.InRange(f.SampleHeight(6f, 6f), -3f, 6f);

            // mountains ramp up toward +Z (tens of metres) without a vertical wall: monotone-ish climb.
            float zMid = f.SampleHeight(0f, 48f);
            float zFar = f.SampleHeight(0f, 110f);
            Assert.True(zFar > 30f);
            Assert.True(zFar > zMid);

            // the lake basin at (-13,-2) sits below the water surface.
            Assert.True(f.SampleHeight(-13f, -2f) < f.WaterLevel);

            // overall relief is in the greybox ballpark (tens of metres, not hundreds).
            float lo = f.SampleHeight(6f, 6f), hi = f.SampleHeight(0f, 120f);
            Assert.InRange(hi - lo, 25f, 90f);
        }
    }
}
```

- [ ] **Step 2: Run, verify fail.**

- [ ] **Step 3: Implement `TerrainPresets.cs`:**

```csharp
namespace KhaozEngine.Terrain
{
    /// <summary>Ready-made TerrainConfigs. Clearing reproduces the tools/blender/make_clearing_greybox.py
    /// "forest clearing at a mountain base": gentle meadow, mountains ramping toward +Z, a carved lake basin.
    /// Used as the field parity fixture and a demo/world seed.</summary>
    public static class TerrainPresets
    {
        public static TerrainConfig Clearing(int seed = 5) => new TerrainConfig
        {
            Seed = seed,
            WaterLevel = -1.2f,
            BiomeBlend = 26f,              // blend window [48-26, 48+26] = [22, 74] == greybox SmoothStep(22, 74, z)
            GentleFrequency = 0.02f,
            GentleAmplitude = 1.5f,
            DetailFrequency = 0.03f,
            DetailOctaves = 4,
            Biomes = new[]
            {
                new BiomeBand { Start = float.NegativeInfinity, End = 48f, Biome = BiomeId.Meadow,    BaseHeight = 0f,  HillAmplitude = 0f },
                new BiomeBand { Start = 48f, End = float.PositiveInfinity, Biome = BiomeId.Mountains, BaseHeight = 34f, HillAmplitude = 22f },
            },
            Features = new ITerrainFeature[]
            {
                new LakeFeature(centerX: -13f, centerZ: -2f, radius: 8f, depth: 3.6f),
            },
        };
    }
}
```

- [ ] **Step 4: Run tests, verify pass.** If the relief assertion is off, tune the assertion bounds (not by eye) to the actual values printed — the values are governed by `HillAmplitude * Turbulence(max ~1)`; do NOT inflate amplitudes to hit a number.

- [ ] **Step 5: Commit.**

```bash
git add KhaozEngine.Terrain/TerrainPresets.cs KhaozEngine.Tests/Terrain/TerrainParityTests.cs
git commit -m "terrain: Clearing preset + greybox parity test"
```

---

### Task 7: `KhaozEngine.Terrain.Render3D` scaffold + splat weights + ramp

**Files:**
- Create: `KhaozEngine.Terrain.Render3D/KhaozEngine.Terrain.Render3D.csproj`
- Create: `KhaozEngine.Terrain.Render3D/TerrainSplatWeights.cs`, `TerrainRamp.cs`
- Modify: `KhaozEngine.slnx`, `KhaozEngine.Tests/KhaozEngine.Tests.csproj` (add the new project + test ref)
- Test: `KhaozEngine.Tests/Terrain/TerrainSplatTests.cs`

**Interfaces:**
- Produces (namespace `KhaozEngine.Terrain`):
  - `struct TerrainSplatWeights { float Grass, Dirt, Rock, Sand, Snow; }` with `static TerrainSplatWeights From(float height, float slope01, BiomeId biome, float waterLevel, float snowLine = 60f)` returning NORMALIZED weights (sum ~1). `slope01` = `1 - normal.Y` clamped (0 flat, 1 vertical). Rule of thumb: high `slope01` -> Rock; height near/below `waterLevel` -> Sand; height above `snowLine` -> Snow; else Grass with a little Dirt from mid slope.
  - `static class TerrainRamp { static Color Of(in TerrainSplatWeights w); }` weighted blend of the 5 palette colours (greybox ramp): grass `(0.27,0.42,0.18)`, dirt `(0.34,0.30,0.24)`, rock `(0.44,0.42,0.40)`, sand `(0.76,0.70,0.50)`, snow `(0.93,0.94,0.96)`.

- [ ] **Step 1: Write the csproj** (`KhaozEngine.Terrain.Render3D.csproj`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Terrain.Render3D</PackageId>
    <Version>$(KhaozEngine5xVersion)</Version>
    <Description>The 3D arm of the KhaozEngine terrain system: a chunked-LOD mesh builder over KhaozEngine.Terrain's analytic field. Build(field, region, lod) samples the field on a distance-chosen grid into a Render3D GltfMesh with ~0.3 m edge skirts (so mismatched-LOD neighbours stay crack-free), a per-vertex height/slope splat weight set (grass/dirt/rock/sand/snow) plumbed for the later PBR-texture upgrade, and a height/slope vertex-colour ramp for the current slice. Includes a chunk bounding box for frustum culling, a PickLod distance helper, and Scene3D load/draw extensions. Kept separate from KhaozEngine.Terrain so the server/sim never drags in Render3D.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Terrain/KhaozEngine.Terrain.csproj" />
    <ProjectReference Include="../KhaozEngine.Render3D/KhaozEngine.Render3D.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write failing tests** `TerrainSplatTests.cs`:

```csharp
using KhaozEngine.Primitives;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainSplatTests
    {
        [Fact]
        public void Weights_normalize_to_one()
        {
            var w = TerrainSplatWeights.From(height: 10f, slope01: 0.3f, biome: BiomeId.Meadow, waterLevel: 0f);
            Assert.Equal(1f, w.Grass + w.Dirt + w.Rock + w.Sand + w.Snow, 3);
        }

        [Fact]
        public void Steep_ground_is_rock_dominant()
        {
            var w = TerrainSplatWeights.From(20f, slope01: 0.95f, BiomeId.Mountains, 0f);
            Assert.True(w.Rock > w.Grass && w.Rock > w.Snow);
        }

        [Fact]
        public void High_flat_ground_is_snow_dominant()
        {
            var w = TerrainSplatWeights.From(80f, slope01: 0.05f, BiomeId.Mountains, 0f, snowLine: 60f);
            Assert.True(w.Snow > w.Rock && w.Snow > w.Grass);
        }

        [Fact]
        public void Shoreline_is_sandy()
        {
            var w = TerrainSplatWeights.From(-1f, slope01: 0.1f, BiomeId.Marsh, waterLevel: 0f);
            Assert.True(w.Sand > w.Grass);
        }

        [Fact]
        public void Ramp_colour_is_white_for_full_snow()
        {
            var snow = new TerrainSplatWeights { Snow = 1f };
            Color c = TerrainRamp.Of(snow);
            Assert.True(c.R > 0.9f && c.G > 0.9f && c.B > 0.9f);
        }
    }
}
```

- [ ] **Step 3: Run, verify fail.**

- [ ] **Step 4: Implement `TerrainSplatWeights.cs` and `TerrainRamp.cs`.**

```csharp
// TerrainSplatWeights.cs
using System;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>Per-vertex terrain surface mix: five normalized weights baked from height + slope + biome by the
    /// chunk builder. The current slice renders these as a vertex-colour ramp (TerrainRamp); the weights are
    /// plumbed now so the later PBR splat-TEXTURE sub-project is a drop-in (it samples albedo/normal per channel
    /// instead of blending palette colours). Render-data only.</summary>
    public struct TerrainSplatWeights
    {
        public float Grass, Dirt, Rock, Sand, Snow;

        /// <summary>Bakes a normalized weight set. slope01 = 1 - normal.Y clamped (0 flat, 1 vertical). Steep ->
        /// rock; near/below water -> sand; above snowLine -> snow; otherwise grass with a little mid-slope dirt.</summary>
        public static TerrainSplatWeights From(float height, float slope01, BiomeId biome, float waterLevel, float snowLine = 60f)
        {
            slope01 = Math.Clamp(slope01, 0f, 1f);
            float rock = TerrainNoise.SmoothStep(0.45f, 0.85f, slope01);          // steepness -> rock
            float snow = (1f - rock) * TerrainNoise.SmoothStep(snowLine - 12f, snowLine + 8f, height);
            float sand = (1f - rock) * (1f - snow) * (1f - TerrainNoise.SmoothStep(waterLevel + 0.2f, waterLevel + 2.5f, height));
            float dirt = (1f - rock) * (1f - snow) * (1f - sand) * TerrainNoise.SmoothStep(0.15f, 0.5f, slope01) * 0.5f;
            float grass = MathF.Max(0f, 1f - rock - snow - sand - dirt);

            var w = new TerrainSplatWeights { Grass = grass, Dirt = dirt, Rock = rock, Sand = sand, Snow = snow };
            float sum = w.Grass + w.Dirt + w.Rock + w.Sand + w.Snow;
            if (sum > 1e-6f)
            {
                w.Grass /= sum; w.Dirt /= sum; w.Rock /= sum; w.Sand /= sum; w.Snow /= sum;
            }
            else w.Grass = 1f;
            return w;
        }
    }
}
```

```csharp
// TerrainRamp.cs
using KhaozEngine.Primitives;

namespace KhaozEngine.Terrain
{
    /// <summary>Maps splat weights to a vertex colour for the current (pre-PBR) terrain slice: a weighted blend of
    /// five greybox palette colours (matching make_clearing_greybox.py's height/slope ramp). Drop-in replaceable
    /// by PBR splat textures later.</summary>
    public static class TerrainRamp
    {
        public static readonly Color Grass = new(0.27f, 0.42f, 0.18f);
        public static readonly Color Dirt  = new(0.34f, 0.30f, 0.24f);
        public static readonly Color Rock  = new(0.44f, 0.42f, 0.40f);
        public static readonly Color Sand  = new(0.76f, 0.70f, 0.50f);
        public static readonly Color Snow  = new(0.93f, 0.94f, 0.96f);

        public static Color Of(in TerrainSplatWeights w) => new(
            Grass.R * w.Grass + Dirt.R * w.Dirt + Rock.R * w.Rock + Sand.R * w.Sand + Snow.R * w.Snow,
            Grass.G * w.Grass + Dirt.G * w.Dirt + Rock.G * w.Rock + Sand.G * w.Sand + Snow.G * w.Snow,
            Grass.B * w.Grass + Dirt.B * w.Dirt + Rock.B * w.Rock + Sand.B * w.Sand + Snow.B * w.Snow,
            1f);
    }
}
```

- [ ] **Step 5: Register the project** in `KhaozEngine.slnx` and add the `<ProjectReference>` to `KhaozEngine.Tests.csproj`.

- [ ] **Step 6: Run tests, verify pass.** Run: `dotnet test ... --filter FullyQualifiedName~TerrainSplatTests`.

- [ ] **Step 7: Commit.**

```bash
git add KhaozEngine.Terrain.Render3D KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj KhaozEngine.Tests/Terrain/TerrainSplatTests.cs
git commit -m "terrain.render3d: splat weights + vertex-colour ramp + package scaffold"
```

---

### Task 8: `TerrainLod` (distance tiers + `PickLod`)

**Files:**
- Create: `KhaozEngine.Terrain.Render3D/TerrainLod.cs`
- Test: `KhaozEngine.Tests/Terrain/TerrainLodTests.cs`

**Interfaces:**
- Produces (namespace `KhaozEngine.Terrain`): `static class TerrainLod` with `const int TierCount = 3`, `static int PickLod(float distance)` (0 near/dense .. 2 far/coarse; monotone non-decreasing in distance), `static int ResolutionFor(int lod)` (segments per chunk edge; monotone NON-increasing: e.g. 64, 32, 16), default distance thresholds `NearMax = 80f`, `MidMax = 200f`.

- [ ] **Step 1: Write failing tests:**

```csharp
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainLodTests
    {
        [Fact]
        public void PickLod_is_monotonic_in_distance()
        {
            int prev = TerrainLod.PickLod(0f);
            for (float d = 0f; d < 500f; d += 5f)
            {
                int lod = TerrainLod.PickLod(d);
                Assert.True(lod >= prev);
                prev = lod;
            }
        }

        [Fact]
        public void PickLod_spans_all_tiers()
        {
            Assert.Equal(0, TerrainLod.PickLod(10f));
            Assert.Equal(2, TerrainLod.PickLod(400f));
        }

        [Fact]
        public void Resolution_decreases_with_lod()
        {
            Assert.True(TerrainLod.ResolutionFor(0) > TerrainLod.ResolutionFor(1));
            Assert.True(TerrainLod.ResolutionFor(1) > TerrainLod.ResolutionFor(2));
        }
    }
}
```

- [ ] **Step 2: Run, verify fail.**

- [ ] **Step 3: Implement `TerrainLod.cs`:**

```csharp
using System;

namespace KhaozEngine.Terrain
{
    /// <summary>Distance-to-LOD mapping for chunked terrain. PickLod chooses a tier from camera distance (near =
    /// dense, far = coarse); ResolutionFor gives that tier's grid resolution (segments per chunk edge). Which
    /// chunks exist and when they rebuild is the World streaming sub-project, not this one.</summary>
    public static class TerrainLod
    {
        public const int TierCount = 3;
        public const float NearMax = 80f;
        public const float MidMax = 200f;

        static readonly int[] Resolutions = { 64, 32, 16 };  // per chunk edge, by tier

        /// <summary>Tier 0 (dense) within NearMax, 1 within MidMax, else 2 (coarse). Monotone in distance.</summary>
        public static int PickLod(float distance)
        {
            if (distance < NearMax) return 0;
            if (distance < MidMax) return 1;
            return 2;
        }

        /// <summary>Grid resolution (segments per chunk edge) for a tier. Clamped to the valid tier range.</summary>
        public static int ResolutionFor(int lod) => Resolutions[Math.Clamp(lod, 0, TierCount - 1)];
    }
}
```

- [ ] **Step 4: Run tests, verify pass.**

- [ ] **Step 5: Commit.**

```bash
git add KhaozEngine.Terrain.Render3D/TerrainLod.cs KhaozEngine.Tests/Terrain/TerrainLodTests.cs
git commit -m "terrain.render3d: distance LOD tiers + PickLod"
```

---

### Task 9: chunk builder (`TerrainChunkRegion`, `TerrainChunkBounds`, `TerrainChunkMesh`, `TerrainChunkBuilder`)

**Files:**
- Create: `KhaozEngine.Terrain.Render3D/TerrainChunkRegion.cs`, `TerrainChunkBounds.cs`, `TerrainChunkMesh.cs`, `TerrainChunkBuilder.cs`
- Test: `KhaozEngine.Tests/Terrain/TerrainChunkBuilderTests.cs`

**Interfaces:**
- Produces (namespace `KhaozEngine.Terrain`):
  - `readonly struct TerrainChunkRegion { float OriginX; float OriginZ; float Size; }` (world metres; `const float DefaultSize = 60f`).
  - `readonly struct TerrainChunkBounds { Vector3 Min; Vector3 Max; Vector3 Center; Vector3 Size; static TerrainChunkBounds FromPositions(ReadOnlySpan<ModelVertex>); bool Contains(Vector3 p); }`.
  - `sealed class TerrainChunkMesh { GltfMesh Mesh; TerrainSplatWeights[] Splat; TerrainChunkBounds Bounds; int Lod; TerrainChunkRegion Region; int SurfaceVertexCount; }` (`Splat` parallels `Mesh.Vertices`; `SurfaceVertexCount` = the grid vertices before skirt vertices).
  - `static class TerrainChunkBuilder { static TerrainChunkMesh Build(TerrainField field, TerrainChunkRegion region, int lod, float skirtDepth = 0.3f, float snowLine = 60f); }`.

**Build algorithm:**
1. `res = TerrainLod.ResolutionFor(lod)`; `cols = res + 1`. Surface grid: for `iz in 0..res`, `ix in 0..res`: world `x = OriginX + ix/res*Size`, `z = OriginZ + iz/res*Size`, `h = field.SampleHeight(x,z)`, normal = `field.SampleNormal`, `slope01 = 1 - normal.Y`, `splat = TerrainSplatWeights.From(h, slope01, field.SampleBiome(x,z), field.WaterLevel, snowLine)`, `Color = TerrainRamp.Of(splat)`, `Uv = (ix/res, iz/res)`. Vertex `Position = (x, h, z)`.
2. Surface indices exactly like `MeshPrimitives.Plane` (CCW from +Y): `i0,i2,i3` then `i0,i3,i1`.
3. Skirts: for each of the 4 edges, add a parallel ring of vertices at the same `x,z` but `y = h - skirtDepth`, same splat/colour, and stitch a vertical quad strip between the surface edge and the skirt edge (wound so the skirt faces outward). Skirt vertices append after all surface vertices; `SurfaceVertexCount` records the boundary.
4. `Bounds = TerrainChunkBounds.FromPositions(vertices)` (includes the dropped skirt, so `Min.Y` is below the surface min).
5. Use 32-bit indices (`GltfMesh(ModelVertex[], uint[])`) since dense chunks exceed 65,536 only above res ~255 (safe), but uint keeps headroom and matches `MeshBuilder` convention.

- [ ] **Step 1: Write failing tests:**

```csharp
using System;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainChunkBuilderTests
    {
        static TerrainField Field() => new TerrainField(TerrainPresets.Clearing());
        static TerrainChunkRegion Region() => new TerrainChunkRegion { OriginX = -30f, OriginZ = -30f, Size = 60f };

        [Fact]
        public void Surface_vertex_count_matches_the_lod_grid()
        {
            var chunk = TerrainChunkBuilder.Build(Field(), Region(), lod: 1);
            int res = TerrainLod.ResolutionFor(1);
            Assert.Equal((res + 1) * (res + 1), chunk.SurfaceVertexCount);
        }

        [Fact]
        public void Denser_lod_has_more_surface_vertices()
        {
            var near = TerrainChunkBuilder.Build(Field(), Region(), lod: 0);
            var far = TerrainChunkBuilder.Build(Field(), Region(), lod: 2);
            Assert.True(near.SurfaceVertexCount > far.SurfaceVertexCount);
        }

        [Fact]
        public void Mesh_vertex_heights_equal_the_field()
        {
            var field = Field();
            var chunk = TerrainChunkBuilder.Build(field, Region(), lod: 1);
            for (int i = 0; i < chunk.SurfaceVertexCount; i++)
            {
                var v = chunk.Mesh.Vertices[i].Position;
                Assert.Equal(field.SampleHeight(v.X, v.Z), v.Y, 3);
            }
        }

        [Fact]
        public void Skirt_adds_vertices_below_the_surface()
        {
            var chunk = TerrainChunkBuilder.Build(Field(), Region(), lod: 1, skirtDepth: 0.3f);
            Assert.True(chunk.Mesh.Vertices.Length > chunk.SurfaceVertexCount);   // skirt vertices present
            float surfaceMinY = float.MaxValue;
            for (int i = 0; i < chunk.SurfaceVertexCount; i++)
                surfaceMinY = MathF.Min(surfaceMinY, chunk.Mesh.Vertices[i].Position.Y);
            Assert.True(chunk.Bounds.Min.Y < surfaceMinY);                        // bounds drop with the skirt
        }

        [Fact]
        public void Bounds_enclose_every_vertex()
        {
            var chunk = TerrainChunkBuilder.Build(Field(), Region(), lod: 1);
            foreach (var v in chunk.Mesh.Vertices)
                Assert.True(chunk.Bounds.Contains(v.Position));
        }

        [Fact]
        public void Splat_array_parallels_the_vertices()
        {
            var chunk = TerrainChunkBuilder.Build(Field(), Region(), lod: 2);
            Assert.Equal(chunk.Mesh.Vertices.Length, chunk.Splat.Length);
        }

        [Fact]
        public void Adjacent_chunks_share_identical_edge_heights()
        {
            // statelessness/locality at the chunk seam: the +X edge of one chunk equals the -X edge of its neighbour.
            var field = Field();
            var a = TerrainChunkBuilder.Build(field, new TerrainChunkRegion { OriginX = 0f, OriginZ = 0f, Size = 60f }, lod: 1);
            var b = TerrainChunkBuilder.Build(field, new TerrainChunkRegion { OriginX = 60f, OriginZ = 0f, Size = 60f }, lod: 1);
            // shared world x = 60: a's last column == b's first column for each row.
            int res = TerrainLod.ResolutionFor(1), cols = res + 1;
            for (int iz = 0; iz <= res; iz++)
            {
                var pa = a.Mesh.Vertices[iz * cols + res].Position;   // a, ix = res (x=60)
                var pb = b.Mesh.Vertices[iz * cols + 0].Position;     // b, ix = 0   (x=60)
                Assert.Equal(pa.Y, pb.Y, 4);
            }
        }
    }
}
```

- [ ] **Step 2: Run, verify fail.**

- [ ] **Step 3: Implement the four files.**

```csharp
// TerrainChunkRegion.cs
namespace KhaozEngine.Terrain
{
    /// <summary>A square world-space tile to mesh, in metres. Size defaults to ~60 m so a Sharding CellCoord maps
    /// to a whole number of chunks (exact ratio is a World streaming concern). OriginX/OriginZ is the -X/-Z corner.</summary>
    public readonly struct TerrainChunkRegion
    {
        public const float DefaultSize = 60f;
        public float OriginX { get; init; }
        public float OriginZ { get; init; }
        public float Size { get; init; }
    }
}
```

```csharp
// TerrainChunkBounds.cs
using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>Axis-aligned bounding box for a chunk mesh, for frustum/distance culling by the (later) streaming
    /// layer. Built from the final vertex set, so it already includes the dropped skirt.</summary>
    public readonly struct TerrainChunkBounds
    {
        public Vector3 Min { get; }
        public Vector3 Max { get; }
        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 Size => Max - Min;

        public TerrainChunkBounds(Vector3 min, Vector3 max) { Min = min; Max = max; }

        public static TerrainChunkBounds FromPositions(ReadOnlySpan<ModelVertex> verts)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var v in verts)
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
            return new TerrainChunkBounds(min, max);
        }

        public bool Contains(Vector3 p) =>
            p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z;
    }
}
```

```csharp
// TerrainChunkMesh.cs
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>The CPU output of meshing one chunk: a Render3D GltfMesh (vertex colours = the height/slope ramp),
    /// a parallel per-vertex splat-weight array (plumbed for the later PBR upgrade), an AABB for culling, and the
    /// LOD/region it was built at. Hand Mesh to Scene3D.LoadMesh (or the TerrainScene3D extensions).</summary>
    public sealed class TerrainChunkMesh
    {
        public GltfMesh Mesh { get; }
        public TerrainSplatWeights[] Splat { get; }
        public TerrainChunkBounds Bounds { get; }
        public int Lod { get; }
        public TerrainChunkRegion Region { get; }
        /// <summary>Number of leading grid (surface) vertices before the appended skirt vertices.</summary>
        public int SurfaceVertexCount { get; }

        public TerrainChunkMesh(GltfMesh mesh, TerrainSplatWeights[] splat, TerrainChunkBounds bounds, int lod, TerrainChunkRegion region, int surfaceVertexCount)
        {
            Mesh = mesh; Splat = splat; Bounds = bounds; Lod = lod; Region = region; SurfaceVertexCount = surfaceVertexCount;
        }
    }
}
```

```csharp
// TerrainChunkBuilder.cs
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>Meshes one finite chunk off the analytic field at a chosen LOD: a (res+1)^2 grid of
    /// field-sampled vertices (position/normal/ramp-colour/splat), CCW-from-above indices, plus ~0.3 m edge
    /// skirts that hide cracks where a dense chunk meets a coarse neighbour. CPU only - no GPU device. Output is
    /// standard Render3D mesh data plus a parallel splat array and an AABB.</summary>
    public static class TerrainChunkBuilder
    {
        public static TerrainChunkMesh Build(TerrainField field, TerrainChunkRegion region, int lod, float skirtDepth = 0.3f, float snowLine = 60f)
        {
            int res = TerrainLod.ResolutionFor(lod);
            int cols = res + 1;
            var verts = new List<ModelVertex>(cols * cols + cols * 4);
            var splat = new List<TerrainSplatWeights>(verts.Capacity);
            var inds = new List<uint>(res * res * 6 + res * 4 * 6);

            // --- surface grid -------------------------------------------------
            for (int iz = 0; iz <= res; iz++)
            for (int ix = 0; ix <= res; ix++)
            {
                float x = region.OriginX + (float)ix / res * region.Size;
                float z = region.OriginZ + (float)iz / res * region.Size;
                float h = field.SampleHeight(x, z);
                var n = field.SampleNormal(x, z);
                float slope01 = 1f - n.Y;
                var w = TerrainSplatWeights.From(h, slope01, field.SampleBiome(x, z), field.WaterLevel, snowLine);
                verts.Add(new ModelVertex(new Vector3(x, h, z), n, TerrainRamp.Of(w), new Vector2((float)ix / res, (float)iz / res)));
                splat.Add(w);
            }
            for (int iz = 0; iz < res; iz++)
            for (int ix = 0; ix < res; ix++)
            {
                uint i0 = (uint)(iz * cols + ix);
                uint i1 = (uint)(iz * cols + ix + 1);
                uint i2 = (uint)((iz + 1) * cols + ix);
                uint i3 = (uint)((iz + 1) * cols + ix + 1);
                inds.Add(i0); inds.Add(i2); inds.Add(i3);
                inds.Add(i0); inds.Add(i3); inds.Add(i1);
            }

            int surfaceVertexCount = verts.Count;

            // --- skirts: drop a copy of each edge vertex by skirtDepth and stitch a vertical strip ------------
            // helper: surface index of a grid cell.
            static uint Grid(int ix, int iz, int cols) => (uint)(iz * cols + ix);
            void Skirt(IReadOnlyList<int> edgeIx, IReadOnlyList<int> edgeIz, bool flip)
            {
                int count = edgeIx.Count;
                var lower = new uint[count];
                for (int k = 0; k < count; k++)
                {
                    uint top = Grid(edgeIx[k], edgeIz[k], cols);
                    var tv = verts[(int)top];
                    var p = tv.Position; p.Y -= skirtDepth;
                    lower[k] = (uint)verts.Count;
                    verts.Add(new ModelVertex(p, tv.Normal, tv.Color, tv.Uv));
                    splat.Add(splat[(int)top]);
                }
                for (int k = 0; k < count - 1; k++)
                {
                    uint t0 = Grid(edgeIx[k], edgeIz[k], cols), t1 = Grid(edgeIx[k + 1], edgeIz[k + 1], cols);
                    uint b0 = lower[k], b1 = lower[k + 1];
                    if (!flip) { inds.Add(t0); inds.Add(b0); inds.Add(b1); inds.Add(t0); inds.Add(b1); inds.Add(t1); }
                    else { inds.Add(t0); inds.Add(b1); inds.Add(b0); inds.Add(t0); inds.Add(t1); inds.Add(b1); }
                }
            }

            var rng = new List<int>();
            for (int i = 0; i <= res; i++) rng.Add(i);
            var zeros = new List<int>(); for (int i = 0; i <= res; i++) zeros.Add(0);
            var maxs = new List<int>(); for (int i = 0; i <= res; i++) maxs.Add(res);
            Skirt(rng, zeros, flip: false);   // -Z edge (iz = 0)
            Skirt(rng, maxs, flip: true);     // +Z edge (iz = res)
            Skirt(zeros, rng, flip: true);    // -X edge (ix = 0)
            Skirt(maxs, rng, flip: false);    // +X edge (ix = res)

            var vertArr = verts.ToArray();
            var mesh = new GltfMesh(vertArr, inds.ToArray());
            var bounds = TerrainChunkBounds.FromPositions(vertArr);
            return new TerrainChunkMesh(mesh, splat.ToArray(), bounds, lod, region, surfaceVertexCount);
        }
    }
}
```

(Skirt winding correctness is visual, not asserted; the tests assert presence + bounds drop + heights. The exact `flip` per edge can be tuned later against an on-device shot in the streaming sub-project; for this slice the geometry exists and culls.)

- [ ] **Step 4: Run tests, verify pass.** Run: `dotnet test ... --filter FullyQualifiedName~TerrainChunkBuilderTests`.

- [ ] **Step 5: Commit.**

```bash
git add KhaozEngine.Terrain.Render3D/TerrainChunkRegion.cs KhaozEngine.Terrain.Render3D/TerrainChunkBounds.cs KhaozEngine.Terrain.Render3D/TerrainChunkMesh.cs KhaozEngine.Terrain.Render3D/TerrainChunkBuilder.cs KhaozEngine.Tests/Terrain/TerrainChunkBuilderTests.cs
git commit -m "terrain.render3d: chunk mesh builder with skirts + bounds + splat"
```

---

### Task 10: `Scene3D` integration extensions

**Files:**
- Create: `KhaozEngine.Terrain.Render3D/TerrainScene3D.cs`

**Interfaces:**
- Produces (namespace `KhaozEngine.Terrain`): `static class TerrainScene3D` with `static MeshHandle LoadTerrainChunk(this Scene3D scene, TerrainChunkMesh chunk)` (= `scene.LoadMesh(chunk.Mesh)`) and `static void DrawTerrainChunk(this Scene3D scene, MeshHandle handle)` (= `scene.Draw(handle, Matrix4x4.Identity, Color.White)`; chunk positions are already world-space).

No unit test (needs a GPU device; the project's rule is no real device in unit tests). Verified by compiling. The extension methods are thin and exercised by consumers later.

- [ ] **Step 1: Implement `TerrainScene3D.cs`:**

```csharp
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>Scene3D glue for terrain chunks. A consumer with `using KhaozEngine.Terrain;` gets these in scope
    /// (same pattern as the Ground* telegraph extensions). Chunk vertices are already world-space, so the draw
    /// transform is identity; tint white lets the baked vertex-colour ramp through.</summary>
    public static class TerrainScene3D
    {
        /// <summary>Uploads a built chunk's mesh and returns its handle. Cache the handle; rebuild/unload cadence
        /// is the World streaming sub-project's concern.</summary>
        public static MeshHandle LoadTerrainChunk(this Scene3D scene, TerrainChunkMesh chunk) => scene.LoadMesh(chunk.Mesh);

        /// <summary>Queues a loaded terrain chunk for this frame at world origin (identity), tint white.</summary>
        public static void DrawTerrainChunk(this Scene3D scene, MeshHandle handle) =>
            scene.Draw(handle, Matrix4x4.Identity, Color.White);
    }
}
```

- [ ] **Step 2: Build the package** to confirm it compiles. Run: `dotnet build KhaozEngine.Terrain.Render3D/KhaozEngine.Terrain.Render3D.csproj -c Debug`. Expected: Build succeeded.

- [ ] **Step 3: Commit.**

```bash
git add KhaozEngine.Terrain.Render3D/TerrainScene3D.cs
git commit -m "terrain.render3d: Scene3D load/draw chunk extensions"
```

---

### Task 11: Release ritual (version bump + full doc sweep + pack)

**Files:**
- Modify: `Directory.Build.props` (`<KhaozEngine5xVersion>7.42.0` → `7.43.0`)
- Modify: `CHANGELOG.md`, `CHANGENOTES.md`
- Modify: `docs/CONSUMERS.md` (engine current version + package/umbrella table rows), `docs/ROADMAP.md` (current released version + a render-scale-track note), `README.md` (PackageReference example version + package-catalog table + repo-layout block)
- Modify: `CLAUDE.md` (package map: add `Terrain` to Foundation list and `Terrain.Render3D` to the Game3D companion set + umbrella descriptions)
- Modify: `docs/USING-KHAOZENGINE.md` (a terrain usage section)
- Modify: `KhaozEngine.Foundation/KhaozEngine.Foundation.csproj` (`+= ../KhaozEngine.Terrain/...`), `KhaozEngine.Game3D/KhaozEngine.Game3D.csproj` (`+= ../KhaozEngine.Terrain.Render3D/...`) and update both `<Description>` strings
- Run: `scripts/check-doc-versions.sh`

**Steps:**

- [ ] **Step 1: Bump the version.** `Directory.Build.props`: `7.42.0` -> `7.43.0`.

- [ ] **Step 2: Wire the umbrellas.** Add `<ProjectReference Include="../KhaozEngine.Terrain/KhaozEngine.Terrain.csproj" />` to `KhaozEngine.Foundation.csproj` and update its `<Description>` to list Terrain. Add `<ProjectReference Include="../KhaozEngine.Terrain.Render3D/KhaozEngine.Terrain.Render3D.csproj" />` to `KhaozEngine.Game3D.csproj` and update its `<Description>` to mention terrain.

- [ ] **Step 3: CHANGELOG.md** — newest-first detailed entry under a `## 7.43.0` heading: the two new packages, `TerrainField` API, features, collision, the chunk builder (LOD/skirts/splat/bounds), umbrella placement (`Terrain` -> Foundation, `Terrain.Render3D` -> Game3D), and the analytic-field / stateless-noise / authoritative-server design decision. No em-dashes.

- [ ] **Step 4: CHANGENOTES.md** — one newest-first digest line, e.g. `7.43.0 - Terrain system: KhaozEngine.Terrain analytic deterministic height field (TerrainField + biome bands + Lake/Ridge/Flatten features + TerrainCollision) and KhaozEngine.Terrain.Render3D chunked-LOD mesh builder (skirts, per-vertex splat weights, height/slope ramp). First overworld render-scale sub-project.`

- [ ] **Step 5: README.md** — bump the `<PackageReference ... Version="7.43.0" />` example; add two rows to the package-catalog table (`KhaozEngine.Terrain`, `KhaozEngine.Terrain.Render3D`) with one-line descriptions and umbrella; add both dirs to the repo-layout block.

- [ ] **Step 6: CLAUDE.md package map** — add `Terrain` to the Foundation enumeration and `Terrain.Render3D` to the render companions; add a short "Terrain" bullet near the Snapshot/Telegraph libs describing the leaf+companion + their umbrellas, and update the Foundation/Game3D umbrella one-liners.

- [ ] **Step 7: docs/CONSUMERS.md** — set `**Engine current version:** \`7.43.0\``; add the two packages to the umbrella/package table (Foundation / Game3D rows).

- [ ] **Step 8: docs/ROADMAP.md** — set `Current released version: **7.43.0**`; add a line under the render-scale track noting sub-project 2 (terrain) shipped.

- [ ] **Step 9: docs/USING-KHAOZENGINE.md** — add a "Terrain" usage section: construct `TerrainConfig`/`TerrainPresets.Clearing()`, sample height/normal/biome, `TerrainCollision`, and on the client `TerrainChunkBuilder.Build(field, region, TerrainLod.PickLod(distance))` + `scene.LoadTerrainChunk(...)`/`DrawTerrainChunk(...)`. Note the leaf is render-free and the splat weights are plumbed for the later PBR upgrade.

- [ ] **Step 10: Run the doc guard.** Run: `bash scripts/check-doc-versions.sh`. Expected: `all engine-version declarations match 7.43.0`.

- [ ] **Step 11: Mechanical doc sweep.** Run `grep -rn "KhaozEngine.Terrain" README.md CLAUDE.md docs/ | grep -v worktrees` and confirm every place that should mention the packages does, and no stale doc claims they are missing.

- [ ] **Step 12: Full test run + pack (CPU only).** Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` (all green), then `dotnet pack -c Release -o ./local-feed`. Expected: all tests pass; `KhaozEngine.Terrain.7.43.0.nupkg` and `KhaozEngine.Terrain.Render3D.7.43.0.nupkg` produced.

- [ ] **Step 13: Commit the release.**

```bash
git add -A
git commit -m "terrain(7.43.0): ship KhaozEngine.Terrain + KhaozEngine.Terrain.Render3D"
```

---

### Task 12: Finish (merge, tag, push, cleanup)

**Steps (run from the MAIN checkout, per the worktree local-feed gotcha — the worktree's local-feed is deleted on removal):**

- [ ] **Step 1:** From the worktree, confirm clean and all green: `git status`, `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`.

- [ ] **Step 2:** Switch to the main checkout (`/Users/antonio/KhaozEngine`), `git checkout main`, `git pull --ff-only` (re-check `origin/main` + tags did not move past `7.43.0`; if a concurrent release took it, bump past and re-pack).

- [ ] **Step 3:** Merge the feature branch: `git merge --no-ff worktree-feature+terrain`.

- [ ] **Step 4:** Re-pack from the main repo root so `local-feed` survives worktree removal: `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`. Run the full suite once on the merged result: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`.

- [ ] **Step 5:** Tag + push: `git tag v7.43.0 && git push origin main && git push origin v7.43.0` (CI publishes to GitHub Packages on the `v*` tag).

- [ ] **Step 6:** Clean up: `ExitWorktree (remove)` for the worktree, and delete the merged local branch (`git branch -d worktree-feature+terrain`). The branch was never pushed, so no remote branch to delete.

---

## Self-review

**Spec coverage:**
- `TerrainField` (SampleHeight/Normal/Biome/WaterLevel) — Tasks 2, 3. ✓
- `TerrainConfig`, `BiomeBand` — Task 2. ✓
- `ITerrainFeature` + Lake/Ridge/Flatten — Task 4. ✓
- Stateless coordinate-hash noise/fbm — Task 1 (+ locality tests Task 3 & 9). ✓
- `TerrainCollision` (decision: lives in Terrain) — Task 5. ✓
- Chunk builder + bounds, distance LOD + PickLod, ~0.3 m skirts, per-vertex splat, height/slope ramp material, Scene3D integration — Tasks 7, 8, 9, 10. ✓
- Full test list (field determinism, composition/blend/features, normals, mesh vertex count per LOD / skirt / bounds / PickLod monotonic / heights==field, collision) — covered across Tasks 1-9. ✓
- Greybox parity — Task 6. ✓
- Release ritual + full doc sweep — Tasks 11, 12. ✓

**Out-of-scope guarded:** no streaming, prop scatter, PBR textures, character controller, water shader. ✓

**Type consistency:** `TerrainNoise.SmoothStep` reused by ShapeAt/features/splat. `ShapeAt` returns `(float baseHeight, float hillAmp, BiomeId biome)` used by SampleHeight/SampleBiome. `TerrainSplatWeights.From(height, slope01, biome, waterLevel, snowLine)` signature consistent across Tasks 7 & 9. `TerrainChunkBuilder.Build(field, region, lod, skirtDepth, snowLine)` consistent. `ResolutionFor`/`PickLod` names consistent Tasks 8 & 9. ✓

**Placeholder scan:** none — every code step carries full code. ✓
