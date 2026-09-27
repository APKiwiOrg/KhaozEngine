using System;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23 from the side of step 3's exceptions: a mostly covered footprint
    /// keeps its history where the nearest stored depth is narrow and no moving surface showed there last frame, or
    /// where the pixel carries a lock a ridge refreshed on the last frame, which a keyed object leaving a pixel could
    /// satisfy. Keyed lines one and two internal texels wide cross the textured wall at 2 internal pixels a frame, and
    /// a box whose texture gives its pixels ridges, and so locks, crosses it at 2 display pixels a frame
    /// (<see cref="TemporalNarrowCrossingRuns"/>). Each trail is read as <see cref="TemporalGhostingGpuTests"/> reads
    /// the crossing, by the excess over a floor that restarts the bare wall on the frame the object uncovered the
    /// pixel.
    /// <para>
    /// The line two texels wide at both presets and the line one texel wide at Native are asserted at acceptance 3's
    /// one pixel in 200 of the trail, and the ridged box against a bound that guards the lock clause. The line one
    /// texel wide misses acceptance 3 at Quality, and the table test prints each case with its reason. HDR is off, the
    /// sharpen is at its default, Native and Quality are measured, and the measured values in the comments are Metal
    /// on Apple silicon.
    /// </para>
    /// </summary>
    public sealed class TemporalNarrowCrossingGpuTests(TemporalNarrowCrossingRuns runs, ITestOutputHelper output)
        : IClassFixture<TemporalNarrowCrossingRuns>
    {
        // A trail region smaller than this measured nothing.
        const int MinTrailPixels = 100;

        // Acceptance 3's share of a trail region that may exceed its floor, never fewer than one pixel.
        const int AllowedOverOneIn = 200;

        // The ridged box's trail pixels in excess of their floor, of 420. Measured 3 and 4 at Native and Quality. A
        // lock clause taking any lock whose hold is whole left 10 and 39, and the resolve before amendment 23 55 and
        // 211.
        const int MaxRidgedBoxExcess = 7;

        /// <summary>The issues that track what the line one texel wide and the ridged box keep.</summary>
        const string ThinLineIssue = "https://github.com/APKiwiOrg/KhaozEngine/issues/1186",
            FreshTintIssue = "https://github.com/APKiwiOrg/KhaozEngine/issues/1187";

        static int Allowed(int count) => Math.Max(1, count / AllowedOverOneIn);

        static string Describe(CrossingTrail t) =>
            $"{t.Name}: trail {t.Total}, acceptance 3 allows {Allowed(t.Total.Checked)}, reach {t.Reach} px, oldest "
            + $"age {t.OldestAge}. Two frames after: {t.Age(TemporalGhostingRuns.FirstAge)}";

        /// <summary>
        /// A ridged, textured keyed box's pixels carry locks, and its motion at 2 display pixels a frame releases a
        /// third of each, more than the half of <c>LockDecay</c> a lock may lose and still keep a mostly covered
        /// footprint's history, so where it leaves the history drops.
        /// What stays is the column its trailing edge left two frames before, whose first samples took the box's colour
        /// through the reconstruction (https://github.com/APKiwiOrg/KhaozEngine/issues/1187).
        /// </summary>
        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        public void A_ridged_keyed_box_keeps_no_history_by_its_locks_where_it_left(TemporalUpscale preset)
        {
            CrossingTrail t = runs.Run(NarrowCrossing.RidgedBox, preset);
            string ctx = Describe(t);
            output.WriteLine(ctx);
            Assert.True(t.Total.Checked > MinTrailPixels,
                $"the trail region holds {t.Total.Checked} pixels, so nothing was measured. {ctx}");
            Assert.True(t.Total.Excess <= MaxRidgedBoxExcess,
                $"the box's locks keep its colour where it left. {ctx}");
        }

        /// <summary>
        /// A keyed line two internal texels wide is as narrow as two still blades side by side the jitter missed, but
        /// the state it leaves says a moving surface showed there, so where it leaves the history drops, and the pixels
        /// beside it that took its colour restart once it moves on. Measured 0 of 420 at Native and 0 of 630 at
        /// Quality, against the 2 and 3 acceptance 3 allows. With the narrow exception taking any narrow feature it
        /// kept its colour there, 50 and 136, and with the state's veto alone 0 and 3.
        /// </summary>
        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        public void A_keyed_line_two_texels_wide_leaves_no_trail_where_it_left(TemporalUpscale preset)
        {
            CrossingTrail t = runs.Run(NarrowCrossing.LineTwoTexels, preset);
            string ctx = Describe(t);
            output.WriteLine(ctx);
            Assert.True(t.Total.Checked > MinTrailPixels,
                $"the trail region holds {t.Total.Checked} pixels, so nothing was measured. {ctx}");
            Assert.True(t.Total.Excess <= Allowed(t.Total.Checked),
                $"the narrow exception keeps the line's colour where it left. {ctx}");
        }

        /// <summary>
        /// A keyed line one internal texel wide, at Native, where the pixels beside it that took its colour restart
        /// once it moves on. Measured 0 of 420 against the 2 acceptance 3 allows. The state's veto alone left 11, and
        /// the narrow exception taking any narrow feature 128. Quality keeps 8 of 630 against 3, and the table test
        /// prints it (https://github.com/APKiwiOrg/KhaozEngine/issues/1186).
        /// </summary>
        [GpuFact]
        public void A_keyed_line_one_texel_wide_leaves_no_trail_where_it_left_at_native()
        {
            CrossingTrail t = runs.Run(NarrowCrossing.LineOneTexel, TemporalUpscale.Native);
            string ctx = Describe(t);
            output.WriteLine(ctx);
            Assert.True(t.Total.Checked > MinTrailPixels,
                $"the trail region holds {t.Total.Checked} pixels, so nothing was measured. {ctx}");
            Assert.True(t.Total.Excess <= Allowed(t.Total.Checked), $"the line's colour stays where it left. {ctx}");
        }

        /// <summary>Every crossing at Native and Quality, by age at Native, and the reason the line one texel wide
        /// at Quality and the ridged box miss acceptance 3.</summary>
        [GpuFact]
        public void The_narrow_crossing_table_prints_every_case()
        {
            output.WriteLine("| Case | Preset | Checked | Excess | Acceptance 3 allows | Worst excess | Reach | Oldest "
                + "| Age 2 excess |");
            output.WriteLine("|" + string.Concat(System.Linq.Enumerable.Repeat(" --- |", 9)));
            foreach (NarrowCrossing crossing in Enum.GetValues<NarrowCrossing>())
                foreach (TemporalUpscale preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
                {
                    CrossingTrail c = runs.Run(crossing, preset);
                    TrailTally t = c.Total, two = c.Age(TemporalGhostingRuns.FirstAge);
                    output.WriteLine($"| {crossing} | {preset} | {t.Checked} | {t.Excess} | {Allowed(t.Checked)} "
                        + $"| {t.WorstExcess:0.000} | {c.Reach} | {c.OldestAge} | {two.Excess} of {two.Checked} |");
                    Assert.True(t.Checked > MinTrailPixels, $"{c.Name}: the trail region measured nothing");
                }
            foreach (NarrowCrossing crossing in Enum.GetValues<NarrowCrossing>())
            {
                CrossingTrail c = runs.Run(crossing, TemporalUpscale.Native);
                for (int age = TemporalGhostingRuns.FirstAge; age < TemporalGhostingRuns.TrailFrames; age++)
                    output.WriteLine($"{c.Name}, age {age}: {c.Age(age)}");
            }
            output.WriteLine("Reported, not asserted against acceptance 3:");
            output.WriteLine($"line one texel wide at Quality ({ThinLineIssue}): its colour reaches the pixels around "
                + "it through the reconstruction, and over a grey texture the clip, whose chroma range is nothing, "
                + "pulls such a pixel's luma to the neighbourhood mean at full confidence, where a ridge of the "
                + "texture can hold it. " + Describe(runs.Run(NarrowCrossing.LineOneTexel, TemporalUpscale.Quality)));
            foreach (TemporalUpscale preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
            {
                output.WriteLine($"ridged box ({FreshTintIssue}): the column its trailing edge left two frames "
                    + "before, whose first samples took the box's colour through the reconstruction. "
                    + Describe(runs.Run(NarrowCrossing.RidgedBox, preset)));
            }
            output.WriteLine($"every run: {runs.Seconds:0.0} s");
        }
    }
}
