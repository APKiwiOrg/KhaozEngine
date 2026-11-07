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
        [Theory]
        [InlineData(false, 1f)]
        [InlineData(true, -1f)]
        public void Caret_orientation_preserves_level_arms(bool pointingUp, float direction)
        {
            var (left, mid, right) = GuiDraw.CaretGeometry(new Vector2(100, 50), halfWidth: 4, halfHeight: 2, pointingUp);
            Assert.Equal(96f, left.X, 3);
            Assert.Equal(104f, right.X, 3);
            Assert.True((mid.Y - left.Y) * direction > 0f);
            Assert.Equal(left.Y, right.Y, 3);   // arms level with each other
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

        [Theory]
        [InlineData(0f, 0f, 2f, new float[]
        {
            0, 0, 20, 2,
            0, 18, 20, 2,
            0, 2, 2, 16,
            18, 2, 2, 16,
        }, 144f)]
        [InlineData(4f, 8f, 12f, new float[]
        {
            4, 8, 20, 12,
            4, 20, 20, 8,
        }, 400f)]
        public void Single_coverage_border_clips_non_overlapping_strips(
            float x, float y, float thickness, float[] expectedCoordinates, float expectedArea)
        {
            Rect[] strips = GuiDraw.BorderSingleCoverageGeometry(new Rect(x, y, 20, 20), thickness);

            Assert.Equal(expectedCoordinates.Length / 4, strips.Length);
            for (int i = 0; i < strips.Length; i++)
                Assert.Equal(new Rect(expectedCoordinates[i * 4], expectedCoordinates[i * 4 + 1],
                    expectedCoordinates[i * 4 + 2], expectedCoordinates[i * 4 + 3]), strips[i]);
            Assert.Equal(expectedArea, TotalArea(strips), 3);
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

            Assert.NotEmpty(strips);
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
