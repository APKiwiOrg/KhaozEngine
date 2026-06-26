using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainFeatureTests
    {
        [Fact]
        public void Lake_lowers_height_inside_radius_and_leaves_outside()
        {
            var lake = new LakeFeature(0f, 0f, 8f, 3.6f);
            float center = lake.Apply(0f, 0f, 10f);
            float edge = lake.Apply(20f, 0f, 10f);
            Assert.True(center < 10f);                       // carved
            Assert.True(center <= lake.Apply(4f, 0f, 10f));  // deepest at centre
            Assert.Equal(10f, edge, 3);                      // untouched well outside
        }

        [Fact]
        public void Ridge_raises_along_the_line_but_dips_at_the_pass()
        {
            // line through origin along +X, pass at x=0.
            var ridge = new RidgeFeature(Vector2.Zero, new Vector2(1f, 0f), height: 30f, width: 4f, passAlong: 0f, passWidth: 10f);
            float onWall = ridge.Apply(40f, 0f, 0f);     // on the line, far from pass
            float atPass = ridge.Apply(0f, 0f, 0f);      // on the line, at the pass
            float offLine = ridge.Apply(40f, 30f, 0f);   // far perpendicular
            Assert.True(onWall > 20f);
            Assert.True(atPass < 5f);
            Assert.True(offLine < 1f);
        }

        [Fact]
        public void Flatten_levels_its_region_to_target()
        {
            var flat = new FlattenFeature(0f, 0f, 10f, targetHeight: 5f);
            Assert.Equal(5f, flat.Apply(0f, 0f, 50f), 1);   // centre pulled to target
            Assert.Equal(40f, flat.Apply(40f, 0f, 40f), 1); // outside untouched
        }
    }
}
