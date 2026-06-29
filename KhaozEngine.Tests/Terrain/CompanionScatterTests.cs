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
