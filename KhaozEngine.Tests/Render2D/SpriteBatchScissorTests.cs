using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    public class SpriteBatchScissorTests
    {
        [Fact]
        public void Scales_viewport_points_to_framebuffer_pixels_for_retina()
        {
            // viewport 480x270 points, framebuffer 960x540 px -> 2x.
            var (x, y, w, h) = SpriteBatch.ComputeScissor(new Rect(100, 50, 200, 80), 480, 270, 960, 540);
            Assert.Equal((200u, 100u, 400u, 160u), (x, y, w, h));
        }

        [Fact]
        public void Clamps_a_rect_that_extends_past_the_right_edge()
        {
            var (x, y, w, h) = SpriteBatch.ComputeScissor(new Rect(400, 0, 200, 100), 480, 270, 480, 270);
            Assert.Equal((400u, 0u, 80u, 100u), (x, y, w, h));   // width clipped to the framebuffer
        }

        [Fact]
        public void Clamps_a_rect_with_a_negative_origin()
        {
            var (x, y, w, h) = SpriteBatch.ComputeScissor(new Rect(-20, -10, 50, 50), 480, 270, 480, 270);
            Assert.Equal((0u, 0u, 30u, 40u), (x, y, w, h));      // left/top clipped, width/height reduced
        }
    }
}
