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
