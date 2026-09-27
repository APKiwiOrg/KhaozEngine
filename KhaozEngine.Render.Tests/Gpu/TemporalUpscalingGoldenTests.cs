using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN section 7 item 6: at Quality a converged still frame is closer to a native
    /// reference than the same frame bilinearly upscaled, on a resolution chart. The same frame is the chart converged
    /// by temporal anti-aliasing at Native on a display the size of Quality's internal target, through the same stage,
    /// so it frames the same view at the same aspect as Quality's internal target does.
    /// <para>
    /// The native reference renders the chart with anti-aliasing off at 8 times the size per axis and box-filters it
    /// on the CPU (<see cref="TemporalAcceptance.Supersampled"/>). Eight matches the convergence acceptance and gives
    /// the rotated square and the 1.2 pixel bars, whose edges miss the quarter-pixel lattice, 64 coverage steps a
    /// pixel. A 4x reference ranks the images the same.
    /// </para>
    /// <para>
    /// HDR is off in every render, the reference included, so the chain has no tonemap and the order of averaging and
    /// tonemapping cannot matter. The chart is white on a dark ground, so the resolve's luma-weighted blend darkens its
    /// thinnest bars by design. Both compared images are temporal outputs and share that bias, so the gate stays
    /// against the plain box average. The distance to the luma-weighted average of the same samples, the bias and
    /// each bar group's retained contrast are printed, not asserted.
    /// </para>
    /// </summary>
    public sealed class TemporalUpscalingGoldenTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180, IW = 213, IH = 120, Factor = 8, Frames = 48;

        // Acceptance threshold, a first estimate the first real-Metal run retunes in one place: Quality's error
        // against native, as a share of the bilinear upscale's.
        const double MaxErrorShareOfBilinear = 0.85;

        // The chart sits a quarter of a display pixel off the pixel grid on both axes. On the grid the 1 pixel bars'
        // edges fall on pixel centres, so the reference averages that whole group to one flat grey and the finest
        // group would reward blur instead of measuring it.
        const float OffsetPixels = 0.25f;

        const float Px = 4.5f / H;
        static readonly float[] BarPixels = { 3f, 2f, 1.5f, 1.2f, 1f };

        // Five groups of five bars, bar and gap equal at 3, 2, 1.5, 1.2 and 1 display pixel: vertical bars on the top
        // row, horizontal bars on the bottom row, and a square turned 20 degrees between them.
        static void Chart(FrontStage stage, Scene3D s)
        {
            var white = new Color(0.95f, 0.95f, 0.95f, 1f);
            float shift = OffsetPixels * Px;
            for (int g = 0; g < BarPixels.Length; g++)
            {
                float bar = BarPixels[g] * Px, left = -3.6f + g * 1.5f + shift;
                for (int b = 0; b < 5; b++)
                {
                    float o = left + b * 2f * bar;
                    s.Draw(stage.Box, Matrix4x4.CreateScale(bar, 1.2f, 0.1f)
                        * Matrix4x4.CreateTranslation(o, 1.2f, 0f), white);
                    s.Draw(stage.Box, Matrix4x4.CreateScale(1.2f, bar, 0.1f)
                        * Matrix4x4.CreateTranslation(left + 0.6f, -1.8f - shift + b * 2f * bar, 0f), white);
                }
            }
            s.Draw(stage.Box, Matrix4x4.CreateScale(0.9f, 0.9f, 0.1f) * Matrix4x4.CreateRotationZ(0.35f)
                * Matrix4x4.CreateTranslation(0f, -0.2f, 0f), white);
        }

        static Action<Scene3D> Setup(FrontStage stage, AntiAliasing aa, TemporalUpscale preset) => s =>
        {
            stage.Setup(s, aa, preset);
            s.Post.Hdr.Enabled = false;   // the legacy chain, no tonemap, see the class summary
        };

        [GpuFact]
        public void QualityUpscalingBeatsABilinearUpscaleOfTheSameFrame()
        {
            var stage = new FrontStage(W, H, 4.5f);
            void Draw(Scene3D s, int _) => Chart(stage, s);

            // Both references average the same anti-aliasing off samples.
            byte[] samples = TemporalAcceptance.Supersampled(W * Factor, H * Factor, 1,
                Setup(stage, AntiAliasing.Off, TemporalUpscale.Native), Draw);
            byte[] native = Rgba8Stats.BoxDownsample(samples, W * Factor, H * Factor, Factor);
            byte[] weighted = LumaWeightedReference.Downsample(samples, W * Factor, H * Factor, Factor);

            byte[] quality;
            TemporalDiagnostics d;
            using (var fx = new TemporalFixture(W, H, Setup(stage, AntiAliasing.Temporal, TemporalUpscale.Quality)))
            {
                fx.Frames(Frames - 1, Draw);
                quality = fx.Frame(Draw);
                d = fx.Scene.LastTemporalDiagnostics;
            }
            // The scene camera takes the display's aspect, 213 by 120 here, while Quality's internal target keeps the
            // 320 by 180 view. The stage's own camera as the override frames that view on the small display.
            Action<Scene3D> smallSetup = Setup(stage, AntiAliasing.Temporal, TemporalUpscale.Native);
            byte[] small;
            using (var fx = new TemporalFixture(IW, IH, s => { smallSetup(s); s.CameraOverride = stage.Camera(); }))
            {
                fx.Frames(Frames - 1, Draw);
                small = fx.Frame(Draw);
            }
            byte[] bilinear = TemporalAcceptance.BilinearUpscale(small, IW, IH, W, H);

            var all = new PixelRect(4, 4, W - 4, H - 4);
            double eq = TemporalAcceptance.MeanAbsLuma(quality, native, W, all);
            double eb = TemporalAcceptance.MeanAbsLuma(bilinear, native, W, all);
            output.WriteLine($"HDR off, frame {Frames}, Quality internal {d.InternalWidth}x{d.InternalHeight} of "
                + $"{d.DisplayWidth}x{d.DisplayHeight}, ratio {d.UpscaleRatio:0.000000}");
            output.WriteLine($"mean abs luma against the 8x box average: Quality upscale {eq:0.00000}, bilinear "
                + $"upscale {eb:0.00000}, share {eq / eb:0.0000} against a bound of {MaxErrorShareOfBilinear}");
            output.WriteLine("mean abs luma against the luma-weighted average: Quality upscale "
                + $"{TemporalAcceptance.MeanAbsLuma(quality, weighted, W, all):0.00000}, bilinear upscale "
                + $"{TemporalAcceptance.MeanAbsLuma(bilinear, weighted, W, all):0.00000}");
            PrintBias(native, weighted, quality, bilinear, all);
            PrintGroups(native, quality, bilinear);

            Assert.InRange(d.InternalWidth, IW - 1, IW + 1);
            Assert.InRange(d.InternalHeight, IH - 1, IH + 1);
            Assert.True(eq < MaxErrorShareOfBilinear * eb, "Quality must be at least 15% closer to native than a "
                + $"bilinear upscale: {eq:0.0000} against {eb:0.0000}");
            GoldenCompare.AssertOrUpdate("temporal_upscale_quality_chart", quality, W, H);
        }

        /// <summary>The luma weighting bias: the distance between the two references, and the mean luma of each image
        /// over the feature pixels, those whose box average is more than 0.02 brighter than the ground. The ground is
        /// the darkest box average in the region, which no bar covers.</summary>
        void PrintBias(byte[] box, byte[] weighted, byte[] quality, byte[] bilinear, PixelRect region)
        {
            float ground = float.MaxValue;
            for (int y = region.Y0; y < region.Y1; y++)
                for (int x = region.X0; x < region.X1; x++)
                    ground = MathF.Min(ground, TemporalAcceptance.Luma(box, W, x, y));
            byte[][] images = { box, weighted, quality, bilinear };
            var sums = new double[images.Length];
            int count = 0;
            for (int y = region.Y0; y < region.Y1; y++)
                for (int x = region.X0; x < region.X1; x++)
                {
                    if (TemporalAcceptance.Luma(box, W, x, y) <= ground + 0.02f) continue;
                    count++;
                    for (int k = 0; k < images.Length; k++) sums[k] += TemporalAcceptance.Luma(images[k], W, x, y);
                }
            double Mean(int k) => count == 0 ? 0 : sums[k] / count;
            output.WriteLine($"bias: references {TemporalAcceptance.MeanAbsLuma(weighted, box, W, region):0.00000} "
                + $"apart, feature pixels {count} on a ground of {ground:0.0000}: box {Mean(0):0.0000}, weighted "
                + $"{Mean(1):0.0000}, Quality {Mean(2):0.0000}, bilinear {Mean(3):0.0000}");
        }

        /// <summary>Each bar group's local contrast in the reference, and the share of it Quality and the bilinear
        /// upscale keep. A group whose bars resolve keeps most of it.</summary>
        void PrintGroups(byte[] native, byte[] quality, byte[] bilinear)
        {
            float shift = OffsetPixels * Px;
            for (int g = 0; g < BarPixels.Length; g++)
            {
                float bar = BarPixels[g] * Px, left = -3.6f + g * 1.5f + shift, bottom = -1.8f - shift;
                var vertical = new PixelRect(Col(left - bar / 2f) - 1, Row(1.7f), Col(left + 8.5f * bar) + 1,
                    Row(0.7f));
                var horizontal = new PixelRect(Col(left + 0.1f), Row(bottom + 8.5f * bar) - 1, Col(left + 1.1f),
                    Row(bottom - bar / 2f) + 1);
                output.WriteLine($"{BarPixels[g]} px bars: vertical {Kept(vertical)}, horizontal {Kept(horizontal)}");
            }

            string Kept(PixelRect r)
            {
                double n = TemporalAcceptance.LocalContrast(native, W, H, r);
                double q = TemporalAcceptance.LocalContrast(quality, W, H, r);
                double b = TemporalAcceptance.LocalContrast(bilinear, W, H, r);
                return $"reference contrast {n:0.0000}, Quality keeps {q / n:0.000}, bilinear keeps {b / n:0.000}";
            }
        }

        static int Col(float x) => (int)MathF.Round((x + W * Px / 2f) / Px);

        static int Row(float y) => (int)MathF.Round((H * Px / 2f - y) / Px);
    }
}
