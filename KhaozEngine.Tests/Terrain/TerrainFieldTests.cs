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
    }
}
