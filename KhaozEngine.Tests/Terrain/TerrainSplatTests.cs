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
