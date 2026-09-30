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

        // Stands for acceptance 3 in PastTheReach.
        const int Acceptance3 = -1;

        /// <summary>Every walk the resolve leaves over acceptance 3 near the edge, and its bound: a regression bound
        /// over the jitter's start phases (<see cref="TemporalFollowPhaseGpuTests"/>). Within the reconstruction's
        /// reach of the avatar a pixel keeps one history, which either holds its edge's anti-aliasing in place or shows
        /// the ground passing under it, and the jitter decides which
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1207">#1207</see>). A history for the edge's
        /// coverage apart from the ground's is the fix
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1191">#1191</see>). The worst over the phases
        /// and about a quarter more, at least 2, or the bound the walk held at its first phase where every phase
        /// already met it. The comment gives the worst, the region's pixels, the phases run and the value at the
        /// first phase. Every other walk meets acceptance 3 from every phase measured.</summary>
        static readonly (float Pitch, FollowHeading Heading, float Speed, TemporalUpscale Preset, int Bound)[]
            Residuals =
        {
            (LowPitch, FollowHeading.Sideways, 0.5f, N, 4),            // worst 2 of 71 over 8 phases, phase 0 2
            (LowPitch, FollowHeading.Sideways, 1f, N, 4),              // worst 2 of 160 over 8 phases, phase 0 0
            (BootPitch, FollowHeading.Away, 2f, Q, 4),                 // worst 2 of 222 over 4 phases, phase 0 0
            (BootPitch, FollowHeading.Sideways, 1f, Q, 4),             // worst 2 of 160 over 4 phases, phase 0 0
            (BootPitch, FollowHeading.Sideways, 2f, Q, 4),             // worst 3 of 311 over 18 phases, phase 0 2
            (BootPitch, FollowHeading.Sideways, 2.5f, Q, 5),           // worst 3 of 377 over 4 phases, phase 0 1
            (BootPitch, FollowHeading.Towards, 1f, Q, 4),              // worst 2 of 60 over 18 phases, phase 0 2
            (LowPitch, FollowHeading.Away, 1.5f, Q, 4),                // worst 2 of 184 over 18 phases, phase 0 0
            (LowPitch, FollowHeading.Away, 2.5f, Q, 5),                // worst 5 of 436 over 4 phases, phase 0 5
            (LowPitch, FollowHeading.Sideways, 0.5f, Q, 9),            // worst 7 of 71 over 4 phases, phase 0 2
            (LowPitch, FollowHeading.Sideways, 1f, Q, 5),              // worst 3 of 160 over 4 phases, phase 0 2
            (LowPitch, FollowHeading.Sideways, 1.5f, Q, 5),            // worst 3 of 226 over 4 phases, phase 0 2
            (LowPitch, FollowHeading.Sideways, 2f, Q, 4),              // worst 2 of 313 over 4 phases, phase 0 1
            (LowPitch, FollowHeading.Towards, 3f, Q, 4),               // worst 2 of 92 over 4 phases, phase 0 2
            (BootPitch, FollowHeading.Away, 1.5f, P, 4),               // worst 2 of 144 over 4 phases, phase 0 0
            (BootPitch, FollowHeading.Away, 3f, P, 4),                 // worst 2 of 368 over 4 phases, phase 0 2
            (BootPitch, FollowHeading.Sideways, 0.5f, P, 6),           // worst 4 of 69 over 4 phases, phase 0 4
            (BootPitch, FollowHeading.Sideways, 1f, P, 8),             // worst 6 of 160 over 4 phases, phase 0 3
            (BootPitch, FollowHeading.Sideways, 2f, P, 10),            // worst 8 of 311 over 4 phases, phase 0 3
            (BootPitch, FollowHeading.Sideways, 2.5f, P, 13),          // worst 11 of 377 over 32 phases, phase 0 2
            (BootPitch, FollowHeading.Towards, 1f, P, 12),             // worst 10 of 60 over 32 phases, phase 0 4
            (BootPitch, FollowHeading.Towards, 1.5f, P, 10),           // worst 8 of 96 over 32 phases, phase 0 0
            (BootPitch, FollowHeading.Towards, 2f, P, 13),             // worst 11 of 132 over 32 phases, phase 0 0
            (BootPitch, FollowHeading.Towards, 2.5f, P, 12),           // worst 10 of 144 over 32 phases, phase 0 1
            (BootPitch, FollowHeading.Towards, 3f, P, 5),              // worst 4 of 180 over 4 phases, phase 0 3
            (LowPitch, FollowHeading.Away, 0.5f, P, 12),               // worst 10 of 48 over 32 phases, phase 0 0
            (LowPitch, FollowHeading.Away, 1f, P, 4),                  // worst 2 of 126 over 4 phases, phase 0 0
            (LowPitch, FollowHeading.Away, 1.5f, P, 5),                // worst 3 of 184 over 32 phases, phase 0 1
            (LowPitch, FollowHeading.Away, 2f, P, 5),                  // worst 5 of 268 over 4 phases, phase 0 2
            (LowPitch, FollowHeading.Away, 2.5f, P, 11),               // worst 9 of 436 over 4 phases, phase 0 3
            (LowPitch, FollowHeading.Away, 3f, P, 8),                  // worst 6 of 548 over 4 phases, phase 0 5
            (LowPitch, FollowHeading.Sideways, 0.5f, P, 4),            // worst 4 of 71 over 4 phases, phase 0 1
            (LowPitch, FollowHeading.Sideways, 1f, P, 8),              // worst 6 of 160 over 4 phases, phase 0 6
            (LowPitch, FollowHeading.Sideways, 1.5f, P, 4),            // worst 4 of 226 over 4 phases, phase 0 2
            (LowPitch, FollowHeading.Sideways, 2f, P, 9),              // worst 7 of 313 over 4 phases, phase 0 6
            (LowPitch, FollowHeading.Sideways, 2.5f, P, 9),            // worst 9 of 393 over 4 phases, phase 0 6
            (LowPitch, FollowHeading.Sideways, 3f, P, 9),              // worst 7 of 468 over 32 phases, phase 0 0
            (LowPitch, FollowHeading.Towards, 2f, P, 5),               // worst 3 of 60 over 4 phases, phase 0 0
            (BootPitch, FollowHeading.Away, 0.5f, U, 28),              // worst 24 of 36 over 4 phases, phase 0 22
            (BootPitch, FollowHeading.Away, 1f, U, 58),                // worst 47 of 96 over 4 phases, phase 0 37
            (BootPitch, FollowHeading.Away, 1.5f, U, 47),              // worst 41 of 144 over 4 phases, phase 0 41
            (BootPitch, FollowHeading.Away, 2f, U, 47),                // worst 38 of 222 over 72 phases, phase 0 18
            (BootPitch, FollowHeading.Away, 2.5f, U, 21),              // worst 20 of 284 over 4 phases, phase 0 16
            (BootPitch, FollowHeading.Away, 3f, U, 5),                 // worst 3 of 368 over 4 phases, phase 0 3
            (BootPitch, FollowHeading.Sideways, 0.5f, U, 25),          // worst 20 of 69 over 4 phases, phase 0 10
            (BootPitch, FollowHeading.Sideways, 1f, U, 43),            // worst 41 of 160 over 4 phases, phase 0 35
            (BootPitch, FollowHeading.Sideways, 1.5f, U, 40),          // worst 32 of 226 over 4 phases, phase 0 26
            (BootPitch, FollowHeading.Sideways, 2f, U, 42),            // worst 41 of 311 over 4 phases, phase 0 35
            (BootPitch, FollowHeading.Sideways, 2.5f, U, 36),          // worst 29 of 377 over 4 phases, phase 0 29
            (BootPitch, FollowHeading.Sideways, 3f, U, 28),            // worst 24 of 464 over 4 phases, phase 0 22
            (BootPitch, FollowHeading.Towards, 0.5f, U, 9),            // worst 7 of 24 over 4 phases, phase 0 4
            (BootPitch, FollowHeading.Towards, 1f, U, 25),             // worst 20 of 60 over 72 phases, phase 0 6
            (BootPitch, FollowHeading.Towards, 1.5f, U, 21),           // worst 17 of 96 over 4 phases, phase 0 10
            (BootPitch, FollowHeading.Towards, 2f, U, 22),             // worst 18 of 132 over 4 phases, phase 0 18
            (BootPitch, FollowHeading.Towards, 2.5f, U, 12),           // worst 10 of 144 over 4 phases, phase 0 5
            (BootPitch, FollowHeading.Towards, 3f, U, 13),             // worst 11 of 180 over 4 phases, phase 0 5
            (LowPitch, FollowHeading.Away, 0.5f, U, 10),               // worst 8 of 48 over 4 phases, phase 0 3
            (LowPitch, FollowHeading.Away, 1f, U, 8),                  // worst 6 of 126 over 4 phases, phase 0 2
            (LowPitch, FollowHeading.Away, 1.5f, U, 17),               // worst 14 of 184 over 4 phases, phase 0 9
            (LowPitch, FollowHeading.Away, 2f, U, 4),                  // worst 2 of 268 over 4 phases, phase 0 0
            (LowPitch, FollowHeading.Away, 2.5f, U, 17),               // worst 14 of 436 over 4 phases, phase 0 14
            (LowPitch, FollowHeading.Away, 3f, U, 9),                  // worst 8 of 548 over 4 phases, phase 0 7
            (LowPitch, FollowHeading.Sideways, 0.5f, U, 20),           // worst 17 of 71 over 4 phases, phase 0 17
            (LowPitch, FollowHeading.Sideways, 1f, U, 33),             // worst 32 of 160 over 4 phases, phase 0 24
            (LowPitch, FollowHeading.Sideways, 1.5f, U, 26),           // worst 21 of 226 over 72 phases, phase 0 14
            (LowPitch, FollowHeading.Sideways, 2f, U, 28),             // worst 23 of 313 over 4 phases, phase 0 16
            (LowPitch, FollowHeading.Sideways, 2.5f, U, 25),           // worst 20 of 393 over 4 phases, phase 0 14
            (LowPitch, FollowHeading.Sideways, 3f, U, 9),              // worst 9 of 468 over 4 phases, phase 0 8
            (LowPitch, FollowHeading.Towards, 1.5f, U, 4),             // worst 2 of 48 over 4 phases, phase 0 1
            (LowPitch, FollowHeading.Towards, 2.5f, U, 4),             // worst 2 of 82 over 4 phases, phase 0 2
            (LowPitch, FollowHeading.Towards, 3f, U, 4),               // worst 2 of 92 over 4 phases, phase 0 1
        };

        /// <summary>The bound on each walk's trail past the reconstruction's reach
        /// (<see cref="TemporalPerspectiveFollowRuns.BeyondSpill"/>), set as <see cref="Residuals"/> sets its bounds:
        /// every UltraPerformance walk with ground past the reach on its measured frame, and the Quality and
        /// Performance walks over acceptance 3 there. Every other Quality and Performance walk with ground past the
        /// reach meets acceptance 3 there from every phase measured.</summary>
        static readonly (float Pitch, FollowHeading Heading, float Speed, TemporalUpscale Preset, int Bound)[]
            PastTheReach =
        {
            (BootPitch, FollowHeading.Away, 2f, Q, 4),                 // worst 2 of 220 over 4 phases, phase 0 0
            (BootPitch, FollowHeading.Sideways, 1f, Q, 4),             // worst 2 of 137 over 4 phases, phase 0 0
            (BootPitch, FollowHeading.Sideways, 2f, Q, 5),             // worst 3 of 311 over 18 phases, phase 0 2
            (BootPitch, FollowHeading.Sideways, 2.5f, Q, 5),           // worst 3 of 377 over 4 phases, phase 0 1
            (BootPitch, FollowHeading.Towards, 1f, Q, 4),              // worst 2 of 48 over 18 phases, phase 0 0
            (LowPitch, FollowHeading.Sideways, 1f, Q, 4),              // worst 2 of 134 over 4 phases, phase 0 2
            (LowPitch, FollowHeading.Sideways, 1.5f, Q, 5),            // worst 3 of 224 over 4 phases, phase 0 2
            (LowPitch, FollowHeading.Sideways, 2f, Q, 4),              // worst 2 of 313 over 4 phases, phase 0 1
            (BootPitch, FollowHeading.Away, 1.5f, P, 4),               // worst 2 of 128 over 4 phases, phase 0 0
            (BootPitch, FollowHeading.Away, 3f, P, 4),                 // worst 2 of 364 over 4 phases, phase 0 2
            (BootPitch, FollowHeading.Sideways, 2f, P, 5),             // worst 3 of 288 over 4 phases, phase 0 0
            (BootPitch, FollowHeading.Sideways, 2.5f, P, 12),          // worst 10 of 375 over 32 phases, phase 0 2
            (BootPitch, FollowHeading.Towards, 1f, P, 9),              // worst 7 of 36 over 32 phases, phase 0 0
            (BootPitch, FollowHeading.Towards, 1.5f, P, 6),            // worst 4 of 72 over 32 phases, phase 0 0
            (BootPitch, FollowHeading.Towards, 2f, P, 6),              // worst 4 of 108 over 32 phases, phase 0 0
            (BootPitch, FollowHeading.Towards, 2.5f, P, 10),           // worst 8 of 132 over 32 phases, phase 0 0
            (BootPitch, FollowHeading.Towards, 3f, P, 4),              // worst 2 of 168 over 4 phases, phase 0 2
            (LowPitch, FollowHeading.Away, 1.5f, P, 4),                // worst 2 of 168 over 32 phases, phase 0 1
            (LowPitch, FollowHeading.Sideways, 0.5f, P, 4),            // worst 2 of 19 over 4 phases, phase 0 0
            (LowPitch, FollowHeading.Sideways, 1f, P, 5),              // worst 3 of 108 over 4 phases, phase 0 3
            (LowPitch, FollowHeading.Sideways, 2f, P, 5),              // worst 3 of 287 over 4 phases, phase 0 3
            (LowPitch, FollowHeading.Sideways, 2.5f, P, 9),            // worst 7 of 376 over 4 phases, phase 0 4
            (LowPitch, FollowHeading.Sideways, 3f, P, 8),              // worst 6 of 466 over 32 phases, phase 0 0
            (BootPitch, FollowHeading.Away, 1f, U, 26),                // worst 21 of 48 over 4 phases, phase 0 16
            (BootPitch, FollowHeading.Away, 1.5f, U, 25),              // worst 22 of 100 over 4 phases, phase 0 20
            (BootPitch, FollowHeading.Away, 2f, U, 30),                // worst 24 of 178 over 72 phases, phase 0 13
            (BootPitch, FollowHeading.Away, 2.5f, U, 18),              // worst 17 of 252 over 4 phases, phase 0 14
            (BootPitch, FollowHeading.Away, 3f, U, Acceptance3),       // worst 1 of 336 over 4 phases, phase 0 1
            (BootPitch, FollowHeading.Sideways, 1f, U, 21),            // worst 17 of 68 over 4 phases, phase 0 8
            (BootPitch, FollowHeading.Sideways, 1.5f, U, 22),          // worst 18 of 155 over 4 phases, phase 0 18
            (BootPitch, FollowHeading.Sideways, 2f, U, 33),            // worst 27 of 242 over 4 phases, phase 0 21
            (BootPitch, FollowHeading.Sideways, 2.5f, U, 28),          // worst 25 of 329 over 4 phases, phase 0 23
            (BootPitch, FollowHeading.Sideways, 3f, U, 15),            // worst 12 of 418 over 4 phases, phase 0 12
            (BootPitch, FollowHeading.Towards, 1f, U, 9),              // worst 7 of 12 over 72 phases, phase 0 0
            (BootPitch, FollowHeading.Towards, 1.5f, U, 13),           // worst 11 of 48 over 4 phases, phase 0 5
            (BootPitch, FollowHeading.Towards, 2f, U, 17),             // worst 14 of 84 over 4 phases, phase 0 14
            (BootPitch, FollowHeading.Towards, 2.5f, U, 9),            // worst 7 of 108 over 4 phases, phase 0 5
            (BootPitch, FollowHeading.Towards, 3f, U, 9),              // worst 7 of 144 over 4 phases, phase 0 1
            (LowPitch, FollowHeading.Away, 1f, U, Acceptance3),        // worst 0 of 70 over 4 phases, phase 0 0
            (LowPitch, FollowHeading.Away, 1.5f, U, 11),               // worst 9 of 140 over 4 phases, phase 0 4
            (LowPitch, FollowHeading.Away, 2f, U, Acceptance3),        // worst 0 of 224 over 4 phases, phase 0 0
            (LowPitch, FollowHeading.Away, 2.5f, U, 6),                // worst 4 of 368 over 4 phases, phase 0 4
            (LowPitch, FollowHeading.Away, 3f, U, Acceptance3),        // worst 2 of 480 over 4 phases, phase 0 0
            (LowPitch, FollowHeading.Sideways, 1f, U, 13),             // worst 11 of 56 over 4 phases, phase 0 4
            (LowPitch, FollowHeading.Sideways, 1.5f, U, 17),           // worst 14 of 146 over 72 phases, phase 0 9
            (LowPitch, FollowHeading.Sideways, 2f, U, 17),             // worst 14 of 235 over 4 phases, phase 0 10
            (LowPitch, FollowHeading.Sideways, 2.5f, U, 18),           // worst 15 of 324 over 4 phases, phase 0 10
            (LowPitch, FollowHeading.Sideways, 3f, U, 8),              // worst 6 of 414 over 4 phases, phase 0 4
            (LowPitch, FollowHeading.Towards, 2f, U, Acceptance3),     // worst 0 of 12 over 4 phases, phase 0 0
            (LowPitch, FollowHeading.Towards, 2.5f, U, Acceptance3),   // worst 1 of 34 over 4 phases, phase 0 1
            (LowPitch, FollowHeading.Towards, 3f, U, Acceptance3),     // worst 0 of 44 over 4 phases, phase 0 0
        };

        internal static int Bound(float pitch, FollowHeading heading, float speed, TemporalUpscale preset, int count)
        {
            foreach (var r in Residuals)
                if (r.Pitch == pitch && r.Heading == heading && r.Speed == speed && r.Preset == preset) return r.Bound;
            return Allowed(count);
        }

        /// <summary>The past-the-reach fact's bound on a walk over a region of <paramref name="count"/> pixels
        /// (<see cref="PastTheReach"/>): acceptance 3 at Quality and Performance where the table holds none, and null
        /// at Native and on an UltraPerformance walk the table holds none for.</summary>
        internal static int? ReachBound(float pitch, FollowHeading heading, float speed, TemporalUpscale preset,
            int count)
        {
            foreach (var r in PastTheReach)
                if (r.Pitch == pitch && r.Heading == heading && r.Speed == speed && r.Preset == preset)
                    return r.Bound == Acceptance3 ? Allowed(count) : r.Bound;
            return preset is Q or P ? Allowed(count) : null;
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
        /// The ground the avatar uncovers keeps no more of its colour than acceptance 3 or its bound in
        /// <see cref="Residuals"/> allows, a regression bound that holds from every start phase of the jitter
        /// measured. From every phase, 34 of the 36 walks at Native meet acceptance 3, half a display pixel a frame to
        /// three at both pitches and every heading, where walking away at the boot pitch kept 17 to 55 pixels before
        /// the avatar's own pixels stored the band mark, and 24 at Quality, 12 at Performance and 3 at
        /// UltraPerformance, where the reconstruction spreads the avatar's texel over the ground. The others read up
        /// to 2 at Native, 7 at Quality, 11 at Performance and 47 at UltraPerformance on their worst phase: pixels the
        /// band drop restarted on the measured frame, the feet row behind a sideways walk, and the one history a pixel
        /// beside the edge keeps (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1207">#1207</see>). By
        /// default it holds the six walks at the boot pitch walking away at half a display pixel a frame, 1 and 2, at
        /// Native and Quality. The rest of the table, 138 walks at both pitches, every heading, speed and preset, every
        /// bound in <see cref="Residuals"/> among them, runs only with <c>KE_TEMPORAL_ACCEPTANCE_TABLE=1</c>, which no
        /// workflow sets.
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
        /// Past the reconstruction's reach, 3 display pixels from the pixels showing the avatar at Quality, 4 at
        /// Performance and 6 at UltraPerformance, where it cannot spread the avatar's texel and only a history kept
        /// from it can leave its colour, each walk holds acceptance 3 or its bound in <see cref="PastTheReach"/> from
        /// every start phase measured: 27 of the 35 walks with ground there at Quality, 18 of 33 at Performance and 7
        /// of 28 at UltraPerformance meet acceptance 3, and the others read up to 3, 10 and 27 on their worst phase.
        /// The whole trail's bounds at UltraPerformance mostly measure that spread: the resolve before the band cannot
        /// fail 15 of them. Here, on the first phase, 13 of the 28 walks at UltraPerformance failed on it, and the
        /// same 13 with the band drop removed. At half a display pixel a frame no ground lies past the reach at
        /// UltraPerformance. By default it holds the boot pitch walking away at 1 and 2 display pixels a frame on
        /// Quality and at 1.5 and 2 on UltraPerformance, and the rest only with
        /// <c>KE_TEMPORAL_ACCEPTANCE_TABLE=1</c>, which no workflow sets.
        /// </summary>
        [GpuFact]
        public void A_followed_avatar_leaves_no_trail_past_the_reconstructions_reach()
        {
            var over = new List<string>();
            int walks = 0;
            foreach (var (pitch, heading, speed, preset) in ReachWalks())
            {
                CrossingTrail c = runs.BeyondSpill(preset, speed, pitch, heading);
                int bound = ReachBound(pitch, heading, speed, preset, c.Total.Checked)!.Value;
                if (c.Total.Checked == 0 && preset != U) continue;
                Assert.True(c.Total.Checked > 0, $"{c.Name}: no ground past the reach");
                if (c.Total.Excess > bound) over.Add($"{Describe(c)}, bound {bound}");
                walks++;
            }
            foreach (string line in over) output.WriteLine(line);
            output.WriteLine($"{walks - over.Count} of {walks} walks within their bounds past the reach");
            Assert.True(over.Count == 0, $"the avatar's colour stays past the reach: {string.Join(". ", over)}");
        }

        // The walks the past-the-reach fact holds: at Quality and Performance every walk of the table, where a walk
        // with no ground past the reach is left out, and at UltraPerformance each walk in PastTheReach. With the table
        // switch off, the boot pitch walking away at 1 and 2 display pixels a frame on Quality and at 1.5 and 2 on
        // UltraPerformance.
        static IEnumerable<(float Pitch, FollowHeading Heading, float Speed, TemporalUpscale Preset)> ReachWalks()
        {
            bool full = TemporalStabilityRuns.FullTable;
            foreach (TemporalUpscale preset in new[] { Q, P })
                foreach (float pitch in new[] { BootPitch, LowPitch })
                    foreach (FollowHeading heading in Headings)
                        foreach (float speed in Speeds)
                            if (full || preset == Q && pitch == BootPitch && heading == FollowHeading.Away
                                && speed is 1f or 2f)
                                yield return (pitch, heading, speed, preset);
            foreach (var r in PastTheReach)
                if (r.Preset == U && (full || r.Pitch == BootPitch && r.Heading == FollowHeading.Away
                    && r.Speed is 1.5f or 2f))
                    yield return (r.Pitch, r.Heading, r.Speed, r.Preset);
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
