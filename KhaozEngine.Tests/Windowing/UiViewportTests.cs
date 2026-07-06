using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Point-space UI viewport: authors in logical points and maps 1 point -> DpiScale device pixels (no
    /// letterbox), so text/chrome baked at that scale stay crisp. All pure math (scale + point&lt;-&gt;device
    /// mapping + clip projection), headless-testable like <see cref="DesignViewport"/>.
    /// </summary>
    public sealed class UiViewportTests
    {
        [Fact]
        public void Retina_2x_authors_in_logical_points_at_scale_2()
        {
            var vp = new UiViewport(2048, 1280, 1024, 640);   // Retina: 2x framebuffer over a 1024x640 logical window

            Assert.Equal(1024, vp.Width);        // logical points, not framebuffer pixels
            Assert.Equal(640, vp.Height);
            Assert.Equal(2f, vp.DpiScale, 3);
            Assert.Equal(2f, vp.ScaleX, 3);
            Assert.Equal(2f, vp.ScaleY, 3);
            Assert.Equal(0f, vp.OffsetX, 3);     // no letterbox
            Assert.Equal(0f, vp.OffsetY, 3);
            Assert.Equal(new Rect(0, 0, 1024, 640), vp.DesignBounds);
        }

        [Fact]
        public void Fractional_scale_keeps_logical_size_and_a_15x_dpi()
        {
            var vp = new UiViewport(1536, 960, 1024, 640);    // 150%-scaled display

            Assert.Equal(1.5f, vp.DpiScale, 3);
            Assert.Equal(1024, vp.Width);                     // UI stays authored at logical size
            Assert.Equal(640, vp.Height);
        }

        [Fact]
        public void Point_maps_to_device_pixels_and_back()
        {
            var vp = new UiViewport(2048, 1280, 1024, 640);

            Assert.Equal(new Vector2(200, 100), vp.DesignToScreen(new Vector2(100, 50)));   // point -> device px
            var round = vp.ScreenToDesign(vp.DesignToScreen(new Vector2(123.5f, 410f)));
            Assert.Equal(123.5f, round.X, 3);
            Assert.Equal(410f, round.Y, 3);
        }

        [Fact]
        public void ContentBounds_is_the_full_framebuffer_no_bars()
        {
            var vp = new UiViewport(1536, 960, 1024, 640);
            Assert.Equal(new Rect(0, 0, 1536, 960), vp.ContentBounds);
        }

        [Fact]
        public void GetClipProjection_fills_the_framebuffer_from_logical_points()
        {
            var vp = new UiViewport(2048, 1280, 1024, 640);
            var proj = vp.GetClipProjection(2048, 1280);   // batch passes the framebuffer size

            var topLeft = Vector4.Transform(new Vector4(0, 0, 0, 1), proj);
            var bottomRight = Vector4.Transform(new Vector4(vp.Width, vp.Height, 0, 1), proj);

            Assert.Equal(-1f, topLeft.X, 3);     // logical origin -> clip top-left
            Assert.Equal(1f, topLeft.Y, 3);
            Assert.Equal(1f, bottomRight.X, 3);  // logical extent -> clip bottom-right (fills)
            Assert.Equal(-1f, bottomRight.Y, 3);
        }

        [Fact]
        public void Update_from_frame_uses_framebuffer_and_logical_sizes()
        {
            var frame = new Frame { Width = 2048, Height = 1280, LogicalWidth = 1024, LogicalHeight = 640 };
            var vp = new UiViewport();
            vp.Update(frame);

            Assert.Equal(1024, vp.Width);
            Assert.Equal(2f, vp.DpiScale, 3);
            Assert.Equal(2f, frame.DpiScale, 3);   // Frame exposes the same scale the viewport bakes/snaps to
        }

        [Fact]
        public void NonPositive_size_is_ignored()
        {
            var vp = new UiViewport(2048, 1280, 1024, 640);
            float dpi = vp.DpiScale;

            vp.Update(0, 0, 100, 100);
            vp.Update(1920, 1080, 0, 0);

            Assert.Equal(dpi, vp.DpiScale, 3);     // unchanged
            Assert.Equal(1024, vp.Width);
        }

        [Fact]
        public void Frame_dpi_scale_falls_back_to_one_before_logical_size_is_known()
        {
            var frame = new Frame { Width = 800, Height = 600 };   // LogicalWidth still 0
            Assert.Equal(1f, frame.DpiScale, 3);
        }
    }
}
