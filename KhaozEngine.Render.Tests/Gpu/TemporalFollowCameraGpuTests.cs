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

        // The walks the table prints, in display pixels a frame.
        static readonly float[] Speeds = { 0.5f, 1f, 1.5f, 2f, 2.5f, 3f };

        static readonly TemporalUpscale[] Presets =
        {
            TemporalUpscale.Native, TemporalUpscale.Quality, TemporalUpscale.Performance,
            TemporalUpscale.UltraPerformance,
        };

        static int Allowed(int count) => Math.Max(1, count / AllowedOverOneIn);

        static string Describe(CrossingTrail t) =>
            $"{t.Name}: trail {t.Total}, acceptance 3 allows {Allowed(t.Total.Checked)}, reach {t.Reach} px, oldest "
            + $"age {t.OldestAge}. Two frames after: {t.Age(TemporalGhostingRuns.FirstAge)}";

        /// <summary>
        /// The wall the box uncovers keeps none of its colour at Native and Quality, where the walk is 2.5 and 1.67
        /// internal pixels a frame. The box's pixels store the state of a surface that travelled past the dilation's
        /// reach, so a footprint it covered keeps its history only where no part of it was the box. Measured 0 of 510
        /// at both against the 2 acceptance 3 allows. Before, the lock exception kept the box's history where its
        /// ridged pixels held locks near whole, and a footprint half on the box kept a history half its colour: 38 and
        /// 22.
        /// </summary>
        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        public void A_followed_keyed_box_leaves_no_trail_on_the_wall_it_uncovers(TemporalUpscale preset)
        {
            CrossingTrail t = runs.Run(preset, WalkPixels);
            string ctx = Describe(t);
            output.WriteLine(ctx);
            Assert.True(t.Total.Checked > MinTrailPixels,
                $"the trail region holds {t.Total.Checked} pixels, so nothing was measured. {ctx}");
            Assert.True(t.Total.Excess <= Allowed(t.Total.Checked), $"the box's colour stays where it left. {ctx}");
        }

        /// <summary>Every preset's trail at every walk speed, with the walk in internal pixels a frame, and by age at
        /// Native and Quality at <see cref="WalkPixels"/>. Under DilationReachInternalPixels (1.25 internal pixels a
        /// frame) the pixels beside the trailing edge take the box's motion by dilation and read its own history, and
        /// the wall's pan carries it into a band behind the box.</summary>
        [GpuFact]
        public void The_follow_camera_table_prints_every_preset()
        {
            output.WriteLine("| Speed | Preset | Internal | Checked | Excess | Acceptance 3 allows | Worst excess "
                + "| Reach | Oldest | Age 2 excess |");
            output.WriteLine("|" + string.Concat(Enumerable.Repeat(" --- |", 10)));
            foreach (float speed in Speeds)
                foreach (TemporalUpscale preset in Presets)
                {
                    CrossingTrail c = runs.Run(preset, speed);
                    TrailTally t = c.Total, two = c.Age(TemporalGhostingRuns.FirstAge);
                    float internalPixels = speed / TemporalSettings.DisplayOverInternal(preset);
                    output.WriteLine($"| {speed} | {preset} | {internalPixels:0.00} | {t.Checked} | {t.Excess} "
                        + $"| {Allowed(t.Checked)} | {t.WorstExcess:0.000} | {c.Reach} | {c.OldestAge} "
                        + $"| {two.Excess} of {two.Checked} |");
                    if (speed == WalkPixels)
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
