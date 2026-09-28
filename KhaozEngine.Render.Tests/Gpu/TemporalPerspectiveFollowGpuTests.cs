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

        const TemporalUpscale N = TemporalUpscale.Native, Q = TemporalUpscale.Quality,
            P = TemporalUpscale.Performance, U = TemporalUpscale.UltraPerformance;

        /// <summary>Every walk the resolve leaves over acceptance 3, and its bound: the measured excess and about a
        /// quarter more, at least 2 (amendment 23), the measured value in the comment. Where the resolve before the
        /// band left more, and a bound under that still leaves the measured value a margin, the bound sits under it, so
        /// four more bounds fail on that resolve. Three cannot: at the boot pitch away at half a display pixel a frame
        /// on UltraPerformance, 23 against 24, and at 3 on Performance, 2 against 3, and at the low pitch sideways at
        /// half a pixel on Performance, 2 against 3, it left one pixel more, so a bound under it would be the measured
        /// value itself. Every other walk holds acceptance 3.</summary>
        static readonly (float Pitch, FollowHeading Heading, float Speed, TemporalUpscale Preset, int Bound)[]
            Residuals =
        {
            (BootPitch, FollowHeading.Away, 0.5f, U, 28),              // measured 23 of 36
            (BootPitch, FollowHeading.Away, 1f, U, 46),                // measured 37 of 96
            (BootPitch, FollowHeading.Away, 1.5f, U, 47),              // measured 42 of 144, 48 before the band
            (BootPitch, FollowHeading.Away, 2f, U, 22),                // measured 18 of 222
            (BootPitch, FollowHeading.Away, 2.5f, U, 21),              // measured 17 of 284
            (BootPitch, FollowHeading.Away, 3f, P, 4),                 // measured 2 of 368
            (BootPitch, FollowHeading.Away, 3f, U, 6),                 // measured 4 of 368
            (BootPitch, FollowHeading.Sideways, 0.5f, P, 6),           // measured 4 of 69
            (BootPitch, FollowHeading.Sideways, 0.5f, U, 12),          // measured 10 of 69
            (BootPitch, FollowHeading.Sideways, 1f, P, 5),             // measured 3 of 160
            (BootPitch, FollowHeading.Sideways, 1f, U, 43),            // measured 35 of 160
            (BootPitch, FollowHeading.Sideways, 1.5f, U, 31),          // measured 25 of 226
            (BootPitch, FollowHeading.Sideways, 2f, Q, 4),             // measured 2 of 311
            (BootPitch, FollowHeading.Sideways, 2f, P, 5),             // measured 3 of 311
            (BootPitch, FollowHeading.Sideways, 2f, U, 42),            // measured 34 of 311
            (BootPitch, FollowHeading.Sideways, 2.5f, P, 4),           // measured 2 of 377
            (BootPitch, FollowHeading.Sideways, 2.5f, U, 36),          // measured 29 of 377
            (BootPitch, FollowHeading.Sideways, 3f, U, 28),            // measured 23 of 464
            (BootPitch, FollowHeading.Towards, 0.5f, U, 6),            // measured 4 of 24
            (BootPitch, FollowHeading.Towards, 1f, Q, 4),              // measured 2 of 60
            (BootPitch, FollowHeading.Towards, 1f, P, 5),              // measured 4 of 60, 6 before the band
            (BootPitch, FollowHeading.Towards, 1f, U, 8),              // measured 6 of 60
            (BootPitch, FollowHeading.Towards, 1.5f, U, 10),           // measured 8 of 96
            (BootPitch, FollowHeading.Towards, 2f, U, 22),             // measured 18 of 132
            (BootPitch, FollowHeading.Towards, 2.5f, U, 9),            // measured 7 of 144
            (BootPitch, FollowHeading.Towards, 3f, P, 5),              // measured 3 of 180
            (BootPitch, FollowHeading.Towards, 3f, U, 9),              // measured 7 of 180
            (LowPitch, FollowHeading.Away, 0.5f, U, 13),               // measured 11 of 48
            (LowPitch, FollowHeading.Away, 1f, U, 16),                 // measured 13 of 126
            (LowPitch, FollowHeading.Away, 1.5f, U, 36),               // measured 29 of 184
            (LowPitch, FollowHeading.Away, 2f, P, 5),                  // measured 3 of 268
            (LowPitch, FollowHeading.Away, 2f, U, 26),                 // measured 21 of 268
            (LowPitch, FollowHeading.Away, 2.5f, Q, 5),                // measured 4 of 436, 6 before the band
            (LowPitch, FollowHeading.Away, 2.5f, P, 6),                // measured 4 of 436
            (LowPitch, FollowHeading.Away, 2.5f, U, 31),               // measured 25 of 436
            (LowPitch, FollowHeading.Away, 3f, P, 8),                  // measured 6 of 548
            (LowPitch, FollowHeading.Away, 3f, U, 9),                  // measured 7 of 548
            (LowPitch, FollowHeading.Sideways, 0.5f, N, 4),            // measured 2 of 71
            (LowPitch, FollowHeading.Sideways, 0.5f, Q, 5),            // measured 3 of 71
            (LowPitch, FollowHeading.Sideways, 0.5f, P, 4),            // measured 2 of 71
            (LowPitch, FollowHeading.Sideways, 0.5f, U, 20),           // measured 16 of 71
            (LowPitch, FollowHeading.Sideways, 1f, N, 5),              // measured 3 of 160
            (LowPitch, FollowHeading.Sideways, 1f, Q, 5),              // measured 3 of 160
            (LowPitch, FollowHeading.Sideways, 1f, P, 8),              // measured 6 of 160
            (LowPitch, FollowHeading.Sideways, 1f, U, 33),             // measured 27 of 160
            (LowPitch, FollowHeading.Sideways, 1.5f, P, 4),            // measured 2 of 226
            (LowPitch, FollowHeading.Sideways, 1.5f, U, 16),           // measured 13 of 226
            (LowPitch, FollowHeading.Sideways, 2f, P, 9),              // measured 7 of 313
            (LowPitch, FollowHeading.Sideways, 2f, U, 21),             // measured 18 of 313, 22 before the band
            (LowPitch, FollowHeading.Sideways, 2.5f, P, 9),            // measured 7 of 393
            (LowPitch, FollowHeading.Sideways, 2.5f, U, 17),           // measured 14 of 393
            (LowPitch, FollowHeading.Sideways, 3f, U, 9),              // measured 7 of 468
            (LowPitch, FollowHeading.Towards, 2.5f, U, 4),             // measured 2 of 82
            (LowPitch, FollowHeading.Towards, 3f, Q, 4),               // measured 2 of 92
        };

        /// <summary>Every UltraPerformance walk with ground past the reconstruction's reach on its measured frame, and
        /// the bound on its trail there (<see cref="TemporalPerspectiveFollowRuns.BeyondSpill"/>): acceptance 3 where
        /// it is met, else the measured excess and about a quarter more, at least 2, or one under what the resolve
        /// before the band left where that still leaves the measured value a margin.</summary>
        static readonly (float Pitch, FollowHeading Heading, float Speed, int Bound)[] PastTheReach =
        {
            (BootPitch, FollowHeading.Away, 1f, 20),                   // measured 16 of 48
            (BootPitch, FollowHeading.Away, 1.5f, 25),                 // measured 21 of 100, 26 before the band
            (BootPitch, FollowHeading.Away, 2f, 16),                   // measured 13 of 178, 29 before the band
            (BootPitch, FollowHeading.Away, 2.5f, 18),                 // measured 15 of 252, 23 before the band
            (BootPitch, FollowHeading.Away, 3f, 4),                    // measured 2 of 336, 1 before the band
            (BootPitch, FollowHeading.Sideways, 1f, 10),               // measured 8 of 68, 14 before the band
            (BootPitch, FollowHeading.Sideways, 1.5f, 22),             // measured 18 of 155, 30 before the band
            (BootPitch, FollowHeading.Sideways, 2f, 26),               // measured 21 of 242, 32 before the band
            (BootPitch, FollowHeading.Sideways, 2.5f, 28),             // measured 23 of 329, 39 before the band
            (BootPitch, FollowHeading.Sideways, 3f, 16),               // measured 13 of 418, 14 before the band
            (BootPitch, FollowHeading.Towards, 1f, 1),                 // measured 0 of 12
            (BootPitch, FollowHeading.Towards, 1.5f, 6),               // measured 4 of 48, 5 before the band
            (BootPitch, FollowHeading.Towards, 2f, 17),                // measured 14 of 84, 7 before the band
            (BootPitch, FollowHeading.Towards, 2.5f, 9),               // measured 7 of 108, 4 before the band
            (BootPitch, FollowHeading.Towards, 3f, 5),                 // measured 3 of 144
            (LowPitch, FollowHeading.Away, 1f, 1),                     // measured 0 of 70, 12 before the band
            (LowPitch, FollowHeading.Away, 1.5f, 17),                  // measured 14 of 140, 23 before the band
            (LowPitch, FollowHeading.Away, 2f, 8),                     // measured 6 of 224, 15 before the band
            (LowPitch, FollowHeading.Away, 2.5f, 7),                   // measured 5 of 368, 10 before the band
            (LowPitch, FollowHeading.Away, 3f, 2),                     // measured 0 of 480
            (LowPitch, FollowHeading.Sideways, 1f, 7),                 // measured 5 of 56, 4 before the band
            (LowPitch, FollowHeading.Sideways, 1.5f, 10),              // measured 8 of 146, 17 before the band
            (LowPitch, FollowHeading.Sideways, 2f, 13),                // measured 12 of 235, 14 before the band
            (LowPitch, FollowHeading.Sideways, 2.5f, 11),              // measured 9 of 324, 5 before the band
            (LowPitch, FollowHeading.Sideways, 3f, 5),                 // measured 3 of 414
            (LowPitch, FollowHeading.Towards, 2f, 1),                  // measured 0 of 12
            (LowPitch, FollowHeading.Towards, 2.5f, 1),                // measured 1 of 34
            (LowPitch, FollowHeading.Towards, 3f, 1),                  // measured 0 of 44
        };

        static int Bound(float pitch, FollowHeading heading, float speed, TemporalUpscale preset, int count)
        {
            foreach (var r in Residuals)
                if (r.Pitch == pitch && r.Heading == heading && r.Speed == speed && r.Preset == preset) return r.Bound;
            return Allowed(count);
        }

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

        /// <summary>
        /// The ground the avatar uncovers keeps none of its colour at Native and Quality on 64 of the 72 walks, half a
        /// display pixel a frame to three at both pitches and every heading, where walking away at the boot pitch kept
        /// 17 to 55 pixels before the avatar's own pixels stored the band mark. The other 8 stay 1 to 2 pixels over,
        /// on pixels the band drop restarted on the measured frame, read against a floor restarted on the frame the
        /// avatar uncovered them, and on the feet row behind a sideways walk
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1191">#1191</see>). Performance meets acceptance
        /// 3 on 21 walks and UltraPerformance on 5, where the reconstruction spreads the avatar's texel over the
        /// ground. Every walk over acceptance 3 holds its measured excess and about a quarter more
        /// (<see cref="Residuals"/>). By default it holds the six walks at the boot pitch walking away at half a
        /// display pixel a frame, 1 and 2, at Native and Quality. The rest of the table, 138 walks at both pitches,
        /// every heading, speed and preset, every bound in <see cref="Residuals"/> among them, runs only with
        /// <c>KE_TEMPORAL_ACCEPTANCE_TABLE=1</c>, which no workflow sets.
        /// </summary>
        [GpuFact]
        public void A_followed_avatar_leaves_no_trail_on_the_ground_it_uncovers()
        {
            var over = new List<string>();
            int walks = 0;
            foreach (var (pitch, heading, speed, preset) in TableWalks())
            {
                CrossingTrail c = runs.Run(preset, speed, pitch, heading);
                int bound = Bound(pitch, heading, speed, preset, c.Total.Checked);
                Assert.True(c.Total.Checked > 0, $"{c.Name}: the trail region measured nothing");
                if (c.Total.Excess > bound) over.Add($"{Describe(c)}, bound {bound}");
                walks++;
            }
            foreach (string line in over) output.WriteLine(line);
            output.WriteLine($"{walks - over.Count} of {walks} walks within their bounds");
            Assert.True(over.Count == 0, $"the avatar's colour stays where it left: {string.Join(". ", over)}");
        }

        /// <summary>
        /// Past the reconstruction's reach on UltraPerformance, 6 display pixels from the pixels showing the avatar,
        /// where it cannot spread the avatar's texel and only a history kept from it can leave its colour, each walk
        /// holds its bound in <see cref="PastTheReach"/>. The whole trail's bounds above mostly measure that spread:
        /// the resolve before the band cannot fail 15 of them. Here 13 of the 28 walks with ground past the reach fail
        /// on it, and the same 13 with the band drop removed. At half a display pixel a frame no ground lies past the
        /// reach on the measured frame. By default it holds the boot pitch walking away at 1.5 and 2 display pixels a
        /// frame, and the rest only with <c>KE_TEMPORAL_ACCEPTANCE_TABLE=1</c>, which no workflow sets.
        /// </summary>
        [GpuFact]
        public void A_followed_avatar_leaves_no_trail_past_the_reconstruction_at_UltraPerformance()
        {
            var over = new List<string>();
            int walks = 0;
            foreach (var (pitch, heading, speed, bound) in PastTheReach)
            {
                if (!TemporalStabilityRuns.FullTable
                    && !(pitch == BootPitch && heading == FollowHeading.Away && speed is 1.5f or 2f)) continue;
                CrossingTrail c = runs.BeyondSpill(U, speed, pitch, heading);
                Assert.True(c.Total.Checked > 0, $"{c.Name}: no ground past the reach");
                if (c.Total.Excess > bound) over.Add($"{Describe(c)}, bound {bound}");
                walks++;
            }
            foreach (string line in over) output.WriteLine(line);
            output.WriteLine($"{walks - over.Count} of {walks} walks within their bounds past the reach");
            Assert.True(over.Count == 0, $"the avatar's colour stays past the reach: {string.Join(". ", over)}");
        }

        /// <summary>Every walk's trail, with the walk in internal pixels a frame. With
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
            output.WriteLine("| Pitch | Heading | Speed | Preset | Past the reach, display px | Checked | Excess |");
            output.WriteLine("|" + string.Concat(Enumerable.Repeat(" --- |", 7)));
            foreach (var (pitch, heading, speed, preset) in TableWalks())
            {
                TrailTally b = runs.BeyondSpill(preset, speed, pitch, heading).Total;
                output.WriteLine($"| {pitch} | {heading} | {speed} | {preset} "
                    + $"| {TemporalFollowCameraRuns.SpillPixels(preset)} | {b.Checked} | {b.Excess} |");
            }
            output.WriteLine(TemporalStabilityRuns.FullTable ? "the full table"
                : $"the boot pitch walking away only. Set {TemporalStabilityRuns.TableVariable}=1 for the full table");
            output.WriteLine($"every run: {runs.Seconds:0.0} s");
        }
    }
}
