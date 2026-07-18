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

        [Fact]
        public void RoundedShape_InsetShrinksTheSdfBoxInsideTheQuad()
        {
            // The quad keeps its full w x h (its fragments span the whole rect), but the SDF box the shader
            // shapes against is inset by `inset` on every side. This is what lets a soft falloff resolve to zero
            // INSIDE the quad geometry (no truncation at the quad's flat edge) - the hover-glow bloom fix.
            Vector4 s = SpriteBatch.RoundedShape(200f, 100f, radius: 8f, softness: 3f, inset: 12f);
            Assert.Equal(new Vector4(100f - 12f, 50f - 12f, 8f, 3f), s);
        }

        [Fact]
        public void RoundedShape_DefaultInsetIsZero_ByteIdenticalToToday()
        {
            // Omitting inset must produce exactly the pre-fix packing (half-extents == quad half-extents).
            Assert.Equal(SpriteBatch.RoundedShape(200f, 100f, 8f, 3f),
                         SpriteBatch.RoundedShape(200f, 100f, 8f, 3f, inset: 0f));
        }

        [Fact]
        public void RoundedLocals_ZeroSize_AllZero()
        {
            var (tl, tr, br, bl) = SpriteBatch.RoundedLocals(0f, 0f);
            Assert.Equal(Vector2.Zero, tl);
            Assert.Equal(Vector2.Zero, tr);
            Assert.Equal(Vector2.Zero, br);
            Assert.Equal(Vector2.Zero, bl);
        }

        [Fact]
        public void RoundedMode_FilledVsStroke()
        {
            // Filled fill: stroke 0, modeFlag 1.
            Assert.Equal(new Vector2(0f, 1f), SpriteBatch.RoundedMode(strokeWidth: 0f));
            // Border ring: stroke > 0, modeFlag 1.
            Assert.Equal(new Vector2(2.5f, 1f), SpriteBatch.RoundedMode(strokeWidth: 2.5f));
        }
    }
}
