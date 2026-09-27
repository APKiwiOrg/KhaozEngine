using System;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN section 7 item 3: a keyed object crossing the view leaves no trail longer
    /// than one display pixel two frames after it passes, and a particle burst over a moving background does the same
    /// through the reactive estimate. Beside it, the same crossing unkeyed, a 10 m keyed teleport inside the 16 m cut
    /// distance, which keeps history and moves by the jump, and an orthographic zoom, which leaves no ghost.
    /// <para>
    /// Each run is compared with the same path without the object, at the same frame index and jitter. A trail pixel is
    /// one the object last covered two or more frames ago, more than one pixel from where it is now
    /// (<see cref="TemporalAcceptance.Trail"/>), and it is read by its largest channel difference, which also sees a
    /// tint of the same luma. The crossing runs over the flat wall and over the textured wall, whose variation widens
    /// the neighbourhood clip. The column right behind the box's trailing edge takes the box's motion by nearest-depth
    /// dilation, so it is measured on its own two frames later. A trail pixel was uncovered only a few frames before,
    /// so it is compared with a floor that starts the bare wall with no history on the frame the box uncovered it
    /// (<see cref="TemporalGhostingRuns"/>), and the bound applies to the excess over that floor.
    /// </para>
    /// <para>
    /// Three results are printed and not asserted, each for the reason on its test: the unkeyed crossing over the
    /// textured wall, the corners a teleported box leaves for a few frames, and the burst below Native.
    /// </para>
    /// <para>
    /// HDR is off and the sharpen is at its default. Native and Quality are asserted, and the table test prints every
    /// preset. Every bound is relative to runs of the same session, no image is stored, and every measured value is
    /// printed.
    /// </para>
    /// </summary>
    public sealed class TemporalGhostingGpuTests(TemporalGhostingRuns runs, ITestOutputHelper output)
        : IClassFixture<TemporalGhostingRuns>
    {
        // Every bound below is relative to runs of the same session. The measured values in the comments are Metal on
        // Apple silicon.

        // A trail region smaller than this measured nothing.
        const int MinTrailPixels = 100;

        // At most one pixel in this many of a trail region, and never fewer than one, may exceed its floor by more
        // than the tolerance: 4 of the crossing's 840 and 1 of the 120 two frames after. Measured 0 over the flat wall
        // keyed or not (1 unkeyed at Quality), and 1 and 0 over the textured wall keyed.
        const int AllowedOverOneIn = 200;

        // The jump frame's motion against the jump: 0.05 internal pixels plus a 1/1024 share of the jump for the
        // half-float motion target. Measured 0.
        const float MotionTolerancePixels = 0.05f, MotionToleranceShare = 1f / 1024f;

        // GhostPixels' luma difference and reference edge step, and at most one ghost pixel in this many of the frame.
        // Measured 0 at every checkpoint and preset.
        const float GhostTolerance = 0.2f, GhostEdgeStep = 0.08f;
        const int AllowedGhostOneIn = 500;

        // A burst region smaller than this measured nothing, and on a frame the particles draw at least this many of
        // its pixels must read as reactive. Measured 1100 at Native and 1174 at Quality, and 0 outside the region.
        const int MinBurstPixels = 400, MinReactivePixels = 100;

        const int W = TemporalGhostingRuns.W, H = TemporalGhostingRuns.H;

        /// <summary>The corner pixels a teleported box leaves are reported, not asserted.</summary>
        const string TeleportCornerIssue = "https://github.com/APKiwiOrg/KhaozEngine/issues/1185";

        static int Allowed(int count) => Math.Max(1, count / AllowedOverOneIn);

        static string Describe(CrossingTrail trail) =>
            $"{trail.Name}: trail {trail.Total}, reach {trail.Reach} px, oldest age {trail.OldestAge}. Two frames "
            + $"after: {trail.Age(TemporalGhostingRuns.FirstAge)}. Trailing column: {trail.Column}";

        void AssertNoTrail(CrossingTrail trail)
        {
            TrailTally two = trail.Age(TemporalGhostingRuns.FirstAge);
            string ctx = Describe(trail);
            output.WriteLine(ctx);
            Assert.True(trail.Total.Checked > MinTrailPixels,
                $"the trail region holds {trail.Total.Checked} pixels, so nothing was measured. {ctx}");
            Assert.True(trail.Column.Checked > 0, $"the trailing column was not measured. {ctx}");
            Assert.True(two.Excess <= Allowed(two.Checked),
                $"pixels the box passed two frames ago still differ from the wall beyond the floor. {ctx}");
            Assert.True(trail.Total.Excess <= Allowed(trail.Total.Checked),
                $"pixels the box passed two or more frames ago still differ from the wall beyond the floor. {ctx}");
        }

        /// <summary>
        /// The keyed box leaves no trail over either wall. Over the flat wall the neighbourhood clip alone removes any
        /// trail within a frame, because the wall's neighbourhood has no variation, so this case still passes with the
        /// disocclusion test disabled or the motion key dropped, and fails only with the clip off. Over the textured
        /// wall the clip range is as wide as the texture's own variation, so the disocclusion test and the reprojection
        /// of the pixels beside the box's moving edge by their own motion are what keep it clean.
        /// </summary>
        [GpuTheory]
        [InlineData(false, TemporalUpscale.Native)]
        [InlineData(false, TemporalUpscale.Quality)]
        [InlineData(true, TemporalUpscale.Native)]
        [InlineData(true, TemporalUpscale.Quality)]
        public void AKeyedCrossingObjectLeavesNoTrail(bool textured, TemporalUpscale preset) =>
            AssertNoTrail(runs.Crossing(keyed: true, textured, preset));

        /// <summary>
        /// The unkeyed box leaves no trail over the flat wall, where the clip removes it. Over the textured wall the
        /// unkeyed crossing is printed by <see cref="TheGhostingTableCoversEveryPreset"/> and not asserted. An object
        /// drawn without a <see cref="MotionKey"/> has no motion vectors, so the resolve reprojects it as if it stood
        /// still and only the neighbourhood clip bounds its trail, which the texture widens. Give every moving object a
        /// motion key.
        /// </summary>
        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        public void AnUnkeyedCrossingObjectOverAFlatWallLeavesNoTrail(TemporalUpscale preset) =>
            AssertNoTrail(runs.Crossing(keyed: false, textured: false, preset));

        /// <summary>
        /// A keyed body's 10 m jump keeps history and its jump frame's motion equals the jump. The pixels its old
        /// place leaves are printed and not asserted: the thin-feature lock holds a few partly covered corner pixels
        /// for about four frames after the jump (https://github.com/APKiwiOrg/KhaozEngine/issues/1185).
        /// </summary>
        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        public void AKeyedTeleportWithinTheCutDistanceKeepsHistoryAndMovesByTheJump(TemporalUpscale preset)
        {
            TeleportRun t = runs.Teleport(preset);
            float errorX = MathF.Abs(t.Motion.X - t.Want) * t.InternalWidth;
            float errorY = MathF.Abs(t.Motion.Y) * t.InternalHeight;
            float allowedX = MotionTolerancePixels + MathF.Abs(t.Want) * t.InternalWidth * MotionToleranceShare;
            string ctx = $"teleport, {preset}: history valid {t.HistoryValid}, motion {t.Motion.X:0.000000},"
                + $"{t.Motion.Y:0.000000} against {t.Want:0.000000},0 (off by {errorX:0.0000} and {errorY:0.0000} "
                + $"internal pixels, allowed {allowedX:0.0000} and {MotionTolerancePixels}). Reported: old place "
                + $"{t.Trail} ({TeleportCornerIssue})";
            output.WriteLine(ctx);
            Assert.True(t.HistoryValid, $"a keyed body's jump must not reset history. {ctx}");
            Assert.True(errorX <= allowedX && errorY <= MotionTolerancePixels,
                $"the jump frame's motion must equal the jump. {ctx}");
        }

        /// <summary>The engine's default isometric camera zooming leaves no ghost off the supersampled reference's
        /// edges. The zoom is a projection change the motion target carries, so it fails with the motion
        /// ignored.</summary>
        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        public void AnOrthographicZoomLeavesNoGhost(TemporalUpscale preset)
        {
            ZoomRun zoom = runs.Zoom(preset, GhostTolerance, GhostEdgeStep);
            foreach (var (frame, level, ghosts) in zoom.Checkpoints)
            {
                string ctx = $"zoom {level:0.000} at frame {frame}, {preset}: {ghosts} ghost pixels (at most "
                    + $"{W * H / AllowedGhostOneIn})";
                output.WriteLine(ctx);
                Assert.True(ghosts <= W * H / AllowedGhostOneIn,
                    $"the zoom leaves ghosts off the reference's edges. {ctx}");
            }
        }

        static string Describe(BurstRun b, TemporalUpscale preset) =>
            $"particle burst, {preset}, region {b.Region}: {b.Trail}. Reactive on frame "
            + $"{TemporalGhostingRuns.ReactiveFrame}: {b.ReactiveInside} pixels inside, {b.ReactiveOutside} outside, "
            + $"largest {b.ReactiveMax:0.000}";

        /// <summary>
        /// The burst leaves no trail at Native. Below Native the region still differs from the converged wall two
        /// frames after the burst, on the stripes' edges and not in the particles' colour: the reactive estimate cut
        /// the history there, and the stripes are still accumulating again under the pan. Those presets are printed
        /// by <see cref="TheGhostingTableCoversEveryPreset"/> and not asserted.
        /// </summary>
        [GpuFact]
        public void AParticleBurstOverAMovingBackgroundLeavesNoTrail()
        {
            BurstRun b = runs.Burst(TemporalUpscale.Native);
            string ctx = Describe(b, TemporalUpscale.Native);
            output.WriteLine(ctx);
            Assert.True(b.Region.Area > MinBurstPixels, $"the burst region is too small to measure. {ctx}");
            Assert.True(b.Trail.Over <= Allowed(b.Trail.Checked),
                $"the burst's pixels still differ from the wall two frames after it ended. {ctx}");
        }

        /// <summary>The reactive estimate marks the particles on a frame they draw, and nothing else, so the burst
        /// exercises it.</summary>
        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        public void TheReactiveEstimateMarksTheParticles(TemporalUpscale preset)
        {
            BurstRun b = runs.Burst(preset);
            string ctx = Describe(b, preset);
            output.WriteLine(ctx);
            Assert.True(b.ReactiveInside >= MinReactivePixels,
                $"the reactive estimate must mark the particles, or the burst does not exercise it. {ctx}");
            Assert.Equal(0, b.ReactiveOutside);
        }

        /// <summary>Every case at every preset, with the crossing's trail by age at Native. It also prints the three
        /// results no test asserts, each labelled with its reason.</summary>
        [GpuFact]
        public void TheGhostingTableCoversEveryPreset()
        {
            output.WriteLine("| Case | Preset | Checked | Over | Luma over | Worst | Floor over | Floor worst | Excess "
                + "| Worst excess | Reach | Oldest | Age 2 excess | Column worst | Column excess |");
            output.WriteLine("|" + string.Concat(System.Linq.Enumerable.Repeat(" --- |", 15)));
            foreach (bool textured in new[] { false, true })
                foreach (bool keyed in new[] { true, false })
                    foreach (TemporalUpscale preset in TemporalGhostingRuns.Presets)
                    {
                        CrossingTrail c = runs.Crossing(keyed, textured, preset);
                        TrailTally t = c.Total, two = c.Age(TemporalGhostingRuns.FirstAge);
                        output.WriteLine($"| {(keyed ? "keyed" : "unkeyed")} {(textured ? "textured" : "flat")} "
                            + $"| {preset} | {t.Checked} | {t.Over} | {t.LumaOver} | {t.Worst:0.000} | {t.FloorOver} "
                            + $"| {t.FloorWorst:0.000} | {t.Excess} | {t.WorstExcess:0.000} | {c.Reach} "
                            + $"| {c.OldestAge} | {two.Excess} of {two.Checked} | {c.Column.Worst:0.000} "
                            + $"| {c.Column.Excess} of {c.Column.Checked} |");
                        Assert.True(t.Checked > MinTrailPixels, $"{c.Name}: the trail region measured nothing");
                    }
            foreach (bool textured in new[] { false, true })
                foreach (bool keyed in new[] { true, false })
                {
                    CrossingTrail c = runs.Crossing(keyed, textured, TemporalUpscale.Native);
                    for (int age = TemporalGhostingRuns.FirstAge; age < TemporalGhostingRuns.TrailFrames; age++)
                        output.WriteLine($"{c.Name}, age {age}: {c.Age(age)}");
                }
            foreach (TemporalUpscale preset in TemporalGhostingRuns.Presets)
            {
                TeleportRun t = runs.Teleport(preset);
                output.WriteLine($"teleport, {preset}: history valid {t.HistoryValid}, motion {t.Motion.X:0.000000},"
                    + $"{t.Motion.Y:0.000000} against {t.Want:0.000000} at {t.InternalWidth} by {t.InternalHeight}, "
                    + $"trail {t.Trail}");
                ZoomRun z = runs.Zoom(preset, GhostTolerance, GhostEdgeStep);
                output.WriteLine($"zoom, {preset}: "
                    + string.Join(", ", Array.ConvertAll(ToArray(z), p => $"frame {p.Frame} {p.Ghosts} ghosts")));
                BurstRun b = runs.Burst(preset);
                output.WriteLine($"particle burst, {preset}: {b.Trail}, reactive {b.ReactiveInside} inside, "
                    + $"{b.ReactiveOutside} outside, largest {b.ReactiveMax:0.000}");
                Assert.True(t.Trail.Checked > MinTrailPixels && b.Trail.Checked > MinBurstPixels,
                    $"{preset}: a trail region measured nothing");
            }
            output.WriteLine("Reported, not asserted:");
            foreach (TemporalUpscale preset in TemporalGhostingRuns.Presets)
                output.WriteLine($"unkeyed over the textured wall, bounded only by the clip, key moving objects. "
                    + Describe(runs.Crossing(keyed: false, textured: true, preset)));
            foreach (TemporalUpscale preset in TemporalGhostingRuns.Presets)
                output.WriteLine($"teleport corners, {preset}, {TeleportCornerIssue}: {runs.Teleport(preset).Trail}");
            foreach (TemporalUpscale preset in TemporalGhostingRuns.Presets)
                if (preset != TemporalUpscale.Native)
                    output.WriteLine("burst below Native, stripe edges accumulating again after the reactive cut. "
                        + Describe(runs.Burst(preset), preset));
            output.WriteLine($"every run: {runs.Seconds:0.0} s");
        }

        static (int Frame, float Zoom, int Ghosts)[] ToArray(ZoomRun z)
        {
            var a = new (int, float, int)[z.Checkpoints.Count];
            for (int i = 0; i < a.Length; i++) a[i] = z.Checkpoints[i];
            return a;
        }
    }
}
