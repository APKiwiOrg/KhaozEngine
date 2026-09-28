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
    /// avatar (<see cref="TemporalFollowCameraRuns"/>). The wall pans under the box. Past the dilation's reach each
    /// wall pixel at its trailing edge reprojects by its own motion onto the box's stored depths and state. Under it
    /// the pixels beside the box's edge take the box's motion by dilation, and the wall pixel clear of the edge drops
    /// the history they left (TemporalResolveTuning.WorldMotionInternalPixels). Each trail is read as
    /// <see cref="TemporalNarrowCrossingGpuTests"/> reads a crossing, by the excess over a floor that restarts the bare
    /// wall on the frame the box uncovered the pixel. HDR is off, the sharpen is at its default, and the measured
    /// values in the comments are Metal on Apple silicon.
    /// </summary>
    public sealed class TemporalFollowCameraGpuTests(TemporalFollowCameraRuns runs, ITestOutputHelper output)
        : IClassFixture<TemporalFollowCameraRuns>
    {
        /// <summary>The walk the table prints by age, in display pixels a frame. It lands the previous position of a
        /// trailing pixel on the boundary between two stored texels, one the box's and one the wall's.</summary>
        const float WalkPixels = 2.5f;

        // A trail region narrower than three columns of the box measured nothing. At half a display pixel a frame the
        // region is 3 pixels wide: the eighth frame back reaches 4 pixels past the box, and the measure skips the one
        // beside it.
        const int MinTrailPixels = 3 * (int)TemporalFollowCameraRuns.SizePixels;

        // Acceptance 3's share of a trail region that may exceed its floor, never fewer than one pixel.
        const int AllowedOverOneIn = 200;

        // Stands for acceptance 3 in Walks.
        const int Acceptance3 = -1;

        // The walks the table prints, in display pixels a frame.
        static readonly float[] Speeds = { 0.5f, 1f, 1.5f, 2f, 2.5f, 3f };

        static readonly TemporalUpscale[] Presets =
        {
            TemporalUpscale.Native, TemporalUpscale.Quality, TemporalUpscale.Performance,
            TemporalUpscale.UltraPerformance,
        };

        /// <summary>Every walk the trail fact holds, and its bound: acceptance 3 at Native and Quality at every walk,
        /// and at Performance from 1.5 display pixels a frame. Elsewhere the measured excess and about a quarter more,
        /// at least 2 (amendment 23), the measured value in the comment.</summary>
        public static TheoryData<TemporalUpscale, float, int> Walks
        {
            get
            {
                var walks = new TheoryData<TemporalUpscale, float, int>();
                foreach (TemporalUpscale preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
                    foreach (float speed in Speeds)
                        walks.Add(preset, speed, Acceptance3);
                walks.Add(TemporalUpscale.Performance, 0.5f, 5);            // measured 3 of 90
                walks.Add(TemporalUpscale.Performance, 1f, 4);              // measured 2 of 210
                foreach (float speed in new[] { 1.5f, 2f, 2.5f, 3f })
                    walks.Add(TemporalUpscale.Performance, speed, Acceptance3);
                walks.Add(TemporalUpscale.UltraPerformance, 0.5f, 45);      // measured 36 of 90
                walks.Add(TemporalUpscale.UltraPerformance, 1f, 44);        // measured 35 of 210
                walks.Add(TemporalUpscale.UltraPerformance, 1.5f, 19);      // measured 15 of 300
                walks.Add(TemporalUpscale.UltraPerformance, 2f, 44);        // measured 35 of 420
                walks.Add(TemporalUpscale.UltraPerformance, 2.5f, 29);      // measured 23 of 510
                walks.Add(TemporalUpscale.UltraPerformance, 3f, 29);        // measured 23 of 630
                return walks;
            }
        }

        static int Allowed(int count) => Math.Max(1, count / AllowedOverOneIn);

        static string Describe(CrossingTrail t) =>
            $"{t.Name}: trail {t.Total}, acceptance 3 allows {Allowed(t.Total.Checked)}, reach {t.Reach} px, oldest "
            + $"age {t.OldestAge}. Two frames after: {t.Age(TemporalGhostingRuns.FirstAge)}";

        /// <summary>
        /// The wall the box uncovers keeps none of its colour at Native and Quality at any walk from half a display
        /// pixel a frame to three: 0 at every walk but 2 display pixels on Quality, 2 of 420 against the 2 acceptance 3
        /// allows. Before, under the reach, the band's history rode out with the wall: 20 and 32 of 90 at half a pixel,
        /// 45 and 87 of 210 at 1, and 101 of 300 at 1.5 on Quality, and past it the lock exception kept the box's
        /// history where its ridged pixels held locks near whole: 38 and 22 of 510 at 2.5. Performance meets
        /// acceptance 3 from 1.5 display pixels a frame, 0 against 168 to 213. UltraPerformance and Performance's
        /// slower walks hold their measured values (<see cref="Walks"/>).
        /// </summary>
        [GpuTheory]
        [MemberData(nameof(Walks))]
        public void A_followed_keyed_box_leaves_no_trail_on_the_wall_it_uncovers(TemporalUpscale preset, float speed,
            int maxExcess)
        {
            CrossingTrail t = runs.Run(preset, speed);
            int bound = maxExcess == Acceptance3 ? Allowed(t.Total.Checked) : maxExcess;
            string ctx = $"{Describe(t)}, bound {bound}";
            output.WriteLine(ctx);
            Assert.True(t.Total.Checked >= MinTrailPixels,
                $"the trail region holds {t.Total.Checked} pixels, so nothing was measured. {ctx}");
            Assert.True(t.Total.Excess <= bound, $"the box's colour stays where it left. {ctx}");
        }

        /// <summary>Every preset's trail at every walk speed, with the walk in internal pixels a frame, and by age at
        /// Native and Quality at <see cref="WalkPixels"/>. Under DilationReachInternalPixels (1.25 internal pixels a
        /// frame) the pixels beside the trailing edge take the box's motion by dilation, and the wall pixel clear of
        /// the edge restarts. At UltraPerformance the reconstruction spreads the box's texel over 6 display pixels of
        /// wall, which the bare-wall floor never shows.</summary>
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
                    Assert.True(t.Checked >= MinTrailPixels, $"{c.Name}: the trail region measured nothing");
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
