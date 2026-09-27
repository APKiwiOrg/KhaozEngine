using System;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23 under a follow camera: a keyed box with ridged pixels walks
    /// across the textured wall while the camera keeps it still on screen, as a third-person camera follows a walking
    /// avatar (<see cref="TemporalFollowCameraRuns"/>). The wall pans under the box, so each wall pixel at its trailing
    /// edge was under the box last frame and reprojects by its own motion onto the box's stored depths and state. Each
    /// trail is read as <see cref="TemporalNarrowCrossingGpuTests"/> reads a crossing, by the excess over a floor that
    /// restarts the bare wall on the frame the box uncovered the pixel. HDR is off, the sharpen is at its default, and
    /// the measured values in the comments are Metal on Apple silicon.
    /// </summary>
    public sealed class TemporalFollowCameraGpuTests(TemporalFollowCameraRuns runs, ITestOutputHelper output)
        : IClassFixture<TemporalFollowCameraRuns>
    {
        /// <summary>The walk in display pixels a frame. It lands the previous position of a trailing pixel on the
        /// boundary between two stored texels, one the box's and one the wall's.</summary>
        const float WalkPixels = 2.5f;

        // A trail region smaller than this measured nothing.
        const int MinTrailPixels = 100;

        // Acceptance 3's share of a trail region that may exceed its floor, never fewer than one pixel.
        const int AllowedOverOneIn = 200;

        static readonly TemporalUpscale[] Presets =
        {
            TemporalUpscale.Native, TemporalUpscale.Quality, TemporalUpscale.Performance,
            TemporalUpscale.UltraPerformance,
        };

        static int Allowed(int count) => Math.Max(1, count / AllowedOverOneIn);

        static string Describe(CrossingTrail t) =>
            $"{t.Name}: trail {t.Total}, acceptance 3 allows {Allowed(t.Total.Checked)}, reach {t.Reach} px, oldest "
            + $"age {t.OldestAge}. Two frames after: {t.Age(TemporalGhostingRuns.FirstAge)}";

        /// <summary>Every preset's trail, and by age at Native and Quality.</summary>
        [GpuFact]
        public void The_follow_camera_table_prints_every_preset()
        {
            output.WriteLine("| Preset | Checked | Excess | Acceptance 3 allows | Worst excess | Reach | Oldest "
                + "| Age 2 excess |");
            output.WriteLine("|" + string.Concat(Enumerable.Repeat(" --- |", 8)));
            foreach (TemporalUpscale preset in Presets)
            {
                CrossingTrail c = runs.Run(preset, WalkPixels);
                TrailTally t = c.Total, two = c.Age(TemporalGhostingRuns.FirstAge);
                output.WriteLine($"| {preset} | {t.Checked} | {t.Excess} | {Allowed(t.Checked)} "
                    + $"| {t.WorstExcess:0.000} | {c.Reach} | {c.OldestAge} | {two.Excess} of {two.Checked} |");
                Assert.True(t.Checked > MinTrailPixels, $"{c.Name}: the trail region measured nothing");
            }
            foreach (TemporalUpscale preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
            {
                CrossingTrail c = runs.Run(preset, WalkPixels);
                output.WriteLine(Describe(c));
                for (int age = TemporalGhostingRuns.FirstAge; age < TemporalGhostingRuns.TrailFrames; age++)
                    output.WriteLine($"{c.Name}, age {age}: {c.Age(age)}");
            }
            output.WriteLine($"every run: {runs.Seconds:0.0} s");
        }
    }
}
