using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Headless tests for generalized scatter exclusion shapes: placements never land inside an
    /// exclusion, empty exclusions change nothing, and tiling invariance holds with exclusions active.</summary>
    public class ScatterExclusionTests
    {
        static TerrainField FlatField() =>
            new TerrainField(new TerrainConfig
            {
                Seed = 7,
                WaterLevel = -1000f,
                GentleAmplitude = 0f,
                Biomes = new[]
                {
                    new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f },
                },
            });

        static ScatterConfig Config() => new ScatterConfig
        {
            Seed = 1234,
            CellSize = 4f,
            Jitter = 1.5f,
            ClearingRadius = 0f,
            MaxHeight = null,
            Biomes = new[]
            {
                new BiomeScatterRule
                {
                    Biome = BiomeId.Meadow,
                    Density = 0.6f,
                    Kinds = new[] { new PropKind("pine_a", 1f) },
                },
            },
        };

        static string Key(PropPlacement p) => $"{p.Id}|{p.X:F4}|{p.Z:F4}|{p.Scale:F4}|{p.Yaw:F4}";

        [Fact]
        public void EmptyExclusions_MatchesBaseline()
        {
            var field = FlatField();
            var area = new RectArea(-40f, -40f, 40f, 40f);
            var baseline = PropScatter.Generate(field, Config(), area);
            var cfg = Config();
            cfg.Exclusions = System.Array.Empty<IArea2D>();
            var withEmpty = PropScatter.Generate(field, cfg, area);
            Assert.Equal(baseline.Select(Key), withEmpty.Select(Key));
        }

        [Fact]
        public void DiscExclusion_RemovesPlacementsInsideOnly()
        {
            var field = FlatField();
            var area = new RectArea(-40f, -40f, 40f, 40f);
            var baseline = PropScatter.Generate(field, Config(), area);
            Assert.True(baseline.Count > 20);   // sanity: enough props to make the test meaningful

            var cfg = Config();
            cfg.Exclusions = new IArea2D[] { new DiscArea2D(0f, 0f, 15f) };
            var excluded = PropScatter.Generate(field, cfg, area);

            // Nothing inside the disc.
            Assert.DoesNotContain(excluded, p => p.X * p.X + p.Z * p.Z <= 15f * 15f);
            // Everything outside the disc survives identically.
            var expectedOutside = baseline.Where(p => p.X * p.X + p.Z * p.Z > 15f * 15f).Select(Key);
            Assert.Equal(expectedOutside, excluded.Select(Key));
        }

        [Fact]
        public void MultipleExclusions_AllApply()
        {
            var field = FlatField();
            var area = new RectArea(-40f, -40f, 40f, 40f);
            var cfg = Config();
            cfg.Exclusions = new IArea2D[]
            {
                new DiscArea2D(-20f, -20f, 8f),
                new BoxArea2D(10f, 10f, 35f, 35f),
            };
            var result = PropScatter.Generate(field, cfg, area);
            Assert.DoesNotContain(result, p => (p.X + 20f) * (p.X + 20f) + (p.Z + 20f) * (p.Z + 20f) <= 64f);
            Assert.DoesNotContain(result, p => p.X >= 10f && p.X <= 35f && p.Z >= 10f && p.Z <= 35f);
        }

        [Fact]
        public void TilingInvariance_HoldsWithExclusions()
        {
            var field = FlatField();
            var cfg = Config();
            cfg.Exclusions = new IArea2D[] { new DiscArea2D(5f, 5f, 12f), new BoxArea2D(-30f, -30f, -10f, -10f) };

            var whole = PropScatter.Generate(field, cfg, new RectArea(-40f, -40f, 40f, 40f));
            var tiles = new List<PropPlacement>();
            tiles.AddRange(PropScatter.Generate(field, cfg, new RectArea(-40f, -40f, 0f, 0f)));
            tiles.AddRange(PropScatter.Generate(field, cfg, new RectArea(0f, -40f, 40f, 0f)));
            tiles.AddRange(PropScatter.Generate(field, cfg, new RectArea(-40f, 0f, 0f, 40f)));
            tiles.AddRange(PropScatter.Generate(field, cfg, new RectArea(0f, 0f, 40f, 40f)));

            Assert.Equal(
                whole.Select(Key).OrderBy(k => k),
                tiles.Select(Key).OrderBy(k => k));
        }
    }
}
