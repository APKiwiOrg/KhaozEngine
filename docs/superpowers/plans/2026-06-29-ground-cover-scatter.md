# Ground-cover scatter + understory companions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a dense short-radius ground-cover prop layer alongside the sparse tree layer, plus a deterministic understory-companion primitive that rings trees with small foliage, both on the existing opaque instanced prop path.

**Architecture:** Two additive pieces. (1) A pure `PropScatter.GenerateCompanions` primitive in `KhaozEngine.Terrain` that maps each host prop to a deterministic, tiling-invariant ring of companion placements, keyed off the host's centimetre-quantized world XZ. (2) A multi-layer `Scene3DChunkSink` in `KhaozEngine.Terrain.Render3D` that holds N `PropLayer`s (scatter or companion), each with its own meshes + draw radius, deriving companion layers from their host scatter layer per chunk so each host's companions emit exactly once.

**Tech Stack:** C# / net10.0, xUnit headless tests, the existing `TerrainField` / `PropScatter` / `Scene3D` stack. MonoGame-free.

## Global Constraints

- **Version: additive minor 7.71.0** (current line 7.70.0 in `Directory.Build.props`). One bump at the end of the batch (Task 5), not per task.
- **No new material:** foliage is solid geometry on the opaque-only prop path. No alpha/transparency.
- **No protocol change. No collision change.** Companion/ground-cover placements are render-only; the sink never feeds them to collision.
- **Determinism + tiling-invariance** is the core property of the companion primitive: companions over a host set equal the union of companions over any tiling of that host set.
- **Back-compat:** the existing single-layer `Scene3DChunkSink` ctor stays and produces byte-identical placements.
- **Every new behaviour ships with a headless test** in `KhaozEngine.Tests` (no GPU; construct fields/configs and assert placements).
- **No em-dashes** anywhere (code comments, CHANGELOG, commits). Conventional-commit subjects `area(scope): summary`; dev commits use `terrain(ground-cover): ...`, the release commit uses `terrain(7.71.0): ...`.
- Namespace for both `KhaozEngine.Terrain` and `KhaozEngine.Terrain.Render3D` project files is `KhaozEngine.Terrain`. `KhaozEngine.Tests` already has `InternalsVisibleTo` for the Render3D project.
- All commands run from the worktree root: `/Users/antonio/KhaozEngine/.claude/worktrees/feature+ground-cover-scatter`.

---

## File Structure

- `KhaozEngine.Terrain/PropScatter.cs` (modify) - add `CompanionConfig` class + `GenerateCompanions` + private companion hash helper. All scatter types already live in this file.
- `KhaozEngine.Tests/Terrain/CompanionScatterTests.cs` (create) - headless tests for the companion primitive.
- `KhaozEngine.Terrain.Render3D/PropLayer.cs` (create) - the `PropLayer` tagged struct + `ScatterLayer`/`CompanionLayer` factories.
- `KhaozEngine.Terrain.Render3D/Scene3DChunkSink.cs` (modify) - generalize to N layers, add `ScatterLayersFor`, derive companions, multi-layer `Draw`, keep the single-layer ctor.
- `KhaozEngine.Tests/Terrain/Scene3DChunkSinkTests.cs` (create) - multi-layer + back-compat + sink-level companion exactly-once + ctor validation tests.
- Release-only doc/version files in Task 5.

---

## Task 1: `CompanionConfig` + `GenerateCompanions` primitive

**Files:**
- Modify: `KhaozEngine.Terrain/PropScatter.cs`
- Test: `KhaozEngine.Tests/Terrain/CompanionScatterTests.cs` (create)

