using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>
    /// The typed <see cref="Color"/> wrapper that de-foot-guns <c>SpriteBatch.Draw</c> (rect and color used to
    /// both be a bare <see cref="Vector4"/>). The destination-rect type reuses <c>Windowing.Rect</c>.
    /// </summary>
    public class ColorRectTests
    {
        [Fact]
        public void Color_ToVector4_RoundTrips()
        {
            var c = new Color(0.1f, 0.2f, 0.3f, 0.4f);
            Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 0.4f), c.ToVector4());
            Assert.Equal(c, Color.FromVector4(c.ToVector4()));
        }

        [Fact]
        public void Color_Implicit_To_Vector4_And_Explicit_Back()
        {
            Vector4 v = new Color(0.5f, 0.6f, 0.7f, 1f); // implicit
            Assert.Equal(new Vector4(0.5f, 0.6f, 0.7f, 1f), v);
            var c = (Color)new Vector4(0.5f, 0.6f, 0.7f, 1f); // explicit
            Assert.Equal(new Color(0.5f, 0.6f, 0.7f, 1f), c);
        }

        [Fact]
        public void Color_FromBytes_Maps_255_To_One()
        {
            Assert.Equal(Color.White, Color.FromBytes(255, 255, 255));
            var half = Color.FromBytes(128, 0, 0, 255);
            Assert.Equal(128f / 255f, half.R, 5);
            Assert.Equal(1f, half.A, 5);
        }

        [Fact]
        public void Color_Default_Alpha_Is_Opaque_And_WithAlpha_Replaces_It()
        {
            Assert.Equal(1f, new Color(0.2f, 0.2f, 0.2f).A);
            Assert.Equal(0.25f, Color.White.WithAlpha(0.25f).A);
            Assert.Equal(Color.White.R, Color.White.WithAlpha(0.25f).R);
        }

        [Fact]
        public void Color_Equality()
        {
            Assert.True(new Color(0.1f, 0.2f, 0.3f, 1f) == new Color(0.1f, 0.2f, 0.3f, 1f));
            Assert.True(Color.White != Color.Black);
        }

        [Fact]
        public void WindowingRect_Used_As_DestRect_Reports_Edges_And_Contains()
        {
            // the rect type the typed Draw overload takes
            var r = new KhaozEngine.Windowing.Rect(10f, 20f, 30f, 40f);
            Assert.Equal(40f, r.Right);   // x + width
            Assert.Equal(60f, r.Bottom);  // y + height
            Assert.True(r.Contains(new Vector2(10f, 20f)));
            Assert.False(r.Contains(new Vector2(40f, 30f))); // right edge exclusive
        }
    }
}
