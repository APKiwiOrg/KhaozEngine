using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The follow walks after a long still hold, report only
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1206">#1206</see>). The follow facts stand the box
    /// still for 16 frames before it walks. After <see cref="LongHold"/> frames the history has converged, and the slow
    /// orthographic walks leave more trail than the facts measure: Quality at half a display pixel a frame and at 1,
    /// and Performance at 1. So these tables print every walk of <see cref="TemporalFollowCameraRuns"/> and
    /// <see cref="TemporalPerspectiveFollowRuns"/> after the long hold, with the display-sized reconstruction on as it
    /// ships, against acceptance 3 and against the short-hold fact's bound, and assert only that each trail region
    /// measured something. The cells over their bounds are the open finding, not a regression guard.
    /// </summary>
    public sealed class TemporalLongHoldFollowGpuTests(TemporalFollowCameraRuns ortho,
        TemporalPerspectiveFollowRuns perspective, ITestOutputHelper output)
        : IClassFixture<TemporalFollowCameraRuns>, IClassFixture<TemporalPerspectiveFollowRuns>
    {
        /// <summary>The still frames before the walk, long enough for the history to converge.</summary>
        public const int LongHold = 96;

        const string Issue = "#1206";

        static readonly float[] Speeds = { 0.5f, 1f, 1.5f, 2f, 2.5f, 3f };

        static readonly TemporalUpscale[] Presets =
        {
            TemporalUpscale.Native, TemporalUpscale.Quality, TemporalUpscale.Performance,
            TemporalUpscale.UltraPerformance,
        };

        static readonly FollowHeading[] Headings =
            { FollowHeading.Away, FollowHeading.Sideways, FollowHeading.Towards };

        const float BootPitch = TemporalPerspectiveFollowGpuTests.BootPitch;

        // Which bounds a trail's excess passes.
        static string Over(int excess, int allowed, int? bound)
        {
            var over = new List<string>();
            if (excess > allowed) over.Add("acceptance 3");
            if (bound is int b && excess > b) over.Add("the fact's bound");
            return over.Count == 0 ? "within" : "over " + string.Join(" and ", over);
        }

        static string Show(int? bound) => bound is int b ? b.ToString() : "none";

        /// <summary>Every orthographic follow walk after <see cref="LongHold"/> still frames, every speed and preset
        /// the follow camera fact covers, and the UltraPerformance trail past the reconstruction's reach.</summary>
        [GpuFact]
        public void The_long_hold_follow_camera_table_prints_every_walk()
        {
            output.WriteLine($"orthographic follow camera after {LongHold} still frames ({Issue}), report only");
            output.WriteLine("| Speed | Preset | Checked | Excess | Acceptance 3 allows | Fact's bound | Worst excess "
                + "| Reach | Oldest | Result |");
            output.WriteLine("|" + string.Concat(Enumerable.Repeat(" --- |", 10)));
            int cells = 0, overAcceptance = 0, overBound = 0;
            foreach (float speed in Speeds)
                foreach (TemporalUpscale preset in Presets)
                {
                    CrossingTrail c = ortho.Run(preset, speed, LongHold);
                    TrailTally t = c.Total;
                    int allowed = TemporalFollowCameraGpuTests.Allowed(t.Checked);
                    int? bound = TemporalFollowCameraGpuTests.Bound(preset, speed, t.Checked);
                    output.WriteLine($"| {speed} | {preset} | {t.Checked} | {t.Excess} | {allowed} | {Show(bound)} "
                        + $"| {t.WorstExcess:0.000} | {c.Reach} | {c.OldestAge} | {Over(t.Excess, allowed, bound)} |");
                    Assert.True(t.Checked > 0, $"{c.Name}: the trail region measured nothing");
                    cells++;
                    if (t.Excess > allowed) overAcceptance++;
                    if (bound is int b && t.Excess > b) overBound++;
                }
            output.WriteLine("| Speed | Past the reach, display px | Checked | Excess | Acceptance 3 allows "
                + "| Fact's bound | Result |");
            output.WriteLine("|" + string.Concat(Enumerable.Repeat(" --- |", 7)));
            foreach (float speed in Speeds)
            {
                TrailTally b = ortho.BeyondSpill(TemporalUpscale.UltraPerformance, speed, LongHold).Total;
                int allowed = TemporalFollowCameraGpuTests.Allowed(b.Checked);
                int? bound = TemporalFollowCameraGpuTests.SpillBound(TemporalUpscale.UltraPerformance, speed,
                    b.Checked);
                int spill = TemporalFollowCameraRuns.SpillPixels(TemporalUpscale.UltraPerformance);
                output.WriteLine($"| {speed} | {spill} | {b.Checked} | {b.Excess} | {allowed} | {Show(bound)} "
                    + $"| {Over(b.Excess, allowed, bound)} |");
            }
            output.WriteLine($"{cells - overAcceptance} of {cells} walks within acceptance 3, {cells - overBound} "
                + $"within the short-hold fact's bounds ({Issue})");
            output.WriteLine($"every run: {ortho.Seconds:0.0} s");
        }

        /// <summary>Every perspective follow walk after <see cref="LongHold"/> still frames, and the UltraPerformance
        /// trail past the reconstruction's reach. By default the boot pitch walking away at half a display pixel a
        /// frame, 1 and 2 at every preset, and with <c>KE_TEMPORAL_ACCEPTANCE_TABLE=1</c> both pitches, every
        /// heading, speed and preset.</summary>
        [GpuFact]
        public void The_long_hold_perspective_follow_table_prints_every_walk()
        {
            bool full = TemporalStabilityRuns.FullTable;
            output.WriteLine($"perspective follow after {LongHold} still frames ({Issue}), report only");
            output.WriteLine("| Pitch | Heading | Speed | Preset | Checked | Excess | Acceptance 3 allows "
                + "| Fact's bound | Worst excess | Reach | Oldest | Result |");
            output.WriteLine("|" + string.Concat(Enumerable.Repeat(" --- |", 12)));
            int cells = 0, overAcceptance = 0, overBound = 0;
            var reach = new List<string>();
            foreach (float pitch in full
                ? new[] { BootPitch, TemporalPerspectiveFollowGpuTests.LowPitch } : new[] { BootPitch })
                foreach (FollowHeading heading in full ? Headings : new[] { FollowHeading.Away })
                    foreach (float speed in full ? Speeds : new[] { 0.5f, 1f, 2f })
                        foreach (TemporalUpscale preset in Presets)
                        {
                            CrossingTrail c = perspective.Run(preset, speed, pitch, heading, LongHold);
                            TrailTally t = c.Total;
                            int allowed = TemporalPerspectiveFollowGpuTests.Allowed(t.Checked);
                            int bound = TemporalPerspectiveFollowGpuTests.Bound(pitch, heading, speed, preset,
                                t.Checked);
                            output.WriteLine($"| {pitch} | {heading} | {speed} | {preset} | {t.Checked} | {t.Excess} "
                                + $"| {allowed} | {bound} | {t.WorstExcess:0.000} | {c.Reach} | {c.OldestAge} "
                                + $"| {Over(t.Excess, allowed, bound)} |");
                            Assert.True(t.Checked > 0, $"{c.Name}: the trail region measured nothing");
                            cells++;
                            if (t.Excess > allowed) overAcceptance++;
                            if (t.Excess > bound) overBound++;
                            if (preset != TemporalUpscale.UltraPerformance) continue;
                            TrailTally b = perspective.BeyondSpill(preset, speed, pitch, heading, LongHold).Total;
                            if (b.Checked == 0) continue;
                            int reachAllowed = TemporalPerspectiveFollowGpuTests.Allowed(b.Checked);
                            int? reachBound = TemporalPerspectiveFollowGpuTests.ReachBound(pitch, heading, speed,
                                preset, b.Checked);
                            reach.Add($"| {pitch} | {heading} | {speed} | {b.Checked} | {b.Excess} | {reachAllowed} "
                                + $"| {Show(reachBound)} | {Over(b.Excess, reachAllowed, reachBound)} |");
                        }
            output.WriteLine("| Pitch | Heading | Speed | Past the reach, checked | Excess | Acceptance 3 allows "
                + "| Fact's bound | Result |");
            output.WriteLine("|" + string.Concat(Enumerable.Repeat(" --- |", 8)));
            foreach (string line in reach) output.WriteLine(line);
            output.WriteLine($"{cells - overAcceptance} of {cells} walks within acceptance 3, {cells - overBound} "
                + $"within the short-hold fact's bounds ({Issue})");
            output.WriteLine(full ? "the full table"
                : $"the boot pitch walking away only. Set {TemporalStabilityRuns.TableVariable}=1 for the full table");
            output.WriteLine($"every run: {perspective.Seconds:0.0} s");
        }
    }
}
