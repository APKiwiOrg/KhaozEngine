using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainLodTests
    {
        [Fact]
        public void PickLod_is_monotonic_in_distance()
        {
            int prev = TerrainLod.PickLod(0f);
            for (float d = 0f; d < 500f; d += 5f)
            {
                int lod = TerrainLod.PickLod(d);
                Assert.True(lod >= prev);
                prev = lod;
            }
        }

        [Fact]
        public void PickLod_spans_all_tiers()
        {
            Assert.Equal(0, TerrainLod.PickLod(10f));
            Assert.Equal(2, TerrainLod.PickLod(400f));
        }

        [Fact]
        public void Resolution_decreases_with_lod()
        {
            Assert.True(TerrainLod.ResolutionFor(0) > TerrainLod.ResolutionFor(1));
            Assert.True(TerrainLod.ResolutionFor(1) > TerrainLod.ResolutionFor(2));
        }
    }
}
