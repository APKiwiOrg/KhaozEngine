using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Render2D
{
    public class SpriteBatchScissorTests
    {
        [Theory]
        [InlineData(100, 50, 200, 80, 960, 540, 200u, 100u, 400u, 160u)]
        [InlineData(400, 0, 200, 100, 480, 270, 400u, 0u, 80u, 100u)]
        [InlineData(-20, -10, 50, 50, 480, 270, 0u, 0u, 30u, 40u)]
        public void Viewport_rects_scale_and_clip_to_the_framebuffer(int left, int top, int width, int height,
            int framebufferWidth, int framebufferHeight, uint expectedX, uint expectedY, uint expectedWidth,
            uint expectedHeight)
        {
            var actual = SpriteBatch.ComputeScissor(new Rect(left, top, width, height),
                480, 270, framebufferWidth, framebufferHeight);

            Assert.Equal((expectedX, expectedY, expectedWidth, expectedHeight), actual);
        }

        [Theory]
        [InlineData(1920, 1200, 200u, 160u, 400u, 160u)]
        [InlineData(3840, 2400, 400u, 320u, 800u, 320u)]
        public void Design_rects_map_through_the_letterbox_then_scale_to_the_framebuffer(int framebufferWidth,
            int framebufferHeight, uint expectedX, uint expectedY, uint expectedWidth, uint expectedHeight)
        {
            var vp = new DesignViewport(960, 540, ScaleMode.Fit);
            vp.Update(1920, 1200);

            var actual = SpriteBatch.ComputeScissor(new Rect(100, 50, 200, 80), vp,
                1920, 1200, framebufferWidth, framebufferHeight);

            Assert.Equal((expectedX, expectedY, expectedWidth, expectedHeight), actual);
        }

        [Fact]
        public void NullViewport_PassesTheRectThroughUnmapped()
        {
            var mapped = SpriteBatch.ComputeScissor(new Rect(100, 50, 200, 80), null, 480, 270, 960, 540);
            var direct = SpriteBatch.ComputeScissor(new Rect(100, 50, 200, 80), 480, 270, 960, 540);
            Assert.Equal(direct, mapped);
        }

        // The nesting half (#106): a clip set inside another one may only shrink the region, never widen it back
        // out. Before the scissor stack, an inner SetScissor replaced the outer region wholesale.
        [Fact]
        public void Intersect_keeps_only_the_overlap_of_two_regions()
        {
            var overlap = SpriteBatch.IntersectScissor((100u, 100u, 200u, 200u), (150u, 50u, 200u, 200u));
            Assert.Equal((150u, 100u, 150u, 150u), overlap);
        }

        [Fact]
        public void Intersect_with_a_region_that_contains_it_is_that_region()
        {
            var inner = (X: 120u, Y: 130u, Width: 40u, Height: 50u);
            Assert.Equal(inner, SpriteBatch.IntersectScissor((100u, 100u, 200u, 200u), inner));
        }

        [Fact]
        public void Intersect_of_disjoint_regions_clips_everything()
        {
            // No overlap at all: a zero-sized region, never a negative (unsigned) one that would wrap huge.
            var empty = SpriteBatch.IntersectScissor((0u, 0u, 50u, 50u), (200u, 200u, 50u, 50u));
            Assert.Equal(0u, empty.Width);
            Assert.Equal(0u, empty.Height);
        }
    }
}
