using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The temporal anti-aliasing mode turns temporal rendering on, sizes the jitter cycle by the display over internal
    /// ratio, stays single-sample, and resets history when the preset or the ratio changes, even where the internal size
    /// does not. Headless: <see cref="HeadlessSceneRig"/> renders through the real frame path on a fake device.
    /// </summary>
    public sealed class TemporalActivationTests
    {
        [Fact]
        public void Temporal_anti_aliasing_turns_temporal_rendering_on_single_sample_and_Pixelated_refuses_it()
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Frame(240, 240);
            Assert.True(rig.Scene.TemporalResolveActive);
            Assert.True(rig.Scene.TemporalActive);
            Assert.Equal(1, rig.Scene.RenderTargetSampleCount);

            rig.Scene.Post.Pixelated = true;
            rig.Frame(240, 240);
            Assert.False(rig.Scene.TemporalResolveActive);
            Assert.False(rig.Scene.TemporalActive);
            Assert.Equal(System.Numerics.Vector2.Zero, rig.Scene.CurrentFrameView.JitterPixels);
        }

        [Theory]
        [InlineData(TemporalUpscale.Native, 240, 8)]
        [InlineData(TemporalUpscale.Quality, 160, 18)]
        [InlineData(TemporalUpscale.Performance, 120, 32)]
        [InlineData(TemporalUpscale.UltraPerformance, 80, 72)]
        public void The_jitter_cycle_follows_the_display_over_internal_ratio(TemporalUpscale preset, int internalSize, int phases)
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Scene.Post.Temporal.Upscale = preset;
            var seen = new HashSet<int>();
            for (int i = 0; i < 2 * phases + 4; i++)
            {
                rig.Frame(240, 240);
                FrameView view = rig.Scene.CurrentFrameView;
                Assert.Equal(TemporalJitter.Offset(view.FrameIndex, phases), view.JitterPixels);
                seen.Add(rig.Scene.LastTemporalDiagnostics.JitterPhase);
            }
            Assert.Equal((internalSize, internalSize), (rig.Scene.RenderTargetWidth, rig.Scene.RenderTargetHeight));
            Assert.Equal(phases, seen.Count);
            Assert.Equal(240f / internalSize, rig.Scene.TemporalDisplayOverInternal(240, 240));
        }

        /// <summary>
        /// The jitter cycle reads the unrounded display over internal scale with the render cap included, the rule on
        /// <see cref="TemporalJitter.PhaseCount"/>. The ratio of the rounded sizes takes a phase too many on
        /// <see cref="TemporalUpscale.Quality"/> and <see cref="TemporalUpscale.UltraPerformance"/>, and the preset's
        /// ratio alone takes too few on <see cref="TemporalUpscale.Native"/> once the cap bites. The frames run to the
        /// index where the right sequence first wraps, the first index whose jitter tells it from a longer one.
        /// </summary>
        [Theory]
        [InlineData(TemporalUpscale.Quality, 3456, 2234, 2304, 1489, 18, 19, 18)]
        [InlineData(TemporalUpscale.UltraPerformance, 2560, 1600, 853, 533, 72, 73, 72)]
        [InlineData(TemporalUpscale.Native, 5120, 2880, 3840, 2160, 15, 15, 8)]
        public void The_jitter_cycle_reads_the_unrounded_scale_with_the_render_cap(TemporalUpscale preset,
            int displayWidth, int displayHeight, int internalWidth, int internalHeight, int phases,
            int fromRoundedSizes, int fromPresetAlone)
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Scene.Post.Temporal.Upscale = preset;
            for (int i = 0; i < phases; i++)
            {
                rig.Frame(displayWidth, displayHeight);
                FrameView view = rig.Scene.CurrentFrameView;
                Assert.Equal(TemporalJitter.Offset(view.FrameIndex, phases), view.JitterPixels);
            }
            Assert.Equal(phases, rig.Scene.CurrentFrameView.FrameIndex);
            Assert.Equal((internalWidth, internalHeight), (rig.Scene.RenderTargetWidth, rig.Scene.RenderTargetHeight));
            Assert.Equal(phases, TemporalJitter.PhaseCount(rig.Scene.TemporalDisplayOverInternal(displayWidth, displayHeight)));
            Assert.Equal(fromRoundedSizes, TemporalJitter.PhaseCount(
                TemporalResolveMath.DisplayOverInternal(displayWidth, displayHeight, internalWidth, internalHeight)));
            Assert.Equal(fromPresetAlone, TemporalJitter.PhaseCount(TemporalSettings.DisplayOverInternal(preset)));
        }

        /// <summary>The ratio re-derives the cap <c>Scene3D.ComputeTargetSize</c> applies, so it has to keep agreeing
        /// with the internal size that method returns: within one internal pixel per axis, with and without the cap, on
        /// a display whose aspect differs from the cap's.</summary>
        [Theory]
        [InlineData(TemporalUpscale.Native, 5120, 2880)]
        [InlineData(TemporalUpscale.Native, 7680, 3000)]
        [InlineData(TemporalUpscale.Quality, 7680, 4320)]
        [InlineData(TemporalUpscale.Balanced, 3456, 2234)]
        [InlineData(TemporalUpscale.Performance, 1001, 777)]
        [InlineData(TemporalUpscale.UltraPerformance, 2560, 1600)]
        public void The_ratio_matches_the_internal_size_to_within_one_pixel(TemporalUpscale preset, int displayWidth,
            int displayHeight)
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Scene.Post.Temporal.Upscale = preset;
            float ratio = rig.Scene.TemporalDisplayOverInternal(displayWidth, displayHeight);
            var (internalWidth, internalHeight) = Scene3D.ComputeTargetSize(rig.Scene.Post, displayWidth, displayHeight);
            Assert.InRange(internalWidth - displayWidth / ratio, -1f, 1f);
            Assert.InRange(internalHeight - displayHeight / ratio, -1f, 1f);
        }

        [Fact]
        public void A_preset_change_resets_history_as_a_render_scale_change()
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            for (int i = 0; i < 3; i++) rig.Frame(240, 240);
            Assert.True(rig.Scene.LastTemporalDiagnostics.HistoryValid);

            rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
            rig.Frame(240, 240);
            Assert.False(rig.Scene.LastTemporalDiagnostics.HistoryValid);
            Assert.Equal(TemporalResetReason.RenderScale, rig.Scene.LastTemporalDiagnostics.LastReset);

            rig.Frame(240, 240);
            Assert.True(rig.Scene.LastTemporalDiagnostics.HistoryValid);
        }

        [Fact]
        public void A_ratio_change_resets_history_even_when_the_capped_internal_size_holds()
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Scene.Post.MaxRenderWidth = 64;
            rig.Scene.Post.MaxRenderHeight = 64;
            rig.Scene.Post.Temporal.UpscaleRatio = 0.75f;      // 240 x 0.75 = 180, capped to 64
            for (int i = 0; i < 3; i++) rig.Frame(240, 240);
            Assert.Equal(64, rig.Scene.RenderTargetWidth);
            Assert.True(rig.Scene.LastTemporalDiagnostics.HistoryValid);

            rig.Scene.Post.Temporal.UpscaleRatio = 0.8f;       // 192, still capped to 64
            rig.Frame(240, 240);
            Assert.Equal(64, rig.Scene.RenderTargetWidth);
            Assert.False(rig.Scene.LastTemporalDiagnostics.HistoryValid);
            Assert.Equal(TemporalResetReason.RenderScale, rig.Scene.LastTemporalDiagnostics.LastReset);
        }

        /// <summary>A capped internal size can hide a display resize from the internal size check, so the display size
        /// is keyed too, and the previous view is dropped with the history.</summary>
        [Fact]
        public void A_display_resize_the_capped_internal_size_hides_is_still_a_resize()
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Scene.Post.MaxRenderWidth = 64;
            rig.Scene.Post.MaxRenderHeight = 64;
            for (int i = 0; i < 3; i++) rig.Frame(240, 240);
            Assert.True(rig.Scene.LastTemporalDiagnostics.HistoryValid);

            rig.Frame(320, 320);
            Assert.Equal((64, 64), (rig.Scene.RenderTargetWidth, rig.Scene.RenderTargetHeight));
            Assert.False(rig.Scene.LastTemporalDiagnostics.HistoryValid);
            Assert.Equal(TemporalResetReason.Resize, rig.Scene.LastTemporalDiagnostics.LastReset);
            Assert.Null(rig.Scene.PreviousFrameView);

            rig.Frame(320, 320);
            Assert.True(rig.Scene.LastTemporalDiagnostics.HistoryValid);
        }

        /// <summary>Supersample does not size the target under temporal anti-aliasing, so changing it keeps history.</summary>
        [Fact]
        public void A_supersample_change_under_temporal_anti_aliasing_keeps_history()
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            for (int i = 0; i < 3; i++) rig.Frame(240, 240);

            rig.Scene.Post.Supersample = 2f;
            rig.Frame(240, 240);
            Assert.Equal(240, rig.Scene.RenderTargetWidth);
            Assert.True(rig.Scene.LastTemporalDiagnostics.HistoryValid);
        }

        [Fact]
        public void Without_temporal_anti_aliasing_the_ratio_is_one_whatever_the_preset()
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Temporal.Upscale = TemporalUpscale.UltraPerformance;
            rig.Scene.ForceTemporalForTests = true;
            rig.Frame(240, 240);
            Assert.False(rig.Scene.TemporalResolveActive);
            Assert.Equal(1f, rig.Scene.TemporalDisplayOverInternal(240, 240));
            Assert.Equal(TemporalJitter.Offset(rig.Scene.CurrentFrameView.FrameIndex, TemporalJitter.NativePhaseCount),
                rig.Scene.CurrentFrameView.JitterPixels);
        }
    }
}
