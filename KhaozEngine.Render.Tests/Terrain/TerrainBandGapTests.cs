using System;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainBandGapTests
    {
        static TerrainField GapField(params BiomeBand[] bands) => new(new TerrainConfig
        {
            BiomeBlend = 10f,
            GentleAmplitude = 0f,
            Biomes = bands,
        });

        static BiomeBand Band(float start, float end, BiomeId biome, float baseHeight, float hillAmplitude) =>
            new()
            {
                Start = start,
                End = end,
                Biome = biome,
                BaseHeight = baseHeight,
                HillAmplitude = hillAmplitude,
            };

        static float Sum(in BiomeWeights weights)
        {
            float sum = 0f;
            foreach (BiomeId biome in Enum.GetValues<BiomeId>()) sum += weights[biome];
            return sum;
        }

        [Theory]
        [InlineData(9.99f, 10f, 2f)]
        [InlineData(10f, 10f, 2f)]
        [InlineData(50f, 20f, 4f)]
        [InlineData(90f, 30f, 6f)]
        [InlineData(90.01f, 30f, 6f)]
        public void Shape_interpolates_between_the_effective_edges_of_an_uncovered_gap(
            float z, float expectedBaseHeight, float expectedHillAmplitude)
        {
            TerrainField field = GapField(
                Band(float.NegativeInfinity, 0f, BiomeId.Forest, 10f, 2f),
                Band(100f, float.PositiveInfinity, BiomeId.Desert, 30f, 6f));

            var shape = field.ShapeAt(z);

            Assert.Equal(expectedBaseHeight, shape.baseHeight, 5);
            Assert.Equal(expectedHillAmplitude, shape.hillAmp, 5);
        }

        [Theory]
        [InlineData(9.99f, 1f, 0f, BiomeId.Forest)]
        [InlineData(10f, 1f, 0f, BiomeId.Forest)]
        [InlineData(50f, 0.5f, 0.5f, BiomeId.Forest)]
        [InlineData(90f, 0f, 1f, BiomeId.Desert)]
        [InlineData(90.01f, 0f, 1f, BiomeId.Desert)]
        public void Biome_shares_follow_the_same_gap_transition_as_the_shape(
            float z, float expectedForest, float expectedDesert, BiomeId expectedDominant)
        {
            TerrainField field = GapField(
                Band(float.NegativeInfinity, 0f, BiomeId.Forest, 10f, 2f),
                Band(100f, float.PositiveInfinity, BiomeId.Desert, 30f, 6f));

            BiomeWeights weights = field.SampleBiomeWeights(0f, z);

            Assert.Equal(expectedForest, weights[BiomeId.Forest], 5);
            Assert.Equal(expectedDesert, weights[BiomeId.Desert], 5);
            Assert.Equal(1f, Sum(weights), 5);
            Assert.Equal(expectedDominant, weights.Dominant);
            Assert.Equal(field.SampleBiome(0f, z), weights.Dominant);
        }

        [Fact]
        public void One_sided_gaps_use_the_nearest_available_band()
        {
            TerrainField field = GapField(
                Band(-100f, -50f, BiomeId.Forest, 10f, 2f),
                Band(50f, 100f, BiomeId.Desert, 30f, 6f));

            Assert.Equal(10f, field.ShapeAt(-200f).baseHeight);
            Assert.Equal(BiomeId.Forest, field.SampleBiomeWeights(0f, -200f).Dominant);
            Assert.Equal(30f, field.ShapeAt(200f).baseHeight);
            Assert.Equal(BiomeId.Desert, field.SampleBiomeWeights(0f, 200f).Dominant);
        }

        [Fact]
        public void Equal_gap_shares_break_the_dominant_tie_by_band_order()
        {
            TerrainField field = GapField(
                Band(100f, float.PositiveInfinity, BiomeId.Desert, 30f, 6f),
                Band(float.NegativeInfinity, 0f, BiomeId.Forest, 10f, 2f));

            BiomeWeights weights = field.SampleBiomeWeights(0f, 50f);

            Assert.Equal(0.5f, weights[BiomeId.Forest], 5);
            Assert.Equal(0.5f, weights[BiomeId.Desert], 5);
            Assert.Equal(BiomeId.Desert, weights.Dominant);
            Assert.Equal(field.SampleBiome(0f, 50f), weights.Dominant);
        }

        [Fact]
        public void A_config_with_no_bands_keeps_the_default_meadow_shape()
        {
            var field = new TerrainField(new TerrainConfig { Biomes = Array.Empty<BiomeBand>() });

            var shape = field.ShapeAt(500f);
            BiomeWeights weights = field.SampleBiomeWeights(0f, 500f);

            Assert.Equal(0f, shape.baseHeight);
            Assert.Equal(0f, shape.hillAmp);
            Assert.Equal(BiomeId.Meadow, shape.biome);
            Assert.Equal(1f, weights[BiomeId.Meadow]);
            Assert.Equal(1f, Sum(weights));
            Assert.Equal(shape.biome, weights.Dominant);
        }
    }
}
