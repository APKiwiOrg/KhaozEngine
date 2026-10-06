using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// The opt-in minimum-canvas policy of <see cref="AdaptiveViewport"/>. A 1280x720 reference with a 960x540
    /// minimum is used throughout. Scale is min(height / 720 * multiplier, width / 960, height / 540), and the
    /// visible design size is the framebuffer divided by that scale. Expected sizes are worked by hand.
    /// </summary>
    public sealed class AdaptiveViewportMinimumCanvasTests
    {
        static AdaptiveViewport Create(float multiplier = 1f)
            => AdaptiveViewport.WithMinimumCanvas(1280, 720, 960, 540, multiplier);

        [Theory]
        [InlineData(1920, 1080, 1f, 1.5f, 1280, 720)]          // 16:9 matches the legacy height fit
        [InlineData(1440, 900, 1f, 1.25f, 1152, 720)]          // 16:10 narrows instead of overflowing
        [InlineData(1280, 960, 1f, 1.3333334f, 960, 720)]      // 4:3 is width-bound at the minimum width
        [InlineData(3440, 1440, 1f, 2f, 1720, 720)]            // ultrawide keeps the height fit
        [InlineData(2560, 1600, 1.25f, 2.6666667f, 960, 600)]  // fractional multiplier, width-bound
        [InlineData(1920, 1080, 1.5f, 2f, 960, 540)]           // large multiplier stops at the minimum canvas
        public void Visible_design_size_follows_the_minimum_canvas_policy(
            int framebufferWidth, int framebufferHeight, float multiplier, float scale, int width, int height)
        {
            var vp = Create(multiplier);

            vp.Update(framebufferWidth, framebufferHeight);

            Assert.Equal(scale, vp.ScaleX, 5);
            Assert.Equal(vp.ScaleX, vp.ScaleY);
            Assert.Equal(width, vp.Width);
            Assert.Equal(height, vp.Height);
            Assert.Equal(0f, vp.OffsetX);
            Assert.Equal(0f, vp.OffsetY);
            Assert.Equal(new Rect(0, 0, width, height), vp.WindowBounds);
        }

        [Theory]
        [InlineData(1920, 1080, 1f)]
        [InlineData(1440, 900, 1f)]
        [InlineData(1280, 960, 1f)]
        [InlineData(3440, 1440, 1f)]
        [InlineData(2560, 1600, 1.25f)]
        [InlineData(1366, 768, 1.1f)]
        public void A_control_anchored_at_the_visible_right_edge_stays_onscreen(
            int framebufferWidth, int framebufferHeight, float multiplier)
        {
            var vp = Create(multiplier);
            vp.Update(framebufferWidth, framebufferHeight);
            var control = new Rect(vp.Width - 120, vp.Height - 40, 120, 40);

            Vector2 bottomRight = vp.DesignToScreen(new Vector2(control.Right, control.Bottom));

            Assert.True(bottomRight.X <= framebufferWidth + 0.01f, $"right edge {bottomRight.X} > {framebufferWidth}");
            Assert.True(bottomRight.Y <= framebufferHeight + 0.01f, $"bottom edge {bottomRight.Y} > {framebufferHeight}");
            Assert.True(vp.Width >= 960);
            Assert.True(vp.Height >= 540);
        }

        [Fact]
        public void Changing_the_multiplier_recomputes_drawing_and_hit_testing_from_the_cached_framebuffer()
        {
            var vp = Create();
            vp.Update(1920, 1080);
            Assert.Equal(1280, vp.Width);

            vp.ScaleMultiplier = 1.25f;

            Assert.Equal(1.25f, vp.ScaleMultiplier);
            Assert.Equal(1.875f, vp.ScaleX, 5);
            Assert.Equal(1024, vp.Width);
            Assert.Equal(576, vp.Height);
            Assert.Equal(new Vector2(1024, 576), vp.ScreenToDesign(new Vector2(1920, 1080)));
            Assert.Equal(new Vector2(1920, 1080), vp.DesignToScreen(new Vector2(1024, 576)));
            Vector4 corner = Vector4.Transform(new Vector4(1024, 576, 0, 1), vp.GetClipProjection(1920, 1080));
            Assert.Equal(1f, corner.X, 4);
            Assert.Equal(-1f, corner.Y, 4);

            vp.ScaleMultiplier = 1.5f;

            Assert.Equal(2f, vp.ScaleX, 5);
            Assert.Equal(960, vp.Width);
            Assert.Equal(540, vp.Height);
        }

        [Fact]
        public void Non_positive_framebuffer_updates_keep_the_current_transform()
        {
            var vp = Create();
            vp.Update(1440, 900);

            vp.Update(0, 900);
            vp.Update(1440, -1);
            vp.ScaleMultiplier = 1f;

            Assert.Equal(1.25f, vp.ScaleX, 5);
            Assert.Equal(1152, vp.Width);
            Assert.Equal(720, vp.Height);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void Invalid_multipliers_are_rejected_without_changing_bounds(float multiplier)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Create(multiplier));

            var vp = Create();
            vp.Update(1920, 1080);
            Assert.Throws<ArgumentOutOfRangeException>(() => vp.ScaleMultiplier = multiplier);

            Assert.Equal(1f, vp.ScaleMultiplier);
            Assert.Equal(1.5f, vp.ScaleX, 5);
            Assert.Equal(new Rect(0, 0, 1280, 720), vp.DesignBounds);
        }

        [Theory]
        [InlineData(0, 720, 960, 540)]
        [InlineData(1280, 0, 960, 540)]
        [InlineData(1280, 720, 0, 540)]
        [InlineData(1280, 720, 960, -1)]
        public void Invalid_canvas_sizes_are_rejected(int referenceWidth, int referenceHeight, int minimumWidth,
            int minimumHeight)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveViewport.WithMinimumCanvas(
                referenceWidth, referenceHeight, minimumWidth, minimumHeight));
        }

        // Each visible design extent must stay at most 2^24 = 16,777,216, where every integer design coordinate is
        // still exact as a float. The initial fit runs at the 1280x720 reference size.
        [Theory]
        [InlineData(1280, 720, 960, 540, 1e-5f)]                  // 720 / 1e-5 = 72,000,000 rows
        [InlineData(1280, 720, 960, 540, 1e-30f)]                 // scale 1e-30, a 1.28e33 wide canvas saturates int
        [InlineData(1280, 720, 960, 540, float.Epsilon)]          // the height fit underflows to a zero scale
        [InlineData(1280, 720, int.MaxValue, 540, 1f)]            // the canvas is never narrower than its minimum
        [InlineData(1280, 720, 960, 16_777_217, 1f)]              // one row past the exact float range
        [InlineData(1, 16_777_216, 16_777_216, 1, 1f)]            // width-bound at 2^-24, 2^48 rows tall
        public void Configurations_whose_initial_canvas_is_unrepresentable_are_rejected(int referenceWidth,
            int referenceHeight, int minimumWidth, int minimumHeight, float multiplier)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveViewport.WithMinimumCanvas(
                referenceWidth, referenceHeight, minimumWidth, minimumHeight, multiplier));
        }

        [Fact]
        public void A_tiny_multiplier_with_a_representable_canvas_is_accepted()
        {
            var vp = Create(1e-4f);

            // 1280x720 at scale 1e-4 is a 12,800,000 x 7,200,000 canvas, inside the exact float range.
            Assert.Equal(1e-4f, vp.ScaleX);
            Assert.Equal(12_800_000, vp.Width);
            Assert.Equal(7_200_000, vp.Height);
        }

        [Theory]
        [InlineData(1e-30f)]          // scale 4.2e-31, a 1.5e33 wide canvas
        [InlineData(float.Epsilon)]   // 300 / 720 * float.Epsilon underflows to a zero scale
        public void Setting_a_multiplier_with_an_unrepresentable_canvas_keeps_the_previous_transform(
            float multiplier)
        {
            var vp = Create();
            vp.Update(640, 300);   // height-bound at 300 / 720, a 1536x720 canvas
            float scale = vp.ScaleX;
            var probe = vp.ScreenToDesign(new Vector2(320, 150));

            Assert.Throws<ArgumentOutOfRangeException>(() => vp.ScaleMultiplier = multiplier);

            Assert.Equal(1f, vp.ScaleMultiplier);
            Assert.Equal(scale, vp.ScaleX);
            Assert.Equal(scale, vp.ScaleY);
            Assert.Equal(new Rect(0, 0, 1536, 720), vp.DesignBounds);
            Assert.Equal(new Rect(0, 0, 1536 * scale, 720 * scale), vp.ContentBounds);
            Assert.Equal(probe, vp.ScreenToDesign(new Vector2(320, 150)));

            // The cached 640x300 framebuffer survives: 1.25 gives min(0.5208, 0.6667, 0.5556), a 1228x576 canvas.
            vp.ScaleMultiplier = 1.25f;
            Assert.Equal(0.5208333f, vp.ScaleX, 5);
            Assert.Equal(1228, vp.Width);
            Assert.Equal(576, vp.Height);
        }

        [Fact]
        public void An_update_whose_canvas_is_unrepresentable_keeps_the_previous_transform()
        {
            var vp = Create(1e-4f);

            // 3840x1080 at scale 1.5e-4 would be 25,600,000 wide, past the exact float range.
            Assert.Throws<ArgumentOutOfRangeException>(() => vp.Update(3840, 1080));

            Assert.Equal(1e-4f, vp.ScaleX);
            Assert.Equal(new Rect(0, 0, 12_800_000, 7_200_000), vp.DesignBounds);

            // The cached framebuffer is still the 1280x720 reference, not the rejected 3840x1080 (which gives 1.5).
            vp.ScaleMultiplier = 1f;
            Assert.Equal(1f, vp.ScaleX, 5);
            Assert.Equal(1280, vp.Width);
            Assert.Equal(720, vp.Height);
        }

        [Fact]
        public void The_initial_state_fits_the_reference_size_before_the_first_update()
        {
            var vp = Create(1.25f);

            // 1280x720 at multiplier 1.25: min(1.25, 1.3333, 1.3333) = 1.25.
            Assert.Equal(1.25f, vp.ScaleX, 5);
            Assert.Equal(1024, vp.Width);
            Assert.Equal(576, vp.Height);
        }

        [Fact]
        public void The_legacy_constructor_keeps_its_fixed_height_and_rejects_a_multiplier()
        {
            var vp = new AdaptiveViewport(1280, 720);
            vp.Update(1280, 960);

            Assert.Equal(1f, vp.ScaleMultiplier);
            Assert.Equal(1280, vp.Width);
            Assert.Equal(720, vp.Height);
            Assert.Throws<InvalidOperationException>(() => vp.ScaleMultiplier = 1.25f);
            Assert.Equal(1280, vp.Width);
            Assert.Equal(720, vp.Height);
        }
    }
}
