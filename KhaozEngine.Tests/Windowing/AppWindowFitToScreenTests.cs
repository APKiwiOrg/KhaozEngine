using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Headless coverage for <see cref="AppWindow.FitToScreen"/> - the pure window-sizing policy that opens a
    /// fixed-design window large enough to read on a desktop monitor (the monitor query + GPU stay out of it).
    /// Design used throughout is Nullwake's 440x956 portrait.
    /// </summary>
    public sealed class AppWindowFitToScreenTests
    {
        const int DW = 440, DH = 956;

        [Fact]
        public void Grows_to_fill_a_large_landscape_monitor_preserving_aspect()
        {
            var (w, h) = AppWindow.FitToScreen(DW, DH, 2560, 1440, screenFraction: 0.9f, maxScale: 4f);

            // Portrait design is height-bound: scale = 0.9*1440/956 ~= 1.355 -> ~596x1296.
            Assert.True(h > DH && w > DW);
            Assert.True(h <= (int)(1440 * 0.9f) + 1);
            // Aspect preserved (uniform scale).
            Assert.Equal((double)DW / DH, (double)w / h, 2);
        }

        [Fact]
        public void Never_shrinks_below_the_design_on_a_height_constrained_screen()
        {
            // A laptop work area shorter than the design height (956): must not shrink (scale clamps to 1).
            var (w, h) = AppWindow.FitToScreen(DW, DH, 1512, 982, screenFraction: 0.9f, maxScale: 2f);
            Assert.Equal(DW, w);
            Assert.Equal(DH, h);
        }

        [Fact]
        public void Caps_at_maxScale_on_a_huge_display()
        {
            var (w, h) = AppWindow.FitToScreen(DW, DH, 6016, 3384, screenFraction: 0.9f, maxScale: 2f);
            Assert.Equal(DW * 2, w);
            Assert.Equal(DH * 2, h);
        }

        [Fact]
        public void Falls_back_to_the_design_when_the_screen_size_is_unknown()
        {
            var (w, h) = AppWindow.FitToScreen(DW, DH, 0, 0);
            Assert.Equal(DW, w);
            Assert.Equal(DH, h);
        }

        [Fact]
        public void A_landscape_design_is_width_bound()
        {
            // 16:9 design on a 1920x1080 screen: width is the binding constraint.
            var (w, h) = AppWindow.FitToScreen(1280, 720, 1920, 1080, screenFraction: 0.9f, maxScale: 4f);
            Assert.True(w > 1280);
            Assert.True(w <= (int)(1920 * 0.9f) + 1);
            Assert.Equal(1280.0 / 720, (double)w / h, 2);
        }
    }
}
