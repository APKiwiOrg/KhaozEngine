using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainParityTests
    {
        [Fact]
        public void Clearing_has_gentle_meadow_mountains_and_a_lake_basin()
        {
            var f = new TerrainField(TerrainPresets.Clearing());

            // meadow floor near the clearing centre is gentle (a few metres of roll, not tens).
            Assert.InRange(f.SampleHeight(6f, 6f), -3f, 6f);

            // mountains ramp up toward +Z (tens of metres) without a vertical wall: monotone-ish climb.
            float zMid = f.SampleHeight(0f, 48f);
            float zFar = f.SampleHeight(0f, 110f);
            Assert.True(zFar > 30f);
            Assert.True(zFar > zMid);

            // the lake basin at (-13,-2) sits below the water surface.
            Assert.True(f.SampleHeight(-13f, -2f) < f.WaterLevel);

            // overall relief is in the greybox ballpark (tens of metres, not hundreds).
            float lo = f.SampleHeight(6f, 6f), hi = f.SampleHeight(0f, 120f);
            Assert.InRange(hi - lo, 25f, 90f);
        }
    }
}
