using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>LastTemporalDiagnostics reports the internal and display sizes, the preset and the effective
    /// ratio.</summary>
    public sealed class TemporalDiagnosticsSizesGpuTests
    {
        [GpuTheory]
        [InlineData(TemporalUpscale.Native, 320, 180)]
        [InlineData(TemporalUpscale.Quality, 213, 120)]
        [InlineData(TemporalUpscale.Balanced, 188, 106)]
        [InlineData(TemporalUpscale.Performance, 160, 90)]
        [InlineData(TemporalUpscale.UltraPerformance, 107, 60)]
        public void TheDiagnosticsReportTheSizesAndThePreset(TemporalUpscale preset, int wantW, int wantH)
        {
            using var fx = new TemporalFixture(320, 180, s =>
            {
                s.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
                s.Post.Temporal.Upscale = preset;
            });
            fx.Frame((_, _) => { });
            TemporalDiagnostics d = fx.Scene.LastTemporalDiagnostics;
            Assert.Equal(320, d.DisplayWidth);
            Assert.Equal(180, d.DisplayHeight);
            Assert.InRange(d.InternalWidth, wantW - 1, wantW + 1);
            Assert.InRange(d.InternalHeight, wantH - 1, wantH + 1);
            Assert.Equal(preset, d.Preset);
            Assert.Equal(wantW / 320f, d.UpscaleRatio, 2);
            Assert.Equal(-1, d.CountsFrameIndex);
        }

        [GpuFact]
        public void AnExplicitRatioOverridesThePresetInTheReportedSize()
        {
            using var fx = new TemporalFixture(320, 180, s =>
            {
                s.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
                s.Post.Temporal.Upscale = TemporalUpscale.Quality;
                s.Post.Temporal.UpscaleRatio = 0.8f;
            });
            fx.Frame((_, _) => { });
            TemporalDiagnostics d = fx.Scene.LastTemporalDiagnostics;
            Assert.InRange(d.InternalWidth, 255, 257);
            Assert.Equal(0.8f, d.UpscaleRatio, 2);
            Assert.Equal(TemporalUpscale.Quality, d.Preset);   // the setting, which the explicit ratio overrides
        }

        /// <summary>A capture inside the frame at another size, after a preset change, leaves the frame's diagnostics
        /// alone, the sizes and preset included.</summary>
        [GpuFact]
        public void ALaterRenderAtAnotherSizeLeavesTheFramesDiagnosticsAlone()
        {
            using var fx = new TemporalFixture(320, 180, s =>
            {
                s.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
                s.Post.Temporal.Upscale = TemporalUpscale.Quality;
            });
            fx.Frames(2, (_, _) => { });
            TemporalDiagnostics frame = fx.Scene.LastTemporalDiagnostics;
            Assert.Equal((213, 120, 320, 180), (frame.InternalWidth, frame.InternalHeight, frame.DisplayWidth,
                frame.DisplayHeight));

            fx.Scene.Post.Temporal.Upscale = TemporalUpscale.Performance;
            fx.RenderSecond(160, 90);
            Assert.Equal((80, 45), (fx.Scene.RenderTargetWidth, fx.Scene.RenderTargetHeight));
            Assert.Equal(frame, fx.Scene.LastTemporalDiagnostics);
        }
    }
}
