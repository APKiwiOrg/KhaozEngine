using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>Headless coverage for the pure rounded/gradient vertex-build helpers (no GPU device).</summary>
    public class SpriteBatchRoundedTests
    {
        [Fact]
        public void RoundedLocals_AreCornerOffsetsFromCentre()
        {
            // For a w x h rect, the four local corners are +/- half-extents from the centre,
            // TL, TR, BR, BL in that order.
            var (tl, tr, br, bl) = SpriteBatch.RoundedLocals(200f, 100f);
            Assert.Equal(new Vector2(-100f, -50f), tl);
            Assert.Equal(new Vector2(100f, -50f), tr);
            Assert.Equal(new Vector2(100f, 50f), br);
            Assert.Equal(new Vector2(-100f, 50f), bl);
        }

        [Fact]
        public void RoundedShape_PacksHalfExtentsRadiusSoftness()
        {
            Vector4 s = SpriteBatch.RoundedShape(200f, 100f, radius: 8f, softness: 3f);
            Assert.Equal(new Vector4(100f, 50f, 8f, 3f), s);
        }
    }
}
