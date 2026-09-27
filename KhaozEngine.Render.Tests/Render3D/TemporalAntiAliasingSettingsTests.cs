using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage of the round 2 settings surface: the Temporal anti-aliasing mode, the Temporal render scale it
    /// forces, the five upscale presets and the explicit ratio, and the internal size they give ComputeTargetSize.
    /// </summary>
    public sealed class TemporalAntiAliasingSettingsTests
    {
        static GpuCapabilities Caps(int maxMsaa) => new(false, true, "test", false, false, maxMsaa);

        [Fact]
        public void Temporal_is_a_single_sample_mode_without_fxaa()
        {
            AntiAliasing taa = AntiAliasing.Temporal;
            Assert.Equal(AntiAliasingMode.Temporal, taa.Mode);
            Assert.Equal(1, taa.MsaaSamples);
            Assert.False(taa.UsesFxaa);
            Assert.Equal("TAA", taa.ToString());
            Assert.Equal(AntiAliasing.Temporal, taa);
            Assert.NotEqual(AntiAliasing.Fxaa, taa);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(4)]
        [InlineData(8)]
        public void ResolveFor_never_turns_temporal_into_msaa(int maxMsaa)
        {
            AntiAliasing resolved = AntiAliasing.Temporal.ResolveFor(Caps(maxMsaa));
            Assert.Equal(AntiAliasing.Temporal, resolved);
            Assert.Equal(1, resolved.MsaaSamples);
        }

        [Theory]
        [InlineData(1, true)]
        [InlineData(4, true)]
        [InlineData(8, true)]
        [InlineData(8, false)]
        public void ResolveFor_keeps_temporal_whether_or_not_temporal_rendering_is_active(int maxMsaa, bool temporalActive)
        {
            AntiAliasing resolved = AntiAliasing.Temporal.ResolveFor(Caps(maxMsaa), temporalActive);
            Assert.Equal(AntiAliasing.Temporal, resolved);
            Assert.Equal(1, resolved.MsaaSamples);
        }

        [Fact]
        public void Temporal_forces_the_temporal_render_scale_and_one_sample()
        {
            var s = new PixelPostProcessSettings();   // raw RenderScale stays FixedInternal
            s.Quality.AntiAliasing = AntiAliasing.Temporal;
            Assert.Equal(AntiAliasingMode.Temporal, s.EffectiveAaMode);
            Assert.Equal(RenderScale.Temporal, s.EffectiveRenderScale);
            Assert.Equal(1, s.EffectiveMsaaSamples);
            Assert.False(s.EffectiveFxaa);
            Assert.Equal(RenderScale.FixedInternal, s.RenderScale);
        }

        [Fact]
        public void Pixelated_refuses_temporal()
        {
            var s = new PixelPostProcessSettings { Pixelated = true };
            s.Quality.AntiAliasing = AntiAliasing.Temporal;
            Assert.Equal(AntiAliasingMode.None, s.EffectiveAaMode);
            Assert.Equal(RenderScale.FixedInternal, s.EffectiveRenderScale);
            Assert.Equal(1f, s.EffectiveUpscaleRatio);
            Assert.Equal((1600, 900), Scene3D.ComputeTargetSize(s, 1920, 1080));
        }

        [Theory]
        [InlineData(TemporalUpscale.Native, 1f)]
        [InlineData(TemporalUpscale.Quality, 1.5f)]
        [InlineData(TemporalUpscale.Balanced, 1.7f)]
        [InlineData(TemporalUpscale.Performance, 2f)]
        [InlineData(TemporalUpscale.UltraPerformance, 3f)]
        public void Each_preset_names_its_display_over_internal_factor(TemporalUpscale preset, float factor)
        {
            Assert.Equal(factor, TemporalSettings.DisplayOverInternal(preset));
            var t = new TemporalSettings { Upscale = preset };
            Assert.Equal(1f / factor, t.ResolvedUpscaleRatio);
        }

        [Fact]
        public void An_explicit_ratio_overrides_the_preset_and_clamps_to_0_33_and_1()
        {
            var t = new TemporalSettings { Upscale = TemporalUpscale.Performance, UpscaleRatio = 0.8f };
            Assert.Equal(0.8f, t.ResolvedUpscaleRatio);
            t.UpscaleRatio = 0.2f;
            Assert.Equal(0.33f, t.ResolvedUpscaleRatio);
            t.UpscaleRatio = 0.4f;
            Assert.Equal(0.4f, t.ResolvedUpscaleRatio);
            t.UpscaleRatio = 1.4f;
            Assert.Equal(1f, t.ResolvedUpscaleRatio);
            t.UpscaleRatio = null;
            Assert.Equal(0.5f, t.ResolvedUpscaleRatio);
        }

        [Theory]
        [InlineData(0.33f, 0.33f, 634, 356)]
        [InlineData(1f, 1f, 1920, 1080)]
        [InlineData(0f, 0.33f, 634, 356)]
        [InlineData(-0.5f, 0.33f, 634, 356)]
        public void An_explicit_ratio_on_or_past_a_bound_resolves_to_that_bound(float ratio, float resolved, int w,
            int h)
        {
            var s = new PixelPostProcessSettings();
            s.Quality.AntiAliasing = AntiAliasing.Temporal;
            s.Temporal.Upscale = TemporalUpscale.Performance;
            s.Temporal.UpscaleRatio = ratio;
            Assert.Equal(resolved, s.Temporal.ResolvedUpscaleRatio);
            Assert.Equal((w, h), Scene3D.ComputeTargetSize(s, 1920, 1080));
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void A_ratio_that_is_not_finite_falls_back_to_the_preset(float ratio)
        {
            var t = new TemporalSettings { Upscale = TemporalUpscale.Quality, UpscaleRatio = ratio };
            Assert.Equal(1f / 1.5f, t.ResolvedUpscaleRatio);
        }

        [Theory]
        [InlineData(TemporalUpscale.Native, 1920, 1080)]
        [InlineData(TemporalUpscale.Quality, 1280, 720)]
        [InlineData(TemporalUpscale.Balanced, 1129, 635)]
        [InlineData(TemporalUpscale.Performance, 960, 540)]
        [InlineData(TemporalUpscale.UltraPerformance, 640, 360)]
        public void The_internal_target_is_the_display_times_the_ratio(TemporalUpscale preset, int w, int h)
        {
            var s = new PixelPostProcessSettings();
            s.Quality.AntiAliasing = AntiAliasing.Temporal;
            s.Temporal.Upscale = preset;
            Assert.Equal((w, h), Scene3D.ComputeTargetSize(s, 1920, 1080));
            Assert.Equal(1f / TemporalSettings.DisplayOverInternal(preset), s.EffectiveUpscaleRatio);
        }

        [Fact]
        public void UltraPerformance_on_a_retina_display_shades_fewer_pixels_than_the_fixed_default()
        {
            var s = new PixelPostProcessSettings();
            s.Quality.AntiAliasing = AntiAliasing.Temporal;
            s.Temporal.Upscale = TemporalUpscale.Performance;
            var (pw, ph) = Scene3D.ComputeTargetSize(s, 3456, 2234);
            s.Temporal.Upscale = TemporalUpscale.UltraPerformance;
            var (uw, uh) = Scene3D.ComputeTargetSize(s, 3456, 2234);
            Assert.Equal((1728, 1117), (pw, ph));
            Assert.Equal((1152, 745), (uw, uh));
            Assert.True(pw * ph > 1600 * 900);
            Assert.True(uw * uh < 1600 * 900);
        }

        [Fact]
        public void Quality_at_the_reference_display_gives_the_internal_size_the_history_figure_prices()
        {
            // TemporalFormatsTests prices a Quality history at 3456x2234 with 2304x1489 previous depths.
            var s = new PixelPostProcessSettings();
            s.Quality.AntiAliasing = AntiAliasing.Temporal;
            s.Temporal.Upscale = TemporalUpscale.Quality;
            Assert.Equal((2304, 1489), Scene3D.ComputeTargetSize(s, 3456, 2234));
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        public void A_tiny_viewport_at_the_smallest_ratios_still_gets_a_one_pixel_target(int viewportW, int viewportH)
        {
            var s = new PixelPostProcessSettings();
            s.Quality.AntiAliasing = AntiAliasing.Temporal;
            s.Temporal.Upscale = TemporalUpscale.UltraPerformance;
            Assert.Equal((1, 1), Scene3D.ComputeTargetSize(s, viewportW, viewportH));
            s.Temporal.UpscaleRatio = 0.33f;
            Assert.Equal((1, 1), Scene3D.ComputeTargetSize(s, viewportW, viewportH));
        }

        [Fact]
        public void The_temporal_internal_target_still_honours_the_cap_with_aspect()
        {
            var s = new PixelPostProcessSettings { MaxRenderWidth = 1280, MaxRenderHeight = 720 };
            s.Quality.AntiAliasing = AntiAliasing.Temporal;   // Native asks for 3840x2160
            Assert.Equal((1280, 720), Scene3D.ComputeTargetSize(s, 3840, 2160));
            Assert.False(Scene3D.WantsMipDownsample(s, 3840, 2160));
        }

        [Fact]
        public void Temporal_never_asks_for_a_mip_chain()
        {
            var s = new PixelPostProcessSettings();
            s.Quality.AntiAliasing = AntiAliasing.Temporal;
            s.Temporal.Upscale = TemporalUpscale.Performance;
            Assert.False(Scene3D.WantsMipDownsample(s, 1920, 1080));
        }

        [Fact]
        public void A_raw_temporal_render_scale_without_temporal_aa_renders_at_the_ratio()
        {
            var s = new PixelPostProcessSettings { RenderScale = RenderScale.Temporal };
            s.Temporal.Upscale = TemporalUpscale.Performance;
            Assert.Equal((960, 540), Scene3D.ComputeTargetSize(s, 1920, 1080));
        }

        [Fact]
        public void MatchViewport_supersampling_keeps_its_factor()
        {
            var s = new PixelPostProcessSettings { RenderScale = RenderScale.MatchViewport, Supersample = 2f };
            Assert.Equal(2f, s.EffectiveViewportScale);
            Assert.Equal(1f, s.EffectiveUpscaleRatio);
            s.Supersample = 0.5f;
            Assert.Equal(1f, s.EffectiveViewportScale);
        }

        [Theory]
        [InlineData(TemporalUpscale.Native, 8)]
        [InlineData(TemporalUpscale.Quality, 18)]
        [InlineData(TemporalUpscale.Balanced, 24)]
        [InlineData(TemporalUpscale.Performance, 32)]
        [InlineData(TemporalUpscale.UltraPerformance, 72)]
        public void The_jitter_cycle_grows_with_the_preset(TemporalUpscale preset, int phases)
            => Assert.Equal(phases, TemporalJitter.PhaseCount(TemporalSettings.DisplayOverInternal(preset)));
    }
}
