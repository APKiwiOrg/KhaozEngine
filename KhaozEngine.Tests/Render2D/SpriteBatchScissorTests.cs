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

        [Fact]
        public void Maps_a_design_space_clip_rect_through_the_viewport_letterbox()
        {
            // 960x540 design Fit into a 1920x1200 window: scale 2, 60px top/bottom bars.
            var vp = new DesignViewport(960, 540, ScaleMode.Fit);
            vp.Update(1920, 1200);
            // A design clip rect should land at design*2 + the 60px vertical offset, then map 1:1 to the framebuffer.
            var (x, y, w, h) = SpriteBatch.ComputeScissor(new Rect(100, 50, 200, 80), vp, 1920, 1200, 1920, 1200);
            Assert.Equal((200u, 160u, 400u, 160u), (x, y, w, h));
        }

        [Fact]
        public void Maps_through_the_viewport_then_scales_for_retina()
        {
            var vp = new DesignViewport(960, 540, ScaleMode.Fit);
            vp.Update(1920, 1200);                                 // window points
            // framebuffer is 2x the window (Retina): design rect -> screen points -> 2x framebuffer pixels.
            var (x, y, w, h) = SpriteBatch.ComputeScissor(new Rect(100, 50, 200, 80), vp, 1920, 1200, 3840, 2400);
            Assert.Equal((400u, 320u, 800u, 320u), (x, y, w, h));
        }

        [Fact]
        public void NullViewport_PassesTheRectThroughUnmapped()
        {
            var mapped = SpriteBatch.ComputeScissor(new Rect(100, 50, 200, 80), null, 480, 270, 960, 540);
            var direct = SpriteBatch.ComputeScissor(new Rect(100, 50, 200, 80), 480, 270, 960, 540);
            Assert.Equal(direct, mapped);
        }
    }
}