**Interfaces:**
- Consumes: `TerrainField.SampleHeight(float,float)`, `PropPlacement`, `PropKind`, `TerrainNoise.Hash2(int,int,int)`, the existing private `PropScatter.PickKind(PropKind[], float)`.
- Produces:
  - `public sealed class CompanionConfig` with public fields `int Seed; string[] HostKinds; PropKind[] Kinds; int CountMin; int CountMax; float RadiusMin; float RadiusMax; float ScaleMin; float ScaleMax; float? MaxHeight;`
  - `public static IReadOnlyList<PropPlacement> PropScatter.GenerateCompanions(TerrainField field, IReadOnlyList<PropPlacement> hosts, CompanionConfig config)`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/Terrain/CompanionScatterTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Headless tests for the deterministic understory-companion primitive (render-free leaf, no GPU):
    /// determinism, tiling/streaming invariance, ring + count bounds, host-kind filter, kind membership, the
    /// MaxHeight exclusion, and the no-collision-coupling contract.</summary>
    public class CompanionScatterTests
    {
        // Flat single-biome field: SampleHeight == height everywhere, SampleBiome == Meadow.
        static TerrainField FlatField(float height = 0f) =>
            new TerrainField(new TerrainConfig
            {
                Seed = 7,
                WaterLevel = -1000f,
                GentleAmplitude = 0f,
                Biomes = new[]
                {
                    new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = height, HillAmplitude = 0f },
                },
            });

        // Gently-rolling field so ring sample heights straddle 0 (for the MaxHeight test).
        static TerrainField RollingField() =>
            new TerrainField(new TerrainConfig
            {
                Seed = 3,
                WaterLevel = -1000f,
                GentleAmplitude = 2f,
                GentleFrequency = 0.05f,
                Biomes = new[]
                {
                    new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f },
                },
            });

        // A host scatter that places a "pine_a" tree in every cell (density 1, no clearing, no height cap).
        static ScatterConfig TreeHosts() =>
            new ScatterConfig
            {
                Seed = 1234,
                CellSize = 8f,
                Jitter = 1.5f,
                ClearingRadius = 0f,
                MaxHeight = null,
                Biomes = new[]
                {
                    new BiomeScatterRule { Biome = BiomeId.Meadow, Density = 1f, Kinds = new[] { new PropKind("pine_a", 1f) } },
                },
            };

        static CompanionConfig Comp() =>
            new CompanionConfig
            {
                Seed = 555,
                HostKinds = new[] { "pine_a" },
                Kinds = new[] { new PropKind("bush", 0.5f), new PropKind("fern", 0.5f) },
                CountMin = 2,
                CountMax = 4,
                RadiusMin = 0.6f,
                RadiusMax = 1.8f,
                ScaleMin = 0.7f,
                ScaleMax = 1.1f,
                MaxHeight = null,
            };

        static string Key(PropPlacement p) => $"{p.X:F3},{p.Z:F3},{p.Y:F3},{p.Id},{p.Variant}";

        [Fact]
        public void GenerateCompanions_IsDeterministic()
        {
            TerrainField f = FlatField();
            var hosts = PropScatter.Generate(f, TreeHosts(), new RectArea(-40, -40, 40, 40));
            Assert.NotEmpty(hosts);

            var a = PropScatter.GenerateCompanions(f, hosts, Comp());
            var b = PropScatter.GenerateCompanions(f, hosts, Comp());

            Assert.NotEmpty(a);
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].Id, b[i].Id);
                Assert.Equal(a[i].X, b[i].X, 5);
                Assert.Equal(a[i].Z, b[i].Z, 5);
                Assert.Equal(a[i].Y, b[i].Y, 5);
                Assert.Equal(a[i].Scale, b[i].Scale, 5);
                Assert.Equal(a[i].Yaw, b[i].Yaw, 5);
                Assert.Equal(a[i].Variant, b[i].Variant);
            }
        }

        [Fact]
        public void GenerateCompanions_IsTilingInvariant()
        {
            TerrainField f = FlatField();
            ScatterConfig hostCfg = TreeHosts();
            CompanionConfig c = Comp();

            var whole = PropScatter.GenerateCompanions(f,
                PropScatter.Generate(f, hostCfg, new RectArea(-50, -50, 50, 50)), c);

            var q1 = PropScatter.GenerateCompanions(f, PropScatter.Generate(f, hostCfg, new RectArea(-50, -50, 0, 0)), c);
            var q2 = PropScatter.GenerateCompanions(f, PropScatter.Generate(f, hostCfg, new RectArea(0, -50, 50, 0)), c);
            var q3 = PropScatter.GenerateCompanions(f, PropScatter.Generate(f, hostCfg, new RectArea(-50, 0, 0, 50)), c);
            var q4 = PropScatter.GenerateCompanions(f, PropScatter.Generate(f, hostCfg, new RectArea(0, 0, 50, 50)), c);

            var union = q1.Concat(q2).Concat(q3).Concat(q4).ToList();
            Assert.NotEmpty(whole);
            Assert.Equal(whole.Count, union.Count);
            Assert.Equal(whole.Select(Key).OrderBy(s => s).ToList(), union.Select(Key).OrderBy(s => s).ToList());
        }

        [Fact]
        public void GenerateCompanions_RingAndCountWithinBounds()
        {
            TerrainField f = FlatField();
            CompanionConfig c = Comp();
            var host = new PropPlacement("pine_a", 5f, 0f, 7f, 1f, 0f, 0);

            var comps = PropScatter.GenerateCompanions(f, new[] { host }, c);

            Assert.InRange(comps.Count, c.CountMin, c.CountMax);
            foreach (PropPlacement p in comps)
            {
                float r = MathF.Sqrt((p.X - host.X) * (p.X - host.X) + (p.Z - host.Z) * (p.Z - host.Z));
                Assert.InRange(r, c.RadiusMin - 1e-3f, c.RadiusMax + 1e-3f);
            }
        }

        [Fact]
        public void GenerateCompanions_OnlyHostKindsSpawn()
        {
            TerrainField f = FlatField();
            CompanionConfig c = Comp();   // HostKinds = { "pine_a" }

            var nonHost = new PropPlacement("rock_a", 5f, 0f, 7f, 1f, 0f, 0);
            Assert.Empty(PropScatter.GenerateCompanions(f, new[] { nonHost }, c));

            var host = new PropPlacement("pine_a", 5f, 0f, 7f, 1f, 0f, 0);
            Assert.NotEmpty(PropScatter.GenerateCompanions(f, new[] { host }, c));
        }

        [Fact]
        public void GenerateCompanions_KindsAreFromTheConfig()
        {
            TerrainField f = FlatField();
            CompanionConfig c = Comp();
            var hosts = PropScatter.Generate(f, TreeHosts(), new RectArea(-40, -40, 40, 40));

            var comps = PropScatter.GenerateCompanions(f, hosts, c);
            var allowed = new HashSet<string>(c.Kinds.Select(k => k.Id));

            Assert.NotEmpty(comps);
            foreach (PropPlacement p in comps)
            {
                Assert.Contains(p.Id, allowed);
                Assert.InRange(p.Variant, 0, c.Kinds.Length - 1);
            }
        }

        [Fact]
        public void GenerateCompanions_RespectsMaxHeight()
        {
            TerrainField f = RollingField();   // heights straddle 0
            var hosts = PropScatter.Generate(f, TreeHosts(), new RectArea(-80, -80, 80, 80));
            Assert.NotEmpty(hosts);

            var uncapped = PropScatter.GenerateCompanions(f, hosts, Comp());
            CompanionConfig capped = Comp();
            capped.MaxHeight = 0f;
            var withCap = PropScatter.GenerateCompanions(f, hosts, capped);

            Assert.NotEmpty(uncapped);
            Assert.True(withCap.Count < uncapped.Count, "MaxHeight should exclude some companions");
            foreach (PropPlacement p in withCap)
                Assert.True(f.SampleHeight(p.X, p.Z) <= 0f, $"companion above cap at {p.X},{p.Z}");
        }

        [Fact]
        public void Companions_AreRenderOnly_IdsDisjointFromHostColliderKinds()
        {
            // Contract: companions use foliage ids disjoint from the host (tree) kinds, so a consumer that
            // builds colliders from the tree scatter (PropColliders.FromScatter over the host config) never
            // includes a companion. GenerateCompanions itself touches no collision type.
            TerrainField f = FlatField();
            var hosts = PropScatter.Generate(f, TreeHosts(), new RectArea(-40, -40, 40, 40));
            var comps = PropScatter.GenerateCompanions(f, hosts, Comp());

            var hostKinds = new HashSet<string>(TreeHosts().Biomes[0].Kinds.Select(k => k.Id));
            Assert.NotEmpty(comps);
            foreach (PropPlacement p in comps)
                Assert.DoesNotContain(p.Id, hostKinds);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CompanionScatterTests" -v q`
Expected: FAIL to compile / `CompanionConfig` and `GenerateCompanions` do not exist.

- [ ] **Step 3: Add `CompanionConfig` to `PropScatter.cs`**

In `KhaozEngine.Terrain/PropScatter.cs`, add this class after the `ScatterConfig` class (before the `PropScatter` static class):

```csharp
    /// <summary>Inputs for <see cref="PropScatter.GenerateCompanions"/>: rings each host prop whose
    /// <see cref="PropPlacement.Id"/> is in <see cref="HostKinds"/> with a few small-foliage instances, so
    /// trees are dressed at the base instead of standing on bare ground. Every value (count, ring angle/radius,
    /// kind, scale, yaw) hashes off the host's centimetre-quantized world XZ + per-channel salts, so it is
    /// deterministic and tiling-invariant (the host set is tiling-invariant and each host maps independently to
    /// its companions). Render-only: companion ids carry no collider.</summary>
    public sealed class CompanionConfig
    {
        public int Seed = 1337;
        /// <summary>Host ids that spawn companions (e.g. the tree kit ids). A host whose Id is not here spawns none.</summary>
        public string[] HostKinds = Array.Empty<string>();
        /// <summary>Weighted companion kit ids (bush / fern / ...).</summary>
        public PropKind[] Kinds = Array.Empty<PropKind>();
        public int CountMin = 2;
        public int CountMax = 4;
        /// <summary>Ring offset from the host base, metres.</summary>
        public float RadiusMin = 0.6f;
        public float RadiusMax = 1.8f;
        public float ScaleMin = 0.7f;
        public float ScaleMax = 1.1f;
        /// <summary>Skip a companion whose resampled ground height exceeds this (same off-mountain exclusion as
        /// the host layer); null = no cap.</summary>
        public float? MaxHeight;
    }
```

- [ ] **Step 4: Add `GenerateCompanions` + helpers to the `PropScatter` class**

In the `PropScatter` static class in the same file, add the companion salts next to the existing salt constants:

```csharp
        // Independent companion hash channels (distinct from the scatter salts above).
        const int SaltCompanionCount = 0x2C1B3A4D;
        const int SaltCompanionAngle = 0x6E5F7081;
        const int SaltCompanionRadius = 0x3461F8B2;
        const int SaltCompanionKind = 0x51C0FFEE;
        const int SaltCompanionScale = 0x1ADF00D5;
        const int SaltCompanionYaw = 0x24681357;
```

Then add these methods inside the `PropScatter` class (after `Generate`):

```csharp
        /// <summary>Ring each host whose <see cref="PropPlacement.Id"/> is in <paramref name="config"/>'s
        /// <see cref="CompanionConfig.HostKinds"/> with <c>Count</c> small-foliage companions in a jittered ring,
        /// Y resampled from the field. Pure per-host: count/angle/radius/kind/scale/yaw hash off the host's
        /// centimetre-quantized world XZ + per-channel salts (never the host's list index, which is not
        /// tiling-invariant), so the result is deterministic and the union over any tiling of the hosts equals
        /// the whole. Render-only - companion placements carry no collider.</summary>
        public static IReadOnlyList<PropPlacement> GenerateCompanions(
            TerrainField field, IReadOnlyList<PropPlacement> hosts, CompanionConfig config)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (hosts == null) throw new ArgumentNullException(nameof(hosts));
            if (config == null) throw new ArgumentNullException(nameof(config));

            var result = new List<PropPlacement>();
            if (config.Kinds.Length == 0 || config.HostKinds.Length == 0 || config.CountMax < config.CountMin)
                return result;

            int span = config.CountMax - config.CountMin + 1;

            for (int i = 0; i < hosts.Count; i++)
            {
                PropPlacement host = hosts[i];
                if (!IsHostKind(config.HostKinds, host.Id)) continue;

                // Centimetre-quantized host position is the stable, tiling-invariant per-host hash key.
                int hx = (int)MathF.Round(host.X * 100f);
                int hz = (int)MathF.Round(host.Z * 100f);

                int count = config.CountMin + (int)(CompanionHash01(hx, hz, config.Seed, SaltCompanionCount, 0) * span);

                for (int j = 0; j < count; j++)
                {
                    float angle = CompanionHash01(hx, hz, config.Seed, SaltCompanionAngle, j) * MathF.Tau;
                    float radius = config.RadiusMin
                                   + CompanionHash01(hx, hz, config.Seed, SaltCompanionRadius, j) * (config.RadiusMax - config.RadiusMin);
                    float x = host.X + radius * MathF.Cos(angle);
                    float z = host.Z + radius * MathF.Sin(angle);

                    float y = field.SampleHeight(x, z);
                    if (config.MaxHeight is float cap && y > cap) continue;

                    int variant = PickKind(config.Kinds, CompanionHash01(hx, hz, config.Seed, SaltCompanionKind, j));
                    float scale = config.ScaleMin
                                  + CompanionHash01(hx, hz, config.Seed, SaltCompanionScale, j) * (config.ScaleMax - config.ScaleMin);
                    float yaw = CompanionHash01(hx, hz, config.Seed, SaltCompanionYaw, j) * MathF.Tau;

                    result.Add(new PropPlacement(config.Kinds[variant].Id, x, y, z, scale, yaw, variant));
                }
            }
            return result;
        }

        static bool IsHostKind(string[] hostKinds, string id)
        {
            for (int i = 0; i < hostKinds.Length; i++)
                if (string.Equals(hostKinds[i], id, StringComparison.Ordinal)) return true;
            return false;
        }

        // Per-host, per-companion hash channel: mixes the companion index j into seed^salt so a host's N
        // companions are uncorrelated. Returns [0, 1).
        static float CompanionHash01(int hx, int hz, int seed, int salt, int j)
        {
            unchecked
            {
                int mixed = (int)((uint)(seed ^ salt) ^ ((uint)j * 0x9E3779B1u));
                return TerrainNoise.Hash2(hx, hz, mixed) * 0.5f + 0.5f;
            }
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CompanionScatterTests" -v q`
Expected: PASS (7 tests).

- [ ] **Step 6: Commit**

```bash
cd /Users/antonio/KhaozEngine/.claude/worktrees/feature+ground-cover-scatter
git add KhaozEngine.Terrain/PropScatter.cs KhaozEngine.Tests/Terrain/CompanionScatterTests.cs
git commit -m "terrain(ground-cover): PropScatter.GenerateCompanions + CompanionConfig (deterministic, tiling-invariant)"
```

---

## Task 2: `PropLayer` tagged struct

**Files:**
- Create: `KhaozEngine.Terrain.Render3D/PropLayer.cs`
- Test: `KhaozEngine.Tests/Terrain/Scene3DChunkSinkTests.cs` (create; factory tests added here, sink tests added in Task 3)

**Interfaces:**
- Consumes: `ScatterConfig`, `CompanionConfig` (KhaozEngine.Terrain), `MeshHandle` (KhaozEngine.Render3D).
- Produces: `public readonly struct PropLayer` with properties `ScatterConfig? Scatter`, `CompanionConfig? Companions`, `int HostLayerIndex`, `IReadOnlyDictionary<string, MeshHandle> Meshes`, `float DrawRadius`, `bool IsCompanion`; and static factories `PropLayer.ScatterLayer(ScatterConfig, IReadOnlyDictionary<string,MeshHandle>, float)` and `PropLayer.CompanionLayer(int hostLayerIndex, CompanionConfig, IReadOnlyDictionary<string,MeshHandle>, float)`.

- [ ] **Step 1: Write the failing factory tests**

Create `KhaozEngine.Tests/Terrain/Scene3DChunkSinkTests.cs` with the factory tests (sink tests come in Task 3):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Headless tests for the multi-layer chunk sink + the PropLayer tagged struct (no GPU - all assertions
    /// go through the internal ScatterLayersFor / ScatterFor accessors and the PropLayer factories).</summary>
    public class Scene3DChunkSinkTests
    {
        static IReadOnlyDictionary<string, MeshHandle> NoMeshes() => new Dictionary<string, MeshHandle>();

        static ScatterConfig OneKind(string id, int seed, float cell) =>
            new ScatterConfig
            {
                Seed = seed,
                CellSize = cell,
                Jitter = 0.5f,
                ClearingRadius = 0f,
                MaxHeight = null,
                Biomes = new[]
                {
                    new BiomeScatterRule { Biome = BiomeId.Meadow, Density = 1f, Kinds = new[] { new PropKind(id, 1f) } },
                },
            };

        [Fact]
        public void ScatterLayer_factory_sets_a_scatter_layer()
        {
            ScatterConfig cfg = OneKind("pine_a", 1, 8f);
            PropLayer layer = PropLayer.ScatterLayer(cfg, NoMeshes(), 90f);

            Assert.False(layer.IsCompanion);
            Assert.Same(cfg, layer.Scatter);
            Assert.Null(layer.Companions);
            Assert.Equal(90f, layer.DrawRadius);
        }

        [Fact]
        public void CompanionLayer_factory_sets_a_companion_layer()
        {
            var comp = new CompanionConfig { HostKinds = new[] { "pine_a" }, Kinds = new[] { new PropKind("bush", 1f) } };
            PropLayer layer = PropLayer.CompanionLayer(0, comp, NoMeshes(), 40f);

            Assert.True(layer.IsCompanion);
            Assert.Same(comp, layer.Companions);
            Assert.Null(layer.Scatter);
            Assert.Equal(0, layer.HostLayerIndex);
            Assert.Equal(40f, layer.DrawRadius);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Scene3DChunkSinkTests" -v q`
Expected: FAIL to compile / `PropLayer` does not exist.

- [ ] **Step 3: Create `PropLayer.cs`**

Create `KhaozEngine.Terrain.Render3D/PropLayer.cs`:

```csharp
using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>One prop layer for the multi-layer <see cref="Scene3DChunkSink"/>: either a <em>scatter</em> layer
    /// (an independent <see cref="ScatterConfig"/>, e.g. sparse trees at a long draw radius) or a <em>companion</em>
    /// layer (a <see cref="CompanionConfig"/> + the index of its host scatter layer, e.g. understory foliage rung
    /// around the trees at a short draw radius). Each layer carries its own mesh set and draw radius - the short
    /// radius on a dense layer is what keeps it affordable. Build one with <see cref="ScatterLayer"/> or
    /// <see cref="CompanionLayer"/>.</summary>
    public readonly struct PropLayer
    {
        public ScatterConfig? Scatter { get; }
        public CompanionConfig? Companions { get; }
        /// <summary>For a companion layer, the index (into the sink's layer list) of the scatter layer whose
        /// placements are the hosts. Unused (-1) for a scatter layer.</summary>
        public int HostLayerIndex { get; }
        public IReadOnlyDictionary<string, MeshHandle> Meshes { get; }
        public float DrawRadius { get; }

        public bool IsCompanion => Companions != null;

        PropLayer(ScatterConfig? scatter, CompanionConfig? companions, int hostLayerIndex,
                  IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius)
        {
            Scatter = scatter;
            Companions = companions;
            HostLayerIndex = hostLayerIndex;
            Meshes = meshes;
            DrawRadius = drawRadius;
        }

        /// <summary>A scatter layer driven by its own <see cref="ScatterConfig"/>.</summary>
        public static PropLayer ScatterLayer(ScatterConfig scatter,
            IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius)
        {
            if (scatter == null) throw new ArgumentNullException(nameof(scatter));
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            return new PropLayer(scatter, null, -1, meshes, drawRadius);
        }

        /// <summary>A companion layer: rings the placements of the scatter layer at <paramref name="hostLayerIndex"/>
        /// with foliage per <paramref name="companions"/>.</summary>
        public static PropLayer CompanionLayer(int hostLayerIndex, CompanionConfig companions,
            IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius)
        {
            if (companions == null) throw new ArgumentNullException(nameof(companions));
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            if (hostLayerIndex < 0) throw new ArgumentOutOfRangeException(nameof(hostLayerIndex));
            return new PropLayer(null, companions, hostLayerIndex, meshes, drawRadius);
        }
    }
}
```

- [ ] **Step 4: Run to verify the factory tests pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Scene3DChunkSinkTests" -v q`
Expected: PASS (2 factory tests).

- [ ] **Step 5: Commit**

```bash
cd /Users/antonio/KhaozEngine/.claude/worktrees/feature+ground-cover-scatter
git add KhaozEngine.Terrain.Render3D/PropLayer.cs KhaozEngine.Tests/Terrain/Scene3DChunkSinkTests.cs
git commit -m "terrain(ground-cover): PropLayer tagged struct (scatter | companion) for the multi-layer sink"
```

---

## Task 3: multi-layer `Scene3DChunkSink`

**Files:**
- Modify: `KhaozEngine.Terrain.Render3D/Scene3DChunkSink.cs`
- Test: `KhaozEngine.Tests/Terrain/Scene3DChunkSinkTests.cs` (add the sink tests from Task 2's file)

**Interfaces:**
- Consumes: `PropLayer` (Task 2), `PropScatter.Generate`, `PropScatter.GenerateCompanions` (Task 1), `ChunkGrid.AreaOf`, `TerrainChunkBuilder.Build`, `Scene3D.LoadTerrainChunk`/`DrawTerrainChunk`/`UnloadMesh`, `PropRenderer.DrawProps`.
- Produces:
  - new ctor `Scene3DChunkSink(Scene3D scene, TerrainField field, IReadOnlyList<PropLayer> layers, float chunkSize, Scene3D.SplatMaterialHandle material = default)`
  - the existing single-layer ctor, unchanged signature, delegating to the new one
  - `internal IReadOnlyList<PropPlacement>[] ScatterLayersFor(ChunkCoord coord)`
  - `internal IReadOnlyList<PropPlacement> ScatterFor(ChunkCoord coord)` (= `ScatterLayersFor(coord)[0]`)
  - `ChunkLoad.LayerProps` (`IReadOnlyList<PropPlacement>[]`); `ChunkLoad.Props` becomes a get-only alias for `LayerProps[0]`.

- [ ] **Step 1: Add the failing sink tests**

Append these tests to `KhaozEngine.Tests/Terrain/Scene3DChunkSinkTests.cs` (inside the existing class, before the closing brace). They need extra `using`s already present in the file (`System`, `System.Collections.Generic`, `System.Linq`, `KhaozEngine.Render3D`, `KhaozEngine.Terrain`, `Xunit`):

```csharp
        static void AssertSamePlacements(IReadOnlyList<PropPlacement> expected, IReadOnlyList<PropPlacement> got)
        {
            Assert.Equal(expected.Count, got.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Id, got[i].Id);
                Assert.Equal(expected[i].X, got[i].X, 4);
                Assert.Equal(expected[i].Z, got[i].Z, 4);
                Assert.Equal(expected[i].Y, got[i].Y, 4);
                Assert.Equal(expected[i].Scale, got[i].Scale, 4);
                Assert.Equal(expected[i].Yaw, got[i].Yaw, 4);
                Assert.Equal(expected[i].Variant, got[i].Variant);
            }
        }

        static string SetKey(PropPlacement p) => $"{p.X:F3},{p.Z:F3},{p.Y:F3},{p.Id},{p.Variant}";

        [Fact]
        public void Each_scatter_layer_matches_PropScatter_for_the_chunk_area()
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            ScatterConfig trees = ScatterConfig.ForestRing();
            ScatterConfig cover = OneKind("grass", seed: 42, cell: 2f);
            cover.ClearingRadius = 26f;
            cover.MaxHeight = 6f;
            float size = 60f;

            var sink = new Scene3DChunkSink(scene: null!, field, new[]
            {
                PropLayer.ScatterLayer(trees, NoMeshes(), 90f),
                PropLayer.ScatterLayer(cover, NoMeshes(), 40f),
            }, chunkSize: size);

            var coord = new ChunkCoord(-2, -2);
            var got = sink.ScatterLayersFor(coord);
            var area = ChunkGrid.AreaOf(coord, size);

            Assert.Equal(2, got.Length);
            Assert.NotEmpty(got[0]);   // not a vacuous comparison: this meadow chunk has trees and grass
            Assert.NotEmpty(got[1]);
            AssertSamePlacements(PropScatter.Generate(field, trees, area), got[0]);
            AssertSamePlacements(PropScatter.Generate(field, cover, area), got[1]);
        }

        [Fact]
        public void Single_layer_ctor_is_back_compatible()
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            ScatterConfig trees = ScatterConfig.ForestRing();
            float size = 60f;

            var legacy = new Scene3DChunkSink(scene: null!, field, trees, NoMeshes(), chunkSize: size, propDrawRadius: 90f);
            var multi = new Scene3DChunkSink(scene: null!, field, new[] { PropLayer.ScatterLayer(trees, NoMeshes(), 90f) }, chunkSize: size);

            var coord = new ChunkCoord(-2, -2);
            AssertSamePlacements(PropScatter.Generate(field, trees, ChunkGrid.AreaOf(coord, size)), legacy.ScatterFor(coord));
            AssertSamePlacements(legacy.ScatterFor(coord), multi.ScatterFor(coord));
        }

        [Fact]
        public void Companion_layer_emits_each_host_companions_exactly_once_across_chunks()
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            ScatterConfig trees = ScatterConfig.ForestRing();
            var comp = new CompanionConfig
            {
                Seed = 7,
                HostKinds = new[] { "pine_a", "pine_b", "pine_c", "oak_a", "oak_b" },
                Kinds = new[] { new PropKind("bush", 1f) },
                CountMin = 2,
                CountMax = 3,
                RadiusMin = 0.6f,
                RadiusMax = 1.6f,
                MaxHeight = 6f,
            };
            float size = 60f;

            var sink = new Scene3DChunkSink(scene: null!, field, new[]
            {
                PropLayer.ScatterLayer(trees, NoMeshes(), 90f),
                PropLayer.CompanionLayer(0, comp, NoMeshes(), 40f),
            }, chunkSize: size);

            // Companions gathered per chunk over a 4x4 block (each host attached to its own chunk).
            var perChunk = new List<PropPlacement>();
            for (int cx = -2; cx <= 1; cx++)
                for (int cz = -2; cz <= 1; cz++)
                    perChunk.AddRange(sink.ScatterLayersFor(new ChunkCoord(cx, cz))[1]);

            // Reference: derive companions once from all hosts over the same world block [-120,120)x[-120,120).
            var hostsWhole = PropScatter.Generate(field, trees, new RectArea(-120, -120, 120, 120));
            var compWhole = PropScatter.GenerateCompanions(field, hostsWhole, comp);

            Assert.NotEmpty(compWhole);
            Assert.Equal(compWhole.Count, perChunk.Count);   // exactly once: no double-emit at seams, none missing
            Assert.Equal(compWhole.Select(SetKey).OrderBy(s => s).ToList(),
                         perChunk.Select(SetKey).OrderBy(s => s).ToList());
        }

        [Fact]
        public void Ctor_rejects_empty_layers_and_bad_companion_host()
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            var comp = new CompanionConfig { HostKinds = new[] { "pine_a" }, Kinds = new[] { new PropKind("bush", 1f) } };

            Assert.Throws<ArgumentException>(() =>
                new Scene3DChunkSink(scene: null!, field, Array.Empty<PropLayer>(), chunkSize: 60f));

            // Companion host index out of range.
            Assert.Throws<ArgumentException>(() =>
                new Scene3DChunkSink(scene: null!, field,
                    new[] { PropLayer.CompanionLayer(5, comp, NoMeshes(), 40f) }, chunkSize: 60f));

            // Companion host points at another companion (must be a scatter layer).
            Assert.Throws<ArgumentException>(() =>
                new Scene3DChunkSink(scene: null!, field, new[]
                {
                    PropLayer.CompanionLayer(1, comp, NoMeshes(), 40f),
                    PropLayer.CompanionLayer(0, comp, NoMeshes(), 40f),
                }, chunkSize: 60f));
        }
```

- [ ] **Step 2: Run to verify the new sink tests fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Scene3DChunkSinkTests" -v q`
Expected: FAIL to compile (no multi-layer ctor / `ScatterLayersFor`).

- [ ] **Step 3: Rewrite `Scene3DChunkSink.cs`**

Replace the entire body of `KhaozEngine.Terrain.Render3D/Scene3DChunkSink.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>The production <see cref="IChunkSink"/>: turns the streamer's load/unload/re-LOD calls into real
    /// <see cref="Scene3D"/> work. Holds one or more <see cref="PropLayer"/>s - each scatter layer has its own
    /// <see cref="ScatterConfig"/>, mesh set, and draw radius (a dense ground-cover layer at a short radius can
    /// ride alongside the sparse tree layer at a long one), and each companion layer rings its host scatter
    /// layer's placements with foliage. <c>Load</c> builds the chunk mesh at the requested LOD
    /// (<see cref="TerrainChunkBuilder"/>) + scatters every layer for the chunk; <c>ReLod</c> rebuilds the mesh in
    /// place (props are LOD-independent, so they are kept); <c>Unload</c> frees the mesh; <c>Draw</c> queues every
    /// loaded chunk + each layer's in-range props (XZ-culled to that layer's draw radius) every frame. Companions
    /// attach to their host's chunk, so each host emits its companions exactly once (a host lives in one chunk),
    /// even when they spill geometrically into a neighbour. Ships in the package so every game gets streaming for
    /// free.</summary>
    public sealed class Scene3DChunkSink : IChunkSink
    {
        readonly Scene3D _scene;
        readonly TerrainField _field;
        readonly IReadOnlyList<PropLayer> _layers;
        readonly float _chunkSize;
        readonly Scene3D.SplatMaterialHandle _material;
        readonly Dictionary<ChunkCoord, ChunkLoad> _loaded = new();

        /// <summary>Multi-layer sink. Each <see cref="PropLayer"/> is a scatter layer or a companion layer; a
        /// companion layer's <see cref="PropLayer.HostLayerIndex"/> must point at a scatter layer in
        /// <paramref name="layers"/>.</summary>
        public Scene3DChunkSink(Scene3D scene, TerrainField field, IReadOnlyList<PropLayer> layers,
                                float chunkSize, Scene3D.SplatMaterialHandle material = default)
        {
            _scene = scene;
            _field = field ?? throw new ArgumentNullException(nameof(field));
            _layers = layers ?? throw new ArgumentNullException(nameof(layers));
            if (layers.Count == 0)
                throw new ArgumentException("At least one PropLayer is required.", nameof(layers));
            for (int i = 0; i < layers.Count; i++)
            {
                PropLayer l = layers[i];
                if (l.IsCompanion)
                {
                    if (l.HostLayerIndex < 0 || l.HostLayerIndex >= layers.Count)
                        throw new ArgumentException(
                            $"PropLayer {i}: companion HostLayerIndex {l.HostLayerIndex} is out of range.", nameof(layers));
                    if (layers[l.HostLayerIndex].IsCompanion)
                        throw new ArgumentException(
                            $"PropLayer {i}: companion host {l.HostLayerIndex} must be a scatter layer.", nameof(layers));
                }
                else if (l.Scatter == null)
                {
                    throw new ArgumentException($"PropLayer {i} has neither a Scatter nor a Companions config.", nameof(layers));
                }
            }
            _chunkSize = chunkSize;
            _material = material;
        }

        /// <summary>Single-layer sink (back-compat): one scatter config, one mesh set, one draw radius.</summary>
        public Scene3DChunkSink(Scene3D scene, TerrainField field, ScatterConfig scatter,
                                IReadOnlyDictionary<string, MeshHandle> propMeshes, float chunkSize, float propDrawRadius,
                                Scene3D.SplatMaterialHandle material = default)
            : this(scene, field,
                   new[]
                   {
                       PropLayer.ScatterLayer(
                           scatter ?? throw new ArgumentNullException(nameof(scatter)),
                           propMeshes ?? throw new ArgumentNullException(nameof(propMeshes)),
                           propDrawRadius),
                   },
                   chunkSize, material)
        {
        }

        /// <summary>The mutable handle for one loaded chunk (the streamer treats it as opaque).</summary>
        public sealed class ChunkLoad
        {
            public MeshHandle Mesh;
            /// <summary>One placement list per layer (scatter or derived companions), index-aligned to the sink's layers.</summary>
            public IReadOnlyList<PropPlacement>[] LayerProps = Array.Empty<IReadOnlyList<PropPlacement>>();
            public int Lod;

            /// <summary>Back-compat alias: the first layer's placements.</summary>
            public IReadOnlyList<PropPlacement> Props =>
                LayerProps.Length > 0 ? LayerProps[0] : Array.Empty<PropPlacement>();
        }

        /// <summary>The deterministic placements for every layer of a chunk (pure; headless-testable). Scatter
        /// layers first, then companion layers derived from their host layer's placements for THIS chunk.</summary>
        internal IReadOnlyList<PropPlacement>[] ScatterLayersFor(ChunkCoord coord)
        {
            RectArea area = ChunkGrid.AreaOf(coord, _chunkSize);
            var layers = new IReadOnlyList<PropPlacement>[_layers.Count];
            for (int i = 0; i < _layers.Count; i++)
                if (!_layers[i].IsCompanion)
                    layers[i] = PropScatter.Generate(_field, _layers[i].Scatter!, area);
            for (int i = 0; i < _layers.Count; i++)
                if (_layers[i].IsCompanion)
                    layers[i] = PropScatter.GenerateCompanions(_field, layers[_layers[i].HostLayerIndex], _layers[i].Companions!);
            return layers;
        }

        /// <summary>The first layer's placements for a chunk (back-compat for the single-layer path).</summary>
        internal IReadOnlyList<PropPlacement> ScatterFor(ChunkCoord coord) => ScatterLayersFor(coord)[0];

        public object Load(ChunkCoord coord, int lod)
        {
            var mesh = TerrainChunkBuilder.Build(_field, ChunkGrid.RegionOf(coord, _chunkSize), lod);
            var load = new ChunkLoad
            {
                Mesh = _material.IsValid ? _scene.LoadTerrainChunk(mesh, _material) : _scene.LoadTerrainChunk(mesh),
                LayerProps = ScatterLayersFor(coord),
                Lod = lod,
            };
            _loaded[coord] = load;
            return load;
        }

        public void ReLod(ChunkCoord coord, object handle, int lod)
        {
            var load = (ChunkLoad)handle;
            _scene.UnloadMesh(load.Mesh);
            var mesh = TerrainChunkBuilder.Build(_field, ChunkGrid.RegionOf(coord, _chunkSize), lod);
            load.Mesh = _material.IsValid ? _scene.LoadTerrainChunk(mesh, _material) : _scene.LoadTerrainChunk(mesh);
            load.Lod = lod;
            // Props are LOD-independent; keep load.LayerProps.
        }

        public void Unload(ChunkCoord coord, object handle)
        {
            var load = (ChunkLoad)handle;
            _scene.UnloadMesh(load.Mesh);
            _loaded.Remove(coord);
        }

        /// <summary>Draw every loaded chunk mesh and each layer's in-range props (XZ-culled to that layer's draw radius).</summary>
        public void Draw(Vector3 focus)
        {
            foreach (ChunkLoad load in _loaded.Values)
            {
                _scene.DrawTerrainChunk(load.Mesh);
                for (int i = 0; i < _layers.Count; i++)
                    _scene.DrawProps(load.LayerProps[i], _layers[i].Meshes, focus, _layers[i].DrawRadius);
            }
        }
    }
}
```

- [ ] **Step 4: Run the full Scene3DChunkSink + streamer suites to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Scene3DChunkSinkTests|FullyQualifiedName~TerrainStreamerTests" -v q`
Expected: PASS (the 6 sink tests + all existing streamer tests, including `Sink_scatters_props_matching_PropScatter_for_the_chunk_area`, which is the legacy back-compat guard).

- [ ] **Step 5: Run the full terrain test suite (no regressions)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~KhaozEngine.Tests.Terrain" -v q`
Expected: PASS (all terrain tests).

- [ ] **Step 6: Commit**

```bash
cd /Users/antonio/KhaozEngine/.claude/worktrees/feature+ground-cover-scatter
git add KhaozEngine.Terrain.Render3D/Scene3DChunkSink.cs KhaozEngine.Tests/Terrain/Scene3DChunkSinkTests.cs
git commit -m "terrain(ground-cover): multi-layer Scene3DChunkSink (per-layer meshes + draw radius, companion derivation)"
```

---

## Task 4: full build + full test sweep

**Files:** none (verification gate before the release ritual).

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build KhaozEngine.sln -c Release`
Expected: build succeeds, no warnings introduced by the new files. (If `KhaozEngine.sln` is not the solution name, run `ls *.sln` and use that.)

- [ ] **Step 2: Run the entire test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: all tests pass (GPU-gated tests skip on a headless box as usual; no failures).

- [ ] **Step 3: No commit** (verification only). If anything fails, fix under TDD and re-run before proceeding to Task 5.

---

## Task 5: release ritual (version 7.71.0 + CHANGELOG + doc sweep + pack + tag)

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `CLAUDE.md`, `docs/USING-KHAOZENGINE.md`
- Pack: `local-feed/`

**Coordination check first:** confirm no concurrent release took 7.71.0.

- [ ] **Step 1: Re-check the version line is still free**

```bash
cd /Users/antonio/KhaozEngine/.claude/worktrees/feature+ground-cover-scatter
git fetch --quiet origin
git tag | sort -V | tail -5
grep -m1 "<KhaozEngineVersion>" Directory.Build.props
```
Expected: latest tag is `v7.70.0`, `<KhaozEngineVersion>` is `7.70.0`. If a `v7.71.0` already exists (physics or another batch took it), STOP and bump to the next free minor instead (e.g. `8.1.0` if physics's `8.0.0` has landed), updating every version string below to match.

- [ ] **Step 2: Bump the version line**

In `Directory.Build.props`, change `<KhaozEngineVersion>7.70.0</KhaozEngineVersion>` to `<KhaozEngineVersion>7.71.0</KhaozEngineVersion>`.

- [ ] **Step 3: Add the CHANGELOG entry (newest-first, tight first sentence)**

Add at the top of the entries in `CHANGELOG.md`:

```markdown
## 7.71.0

Ground-cover scatter: the chunk sink now holds multiple prop layers and a deterministic understory-companion
primitive rings host props with foliage, so a dense short-radius ground cover rides alongside the sparse trees
and trees are dressed at the base instead of standing on bare ground. Additive; no new material, no protocol or
collision change.

- `KhaozEngine.Terrain`: `PropScatter.GenerateCompanions(field, hosts, CompanionConfig)` - a pure, render-free,
  deterministic primitive that rings each host whose id is in `HostKinds` with `Count` foliage instances in a
  jittered ring (count/angle/radius/kind/scale/yaw hashed off the host's centimetre-quantized world XZ, so it is
  tiling-invariant: companions over a host set equal the union of companions over any tiling of it). `Y` resampled
  from the field; `MaxHeight` excludes off-mountain companions. New `CompanionConfig`.
- `KhaozEngine.Terrain.Render3D`: `Scene3DChunkSink` generalized to N `PropLayer`s, each with its own scatter or
  companion config, mesh set, and draw radius (short for dense ground cover, long for trees). New `PropLayer`
  tagged struct (`PropLayer.ScatterLayer(...)` / `PropLayer.CompanionLayer(hostLayerIndex, ...)`). Companion
  layers are derived per chunk from their host scatter layer, so each host emits its companions exactly once even
  when they spill into a neighbour chunk. The existing single-layer ctor is unchanged and byte-identical.
- Render-only by construction: foliage ids carry no collider, so the server, client prediction, and collision are
  untouched.
- Non-goals (deferred): alpha-cutout grass cards / billboards (needs a transparent material pass), wind sway,
  distance alpha-fade at the cull boundary.
```

- [ ] **Step 4: Update the three guard-checked version declarations**

- `docs/CONSUMERS.md`: set the "Engine current version" line to `7.71.0`.
- `docs/ROADMAP.md`: set the "Current released version" line to `7.71.0`.
- `README.md`: set the `<PackageReference ... Version="..." />` example to `7.71.0`.

Run the guard to confirm: `bash scripts/check-doc-versions.sh`
Expected: passes (all three match `7.71.0`).

- [ ] **Step 5: Sweep the feature docs (not just the guard-checked ones)**

- `CLAUDE.md` package map: in the `Terrain` entry add `PropScatter.GenerateCompanions`/`CompanionConfig`; in the `Terrain.Render3D` entry note `Scene3DChunkSink` is multi-layer via `PropLayer` (`ScatterLayer`/`CompanionLayer`) with per-layer mesh set + draw radius and per-chunk companion derivation.
- `docs/USING-KHAOZENGINE.md`: add a short usage section showing the multi-layer sink ctor with a tree `ScatterLayer` + a ground-cover `ScatterLayer` + a `CompanionLayer(0, ...)`, and a one-liner that foliage is render-only (no collider).
- Mechanical check: `grep -rIn "GenerateCompanions\|CompanionConfig\|PropLayer\|multi-layer" --include="*.md" .` and confirm every doc that should mention these does, and no stale doc claims the sink is single-layer only.

- [ ] **Step 6: Pack to local-feed**

```bash
cd /Users/antonio/KhaozEngine/.claude/worktrees/feature+ground-cover-scatter
mkdir -p local-feed
dotnet pack -c Release -o ./local-feed
```
Expected: `KhaozEngine.Terrain.7.71.0.nupkg` and `KhaozEngine.Terrain.Render3D.7.71.0.nupkg` (plus the rest of the line) appear in `local-feed/`.

- [ ] **Step 7: Commit the release**

```bash
cd /Users/antonio/KhaozEngine/.claude/worktrees/feature+ground-cover-scatter
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md docs/ROADMAP.md README.md CLAUDE.md docs/USING-KHAOZENGINE.md
git commit -m "terrain(7.71.0): multi-layer ground-cover scatter + understory companions"
```

- [ ] **Step 8: Tag (HOLD the push)**

```bash
cd /Users/antonio/KhaozEngine/.claude/worktrees/feature+ground-cover-scatter
git tag v7.71.0
```
Do NOT push the branch, `main`, or the tag yet. Per the engine policy, pushes/tags are held and batched and confirmed with the user before pushing (CI publishes to GitHub Packages on every `v*`). The finish step (merge to `main`, cleanup, and the held push decision) is handled with the user after the plan completes, honoring the merge-ordering rule vs the physics 8.0.0 batch.

---

## Notes for the implementer

- The whole engine builds when you build `KhaozEngine.Tests`; the first build is slow. Subsequent `--filter` runs are fast.
- `scene: null!` is the established headless pattern for the sink (the existing streamer test uses it): `ScatterLayersFor`/`ScatterFor` never touch `Scene3D`, so a null scene is fine in tests that only assert placements.
- Do not add any collider for foliage anywhere. The no-collision-coupling property is the contract.
- Keep the single-layer ctor signature exactly as it was; only its body (delegation) changes.
