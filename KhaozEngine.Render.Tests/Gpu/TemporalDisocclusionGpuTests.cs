using System;
using System.Collections.Generic;
using System.Diagnostics;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN section 7 item 4: a revealed background shows no history from the object that
    /// covered it, on the very first frame after the reveal.
    /// <para>
    /// <see cref="RevealScene"/>'s keyed red box covers the wall for 32 frames and then jumps clear, so the revealed
    /// pixels hold nothing but the box in their history. Their first frame after the reveal is compared with the same
    /// frame of the bare wall rendered from scratch: a fresh scene at the same frame index and jitter, whose resolve
    /// has no history and takes the current frame alone. That is exactly what a pixel whose history was dropped shows,
    /// so any difference is history carried from the box. A converged bare wall is no floor for this, because on the
    /// textured wall a single jittered frame differs from an accumulated one. Each pixel is read by its largest channel
    /// difference, which sees a red tint and also a luma the box left behind: the grey texture has no chroma range, so
    /// the neighbourhood clip strips the box's red but keeps a luma within the texture's range.
    /// </para>
    /// <para>
    /// The flat wall and the textured wall are both asserted at Native and Quality, and the table test prints every
    /// preset. The textured wall is the harder case, because its variation widens the clip. HDR is off and the sharpen
    /// is at its default. Every comparison is between runs of one session, and every measured value is printed.
    /// </para>
    /// </summary>
    public sealed class TemporalDisocclusionGpuTests(TemporalRevealRuns runs, ITestOutputHelper output)
        : IClassFixture<TemporalRevealRuns>
    {
        // The revealed pixels against the from-scratch wall, by the largest channel difference in 8-bit steps: the
        // mean over the region and the worst pixel. Measured a mean of 0 and a worst of 0 at Native on both walls, and
        // means of 0.005 over the flat wall and 0.033 over the textured wall with a worst of 4 at Quality.
        const double MaxMeanSteps = 3;
        const int MaxWorstSteps = 20;

        // On the last covered frame the region must differ from the bare wall by at least this mean, or the box was
        // not there to leave anything. Measured 136 to 138.
        const double MinOccluderMeanSteps = 64;

        /// <summary>
        /// Every revealed pixel shows the bare wall as a render with no history would. Over the textured wall this is
        /// the disocclusion test's work: with it disabled the box's luma survives the clip. Over the flat wall the
        /// neighbourhood clip alone also removes the box at Native, so that case stays clean with the disocclusion
        /// test disabled and fails only with the clip off as well.
        /// </summary>
        [GpuTheory]
        [InlineData(false, TemporalUpscale.Native)]
        [InlineData(false, TemporalUpscale.Quality)]
        [InlineData(true, TemporalUpscale.Native)]
        [InlineData(true, TemporalUpscale.Quality)]
        public void ARevealedBackgroundCarriesNoHistoryFromItsOccluder(bool textured, TemporalUpscale preset)
        {
            RevealRun r = runs.Reveal(textured, preset);
            output.WriteLine(r.ToString());
            Assert.Equal(58 * 58, r.Region.Area);
            Assert.True(r.OccluderMean >= MinOccluderMeanSteps,
                $"the box did not cover the region before the reveal. {r}");
            Assert.True(r.Mean <= MaxMeanSteps, $"the revealed wall carries the box's history on average. {r}");
            Assert.True(r.Worst <= MaxWorstSteps, $"a revealed pixel carries the box's history. {r}");
        }

        /// <summary>
        /// Both walls at every preset. Balanced and UltraPerformance are printed and not asserted: at their scale a
        /// corner pixel of the region lies inside an internal texel the box only partly covered, and the thin-feature
        /// lock holds that corner, so it keeps the box's luma, not its red, for a few frames
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/1185).
        /// </summary>
        [GpuFact]
        public void TheRevealTableCoversEveryPreset()
        {
            output.WriteLine("| Wall | Preset | Occluder mean | Mean | Worst | Red mean | Red worst | Pixels over 12 "
                + "| Converged mean | Converged red worst | Fresh against converged |");
            output.WriteLine("|" + string.Concat(System.Linq.Enumerable.Repeat(" --- |", 11)));
            foreach (bool textured in new[] { false, true })
                foreach (TemporalUpscale preset in Enum.GetValues<TemporalUpscale>())
                {
                    RevealRun r = runs.Reveal(textured, preset);
                    output.WriteLine($"| {(textured ? "textured" : "flat")} | {preset} | {r.OccluderMean:0.0} "
                        + $"| {r.Mean:0.000} | {r.Worst} | {r.RedMean:0.000} | {r.RedWorst} | {r.Over} "
                        + $"| {r.ConvergedMean:0.000} | {r.ConvergedRedWorst} | {r.FreshMean:0.000} |");
                    Assert.Equal(58 * 58, r.Region.Area);
                }
            output.WriteLine($"every run: {runs.Seconds:0.0} s");
        }
    }

    /// <summary>
    /// One reveal, over <see cref="Region"/>, the pixels the box covered less a one-pixel border, in 8-bit steps.
    /// <see cref="Mean"/>, <see cref="Worst"/> and <see cref="Over"/> (pixels over 12 steps) read the largest channel
    /// difference of the first frame after the reveal from the from-scratch wall, and <see cref="RedMean"/> and
    /// <see cref="RedWorst"/> its red excess over that wall. <see cref="OccluderMean"/> is the last covered frame's
    /// largest channel difference from the same wall. <see cref="ConvergedMean"/> and <see cref="ConvergedRedWorst"/>
    /// compare the first frame after the reveal with a converged bare wall instead, and <see cref="FreshMean"/> the
    /// from-scratch wall with the converged one: how far a single frame lies from an accumulated one.
    /// </summary>
    internal sealed record RevealRun(string Name, PixelRect Region, double OccluderMean, double Mean, int Worst,
        double RedMean, int RedWorst, int Over, double ConvergedMean, int ConvergedRedWorst, double FreshMean,
        double Seconds)
    {
        public override string ToString() =>
            $"{Name}, region {Region}: occluder {OccluderMean:0.0}. Against the wall from scratch: mean {Mean:0.000}, "
            + $"worst {Worst}, red mean {RedMean:0.000}, red worst {RedWorst}, {Over} over 12. Against a converged "
            + $"wall: mean {ConvergedMean:0.000}, red worst {ConvergedRedWorst}. Fresh against converged "
            + $"{FreshMean:0.000}";
    }

    /// <summary>
    /// The reveal runs the disocclusion acceptance reads, rendered on first use and kept for the whole test class, so
    /// the table test and each assertion read the same runs. HDR is off in every run and the sharpen stays at its
    /// default.
    /// </summary>
    public sealed class TemporalRevealRuns
    {
        public const int W = 320, H = 180;

        /// <summary>A pixel over this many 8-bit steps from the from-scratch wall counts in
        /// <see cref="RevealRun.Over"/>, 0.05 of full scale.</summary>
        public const int OverSteps = 12;

        readonly Dictionary<(bool Textured, TemporalUpscale Preset), RevealRun> _runs = new();

        /// <summary>Wall time spent rendering and measuring so far, in seconds.</summary>
        internal double Seconds { get; private set; }

        internal RevealRun Reveal(bool textured, TemporalUpscale preset)
        {
            if (_runs.TryGetValue((textured, preset), out RevealRun? cached)) return cached;
            long started = Stopwatch.GetTimestamp();
            var scene = new RevealScene(W, H, textured);
            Action<Scene3D> setup = s =>
            {
                scene.Stage.Setup(s, AntiAliasing.Temporal, preset);
                s.Post.Hdr.Enabled = false;
            };
            const int reveal = RevealScene.RevealFrame;
            byte[] covered, frame, fresh, converged;
            using (var fx = new TemporalFixture(W, H, setup))
            {
                fx.Frames(reveal - 1, scene.Draw);
                covered = fx.Frame(scene.Draw);
                frame = fx.Frame(scene.Draw);
            }
            using (var fx = new TemporalFixture(W, H, setup))
            {
                fx.SkipFrames(reveal);
                fresh = fx.Frame(scene.Background);
            }
            using (var fx = new TemporalFixture(W, H, setup))
            {
                fx.Frames(reveal, scene.Background);
                converged = fx.Frame(scene.Background);
            }

            PixelRect region = scene.Revealed;
            double occluder = 0, sum = 0, redSum = 0, convergedSum = 0, freshSum = 0;
            int worst = 0, redWorst = 0, over = 0, convergedRedWorst = 0;
            for (int y = region.Y0; y < region.Y1; y++)
                for (int x = region.X0; x < region.X1; x++)
                {
                    int d = Steps(frame, fresh, x, y), red = Math.Max(0, frame[Red(x, y)] - fresh[Red(x, y)]);
                    occluder += Steps(covered, fresh, x, y);
                    sum += d;
                    redSum += red;
                    worst = Math.Max(worst, d);
                    redWorst = Math.Max(redWorst, red);
                    if (d > OverSteps) over++;
                    convergedSum += Steps(frame, converged, x, y);
                    convergedRedWorst = Math.Max(convergedRedWorst, frame[Red(x, y)] - converged[Red(x, y)]);
                    freshSum += Steps(fresh, converged, x, y);
                }
            double area = region.Area;
            string name = $"{(textured ? "textured" : "flat")} wall, {preset}";
            double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            var run = new RevealRun(name, region, occluder / area, sum / area, worst, redSum / area, redWorst, over,
                convergedSum / area, convergedRedWorst, freshSum / area, seconds);
            Seconds += run.Seconds;
            return _runs[(textured, preset)] = run;
        }

        static int Red(int x, int y) => (y * W + x) * 4;

        // The largest channel difference of one pixel, in 8-bit steps.
        static int Steps(byte[] a, byte[] b, int x, int y) =>
            (int)MathF.Round(255f * TemporalAcceptance.Difference(a, b, W, x, y, PixelDifference.MaxChannel));
    }
}
