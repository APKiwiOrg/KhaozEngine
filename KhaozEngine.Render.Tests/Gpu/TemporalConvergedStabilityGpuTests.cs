using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DISPLAY-SIZED RECONSTRUCTION AT ULTRAPERFORMANCE ONCE IT RUNS (TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment
    /// 26). A converged pixel takes it from a carried confidence of <see cref="TemporalResolveTuning"/>'s
    /// DisplayKernelConfidenceStart, which UltraPerformance reaches last, where its kernels see the fewest samples.
    /// The stability facts end at frame 79, so these hold the fence's slow pans and the still fence from frame
    /// <see cref="TemporalStabilityRuns.ConvergedWarm"/> (<see cref="TemporalStabilityRuns"/>'s converged partial) to
    /// the bounds the stability facts use at the other presets.
    /// <para>Measured on Metal with the share forced to none on the same windows, against the rule: the pan's error
    /// 1.005 and 1.003 of a frozen image's over the dark background and 1.012 and 1.014 over the wall, its added change
    /// 0.016 and 0.014 of MSAA 4x's and 0.037 and 0.041, the still fence's sharpness 0.045 and 0.047. In the window
    /// from frame 16 the pans measured 1.025 and 1.055 of frozen and the still fence 0.077. The fence is a third of a
    /// display pixel wide, a ninth of an internal texel, and its sharpness falls with the accumulation, with the rule
    /// or without it.</para>
    /// </summary>
    public sealed class TemporalConvergedStabilityGpuTests(TemporalStabilityRuns runs, ITestOutputHelper output)
        : IClassFixture<TemporalStabilityRuns>
    {
        // About 70 percent of the measured 0.047, as the stability class's sharpness floors are.
        const double MinStillSharpness = 0.033;

        [GpuFact]
        public void The_window_opens_once_the_fence_carries_the_confidence_the_rule_starts_from()
        {
            float median = runs.ConvergedMedianConfidence;
            output.WriteLine($"fence under the slow pan, UltraPerformance, frame "
                + $"{TemporalStabilityRuns.ConvergedWarm}: median stored confidence {median:0.000}, start "
                + $"{TemporalResolveTuning.DisplayKernelConfidenceStart}");
            Assert.True(median > TemporalResolveTuning.DisplayKernelConfidenceStart,
                $"the window opens before the display-sized reconstruction runs: median confidence {median:0.000}");
        }

        [GpuTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_slow_pan_over_thin_geometry_does_not_shimmer_once_UltraPerformance_converges(bool overWall)
        {
            StabilityPath path = overWall ? runs.ConvergedWallPan : runs.ConvergedFencePan;
            FlickerStats f = path.Temporal[TemporalUpscale.UltraPerformance];
            double share = f.TemporalError / path.Frozen;
            string ctx = $"{path.Name}, TAA UltraPerformance: error {f.TemporalError:0.00000}, frozen "
                + $"{path.Frozen:0.00000} ({share:0.000} of it, bound "
                + $"{TemporalStabilityGpuTests.MaxErrorShareOfFrozen}). TAA {f}";
            output.WriteLine(ctx);
            Assert.True(share <= TemporalStabilityGpuTests.MaxErrorShareOfFrozen, $"the resolve shimmers. {ctx}");
        }

        [GpuFact(RequiresFourSampleMsaa = true)]
        public void A_slow_pan_over_thin_geometry_flickers_less_than_Msaa4x_once_UltraPerformance_converges() =>
            AssertAddsLessChangeThanMsaa(runs.ConvergedFencePan);

        [GpuFact(RequiresFourSampleMsaa = true)]
        public void A_slow_pan_over_thin_geometry_on_a_wall_flickers_less_than_Msaa4x_once_UltraPerformance_converges()
            => AssertAddsLessChangeThanMsaa(runs.ConvergedWallPan);

        [GpuFact]
        public void Still_thin_geometry_keeps_its_sharpness_once_UltraPerformance_converges()
        {
            FlickerStats f = runs.ConvergedStill;
            string ctx = $"fence still from frame {TemporalStabilityRuns.ConvergedWarm}, TAA UltraPerformance: "
                + $"sharpness {f.Sharpness:0.000} (floor {MinStillSharpness}), energy {f.Energy:0.000}, flips "
                + $"{f.Flips:0.00000}";
            output.WriteLine(ctx);
            Assert.True(f.Sharpness >= MinStillSharpness, $"the resolve lost sharpness it kept before. {ctx}");
        }

        void AssertAddsLessChangeThanMsaa(StabilityPath path)
        {
            FlickerStats taa = path.Temporal[TemporalUpscale.UltraPerformance], msaa = path.Msaa;
            double share = taa.AddedChange / msaa.AddedChange;
            string ctx = $"{path.Name}, TAA UltraPerformance: added {taa.AddedChange:0.00000} against MSAA 4x "
                + $"{msaa.AddedChange:0.00000} ({share:0.000} of it, bound "
                + $"{TemporalStabilityGpuTests.MaxAddedShareOfMsaa}), frozen {path.Frozen:0.00000}";
            output.WriteLine(ctx);
            Assert.True(msaa.AddedChange >= TemporalStabilityGpuTests.MinMsaaAddedShareOfFrozen * path.Frozen,
                $"MSAA 4x must shimmer on this path or the comparison measures nothing. {ctx}");
            Assert.True(share < TemporalStabilityGpuTests.MaxAddedShareOfMsaa,
                $"temporal anti-aliasing must add under half the change MSAA 4x adds. {ctx}");
        }
    }
}
