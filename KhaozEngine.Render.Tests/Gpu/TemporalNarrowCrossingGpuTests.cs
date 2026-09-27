using System;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23 from the side of step 3's exceptions: a mostly covered footprint
    /// keeps its history where the nearest stored depth is narrow or the pixel carries a lock a ridge refreshed on the
    /// last frame, which a keyed object leaving a pixel can also satisfy. Keyed lines one and two internal texels wide
    /// cross the textured wall at 2 internal pixels a frame, and a box whose texture gives its pixels ridges, and so
    /// locks, crosses it at 2 display pixels a frame (<see cref="TemporalNarrowCrossingRuns"/>). Each trail is read as
    /// <see cref="TemporalGhostingGpuTests"/> reads the crossing, by the excess over a floor that restarts the bare
    /// wall on the frame the object uncovered the pixel.
    /// <para>
    /// The ridged box is asserted against a bound that guards the lock clause. None of the three meets acceptance 3's
    /// one pixel in 200 of the trail, and the table test prints each with its reason. HDR is off, the sharpen is at its
    /// default, Native and Quality are measured, and the measured values in the comments are Metal on Apple silicon.
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

        static int Allowed(int count) => Math.Max(1, count / AllowedOverOneIn);

        static string Describe(CrossingTrail t) =>
            $"{t.Name}: trail {t.Total}, acceptance 3 allows {Allowed(t.Total.Checked)}, reach {t.Reach} px, oldest "
            + $"age {t.OldestAge}. Two frames after: {t.Age(TemporalGhostingRuns.FirstAge)}";

        /// <summary>
        /// A ridged, textured keyed box's pixels carry locks its motion releases only by a third at 2 display pixels a
        /// frame, too little to fall within half of <c>LockDecay</c> of whole, so where it leaves the history drops.
        /// What stays is the column its trailing edge left two frames before, whose first samples took the box's colour
        /// through the reconstruction.
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

        /// <summary>Every crossing at Native and Quality, by age at Native, and the reason each misses acceptance
        /// 3.</summary>
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
            foreach (TemporalUpscale preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
            {
                output.WriteLine("line one texel wide: the narrow exception keeps its history where it left, and "
                    + "dropping every mostly covered footprint's history still leaves 11 and 23. The line's colour "
                    + "reaches the pixels around it through the reconstruction, and over a grey texture the clip, "
                    + "whose chroma range is nothing, pulls such a pixel's luma to the neighbourhood mean at full "
                    + "confidence, where a ridge of the texture can hold it. "
                    + Describe(runs.Run(NarrowCrossing.LineOneTexel, preset)));
                output.WriteLine("line two texels wide: the narrow exception keeps its history where it left, as for "
                    + "two still blades side by side. Keeping it only for a surface that stood still leaves 0 and 3, "
                    + "but takes the fast keyed line over the flat wall under its energy floor in "
                    + "TemporalFastEdgeGpuTests. " + Describe(runs.Run(NarrowCrossing.LineTwoTexels, preset)));
                output.WriteLine("ridged box: the column its trailing edge left two frames before, whose first "
                    + "samples took the box's colour through the reconstruction. "
                    + Describe(runs.Run(NarrowCrossing.RidgedBox, preset)));
            }
            output.WriteLine($"every run: {runs.Seconds:0.0} s");
        }
    }
}
