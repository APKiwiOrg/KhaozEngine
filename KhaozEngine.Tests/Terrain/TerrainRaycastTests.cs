using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Headless tests for the terrain raymarcher: hits land on the surface, misses stay misses,
    /// below-origin returns immediately, and results are deterministic.</summary>
    public class TerrainRaycastTests
    {
        static TerrainField FlatField(float height = 2f) =>
            new TerrainField(new TerrainConfig
            {
                Seed = 7,
                GentleAmplitude = 0f,
                Biomes = new[]
                {
                    new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = height, HillAmplitude = 0f },
                },
            });

        static TerrainField RollingField() =>
            new TerrainField(new TerrainConfig
            {
                Seed = 3,
                GentleAmplitude = 2f,
                GentleFrequency = 0.05f,
                Biomes = new[]
                {
                    new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f },
                },
            });

        [Fact]
        public void DiagonalRay_HitsFlatGroundAtExpectedPoint()
        {
            var field = FlatField(2f);
            // From (0, 10, 0) descending at 45 degrees along +X: crosses y=2 at x=8.
            bool hit = TerrainRaycast.Raycast(field, new Vector3(0f, 10f, 0f), new Vector3(1f, -1f, 0f), 100f, out Vector3 p);
            Assert.True(hit);
            Assert.Equal(8f, p.X, 2);
            Assert.Equal(2f, p.Y, 2);
        }

        [Fact]
        public void HorizontalRayAboveGround_Misses()
        {
            var field = FlatField(2f);
            Assert.False(TerrainRaycast.Raycast(field, new Vector3(0f, 10f, 0f), new Vector3(1f, 0f, 0f), 100f, out _));
        }

        [Fact]
        public void OriginBelowSurface_ReturnsOrigin()
        {
            var field = FlatField(2f);
            Assert.True(TerrainRaycast.Raycast(field, new Vector3(5f, 0f, 5f), new Vector3(0f, -1f, 0f), 10f, out Vector3 p));
            Assert.Equal(new Vector3(5f, 0f, 5f), p);
        }

        [Fact]
        public void RollingTerrain_HitLiesOnTheSurface()
        {
            var field = RollingField();
            bool hit = TerrainRaycast.Raycast(field, new Vector3(-20f, 15f, 7f), new Vector3(1f, -0.4f, 0.1f), 200f, out Vector3 p);
            Assert.True(hit);
            Assert.Equal(field.SampleHeight(p.X, p.Z), p.Y, 2);
        }

        [Fact]
        public void Deterministic_SameInputsSameHit()
        {
            var field = RollingField();
            TerrainRaycast.Raycast(field, new Vector3(-20f, 15f, 7f), new Vector3(1f, -0.4f, 0.1f), 200f, out Vector3 a);
            TerrainRaycast.Raycast(field, new Vector3(-20f, 15f, 7f), new Vector3(1f, -0.4f, 0.1f), 200f, out Vector3 b);
            Assert.Equal(a, b);
        }

        [Fact]
        public void MaxDistance_StopsTheMarch()
        {
            var field = FlatField(2f);
            Assert.False(TerrainRaycast.Raycast(field, new Vector3(0f, 10f, 0f), new Vector3(1f, -1f, 0f), 5f, out _));
        }
    }
}
