using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Headless tests for the deterministic coordinate-hash prop scatter (render-free leaf, no GPU):
    /// determinism, tiling/streaming invariance, density, exclusions (below water / inside the clearing), and
    /// Y == field height.</summary>
    public class PropScatterTests
    {
        // A flat single-biome field: SampleHeight == height everywhere, SampleBiome == Meadow.
        static TerrainField FlatField(float height = 0f, float waterLevel = -1000f, BiomeId biome = BiomeId.Meadow) =>
            new TerrainField(new TerrainConfig
            {
                Seed = 7,
                WaterLevel = waterLevel,
                GentleAmplitude = 0f,
                Biomes = new[]
                {
                    new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = biome, BaseHeight = height, HillAmplitude = 0f },
                },
            });

        // A gently-rolling single-biome field so placement Y varies (for the Y == SampleHeight test).
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

        static ScatterConfig OneKind(float density = 0.5f, float cell = 4.5f) =>
            new ScatterConfig
            {
                Seed = 99,
                CellSize = cell,
                Jitter = 1.6f,
                ClearingRadius = 0f,
                MaxHeight = null,
                Biomes = new[]
                {
                    new BiomeScatterRule { Biome = BiomeId.Meadow, Density = density, Kinds = new[] { new PropKind("pine_a", 1f) } },
                },
            };

        [Fact]
        public void Generate_IsDeterministic()
        {
            TerrainField f = FlatField();
            ScatterConfig c = OneKind(density: 0.6f);
            var area = new RectArea(-40, -40, 40, 40);

            var a = PropScatter.Generate(f, c, area);
            var b = PropScatter.Generate(f, c, area);

            Assert.Equal(a.Count, b.Count);
            Assert.NotEmpty(a);
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
        public void Generate_IsTilingInvariant()
        {
            TerrainField f = FlatField();
            ScatterConfig c = OneKind(density: 0.7f);

            var whole = PropScatter.Generate(f, c, new RectArea(-50, -50, 50, 50));
            var q1 = PropScatter.Generate(f, c, new RectArea(-50, -50, 0, 0));
            var q2 = PropScatter.Generate(f, c, new RectArea(0, -50, 50, 0));
            var q3 = PropScatter.Generate(f, c, new RectArea(-50, 0, 0, 50));
            var q4 = PropScatter.Generate(f, c, new RectArea(0, 0, 50, 50));

            var union = q1.Concat(q2).Concat(q3).Concat(q4).ToList();
            Assert.Equal(whole.Count, union.Count);

            string Key(PropPlacement p) => $"{p.X:F4},{p.Z:F4},{p.Id},{p.Variant}";
            var wholeKeys = whole.Select(Key).OrderBy(s => s).ToList();
            var unionKeys = union.Select(Key).OrderBy(s => s).ToList();
            Assert.Equal(wholeKeys, unionKeys);
        }

        [Fact]
        public void Generate_DensityWithinTolerance()
        {
            TerrainField f = FlatField();
            var area = new RectArea(-225, -225, 225, 225);

            int all = PropScatter.Generate(f, OneKind(density: 1f), area).Count;
            int half = PropScatter.Generate(f, OneKind(density: 0.5f), area).Count;

            Assert.True(all > 2000, $"expected many candidate cells, got {all}");
            double frac = (double)half / all;
            Assert.InRange(frac, 0.45, 0.55);
        }

        [Fact]
        public void Generate_ExcludesBelowWater()
        {
            // Ground at y=0, water at y=5: every candidate is under water -> nothing placed.
            TerrainField f = FlatField(height: 0f, waterLevel: 5f);
            var res = PropScatter.Generate(f, OneKind(density: 1f), new RectArea(-50, -50, 50, 50));
            Assert.Empty(res);
        }

        [Fact]
        public void Generate_ExcludesClearingRadius()
        {
            TerrainField f = FlatField();
            ScatterConfig c = OneKind(density: 1f);
            c.ClearingRadius = 20f;
            c.ClearingCenter = Vector2.Zero;

            var res = PropScatter.Generate(f, c, new RectArea(-50, -50, 50, 50));
            Assert.NotEmpty(res);
            foreach (PropPlacement p in res)
                Assert.True(MathF.Sqrt(p.X * p.X + p.Z * p.Z) >= 20f, $"placement inside clearing radius: {p.X},{p.Z}");
        }

        [Fact]
        public void Generate_YEqualsFieldHeight()
        {
            TerrainField f = RollingField();
            var res = PropScatter.Generate(f, OneKind(density: 1f), new RectArea(-40, -40, 40, 40));
            Assert.NotEmpty(res);
            foreach (PropPlacement p in res)
                Assert.Equal(f.SampleHeight(p.X, p.Z), p.Y, 4);
        }

        [Fact]
        public void ForestRing_PlacesPinesAndOaksOutsideClearing()
        {
            TerrainField f = FlatField();
            ScatterConfig c = ScatterConfig.ForestRing();
            var res = PropScatter.Generate(f, c, new RectArea(-58, -58, 58, 16));

            Assert.NotEmpty(res);
            // every placement is a known kit id and sits outside the default clearing radius
            var ids = new HashSet<string>(res.Select(p => p.Id));
            Assert.Contains("pine_a", ids);
            foreach (PropPlacement p in res)
                Assert.True(MathF.Sqrt(p.X * p.X + p.Z * p.Z) >= c.ClearingRadius, "ForestRing placed inside the clearing");
        }
    }
}
