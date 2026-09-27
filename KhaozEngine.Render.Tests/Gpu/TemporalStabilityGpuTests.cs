using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN section 7 item 2 as amendment 22 measures it, the owner's complaint as a
    /// test: a slow camera pan over thin geometry flickers less under temporal anti-aliasing than under MSAA 4x.
    /// Beside it, the engine's default isometric camera zooming does not swim, and a prop mid-fade converges to the
    /// dissolve's true partial coverage, holds it steady and pans calmer than without anti-aliasing.
    /// <para>
    /// Flicker is the added change against a supersampled reference sequence of the same path
    /// (<see cref="FlickerStats.AddedChange"/>): frame-to-frame change the output makes and the reference does not.
    /// Aliasing shimmer adds it, so it ranks MSAA 4x behind temporal anti-aliasing. Raw flips mostly count thin
    /// features legitimately crossing pixels, where no anti-aliasing, MSAA 4x and an ideal box filter score alike and
    /// blur scores lower, so they are printed and not asserted. Blur removes change instead of adding it, so sharpness
    /// floors guard it separately, as regression guards at about 70 percent of the measured value rather than quality
    /// claims. The temporal error against the reference is a shimmer guard only: at the slow pan the resolve sits at
    /// about a frozen image's error by design, the cost of resampling its history at a fractional offset.
    /// </para>
    /// <para>
    /// The zoom is not compared with MSAA 4x at full resolution, where the resolve loses on error and added change by
    /// design (resampling under a flow of several pixels a frame, and the luma weighting's bias moving with the
    /// content). Swimming is low-frequency, so it is measured after a 5 by 5 box low-pass against the one-frame-lag
    /// control. Inside the prop MSAA 4x smooths nothing, because the dissolve's discard keeps or drops all of a
    /// pixel's samples together, so there it equals no anti-aliasing and the prop paths compare temporal
    /// anti-aliasing with no anti-aliasing. A held dissolve's coverage is checked against the supersampled
    /// reference's coverage of the same scene, not against a fixed range, because a dissolve threshold of one half
    /// does not leave half the prop visible.
    /// </para>
    /// <para>
    /// HDR is off and the sharpen is at its default (<see cref="TemporalStabilityRuns"/>). Every bound is relative to
    /// runs of the same session, no image is stored, and every measured value is printed.
    /// </para>
    /// </summary>
    public sealed class TemporalStabilityGpuTests(TemporalStabilityRuns runs, ITestOutputHelper output)
        : IClassFixture<TemporalStabilityRuns>
    {
        // Every bound below is relative to runs of the same session. The measured values in the comments are Metal on
        // Apple silicon.

        // Flicker: temporal added change under half of MSAA 4x's at the slow pan. Measured 0.17 at Native and 0.08 at
        // Quality over the background, 0.32 and 0.19 over the wall.
        const double MaxAddedShareOfMsaa = 0.5;

        // MSAA 4x has to shimmer on the path, or the comparison measures nothing. Measured 0.72 of frozen.
        const double MinMsaaAddedShareOfFrozen = 0.25;

        // Shimmer guard: temporal error at most this share of a frozen image's. Measured 1.02 to 1.03 at every preset.
        // Blur and lag alone stay under twice frozen, and shimmer has no bound.
        const double MaxErrorShareOfFrozen = 1.15;

        // The zoom does not swim: the 5 by 5 low-passed error at most this share of the one-frame-lag control's.
        // Measured 0.25 at Native and 0.35 at Quality.
        const double MaxLowPassedShareOfLag = 0.5;

        // Mid-fade props: temporal added change against no anti-aliasing inside the prop at the slow pan.
        const double MaxPropAddedShareOfOff = 0.5;

        // No anti-aliasing has to add change inside the prop, or the comparison measures nothing.
        const double MinOffAddedShareOfFrozen = 0.25;

        // A held half dissolve converges to the dissolve's true coverage: within this distance of the supersampled
        // reference's coverage, measured 0.026 (0.673 against 0.699). A fixed range around one half does not fit,
        // because this scene's half dissolve leaves about 70 percent of the prop visible, not 50.
        const double MaxHeldCoverageError = 0.08;

        // The most a held dissolve's pixels may flip, per pixel per frame. Measured 0.
        const double MaxHeldFlipRate = 0.01;

        static string Name(TemporalUpscale preset) => $"TAA {preset}";

        // Temporal added change against MSAA 4x's on one path.
        void AssertAddsLessChangeThanMsaa(StabilityPath path, TemporalUpscale preset)
        {
            FlickerStats taa = path.Temporal[preset], msaa = path.Msaa;
            double share = taa.AddedChange / msaa.AddedChange;
            string ctx = $"{path.Name}, {Name(preset)}: added {taa.AddedChange:0.00000} against MSAA 4x "
                + $"{msaa.AddedChange:0.00000} ({share:0.000} of it, bound {MaxAddedShareOfMsaa}), frozen "
                + $"{path.Frozen:0.00000}. TAA {taa}. MSAA 4x {msaa}";
            output.WriteLine(ctx);
            Assert.True(msaa.AddedChange >= MinMsaaAddedShareOfFrozen * path.Frozen,
                $"MSAA 4x must shimmer on this path or the comparison measures nothing. {ctx}");
            Assert.True(share < MaxAddedShareOfMsaa,
                $"temporal anti-aliasing must add under half the change MSAA 4x adds. {ctx}");
        }

        [GpuFact(RequiresFourSampleMsaa = true)]
        public void ASlowPanOverThinGeometryFlickersLessThanMsaa4xAtNative() =>
            AssertAddsLessChangeThanMsaa(runs.FencePan(TemporalStabilityRuns.SlowPan), TemporalUpscale.Native);

        [GpuFact(RequiresFourSampleMsaa = true)]
        public void ASlowPanOverThinGeometryFlickersLessThanMsaa4xAtQuality() =>
            AssertAddsLessChangeThanMsaa(runs.FencePan(TemporalStabilityRuns.SlowPan), TemporalUpscale.Quality);

        [GpuFact(RequiresFourSampleMsaa = true)]
        public void ASlowPanOverThinGeometryOnAWallFlickersLessThanMsaa4xAtNative() =>
            AssertAddsLessChangeThanMsaa(runs.WallPan(TemporalStabilityRuns.SlowPan), TemporalUpscale.Native);

        [GpuFact(RequiresFourSampleMsaa = true)]
        public void ASlowPanOverThinGeometryOnAWallFlickersLessThanMsaa4xAtQuality() =>
            AssertAddsLessChangeThanMsaa(runs.WallPan(TemporalStabilityRuns.SlowPan), TemporalUpscale.Quality);

        [GpuTheory]
        [InlineData(TemporalUpscale.Native, false, 0.40)]   // measured 0.583
        [InlineData(TemporalUpscale.Quality, false, 0.22)]  // measured 0.322
        [InlineData(TemporalUpscale.Native, true, 0.15)]    // measured 0.208
        [InlineData(TemporalUpscale.Quality, true, 0.08)]   // measured 0.114
        public void ThinGeometryKeepsItsSharpness(TemporalUpscale preset, bool panning, double floor)
        {
            FlickerStats f = panning
                ? runs.FencePan(TemporalStabilityRuns.SlowPan).Temporal[preset]
                : runs.Still[preset];
            string ctx = $"fence {(panning ? $"pan {TemporalStabilityRuns.SlowPan}" : "still")}, {Name(preset)}: "
                + $"sharpness {f.Sharpness:0.000} (floor {floor}), energy {f.Energy:0.000}";
            output.WriteLine(ctx);
            Assert.True(f.Sharpness >= floor, $"the resolve lost sharpness it kept before. {ctx}");
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        [InlineData(TemporalUpscale.Balanced)]
        [InlineData(TemporalUpscale.Performance)]
        [InlineData(TemporalUpscale.UltraPerformance)]
        public void ASlowPanOverThinGeometryDoesNotShimmer(TemporalUpscale preset)
        {
            StabilityPath path = runs.FencePan(TemporalStabilityRuns.SlowPan);
            FlickerStats f = path.Temporal[preset];
            double share = f.TemporalError / path.Frozen;
            string ctx = $"{path.Name}, {Name(preset)}: error {f.TemporalError:0.00000}, frozen {path.Frozen:0.00000} "
                + $"({share:0.000} of it, bound {MaxErrorShareOfFrozen})";
            output.WriteLine(ctx);
            Assert.True(share <= MaxErrorShareOfFrozen, $"the resolve shimmers. {ctx}");
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        public void TheDefaultIsometricCameraZoomingDoesNotSwim(TemporalUpscale preset)
        {
            StabilityPath zoom = runs.Zoom;
            FlickerStats taa = zoom.Temporal[preset], msaa = zoom.Msaa, lag = zoom.Lagged;
            double share = taa.LowPassedError / lag.LowPassedError;
            string ctx = $"{zoom.Name}, {Name(preset)}: 5x5 error {taa.LowPassedError:0.00000} against the one-frame "
                + $"lag's {lag.LowPassedError:0.00000} ({share:0.000} of it, bound {MaxLowPassedShareOfLag}). "
                + $"Reported: MSAA 4x 5x5 error {msaa.LowPassedError:0.00000} "
                + $"({taa.LowPassedError / msaa.LowPassedError:0.000} times it), full-resolution error "
                + $"{taa.TemporalError:0.00000} against MSAA 4x {msaa.TemporalError:0.00000}, added "
                + $"{taa.AddedChange:0.00000} against {msaa.AddedChange:0.00000}, sharpness {taa.Sharpness:0.000} "
                + $"against {msaa.Sharpness:0.000}";
            output.WriteLine(ctx);
            Assert.True(share <= MaxLowPassedShareOfLag, $"the zoom swims. {ctx}");
        }

        // Temporal added change against no anti-aliasing's inside the prop.
        void AssertPropPansCalmerThanOff(bool crossfade, TemporalUpscale preset)
        {
            StabilityPath path = runs.PropPan(crossfade);
            FlickerStats taa = path.Temporal[preset], off = path.Off;
            double share = taa.AddedChange / off.AddedChange;
            string ctx = $"{path.Name} pan, {Name(preset)}, region {runs.PropRegion}: added {taa.AddedChange:0.00000} "
                + $"against no AA {off.AddedChange:0.00000} ({share:0.000} of it, bound {MaxPropAddedShareOfOff}), "
                + $"frozen {path.Frozen:0.00000}, MSAA 4x added {path.Msaa.AddedChange:0.00000}. TAA "
                + $"{Describe(path, taa)}. No AA {Describe(path, off)}";
            output.WriteLine(ctx);
            Assert.True(off.AddedChange >= MinOffAddedShareOfFrozen * path.Frozen,
                $"the prop must shimmer without anti-aliasing or the comparison measures nothing. {ctx}");
            Assert.True(share < MaxPropAddedShareOfOff,
                $"temporal anti-aliasing must add under half the change no anti-aliasing adds in the prop. {ctx}");
        }

        [GpuFact]
        public void APropMidDissolvePansCalmerThanWithoutAntiAliasing() =>
            AssertPropPansCalmerThanOff(crossfade: false, TemporalUpscale.Native);

        [GpuFact]
        public void ALodCrossfadePansCalmerThanWithoutAntiAliasing() =>
            AssertPropPansCalmerThanOff(crossfade: true, TemporalUpscale.Native);

        [GpuFact]
        public void APropHeldAtHalfDissolveConvergesToASteadyPartialCoverage()
        {
            HeldDissolve held = runs.Held;
            double error = Math.Abs(held.Coverage - held.ReferenceCoverage);
            string ctx = $"half dissolve, still, TAA Native, region {runs.PropRegion}: coverage {held.Coverage:0.000} "
                + $"against the reference's {held.ReferenceCoverage:0.000} (off by {error:0.000}, bound "
                + $"{MaxHeldCoverageError}), no AA {held.OffCoverage:0.000}, flips {held.Flips:0.00000} per pixel per "
                + $"frame (bound {MaxHeldFlipRate}), own change {held.OwnChange:0.00000}";
            output.WriteLine(ctx);
            Assert.True(error <= MaxHeldCoverageError,
                $"a held dissolve must converge to the dissolve's true coverage. {ctx}");
            Assert.True(held.Flips <= MaxHeldFlipRate, $"a held dissolve must not crawl. {ctx}");
        }

        [GpuFact(RequiresFourSampleMsaa = true)]
        public void TheMotionClarityTableCoversEveryPresetSpeedAndMode()
        {
            var paths = new List<StabilityPath>();
            foreach (float speed in TemporalStabilityRuns.Speeds) paths.Add(runs.FencePan(speed));
            foreach (float speed in TemporalStabilityRuns.Speeds) paths.Add(runs.WallPan(speed));
            paths.Add(runs.Zoom);
            paths.Add(runs.PropPan(crossfade: false));
            paths.Add(runs.PropPan(crossfade: true));

            output.WriteLine("| Path | Mode | Error | Frozen | Own | Added | Removed | Sharp | Energy | Added / MSAA "
                + "| Error / frozen | 5x5 error | Flips | Fast flips |");
            output.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (StabilityPath path in paths)
            {
                var rows = path.Temporal.Select(t => (Name(t.Key), t.Value))
                    .Append(("MSAA 4x", path.Msaa)).Append(("no AA", path.Off)).Append(("lag control", path.Lagged));
                foreach (var (mode, f) in rows) output.WriteLine(Row(path, mode, f));
            }
            foreach (TemporalUpscale preset in TemporalStabilityRuns.Presets)
                output.WriteLine($"fence still, {Name(preset)}: {runs.Still[preset]}");
            HeldDissolve held = runs.Held;
            output.WriteLine($"half dissolve held, TAA Native: coverage {held.Coverage:0.000}, reference "
                + $"{held.ReferenceCoverage:0.000}, no AA {held.OffCoverage:0.000}, flips {held.Flips:0.00000}, own "
                + $"change {held.OwnChange:0.00000}");
            foreach (StabilityPath path in paths) output.WriteLine($"{path.Name}: {path.Seconds:0.0} s");
            output.WriteLine($"every run: {runs.Seconds:0.0} s");

            foreach (StabilityPath path in paths)
            {
                Assert.True(path.Frozen > 0, $"{path.Name}: the reference must move");
                foreach (FlickerStats f in path.Temporal.Values.Append(path.Msaa).Append(path.Off))
                    Assert.True(double.IsFinite(f.TemporalError) && double.IsFinite(f.Sharpness)
                        && (!path.FlatBackground || double.IsFinite(f.Energy)),
                        $"{path.Name}: every measure must be finite. {Describe(path, f)}");
            }
        }

        // Energy reads pixel (2, 2) as the background, so it is shown only where that pixel is flat background.
        static string Energy(StabilityPath path, FlickerStats f) => path.FlatBackground ? $"{f.Energy:0.000}" : "n/a";

        static string Describe(StabilityPath path, FlickerStats f) =>
            $"error {f.TemporalError:0.00000} (frozen {f.ReferenceChange:0.00000}), own {f.OwnChange:0.00000}, added "
            + $"{f.AddedChange:0.00000}, removed {f.RemovedChange:0.00000}, 5x5 error {f.LowPassedError:0.00000}, "
            + $"flips {f.Flips:0.00000} (fast {f.FastFlips:0.00000}), reference floor {f.ReferenceFlips:0.00000} "
            + $"(fast {f.ReferenceFastFlips:0.00000}), sharpness {f.Sharpness:0.000}, energy {Energy(path, f)}";

        static string Row(StabilityPath path, string mode, FlickerStats f) =>
            $"| {path.Name} | {mode} | {f.TemporalError:0.00000} | {f.ReferenceChange:0.00000} | {f.OwnChange:0.00000} "
            + $"| {f.AddedChange:0.00000} | {f.RemovedChange:0.00000} | {f.Sharpness:0.000} | {Energy(path, f)} "
            + $"| {f.AddedChange / path.Msaa.AddedChange:0.00} | {f.TemporalError / path.Frozen:0.000} "
            + $"| {f.LowPassedError:0.00000} | {f.Flips:0.00000} | {f.FastFlips:0.00000} |";
    }
}
