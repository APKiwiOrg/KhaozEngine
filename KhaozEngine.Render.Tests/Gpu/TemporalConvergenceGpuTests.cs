using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN section 7 item 1 and amendment 21: a static scene of thin geometry converges
    /// to within tolerance of an 8x supersampled reference after 32 frames. Every frame from 32 to 48 is checked, two
    /// full jitter cycles, so one lucky phase cannot pass.
    /// <para>
    /// HDR is off, so the chain has no tonemap and the resolve, MSAA and the reference all average the same display
    /// values. The order of averaging and tonemapping then cannot matter. The sharpen is off because the claim is about
    /// the resolve, and the sharpen would move the image away from any average.
    /// </para>
    /// <para>
    /// There are two references because the resolve blends in the luma-weighted space <c>c / (1 + luma(c))</c>, its
    /// firefly protection, which darkens a bright thin feature on a dark ground by design. The mid-contrast fence, the
    /// case closer to grass, is gated against the plain 8x box average, where that bias is small. The high-contrast
    /// fence is gated against a reference averaged the way the resolve averages (<see cref="LumaWeightedReference"/>),
    /// so its gate measures convergence and not the weighting. Its distance to the box average and the bias are
    /// printed, not asserted. Every bound is relative to frames of the same session, and the measured values are
    /// printed.
    /// </para>
    /// </summary>
    public sealed class TemporalConvergenceGpuTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180, Factor = 8;
        const int FirstChecked = 32, LastChecked = 48;   // frame numbers from 1, the first frame is frame 1

        // Acceptance thresholds, first estimates that the first real-Metal run retunes in one place. Errors are mean
        // abs luma against a supersampled reference over the fence's region.
        const double MinAliasingError = 0.02;         // the fence has to alias without anti-aliasing
        const double MaxShareOfAliasingError = 0.35;  // 32 frames remove at least 65% of it
        const double MaxShareOfFirstFrame = 0.6;      // history improves on the first frame by at least 40%
        const double MaxConvergedError = 0.05;        // the converged frame against the reference

        /// <summary>The distances of one scene's frames. <c>Box</c> are against the 8x box average,
        /// <c>Weighted</c> against the luma-weighted average of the same samples. <c>Worst</c> is the largest over
        /// frames 32 to 48.</summary>
        readonly record struct Convergence(double OffBox, double FirstBox, double WorstBox, double OffWeighted,
            double WorstWeighted);

        [GpuFact]
        public void MidContrastThinGeometryConvergesTowardAnEightTimesSupersampledReference()
        {
            var fence = new FenceScene(W, H);
            Convergence c = Measure("mid contrast", fence, (s, _) => MidContrastFence(fence, s));
            string ctx = $"off {c.OffBox:0.00000}, frame 1 {c.FirstBox:0.00000}, worst of frames {FirstChecked} to "
                + $"{LastChecked} {c.WorstBox:0.00000}, all against the 8x box average";

            Assert.True(c.OffBox > MinAliasingError, $"the fence must alias without anti-aliasing. {ctx}");
            Assert.True(c.WorstBox <= MaxShareOfAliasingError * c.OffBox,
                $"32 frames must remove at least 65% of the aliasing error. {ctx}");
            Assert.True(c.WorstBox <= MaxShareOfFirstFrame * c.FirstBox,
                $"history must improve on the first frame by at least 40%. {ctx}");
            Assert.True(c.WorstBox <= MaxConvergedError,
                $"the converged frame must sit within {MaxConvergedError} mean luma of the reference. {ctx}");
        }

        [GpuFact]
        public void HighContrastThinGeometryConvergesTowardTheLumaWeightedSupersampledAverage()
        {
            var fence = new FenceScene(W, H);
            Convergence c = Measure("high contrast", fence, (s, _) => fence.Draw(s, 0f));
            string ctx = $"box: off {c.OffBox:0.00000}, frame 1 {c.FirstBox:0.00000}, worst {c.WorstBox:0.00000}. "
                + $"weighted: off {c.OffWeighted:0.00000}, worst {c.WorstWeighted:0.00000}";

            Assert.True(c.OffBox > MinAliasingError, $"the fence must alias without anti-aliasing. {ctx}");
            Assert.True(c.WorstWeighted <= MaxShareOfAliasingError * c.OffWeighted,
                $"32 frames must remove at least 65% of the aliasing error against the luma-weighted average. {ctx}");
            Assert.True(c.WorstBox <= MaxShareOfFirstFrame * c.FirstBox,
                $"history must improve on the first frame by at least 40%. {ctx}");
            Assert.True(c.WorstBox <= MaxConvergedError,
                $"the converged frame must sit within {MaxConvergedError} mean luma of the reference. {ctx}");
        }

        /// <summary>The fence's bars twice over, 41 bars a third of a pixel wide at 30 degrees, 0.175 m apart, in front
        /// of the flat grey wall. Bars that light to white on a wall of luma 0.6 are a mid contrast, and twice the bars
        /// alias past <see cref="MinAliasingError"/> at that contrast.</summary>
        static void MidContrastFence(FenceScene fence, Scene3D s)
        {
            float px = fence.Stage.PixelWorld;
            for (int i = 0; i <= 40; i++)
                s.Draw(fence.Stage.Box, Matrix4x4.CreateScale(0.35f * px, 3.6f, 0.35f * px)
                    * Matrix4x4.CreateRotationZ(0.52f) * Matrix4x4.CreateTranslation(-3.5f + 7f * i / 40f, 0f, 0f),
                    new Color(0.95f, 0.95f, 0.95f, 1f));
            fence.Stage.Wall(s);
        }

        static Action<Scene3D> Setup(FenceScene fence, AntiAliasing aa) => s =>
        {
            fence.Stage.Setup(s, aa, TemporalUpscale.Native, sharpness: 0f);
            s.Post.Hdr.Enabled = false;   // the legacy chain, no tonemap, see the class summary
        };

        Convergence Measure(string name, FenceScene fence, Action<Scene3D, int> draw)
        {
            Action<Scene3D> reference = Setup(fence, AntiAliasing.Off);
            byte[] box = TemporalAcceptance.Supersampled(W, H, Factor, reference, draw);
            // The same samples unfiltered: a factor of 1 at the reference size is the raw anti-aliasing off render.
            byte[] samples = TemporalAcceptance.Supersampled(W * Factor, H * Factor, 1, reference, draw);
            byte[] weighted = LumaWeightedReference.Downsample(samples, W * Factor, H * Factor, Factor);
            byte[] off = TemporalAcceptance.Snapshot(W, H, Setup(fence, AntiAliasing.Off), s => draw(s, 0));
            byte[] msaa = TemporalAcceptance.Snapshot(W, H, Setup(fence, AntiAliasing.Msaa(4)), s => draw(s, 0));

            byte[] first;
            var window = new byte[LastChecked - FirstChecked + 1][];
            using (var fx = new TemporalFixture(W, H, Setup(fence, AntiAliasing.Temporal)))
            {
                first = fx.Frame(draw);
                fx.Frames(FirstChecked - 2, draw);
                for (int i = 0; i < window.Length; i++) window[i] = fx.Frame(draw);
            }

            PixelRect region = fence.Region;
            double ToBox(byte[] image) => TemporalAcceptance.MeanAbsLuma(image, box, W, region);
            double ToWeighted(byte[] image) => TemporalAcceptance.MeanAbsLuma(image, weighted, W, region);
            var c = new Convergence(ToBox(off), ToBox(first), window.Max(ToBox), ToWeighted(off),
                window.Max(ToWeighted));
            double bestBox = window.Min(ToBox), bestWeighted = window.Min(ToWeighted);

            output.WriteLine($"{name}, HDR off, Native, frames {FirstChecked} to {LastChecked}, mean abs luma:");
            output.WriteLine($"  against the 8x box average: off {c.OffBox:0.00000}, MSAA 4x {ToBox(msaa):0.00000}, "
                + $"frame 1 {c.FirstBox:0.00000}, frame 32 {ToBox(window[0]):0.00000}, frames {bestBox:0.00000} to "
                + $"{c.WorstBox:0.00000}, share of off {bestBox / c.OffBox:0.0000} to {c.WorstBox / c.OffBox:0.0000}");
            output.WriteLine($"  against the luma-weighted average: off {c.OffWeighted:0.00000}, MSAA 4x "
                + $"{ToWeighted(msaa):0.00000}, frame 1 {ToWeighted(first):0.00000}, frame 32 "
                + $"{ToWeighted(window[0]):0.00000}, frames {bestWeighted:0.00000} to {c.WorstWeighted:0.00000}, share "
                + $"of off {bestWeighted / c.OffWeighted:0.0000} to {c.WorstWeighted / c.OffWeighted:0.0000}");
            PrintBias(box, weighted, window[0], off, msaa, region);
            return c;
        }

        /// <summary>The weighting bias: the distance between the two references, and the mean luma of each image over
        /// the feature pixels, those whose box average is more than 0.02 brighter than the background. The background
        /// is the darkest box average in the region, which the bright bars never cover.</summary>
        void PrintBias(byte[] box, byte[] weighted, byte[] frame32, byte[] off, byte[] msaa, PixelRect region)
        {
            float background = float.MaxValue;
            for (int y = region.Y0; y < region.Y1; y++)
                for (int x = region.X0; x < region.X1; x++)
                    background = MathF.Min(background, TemporalAcceptance.Luma(box, W, x, y));
            byte[][] images = { box, weighted, frame32, off, msaa };
            var sums = new double[images.Length];
            int count = 0;
            for (int y = region.Y0; y < region.Y1; y++)
                for (int x = region.X0; x < region.X1; x++)
                {
                    if (TemporalAcceptance.Luma(box, W, x, y) <= background + 0.02f) continue;
                    count++;
                    for (int k = 0; k < images.Length; k++) sums[k] += TemporalAcceptance.Luma(images[k], W, x, y);
                }
            double Mean(int k) => count == 0 ? 0 : sums[k] / count;
            output.WriteLine($"  bias: references {TemporalAcceptance.MeanAbsLuma(weighted, box, W, region):0.00000} "
                + $"apart, region mean luma box {TemporalAcceptance.MeanLuma(box, W, region):0.00000}, weighted "
                + $"{TemporalAcceptance.MeanLuma(weighted, W, region):0.00000}, frame 32 "
                + $"{TemporalAcceptance.MeanLuma(frame32, W, region):0.00000}");
            output.WriteLine($"  feature pixels {count} on a background of {background:0.0000}: box {Mean(0):0.0000}, "
                + $"weighted {Mean(1):0.0000}, frame 32 {Mean(2):0.0000}, off {Mean(3):0.0000}, "
                + $"MSAA 4x {Mean(4):0.0000}");
        }
    }

    /// <summary>
    /// A supersampled reference averaged the way the temporal resolve averages. Each output pixel maps its
    /// <c>factor</c> squared samples into the luma-weighted space <c>c / (1 + luma(c))</c>, averages them there, and
    /// maps the mean back with <c>w / max(1 - luma(w), 1e-4)</c>, with Rec. 709 luma on 0 to 1 values. These are the
    /// resolve's own <c>toWeighted</c> and <c>fromWeighted</c> (ShaderSources.TemporalResolve), so with HDR off, where
    /// the resolve reads the displayed values, this is the image it converges to over the same samples with a box
    /// kernel. Alpha is a plain mean, as the resolve blends it. Each value rounds to the nearest byte.
    /// </summary>
    internal static class LumaWeightedReference
    {
        static double Luma(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

        public static byte[] Downsample(byte[] rgba, int width, int height, int factor)
        {
            if (factor < 1 || width % factor != 0 || height % factor != 0)
                throw new ArgumentException("The image size is not a multiple of the factor.", nameof(factor));
            if (rgba.Length != width * height * 4)
                throw new ArgumentException("The image is not width by height RGBA8 pixels.", nameof(rgba));
            int w = width / factor, h = height / factor, n = factor * factor;
            var small = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double r = 0, g = 0, b = 0, a = 0;
                    for (int dy = 0; dy < factor; dy++)
                        for (int dx = 0; dx < factor; dx++)
                        {
                            int i = ((y * factor + dy) * width + x * factor + dx) * 4;
                            double sr = rgba[i] / 255.0, sg = rgba[i + 1] / 255.0, sb = rgba[i + 2] / 255.0;
                            double k = 1.0 / (1.0 + Luma(sr, sg, sb));
                            r += sr * k; g += sg * k; b += sb * k; a += rgba[i + 3];
                        }
                    r /= n; g /= n; b /= n;
                    double back = 1.0 / Math.Max(1.0 - Luma(r, g, b), 1e-4);
                    int o = (y * w + x) * 4;
                    small[o] = ToByte(r * back);
                    small[o + 1] = ToByte(g * back);
                    small[o + 2] = ToByte(b * back);
                    small[o + 3] = ToByte(a / n / 255.0);
                }
            return small;
        }

        static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v * 255.0, MidpointRounding.AwayFromZero), 0, 255);
    }
}
