using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23 under the perspective follow camera a game uses: a keyed
    /// avatar-sized box with ridged pixels walks over the textured ground while <see cref="FollowCamera3D"/> keeps it
    /// at the screen centre (<see cref="TemporalPerspectiveFollowRuns"/>). Unlike the orthographic wall of
    /// <see cref="TemporalFollowCameraGpuTests"/>, the ground's motion changes with its depth, so neighbouring ground
    /// texels move apart on screen. Each trail is read by the excess over a floor that restarts the bare ground on the
    /// frame the box uncovered the pixel. HDR is off, the sharpen is at its default, and the measured values in the
    /// comments are Metal on Apple silicon.
    /// </summary>
    public sealed class TemporalPerspectiveFollowGpuTests(TemporalPerspectiveFollowRuns runs,
        ITestOutputHelper output) : IClassFixture<TemporalPerspectiveFollowRuns>
    {
        /// <summary>The boot pitch and the low one, in radians.</summary>
        internal const float BootPitch = 0.75f, LowPitch = 0.35f;

        // Acceptance 3's share of a trail region that may exceed its floor, never fewer than one pixel.
        const int AllowedOverOneIn = 200;

        static readonly float[] Speeds = { 0.5f, 1f, 1.5f, 2f, 2.5f, 3f };

        static readonly TemporalUpscale[] Presets =
        {
            TemporalUpscale.Native, TemporalUpscale.Quality, TemporalUpscale.Performance,
            TemporalUpscale.UltraPerformance,
        };

        static readonly FollowHeading[] Headings =
            { FollowHeading.Away, FollowHeading.Sideways, FollowHeading.Towards };

        internal static int Allowed(int count) => Math.Max(1, count / AllowedOverOneIn);

        static string Describe(CrossingTrail t) =>
            $"{t.Name}: trail {t.Total}, acceptance 3 allows {Allowed(t.Total.Checked)}, reach {t.Reach} px, oldest "
            + $"age {t.OldestAge}. Two frames after: {t.Age(TemporalGhostingRuns.FirstAge)}";

        // Every walk of the table, or with the table switch off the walks the table always prints: the boot pitch
        // walking away at Native and Quality.
        static IEnumerable<(float Pitch, FollowHeading Heading, float Speed, TemporalUpscale Preset)> TableWalks()
        {
            bool full = TemporalStabilityRuns.FullTable;
            TemporalUpscale[] presets = full ? Presets : new[] { TemporalUpscale.Native, TemporalUpscale.Quality };
            foreach (float pitch in full ? new[] { BootPitch, LowPitch } : new[] { BootPitch })
                foreach (FollowHeading heading in full ? Headings : new[] { FollowHeading.Away })
                    foreach (float speed in full ? Speeds : new[] { 0.5f, 1f, 2f })
                        foreach (TemporalUpscale preset in presets)
                            yield return (pitch, heading, speed, preset);
        }

        /// <summary>Every walk's trail, with the walk in internal pixels a frame. Report only. With
        /// <c>KE_TEMPORAL_ACCEPTANCE_TABLE=1</c> it prints both pitches, every heading, speed and preset.</summary>
        [GpuFact]
        public void The_perspective_follow_table_prints_every_walk()
        {
            output.WriteLine("| Pitch | Heading | Speed | Preset | Internal | Checked | Excess | Acceptance 3 allows "
                + "| Worst excess | Reach | Oldest | Age 2 excess | Band marks |");
            output.WriteLine("|" + string.Concat(Enumerable.Repeat(" --- |", 13)));
            foreach (var (pitch, heading, speed, preset) in TableWalks())
            {
                CrossingTrail c = runs.Run(preset, speed, pitch, heading);
                TrailTally t = c.Total, two = c.Age(TemporalGhostingRuns.FirstAge);
                float internalPixels = speed / TemporalSettings.DisplayOverInternal(preset);
                output.WriteLine($"| {pitch} | {heading} | {speed} | {preset} | {internalPixels:0.00} | {t.Checked} "
                    + $"| {t.Excess} | {Allowed(t.Checked)} | {t.WorstExcess:0.000} | {c.Reach} | {c.OldestAge} "
                    + $"| {two.Excess} of {two.Checked} | {runs.BandMarks(preset, speed, pitch, heading)} |");
                Assert.True(t.Checked > 0, $"{c.Name}: the trail region measured nothing");
            }
            output.WriteLine(TemporalStabilityRuns.FullTable ? "the full table"
                : $"the boot pitch walking away only. Set {TemporalStabilityRuns.TableVariable}=1 for the full table");
            output.WriteLine($"every run: {runs.Seconds:0.0} s");
        }
    }
}
