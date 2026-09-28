using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Still lines narrower than a texel on the ground a followed keyed box walks over, under the orthographic follow
    /// walk (<see cref="OrthoFollowLines"/>) and the perspective one (<see cref="PerspectiveFollowLines"/>): one across
    /// the box's trailing path and one along its edge (<see cref="TemporalFollowLinesRuns"/>). Beside the box the band
    /// of TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23 reads its own spot's history, and a line pixel leaving it must
    /// neither blink out nor take the box's colour away. HDR is off, the sharpen is at its default, and the measured
    /// values in the comments are Metal on Apple silicon.
    /// </summary>
    public sealed class TemporalFollowLinesGpuTests(TemporalFollowLinesRuns runs, ITestOutputHelper output)
        : IClassFixture<TemporalFollowLinesRuns>
    {
        internal const float ThreeEighths = 0.375f, OneQuarter = 0.25f;

        /// <summary>The share of its energy without the box a still line keeps on its worst frame under the
        /// orthographic follow walk, as the still-camera passers hold it.</summary>
        const double MinShare = 0.85;

        static readonly float[] Speeds = { 0.5f, 1f, 1.5f, 2f, 2.5f, 3f };

        internal static (LineMeasure Across, LineMeasure Along) Run(TemporalFollowLinesRuns runs, bool perspective,
            TemporalUpscale preset, float speed, float texels)
        {
            IFollowLinesWalk walk = perspective
                ? new PerspectiveFollowLines(320, 180, speed, preset, texels)
                : new OrthoFollowLines(320, 180, speed, preset, texels);
            string ground = $"{(perspective ? "perspective" : "orthographic")} {preset} {speed}";
            return runs.Run($"{ground} {texels}", walk, preset, ground);
        }

        internal static string Describe(string what, LineMeasure m) =>
            $"{what}: worst share {m.Worst:0.000} on frame {m.WorstFrame}, trail {m.Trail}, acceptance 3 allows "
            + $"{TemporalPerspectiveFollowGpuTests.Allowed(m.Trail.Checked)}, revealed {m.Revealed}";

        // Every walk of the table, or with the table switch off half a pixel and 2 at three eighths of a texel, and
        // the orthographic walk at 1.5 on Quality a quarter texel wide, the one the band drop is seen to hold.
        static IEnumerable<(bool Perspective, TemporalUpscale Preset, float Speed, float Texels)> TableRuns()
        {
            bool full = TemporalStabilityRuns.FullTable;
            foreach (bool perspective in new[] { false, true })
                foreach (TemporalUpscale preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
                    foreach (float speed in full ? Speeds : new[] { 0.5f, 2f })
                        foreach (float texels in full ? new[] { ThreeEighths, OneQuarter } : new[] { ThreeEighths })
                            yield return (perspective, preset, speed, texels);
            if (!full) yield return (false, TemporalUpscale.Quality, 1.5f, OneQuarter);
        }

        /// <summary>
        /// Under the orthographic follow walk each still line keeps at least <see cref="MinShare"/> of its energy
        /// without the box on its worst frame, across the box's trailing path and along its edge, at Native and
        /// Quality: 0.915 at worst, the line along the edge a quarter texel wide at 1.5 display pixels a frame on
        /// Quality, among the frames it lies past the band's reach and the box did not hide it. The perspective
        /// blades are printed only: along the box's edge they keep 0.77 to 0.85 at Quality from 1 to 2 display pixels
        /// a frame, since dropping the band's history as a blade leaves the band shows its raw sample and keeping it
        /// carries the box's colour under the lock
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1191">#1191</see>). By default it holds half a
        /// pixel and 2 at three eighths of a texel, where removing the band drop leaves every share over 0.96, and the
        /// walk at 1.5 on Quality a quarter texel wide, where it falls to 0.807. The rest of the table, every walk and
        /// both widths, runs only with <c>KE_TEMPORAL_ACCEPTANCE_TABLE=1</c>, which no workflow sets.
        /// </summary>
        [GpuFact]
        public void A_still_line_keeps_its_energy_beside_a_box_the_orthographic_camera_follows()
        {
            var under = new List<string>();
            foreach (var (perspective, preset, speed, texels) in TableRuns())
            {
                if (perspective) continue;
                var (across, along) = Run(runs, perspective, preset, speed, texels);
                foreach (var (name, m) in new[] { ("across", across), ("along", along) })
                {
                    string what = $"orthographic {preset} {speed} {texels} {name}";
                    Assert.False(double.IsNaN(m.Worst), $"{what}: the line covered nothing near the box");
                    if (!(m.Worst >= MinShare)) under.Add(Describe(what, m));
                }
            }
            foreach (string line in under) output.WriteLine(line);
            Assert.True(under.Count == 0, $"a still line blinked out beside the band: {string.Join(". ", under)}");
        }

        /// <summary>Each line's worst frame's share of its energy without the box, and its trail two or more frames
        /// after the band left it. With <c>KE_TEMPORAL_ACCEPTANCE_TABLE=1</c> it prints every walk from
        /// half a display pixel a frame to three and both widths.</summary>
        [GpuFact]
        public void The_follow_lines_table_prints_every_walk()
        {
            output.WriteLine("| Projection | Preset | Speed | Width texels | Line | Worst share | Frame | Checked "
                + "| Excess | Allows | Worst excess | Revealed excess of checked |");
            output.WriteLine("|" + string.Concat(Enumerable.Repeat(" --- |", 12)));
            foreach (var (perspective, preset, speed, texels) in TableRuns())
            {
                var (across, along) = Run(runs, perspective, preset, speed, texels);
                foreach (var (name, m) in new[] { ("across", across), ("along", along) })
                {
                    output.WriteLine($"| {(perspective ? "perspective" : "orthographic")} | {preset} | {speed} "
                        + $"| {texels} | {name} | {m.Worst:0.000} | {m.WorstFrame} | {m.Trail.Checked} "
                        + $"| {m.Trail.Excess} | {TemporalPerspectiveFollowGpuTests.Allowed(m.Trail.Checked)} "
                        + $"| {m.Trail.WorstExcess:0.000} | {m.Revealed.Excess} of {m.Revealed.Checked} |");
                    Assert.False(double.IsNaN(m.Worst), $"{name}: the line covered nothing near the box");
                    if (m.Worst < 0.9)
                        output.WriteLine($"    shares by frame from {TemporalFollowLinesRuns.StillFrames}: "
                            + string.Join(" ", m.Shares.Select(v => v.ToString("0.00"))));
                }
            }
            output.WriteLine(TemporalStabilityRuns.FullTable ? "the full table"
                : "half a pixel and 2 px at three eighths of a texel, and the orthographic 1.5 px on Quality a quarter "
                    + $"texel wide, only. Set {TemporalStabilityRuns.TableVariable}=1 for the full table");
            output.WriteLine($"every run: {runs.Seconds:0.0} s");
        }
    }
}
