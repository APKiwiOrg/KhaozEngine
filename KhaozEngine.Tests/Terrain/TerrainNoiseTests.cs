using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainNoiseTests
    {
        [Fact]
        public void Hash2_is_deterministic_and_seed_sensitive()
        {
            Assert.Equal(TerrainNoise.Hash2(3, -7, 99), TerrainNoise.Hash2(3, -7, 99));
            Assert.NotEqual(TerrainNoise.Hash2(3, -7, 99), TerrainNoise.Hash2(3, -7, 100));
            Assert.InRange(TerrainNoise.Hash2(3, -7, 99), -1f, 1f);
        }

        [Fact]
        public void SmoothStep_clamps_and_midpoints()
        {
            Assert.Equal(0f, TerrainNoise.SmoothStep(10f, 20f, 5f));
            Assert.Equal(1f, TerrainNoise.SmoothStep(10f, 20f, 25f));
            Assert.Equal(0.5f, TerrainNoise.SmoothStep(10f, 20f, 15f), 5);
        }

        [Fact]
        public void Fbm_is_deterministic_and_bounded()
        {
            float a = TerrainNoise.Fbm(12.5f, -3.25f, 7);
            float b = TerrainNoise.Fbm(12.5f, -3.25f, 7);
            Assert.Equal(a, b);
            Assert.InRange(a, -1.5f, 1.5f);
        }

        [Fact]
        public void Turbulence_is_non_negative()
        {
            for (int i = 0; i < 50; i++)
                Assert.True(TerrainNoise.Turbulence(i * 1.3f, i * -0.7f, 5) >= 0f);
        }

        [Fact]
        public void ValueNoise_is_continuous_between_lattice_points()
        {
            // two nearby samples differ by less than a lattice step's worth of range.
            float a = TerrainNoise.ValueNoise(4.10f, 9.00f, 1);
            float b = TerrainNoise.ValueNoise(4.11f, 9.00f, 1);
            Assert.True(System.MathF.Abs(a - b) < 0.1f);
        }
    }
}
