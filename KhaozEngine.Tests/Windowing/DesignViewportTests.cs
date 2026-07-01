using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Resolution independence: a fixed design space (e.g. 960x540) mapped onto an arbitrary window with a
    /// chosen <see cref="ScaleMode"/>. All pure math (scale, letterbox offset, and screen/design mapping),
    /// so it is headless-testable. The window/framebuffer already resizes; this is the missing design layer.
    /// </summary>
    public class DesignViewportTests
    {
        const int DW = 960, DH = 540;   // 16:9 design

        [Fact]
        public void Fit_SameAspect_ScalesUniformlyNoBars()
        {
            var vp = new DesignViewport(DW, DH, ScaleMode.Fit);
            vp.Update(1920, 1080);

            Assert.Equal(2f, vp.ScaleX, 3);
            Assert.Equal(2f, vp.ScaleY, 3);
            Assert.Equal(0f, vp.OffsetX, 3);
            Assert.Equal(0f, vp.OffsetY, 3);
        }

        [Fact]
        public void Fit_TallerWindow_LetterboxesTopAndBottomCentered()
        {
            var vp = new DesignViewport(DW, DH, ScaleMode.Fit);
            vp.Update(1920, 1200);   // 16:10 window, 16:9 design -> bars top/bottom

            Assert.Equal(2f, vp.ScaleX, 3);            // min(1920/960, 1200/540)=min(2,2.22)=2
            Assert.Equal(2f, vp.ScaleY, 3);
            Assert.Equal(0f, vp.OffsetX, 3);
            Assert.Equal((1200 - 540 * 2) / 2f, vp.OffsetY, 3);   // 60px bar top & bottom
            // content region in screen pixels
            Assert.Equal(new Rect(0, 60, 1920, 1080), vp.ContentBounds);
        }

        [Fact]
        public void Fit_WiderWindow_Pillarboxes()
        {
            var vp = new DesignViewport(DW, DH, ScaleMode.Fit);
            vp.Update(2400, 1080);   // wider than 16:9 -> bars left/right

            Assert.Equal(2f, vp.ScaleX, 3);            // min(2400/960=2.5, 1080/540=2)=2
            Assert.Equal((2400 - 960 * 2) / 2f, vp.OffsetX, 3);   // 240px bar each side
            Assert.Equal(0f, vp.OffsetY, 3);
        }

        [Fact]
        public void Fill_CoversAndCropsCentered()
        {
            var vp = new DesignViewport(DW, DH, ScaleMode.Fill);
            vp.Update(2400, 1080);   // Fill -> max(2.5, 2)=2.5

            Assert.Equal(2.5f, vp.ScaleX, 3);
            Assert.Equal(2.5f, vp.ScaleY, 3);
            Assert.Equal(0f, vp.OffsetX, 3);                       // content width 2400 == window
            Assert.Equal((1080 - 540 * 2.5f) / 2f, vp.OffsetY, 3); // negative -> cropped top/bottom
        }

        [Fact]
        public void Stretch_DistortsToFillExactly()
        {
            var vp = new DesignViewport(DW, DH, ScaleMode.Stretch);
            vp.Update(1000, 1000);

            Assert.Equal(1000f / DW, vp.ScaleX, 3);
            Assert.Equal(1000f / DH, vp.ScaleY, 3);
            Assert.Equal(0f, vp.OffsetX, 3);
            Assert.Equal(0f, vp.OffsetY, 3);
        }

        [Theory]
        [InlineData(ScaleMode.Fit)]
        [InlineData(ScaleMode.Fill)]
        [InlineData(ScaleMode.Stretch)]
        public void ScreenToDesign_IsInverseOfDesignToScreen(ScaleMode mode)
        {
            var vp = new DesignViewport(DW, DH, mode);
            vp.Update(1366, 920);
            var d = new Vector2(123.5f, 410f);

            var round = vp.ScreenToDesign(vp.DesignToScreen(d));

            Assert.Equal(d.X, round.X, 2);
            Assert.Equal(d.Y, round.Y, 2);
        }

        [Fact]
        public void DesignCenter_MapsToWindowCenter_UnderFit()
        {
            var vp = new DesignViewport(DW, DH, ScaleMode.Fit);
            vp.Update(1920, 1200);

            var screen = vp.DesignToScreen(new Vector2(DW / 2f, DH / 2f));

            Assert.Equal(960f, screen.X, 2);
            Assert.Equal(600f, screen.Y, 2);   // window center
        }

        [Fact]
        public void GetClipProjection_MapsDesignCornersToClipSpace()
        {
            var vp = new DesignViewport(DW, DH, ScaleMode.Fit);
            vp.Update(1920, 1080);   // same aspect: scale 2, no bars
            var proj = vp.GetClipProjection(1920, 1080);

            // Same transform convention SpriteBatch uses: Vector4.Transform(pos, proj).
            var topLeft = Vector4.Transform(new Vector4(0, 0, 0, 1), proj);
            var center = Vector4.Transform(new Vector4(DW / 2f, DH / 2f, 0, 1), proj);

            Assert.Equal(-1f, topLeft.X, 3);   // design origin -> clip top-left
            Assert.Equal(1f, topLeft.Y, 3);
            Assert.Equal(0f, center.X, 3);     // design center -> clip center
            Assert.Equal(0f, center.Y, 3);
        }

        [Fact]
        public void NonPositiveSize_IsIgnored()
        {
            var vp = new DesignViewport(DW, DH, ScaleMode.Fit);
            vp.Update(1920, 1080);
            float sx = vp.ScaleX;

            vp.Update(0, 0);
            vp.Update(-5, 100);

            Assert.Equal(sx, vp.ScaleX, 3);   // unchanged
        }
    }
}
