using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Headless coverage for the small pure primitives added for the opt-in widget chrome (the dropdown chevron
    /// caret and the shared opacity fade). These are drawing-math helpers with no GPU dependency.
    /// </summary>
    public class GuiDrawPrimitivesTests
    {
        [Fact]
        public void Caret_points_down_when_not_pointing_up()
        {
            var (left, mid, right) = GuiDraw.CaretGeometry(new Vector2(100, 50), halfWidth: 4, halfHeight: 2, pointingUp: false);
            // A downward "v": the middle vertex sits BELOW (larger Y) the two arms.
            Assert.Equal(96f, left.X, 3);
            Assert.Equal(104f, right.X, 3);
            Assert.True(mid.Y > left.Y);
            Assert.Equal(left.Y, right.Y, 3);   // arms level with each other
        }

        [Fact]
        public void Caret_points_up_when_pointing_up()
        {
            var (left, mid, right) = GuiDraw.CaretGeometry(new Vector2(100, 50), halfWidth: 4, halfHeight: 2, pointingUp: true);
            // An upward "^": the middle vertex sits ABOVE (smaller Y) the two arms.
            Assert.True(mid.Y < left.Y);
            Assert.Equal(left.Y, right.Y, 3);
        }

        [Fact]
        public void WithOpacity_scales_only_the_alpha_channel()
        {
            var c = new Vector4(0.2f, 0.4f, 0.6f, 0.8f);
            var faded = GuiDraw.WithOpacity(c, 0.5f);
            Assert.Equal(0.2f, faded.X, 4);
            Assert.Equal(0.4f, faded.Y, 4);
            Assert.Equal(0.6f, faded.Z, 4);
            Assert.Equal(0.4f, faded.W, 4);   // 0.8 * 0.5
        }

        [Fact]
        public void WithOpacity_of_one_is_a_no_op()
        {
            var c = new Vector4(0.1f, 0.2f, 0.3f, 0.9f);
            Assert.Equal(c, GuiDraw.WithOpacity(c, 1f));
        }
    }
}
