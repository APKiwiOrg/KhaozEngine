using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Headless coverage for <see cref="AdaptiveViewport"/> - the responsive design viewport (fixed height, width
    /// tracks the window aspect, fills edge-to-edge, no letterbox). Reference design used throughout is 440x956.
    /// </summary>
    public sealed class AdaptiveViewportTests
    {
        const int RW = 440, RH = 956;

        [Fact]
        public void At_the_design_aspect_width_equals_the_reference()
        {
            var vp = new AdaptiveViewport(RW, RH);
            vp.Update(586, 1274); // ~same 440:956 aspect, just bigger

            Assert.Equal(RW, vp.Width);                 // width stays the reference at the design aspect
            Assert.Equal(1274f / RH, vp.ScaleX, 3);     // height-fit scale
            Assert.Equal(vp.ScaleX, vp.ScaleY);         // uniform
            Assert.Equal(0f, vp.OffsetX);
            Assert.Equal(0f, vp.OffsetY);
        }

        [Fact]
        public void A_wider_window_grows_the_design_width_and_fills_with_no_letterbox()
        {
            var vp = new AdaptiveViewport(RW, RH);
            vp.Update(1000, 820);

            float scale = 820f / RH;
            Assert.Equal((int)System.MathF.Round(1000 / scale), vp.Width); // width = window width in design units
            Assert.True(vp.Width > RW);
            // Content fills the window exactly (no bars).
            Assert.Equal(1000f, vp.ContentBounds.Width, 0);
            Assert.Equal(820f, vp.ContentBounds.Height, 0);
        }

        [Fact]
        public void A_narrower_window_floors_the_width_at_the_reference()
        {
            var vp = new AdaptiveViewport(RW, RH);
            vp.Update(300, 956); // narrower than the design aspect

            Assert.Equal(RW, vp.Width); // floored, doesn't squish below the design width
        }

        [Fact]
        public void DesignToScreen_round_trips_and_has_no_offset()
        {
            var vp = new AdaptiveViewport(RW, RH);
            vp.Update(1000, 820);

            var design = new Vector2(220, 478);
            Vector2 screen = vp.DesignToScreen(design);
            Assert.Equal(design.X * vp.ScaleX, screen.X, 3);   // pure scale, no offset
            Vector2 back = vp.ScreenToDesign(screen);
            Assert.Equal(design.X, back.X, 2);
            Assert.Equal(design.Y, back.Y, 2);
        }

        [Fact]
        public void Ignores_nonpositive_window_sizes()
        {
            var vp = new AdaptiveViewport(RW, RH);
            vp.Update(1000, 820);
            int w = vp.Width;
            float sx = vp.ScaleX;

            vp.Update(0, 0);
            vp.Update(-5, 100);

            Assert.Equal(w, vp.Width);   // unchanged
            Assert.Equal(sx, vp.ScaleX);
        }
    }
}
