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

        static IEnumerable<(bool Perspective, TemporalUpscale Preset, float Speed, float Texels)> TableRuns()
        {
            bool full = TemporalStabilityRuns.FullTable;
            foreach (bool perspective in new[] { false, true })
                foreach (TemporalUpscale preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
                    foreach (float speed in full ? Speeds : new[] { 0.5f, 2f })
                        foreach (float texels in full ? new[] { ThreeEighths, OneQuarter } : new[] { ThreeEighths })
                            yield return (perspective, preset, speed, texels);
        }

        /// <summary>Each line's worst frame's share of its energy without the box, and its trail two or more frames
        /// after the band left it. Report only. With <c>KE_TEMPORAL_ACCEPTANCE_TABLE=1</c> it prints every walk from
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
                : $"half a pixel and 2 px, three eighths of a texel only. Set {TemporalStabilityRuns.TableVariable}=1 "
                    + "for the full table");
            output.WriteLine($"every run: {runs.Seconds:0.0} s");
        }
    }
}
