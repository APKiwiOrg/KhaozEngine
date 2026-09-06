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

        [Fact]
        public void Single_coverage_border_shortens_vertical_strips_around_the_corners()
        {
            Rect[] strips = GuiDraw.BorderSingleCoverageGeometry(new Rect(0, 0, 20, 20), 2f);

            Assert.Equal(4, strips.Length);
            Assert.Equal(new Rect(0, 0, 20, 2), strips[0]);
            Assert.Equal(new Rect(0, 18, 20, 2), strips[1]);
            Assert.Equal(new Rect(0, 2, 2, 16), strips[2]);
            Assert.Equal(new Rect(18, 2, 2, 16), strips[3]);
            Assert.Equal(144f, TotalArea(strips), 3);
        }

        [Fact]
        public void Single_coverage_border_consumes_the_rect_once_when_thickness_reaches_half_height()
        {
            Rect[] strips = GuiDraw.BorderSingleCoverageGeometry(new Rect(4, 8, 20, 20), 12f);

            Assert.Equal(2, strips.Length);
            Assert.Equal(new Rect(4, 8, 20, 12), strips[0]);
            Assert.Equal(new Rect(4, 20, 20, 8), strips[1]);
            Assert.Equal(400f, TotalArea(strips), 3);
        }

        [Theory]
        [InlineData(0, 20, 2)]
        [InlineData(-1, 20, 2)]
        [InlineData(20, 0, 2)]
        [InlineData(20, -1, 2)]
        [InlineData(20, 20, 0)]
        [InlineData(20, 20, -1)]
        public void Single_coverage_border_returns_no_strips_for_non_positive_input(float width, float height, float thickness)
        {
            Assert.Empty(GuiDraw.BorderSingleCoverageGeometry(new Rect(1, 2, width, height), thickness));
        }

        [Fact]
        public void Single_coverage_border_preserves_fractional_geometry_and_accepts_pixel_snapped_geometry()
        {
            Rect fractional = new Rect(0.25f, 1.75f, 9.5f, 6.5f);
            Rect[] direct = GuiDraw.BorderSingleCoverageGeometry(fractional, 1.25f);
            Assert.Equal(new Rect(0.25f, 1.75f, 9.5f, 1.25f), direct[0]);
            Assert.Equal(new Rect(0.25f, 7f, 9.5f, 1.25f), direct[1]);

            const float scale = 2f;
            Rect snapped = ViewportMath.SnapRectToDevice(fractional, new Vector2(scale), Vector2.Zero);
            float snappedThickness = ViewportMath.SnapLengthToDevice(1.25f, scale, 1f);
            Rect[] strips = GuiDraw.BorderSingleCoverageGeometry(snapped, snappedThickness);

            foreach (Rect strip in strips)
            {
                Assert.Equal(System.MathF.Round(strip.X * scale), strip.X * scale, 4);
                Assert.Equal(System.MathF.Round(strip.Y * scale), strip.Y * scale, 4);
                Assert.Equal(System.MathF.Round(strip.Right * scale), strip.Right * scale, 4);
                Assert.Equal(System.MathF.Round(strip.Bottom * scale), strip.Bottom * scale, 4);
            }
        }

        static float TotalArea(Rect[] rects)
        {
            float total = 0f;
            foreach (Rect rect in rects) total += rect.Width * rect.Height;
            return total;
        }
    }
}
