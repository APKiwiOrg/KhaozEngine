using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // Pure (GPU-free) coverage of Scene3D.ComputeTargetSize: the internal render-target sizing decision behind
    // PixelPostProcessSettings.RenderScale. FixedInternal must be unchanged from the historical fixed path;
    // MatchViewport tracks the framebuffer, clamped to the cap with aspect preserved.
    public sealed class RenderScaleTests
    {
        [Fact]
        public void FixedInternal_ignores_viewport_and_returns_fixed_size()
        {
            var s = new PixelPostProcessSettings(); // default mode is FixedInternal, 1600x900
            Assert.Equal(RenderScale.FixedInternal, s.RenderScale);

            Assert.Equal((1600, 900), Scene3D.ComputeTargetSize(s, 320, 240));
            Assert.Equal((1600, 900), Scene3D.ComputeTargetSize(s, 5000, 4000));
            Assert.Equal((1600, 900), Scene3D.ComputeTargetSize(s, 1600, 900));
        }

        [Fact]
        public void FixedInternal_honours_a_custom_fixed_size()
        {
            var s = new PixelPostProcessSettings { RenderWidth = 256, RenderHeight = 144 };
            Assert.Equal((256, 144), Scene3D.ComputeTargetSize(s, 3000, 2000));
        }

        [Fact]
        public void MatchViewport_below_cap_returns_the_exact_viewport()
        {
            var s = new PixelPostProcessSettings { RenderScale = RenderScale.MatchViewport };
            Assert.Equal((1920, 1080), Scene3D.ComputeTargetSize(s, 1920, 1080));
            Assert.Equal((800, 600), Scene3D.ComputeTargetSize(s, 800, 600));
            // Right at the cap is still 1:1.
            Assert.Equal((3840, 2160), Scene3D.ComputeTargetSize(s, 3840, 2160));
        }

        [Fact]
        public void MatchViewport_above_cap_clamps_and_preserves_aspect()
        {
            var s = new PixelPostProcessSettings { RenderScale = RenderScale.MatchViewport }; // cap 3840x2160 (16:9)

            // A 16:9 viewport twice the cap scales down to exactly the cap.
            Assert.Equal((3840, 2160), Scene3D.ComputeTargetSize(s, 7680, 4320));

            // A wider-than-cap-aspect viewport binds on width; height comes in under the cap, aspect kept ~constant.
            var (w, h) = Scene3D.ComputeTargetSize(s, 8000, 2000); // 4:1
            Assert.True(w <= 3840 && h <= 2160, $"within cap: {w}x{h}");
            Assert.Equal(3840, w);                       // width is the binding axis
            Assert.Equal(960, h);                        // 3840 / 4
        }

        [Fact]
        public void MatchViewport_is_stable_at_the_cap_for_a_fixed_aspect()
        {
            // Two different oversized 16:9 viewports must clamp to the SAME target, so EnsureSize doesn't thrash
            // (resize once, then stay) as a maximised window's framebuffer wobbles by a pixel.
            var s = new PixelPostProcessSettings { RenderScale = RenderScale.MatchViewport };
            var a = Scene3D.ComputeTargetSize(s, 5120, 2880);
            var b = Scene3D.ComputeTargetSize(s, 7680, 4320);
            Assert.Equal(a, b);
            Assert.Equal((3840, 2160), a);
        }

        [Fact]
        public void MatchViewport_guards_against_a_degenerate_viewport()
        {
            var s = new PixelPostProcessSettings { RenderScale = RenderScale.MatchViewport };
            Assert.Equal((1, 1), Scene3D.ComputeTargetSize(s, 0, 0));
            Assert.Equal((1, 1), Scene3D.ComputeTargetSize(s, -10, -10));
        }

        [Fact]
        public void MatchViewport_supersample_multiplies_the_target_below_cap()
        {
            var s = new PixelPostProcessSettings { RenderScale = RenderScale.MatchViewport, Supersample = 2f };
            // A 720p window x2 = 1440p internal (under the 3840x2160 cap) - the same effective AA a 2x/Retina display gives.
            Assert.Equal((2560, 1440), Scene3D.ComputeTargetSize(s, 1280, 720));
            Assert.Equal((1600, 1200), Scene3D.ComputeTargetSize(s, 800, 600));
        }

        [Fact]
        public void MatchViewport_supersample_still_clamps_to_the_cap()
        {
            var s = new PixelPostProcessSettings { RenderScale = RenderScale.MatchViewport, Supersample = 2f };
            Assert.Equal((3840, 2160), Scene3D.ComputeTargetSize(s, 1920, 1080));   // 1080p x2 = 4K = exactly the cap
            Assert.Equal((3840, 2160), Scene3D.ComputeTargetSize(s, 2560, 1440));   // 1440p x2 > cap -> clamps (16:9)
        }

        [Fact]
        public void Supersample_below_one_is_clamped_and_FixedInternal_ignores_it()
        {
            var mv = new PixelPostProcessSettings { RenderScale = RenderScale.MatchViewport, Supersample = 0.5f };
            Assert.Equal((1280, 720), Scene3D.ComputeTargetSize(mv, 1280, 720));    // < 1x is treated as 1x
            var fi = new PixelPostProcessSettings { Supersample = 2f };             // FixedInternal ignores Supersample
            Assert.Equal((1600, 900), Scene3D.ComputeTargetSize(fi, 1280, 720));
        }
    }
}
