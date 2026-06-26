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
