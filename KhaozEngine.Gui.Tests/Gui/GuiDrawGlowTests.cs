using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Headless coverage for the pure soft-bloom geometry behind <see cref="GuiDraw.HoverGlow"/> and the
    /// <see cref="GuiDraw.FillStyled"/> drop shadow. The old glow expanded the quad by only half the softness, so
    /// the SDF falloff was truncated at ~50% coverage on the quad's flat edge and read as a hard amber rim. The
    /// fix keeps the SDF box body-sized (so its <c>d=0</c> edge sits on the body outline) while expanding the quad
    /// well beyond it, giving the outer falloff room to fade to zero before the quad ends.
    /// </summary>
    public class GuiDrawGlowTests
    {
        [Fact]
        public void SoftQuadGeometry_KeepsSdfBoxOnTheBodyEdge()
        {
            var body = new Rect(100f, 50f, 200f, 60f);
            var (quad, softness, inset) = GuiDraw.SoftQuadGeometry(body, spread: 10f, offset: Vector2.Zero);

            // The SDF half-extents the shader actually shapes against = quad-half minus inset. They must equal
            // the BODY half-extents, so coverage is full (cov 0.5) exactly at the body outline and fades outward.
            Vector4 shape = SpriteBatch.RoundedShape(quad.Z, quad.W, radius: 7f, softness: softness, inset: inset);
            Assert.Equal(body.Width * 0.5f, shape.X, 3);
            Assert.Equal(body.Height * 0.5f, shape.Y, 3);
        }

        [Fact]
        public void SoftQuadGeometry_FalloffResolvesToZeroBeforeTheQuadEdge()
        {
            // The visible falloff (cov 0.5 -> 0) spans softness/2 in distance outside the body. The quad must
            // extend at least that far past the body on every side, with headroom, so nothing is truncated.
            var (_, softness, inset) = GuiDraw.SoftQuadGeometry(new Rect(0f, 0f, 120f, 40f), spread: 12f, offset: Vector2.Zero);
            Assert.True(softness * 0.5f <= inset, "falloff distance must fit inside the quad's outer margin");
        }

        [Fact]
        public void SoftQuadGeometry_CentredOnBodyWhenNoOffset()
        {
            var body = new Rect(100f, 50f, 200f, 60f);
            var (quad, _, inset) = GuiDraw.SoftQuadGeometry(body, spread: 10f, offset: Vector2.Zero);
            // Quad is the body grown by `inset` on every side.
            Assert.Equal(body.X - inset, quad.X, 3);
            Assert.Equal(body.Y - inset, quad.Y, 3);
            Assert.Equal(body.Width + inset * 2f, quad.Z, 3);
            Assert.Equal(body.Height + inset * 2f, quad.W, 3);
        }

        [Fact]
        public void SoftQuadGeometry_OffsetShiftsTheQuadForDropShadow()
        {
            var body = new Rect(100f, 50f, 200f, 60f);
            var (noOff, _, _) = GuiDraw.SoftQuadGeometry(body, spread: 8f, offset: Vector2.Zero);
            var (off, _, _) = GuiDraw.SoftQuadGeometry(body, spread: 8f, offset: new Vector2(0f, 3f));
            Assert.Equal(noOff.X, off.X, 3);
            Assert.Equal(noOff.Y + 3f, off.Y, 3);
        }
    }
}
