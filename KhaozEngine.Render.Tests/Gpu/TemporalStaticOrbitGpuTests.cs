using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The band of TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23 marks a pixel beside a nearer surface that moved in
    /// the world. Under the perspective follow camera orbiting or strafing a still field
    /// (<see cref="TemporalStaticOrbitRuns"/>) nothing moved, so no pixel may store the mark, and without a stored mark
    /// no pixel drops a band history. The world-motion test reads the static travel of the dilated texel, which on
    /// still content is the motion target's and the reprojection's own error, printed here with its distribution. The
    /// measured values in the comments are Metal on Apple silicon.
    /// </summary>
    public sealed class TemporalStaticOrbitGpuTests(TemporalStaticOrbitRuns runs, ITestOutputHelper output)
        : IClassFixture<TemporalStaticOrbitRuns>
    {
        const int W = 2560, H = 1440, WideW = 3840, WideH = 2160;

        static readonly StaticPath[] Paths =
            { StaticPath.FastOrbit, StaticPath.SlowOrbit, StaticPath.Strafe, StaticPath.Creep };

        const float Boot = TemporalStaticOrbitRuns.BootPitch, Grazing = TemporalStaticOrbitRuns.GrazingPitch,
            Steep = TemporalStaticOrbitRuns.SteepPitch, Near = GroundStage.Distance;

        // Every run of the table, or with the table switch off the far fast orbit, strafe and creep at Quality and the
        // far fast orbit at Quality from the grazing pitch 12 and 30 metres away.
        static IEnumerable<(StaticPath Path, bool Far, TemporalUpscale Preset, int W, int H, float Pitch,
            float Distance)> TableRuns()
        {
            if (!TemporalStabilityRuns.FullTable)
            {
                yield return (StaticPath.FastOrbit, true, TemporalUpscale.Quality, W, H, Boot, Near);
                yield return (StaticPath.Strafe, true, TemporalUpscale.Quality, W, H, Boot, Near);
                yield return (StaticPath.Creep, true, TemporalUpscale.Quality, W, H, Boot, Near);
                yield return (StaticPath.FastOrbit, true, TemporalUpscale.Quality, W, H, Grazing, Near);
                yield return (StaticPath.FastOrbit, true, TemporalUpscale.Quality, W, H, Grazing,
                    TemporalStaticOrbitRuns.MaxZoom);
                yield break;
            }
            var presets = new[] { TemporalUpscale.Native, TemporalUpscale.Quality };
            foreach (float distance in new[] { Near, TemporalStaticOrbitRuns.FarZoom, TemporalStaticOrbitRuns.MaxZoom })
                foreach (float pitch in new[] { Boot, Grazing, Steep })
                    foreach (StaticPath path in Paths)
                        foreach (bool far in new[] { false, true })
                            foreach (TemporalUpscale preset in presets)
                                yield return (path, far, preset, W, H, pitch, distance);
            foreach (float pitch in new[] { Boot, Grazing })
                foreach (StaticPath path in Paths)
                    yield return (path, true, TemporalUpscale.Native, WideW, WideH, pitch, Near);
        }

        /// <summary>
        /// No still surface stores the band mark on any frame the table renders, warm or measured, orbiting fast or
        /// slowly, strafing or creeping, near the origin or across a render-origin step, from the boot pitch, the
        /// grazing one or a steep one, 12, 22 or 30 metres away, and at 3840 by 2160 from the boot and grazing
        /// pitches, and without a stored mark no pixel drops a band history. The world-motion test is in metres
        /// (WorldMotionMetres, 1 mm, and a share of the camera's travel), where a fixed 0.05 internal pixels had no
        /// margin at 3840 wide (0.075). The worst
        /// static travel measured from the boot pitch is 0.211, 0.374 and 0.466 mm at 12, 22 and 30 metres, from the
        /// grazing pitch of 0.26 radians 0.508 mm 12 metres away, at a depth of 10.7 m, 0.778 and 1.23 mm, and from
        /// the steep pitch of 1.36 at most 0.058 mm, each on the fast orbit. So 1 mm is 1.3 times the worst to 22
        /// metres, and 30 metres away from the grazing pitch the fast orbit, its eye moving 1.52 metres a frame, read
        /// up to 0.24 percent of its texels as moved in the world on Metal, 1.23 mm at most, and on WARP a crate's edge
        /// travelled 1.44 mm beside ground moving more on screen and stored the mark. The static travel grows with the
        /// camera's travel, so the test adds WorldMotionEyeFraction of it to the millimetre, 6.9 mm on that orbit. By
        /// default it holds the far fast orbit, strafe and creep at Quality and the far fast
        /// orbit from the grazing pitch 12 and 30 metres away, and with <c>KE_TEMPORAL_ACCEPTANCE_TABLE=1</c> every run
        /// of the table.
        /// </summary>
        [GpuFact]
        public void No_still_surface_stores_the_band_mark_under_an_orbit_a_strafe_or_a_creep()
        {
            bool full = TemporalStabilityRuns.FullTable;
            foreach (var (path, far, preset, w, h, pitch, distance) in TableRuns())
            {
                StaticRun r = runs.Run(path, far, preset, w, h, full, pitch, distance);
                output.WriteLine($"{r.Name}: band marks {r.BandMarks.Sum()} over the measured frames, "
                    + $"{r.WarmBandMarks} over the warm ones");
                Assert.True(r.BandMarks.Sum() == 0 && r.WarmBandMarks == 0,
                    $"{r.Name}: a still surface stored the band mark, so a pixel can drop a band history");
            }
        }

        /// <summary>Band and moved marks per run, the eye's nearest pass to a tower, and with
        /// <c>KE_TEMPORAL_ACCEPTANCE_TABLE=1</c> every path, near and far, Native and Quality at 2560 by 1440 from
        /// each pitch and distance, and Native at 3840 by 2160 from the boot and grazing pitches, with the static
        /// travel's distribution and the texels past the world-motion test. Report only.</summary>
        [GpuFact]
        public void The_still_field_table_prints_the_band_marks_and_the_static_travel()
        {
            bool full = TemporalStabilityRuns.FullTable;
            output.WriteLine("| Run | Band marks (most in a frame, total) | Moved marks (most) | Nearest tower m "
                + "| Travel |");
            output.WriteLine("| --- | --- | --- | --- | --- |");
            foreach (var (path, far, preset, w, h, pitch, distance) in TableRuns())
            {
                StaticRun r = runs.Run(path, far, preset, w, h, full, pitch, distance);
                output.WriteLine($"| {r.Name} | {r.BandMarks.Max()}, {r.BandMarks.Sum()} | {r.MovedMarks.Max()} "
                    + $"| {r.NearestTower:0.00} | {r.Travel?.ToString() ?? "not read"} |");
                Assert.True(r.NearestTower < 1f, $"{r.Name}: the eye passed no tower within a metre");
                if (r.Travel is StaticTravel t)
                    output.WriteLine($"{r.Name}, excess by frame (* a render-origin step): " + string.Join(" ",
                        t.FrameMaxExcess.Select((e, i) => $"{e:0.0000}{(r.OriginSteps[i] ? "*" : "")}")));
            }
            output.WriteLine(full ? "the full table"
                : "the far fast orbit, strafe and creep at Quality, and the far fast orbit from the grazing pitch 12 "
                    + $"and 30 metres away, only. Set {TemporalStabilityRuns.TableVariable}=1 for the full table");
            output.WriteLine($"every run: {runs.Seconds:0.0} s");
        }
    }
}
