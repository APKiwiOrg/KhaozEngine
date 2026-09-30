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
    /// the history they left (TemporalResolveTuning.WorldMotionMetres). Each trail is read as
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

        // Stands for acceptance 3 in Walks and SpillWalks.
        const int Acceptance3 = -1;

        // The walks the table prints, in display pixels a frame.
        static readonly float[] Speeds = { 0.5f, 1f, 1.5f, 2f, 2.5f, 3f };

        static readonly TemporalUpscale[] Presets =
        {
            TemporalUpscale.Native, TemporalUpscale.Quality, TemporalUpscale.Performance,
            TemporalUpscale.UltraPerformance,
        };

        const TemporalUpscale N = TemporalUpscale.Native, Q = TemporalUpscale.Quality,
            P = TemporalUpscale.Performance, U = TemporalUpscale.UltraPerformance;

        /// <summary>Every walk the trail fact holds, and its bound near the edge: a regression bound over the jitter's
        /// start phases (<see cref="TemporalFollowPhaseGpuTests"/>), not acceptance 3. Within the reconstruction's
        /// reach of a followed edge a pixel keeps one history, which either holds the edge's anti-aliasing in place or
        /// shows the wall passing under it, and the jitter decides which
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1207">#1207</see>). A history for the edge's
        /// coverage apart from the wall's is the fix
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1191">#1191</see>). Acceptance 3 where the
        /// worst over the phases meets it, else that worst and about a quarter more, at least 2, or the bound the
        /// walk held at its first phase where every phase already met it. The comment gives the worst, the region's
        /// pixels, the phases run and the value at the first phase. Below Native the region lies within the
        /// reconstruction's reach, so it also reads the current frame's spread of the box's texel, which no history
        /// removes, and <see cref="SpillWalks"/> holds the trail past it.</summary>
        public static TheoryData<TemporalUpscale, float, int> Walks => new()
        {
            { N, 0.5f, Acceptance3 },   // worst 0 of 90 over 8 phases, phase 0 0
            { N, 1f, Acceptance3 },     // worst 0 of 210 over 8 phases, phase 0 0
            { N, 1.5f, 4 },             // worst 2 of 300 over 8 phases, phase 0 0
            { N, 2f, 7 },               // worst 5 of 420 over 8 phases, phase 0 0
            { N, 2.5f, Acceptance3 },   // worst 1 of 510 over 8 phases, phase 0 0
            { N, 3f, Acceptance3 },     // worst 0 of 630 over 8 phases, phase 0 0
            { Q, 0.5f, 9 },             // worst 7 of 90 over 18 phases, phase 0 0
            { Q, 1f, 12 },              // worst 10 of 210 over 18 phases, phase 0 0
            { Q, 1.5f, 6 },             // worst 4 of 300 over 18 phases, phase 0 0
            { Q, 2f, 15 },              // worst 12 of 420 over 18 phases, phase 0 1
            { Q, 2.5f, Acceptance3 },   // worst 0 of 510 over 4 phases, phase 0 0
            { Q, 3f, Acceptance3 },     // worst 0 of 630 over 4 phases, phase 0 0
            { P, 0.5f, 22 },            // worst 18 of 90 over 32 phases, phase 0 3
            { P, 1f, 28 },              // worst 23 of 210 over 32 phases, phase 0 3
            { P, 1.5f, 22 },            // worst 18 of 300 over 32 phases, phase 0 0
            { P, 2f, 26 },              // worst 21 of 420 over 32 phases, phase 0 1
            { P, 2.5f, Acceptance3 },   // worst 0 of 510 over 4 phases, phase 0 0
            { P, 3f, Acceptance3 },     // worst 2 of 630 over 4 phases, phase 0 0
            { U, 0.5f, 45 },            // worst 37 of 90 over 4 phases, phase 0 35
            { U, 1f, 71 },              // worst 57 of 210 over 72 phases, phase 0 34
            { U, 1.5f, 19 },            // worst 16 of 300 over 4 phases, phase 0 15
            { U, 2f, 68 },              // worst 55 of 420 over 72 phases, phase 0 35
            { U, 2.5f, 38 },            // worst 31 of 510 over 4 phases, phase 0 23
            { U, 3f, 38 },              // worst 31 of 630 over 4 phases, phase 0 21
        };

        /// <summary>The walks with wall past the reconstruction's reach at Quality, Performance and UltraPerformance,
        /// and the bound on the trail there (<see cref="TemporalFollowCameraRuns.BeyondSpill"/>), set as
        /// <see cref="Walks"/> sets its bounds. At half a display pixel a frame no wall lies past the reach at Quality
        /// and Performance, and at UltraPerformance the whole trail lies within it, 4 display pixels against
        /// 6.</summary>
        public static TheoryData<TemporalUpscale, float, int> SpillWalks => new()
        {
            { Q, 1f, Acceptance3 },     // worst 0 of 120 over 18 phases, phase 0 0
            { Q, 1.5f, Acceptance3 },   // worst 0 of 240 over 18 phases, phase 0 0
            { Q, 2f, 4 },               // worst 2 of 360 over 18 phases, phase 0 0
            { Q, 2.5f, Acceptance3 },   // worst 0 of 480 over 4 phases, phase 0 0
            { Q, 3f, Acceptance3 },     // worst 0 of 600 over 4 phases, phase 0 0
            { P, 1f, Acceptance3 },     // worst 1 of 90 over 32 phases, phase 0 0
            { P, 1.5f, 4 },             // worst 2 of 210 over 32 phases, phase 0 0
            { P, 2f, Acceptance3 },     // worst 1 of 330 over 32 phases, phase 0 1
            { P, 2.5f, Acceptance3 },   // worst 0 of 450 over 4 phases, phase 0 0
            { P, 3f, Acceptance3 },     // worst 2 of 570 over 4 phases, phase 0 0
            { U, 1f, 11 },              // worst 9 of 30 over 72 phases, phase 0 0
            { U, 1.5f, 4 },             // worst 3 of 150 over 4 phases, phase 0 2
            { U, 2f, 32 },              // worst 26 of 270 over 72 phases, phase 0 16
            { U, 2.5f, 21 },            // worst 17 of 390 over 4 phases, phase 0 9
            { U, 3f, 30 },              // worst 24 of 510 over 4 phases, phase 0 11
        };

        internal static int Allowed(int count) => Math.Max(1, count / AllowedOverOneIn);

        /// <summary>The trail fact's bound on the walk at <paramref name="speed"/> display pixels a frame at
        /// <paramref name="preset"/> over a trail region of <paramref name="count"/> pixels (<see cref="Walks"/>), or
        /// null where the fact holds none.</summary>
        internal static int? Bound(TemporalUpscale preset, float speed, int count)
        {
            foreach (object[] walk in Walks)
                if ((TemporalUpscale)walk[0] == preset && (float)walk[1] == speed)
                    return (int)walk[2] == Acceptance3 ? Allowed(count) : (int)walk[2];
            return null;
        }

        /// <summary>The past-the-reach fact's bound on the walk at <paramref name="speed"/> display pixels a frame at
        /// <paramref name="preset"/> over a region of <paramref name="count"/> pixels (<see cref="SpillWalks"/>), or
        /// null where it holds none.</summary>
        internal static int? SpillBound(TemporalUpscale preset, float speed, int count)
        {
            foreach (object[] walk in SpillWalks)
                if ((TemporalUpscale)walk[0] == preset && (float)walk[1] == speed)
                    return (int)walk[2] == Acceptance3 ? Allowed(count) : (int)walk[2];
            return null;
        }

        static string Describe(CrossingTrail t) =>
            $"{t.Name}: trail {t.Total}, acceptance 3 allows {Allowed(t.Total.Checked)}, reach {t.Reach} px, oldest "
            + $"age {t.OldestAge}. Two frames after: {t.Age(TemporalGhostingRuns.FirstAge)}";

        /// <summary>
        /// The wall the box uncovers keeps no more of its colour than <see cref="Walks"/> allows, a regression bound
        /// that holds from every start phase of the jitter measured. Native meets acceptance 3 from every phase at
        /// half a display pixel a frame, 1, 2.5 and 3, and Quality and Performance at 2.5 and 3. Elsewhere below
        /// Native the trail depends on the phase: at Quality from none to 7, 10, 4 and 12 pixels from half a pixel to
        /// 2, and at Performance up to 23, most of it the current frame's spill within the reconstruction's reach
        /// (<see cref="SpillWalks"/>). Before the band, under the reach, the band's history rode out with the wall on
        /// the first phase: 20 and 32 of 90 at half a pixel, 45 and 87 of 210 at 1, and 101 of 300 at 1.5 on Quality,
        /// and past it the lock exception kept the box's history where its ridged pixels held locks near whole: 38
        /// and 22 of 510 at 2.5.
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

        /// <summary>
        /// Past the reconstruction's reach from the box's edge, 3 display pixels at Quality, 4 at Performance and 6 at
        /// UltraPerformance, the wall shows the box's colour only where a history kept from it survived, so a trail
        /// the band drop missed shows there while the spill does not. Quality and Performance meet acceptance 3 there
        /// from every phase measured but at 2 display pixels a frame on Quality and 1.5 on Performance, 2 pixels at
        /// worst. At UltraPerformance, without the band drop, the trail there read 12, 18, 40, 17 and 30 from 1 display
        /// pixel a frame to 3 on the first phase, against the bounds of <see cref="SpillWalks"/>.
        /// </summary>
        [GpuTheory]
        [MemberData(nameof(SpillWalks))]
        public void A_followed_keyed_box_leaves_no_trail_past_the_reconstructions_reach(TemporalUpscale preset,
            float speed, int maxExcess)
        {
            CrossingTrail t = runs.BeyondSpill(preset, speed);
            int bound = maxExcess == Acceptance3 ? Allowed(t.Total.Checked) : maxExcess;
            string ctx = $"{Describe(t)}, bound {bound}";
            output.WriteLine(ctx);
            Assert.True(t.Total.Checked > 0, $"no wall lies past the reconstruction's reach. {ctx}");
            Assert.True(t.Total.Excess <= bound, $"the box's colour stays past the reconstruction's reach. {ctx}");
        }

        /// <summary>Every preset's trail at every walk speed, with the walk in internal pixels a frame, and by age at
        /// Native and Quality at <see cref="WalkPixels"/>. Under DilationReachInternalPixels (1.25 internal pixels a
        /// frame) the pixels beside the trailing edge take the box's motion by dilation, and the wall pixel clear of
        /// the edge restarts. At UltraPerformance the reconstruction spreads the box's texel over 6 display pixels of
        /// wall, which the bare-wall floor never shows, so it also prints each trail past the reconstruction's reach
        /// (<see cref="TemporalFollowCameraRuns.BeyondSpill"/>).</summary>
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
            foreach (float speed in Speeds)
                foreach (TemporalUpscale preset in Presets)
                {
                    CrossingTrail b = runs.BeyondSpill(preset, speed);
                    output.WriteLine($"{b.Name}: trail {b.Total}, reach {b.Reach} px");
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
