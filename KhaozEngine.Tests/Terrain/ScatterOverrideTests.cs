using System.Linq;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Headless tests for scatter region overrides: density multiplier and kind substitution inside a
    /// shape, first-match-wins ordering, and no effect outside the shape.</summary>
    public class ScatterOverrideTests
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
            Seed = 4321,
            CellSize = 4f,
            Jitter = 1.5f,
            ClearingRadius = 0f,
            MaxHeight = null,
            Biomes = new[]
            {
                new BiomeScatterRule
                {
                    Biome = BiomeId.Meadow,
                    Density = 0.5f,
                    Kinds = new[] { new PropKind("pine_a", 1f) },
                },
            },
        };

        static string Key(PropPlacement p) => $"{p.Id}|{p.X:F4}|{p.Z:F4}";

        [Fact]
        public void ZeroDensityMultiplier_EmptiesTheRegion_LeavesOutsideIdentical()
        {
            var field = FlatField();
            var area = new RectArea(-40f, -40f, 40f, 40f);
            var baseline = PropScatter.Generate(field, Config(), area);

            var cfg = Config();
            cfg.Overrides = new[]
            {
                new ScatterOverride { Area = new DiscArea2D(0f, 0f, 15f), DensityMultiplier = 0f },
            };
            var result = PropScatter.Generate(field, cfg, area);

            Assert.DoesNotContain(result, p => p.X * p.X + p.Z * p.Z <= 15f * 15f);
            var expectedOutside = baseline.Where(p => p.X * p.X + p.Z * p.Z > 15f * 15f).Select(Key);
            Assert.Equal(expectedOutside, result.Select(Key));
        }

        [Fact]
        public void DensityBoost_AddsPlacementsInsideRegionOnly()
        {
            var field = FlatField();
            var area = new RectArea(-40f, -40f, 40f, 40f);
            var baseline = PropScatter.Generate(field, Config(), area);

            var cfg = Config();
            cfg.Overrides = new[]
            {
                new ScatterOverride { Area = new BoxArea2D(-40f, -40f, 0f, 0f), DensityMultiplier = 2f },
            };
            var boosted = PropScatter.Generate(field, cfg, area);

            int baseInside = baseline.Count(p => p.X <= 0f && p.Z <= 0f);
            int boostInside = boosted.Count(p => p.X <= 0f && p.Z <= 0f);
            Assert.True(boostInside > baseInside);

            var baseOutside = baseline.Where(p => !(p.X <= 0f && p.Z <= 0f)).Select(Key);
            var boostOutside = boosted.Where(p => !(p.X <= 0f && p.Z <= 0f)).Select(Key);
            Assert.Equal(baseOutside, boostOutside);
        }

        [Fact]
        public void KindSubstitution_ReplacesKindsInsideRegion()
        {
            var field = FlatField();
            var area = new RectArea(-40f, -40f, 40f, 40f);
            var cfg = Config();
            cfg.Overrides = new[]
            {
                new ScatterOverride
                {
                    Area = new DiscArea2D(0f, 0f, 20f),
                    Kinds = new[] { new PropKind("rock_a", 1f) },
                },
            };
            var result = PropScatter.Generate(field, cfg, area);
            Assert.Contains(result, p => p.Id == "rock_a");
            Assert.All(result.Where(p => p.X * p.X + p.Z * p.Z <= 20f * 20f), p => Assert.Equal("rock_a", p.Id));
            Assert.All(result.Where(p => p.X * p.X + p.Z * p.Z > 20f * 20f), p => Assert.Equal("pine_a", p.Id));
        }

        [Fact]
        public void FirstMatchingOverrideWins()
        {
            var field = FlatField();
            var area = new RectArea(-40f, -40f, 40f, 40f);
            var cfg = Config();
            cfg.Overrides = new[]
            {
                new ScatterOverride { Area = new DiscArea2D(0f, 0f, 20f), Kinds = new[] { new PropKind("rock_a", 1f) } },
                new ScatterOverride { Area = new DiscArea2D(0f, 0f, 20f), DensityMultiplier = 0f },
            };
            var result = PropScatter.Generate(field, cfg, area);
            // The first override (kind substitution, density unchanged) wins, so the disc is NOT empty.
            Assert.Contains(result, p => p.X * p.X + p.Z * p.Z <= 20f * 20f && p.Id == "rock_a");
        }
    }
}
