using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed partial class TemporalFollowPhaseGpuTests
    {
        const float ThreeEighths = TemporalFollowLinesGpuTests.ThreeEighths,
            OneQuarter = TemporalFollowLinesGpuTests.OneQuarter;

        /// <summary>Each still line's worst share of its energy under the orthographic follow walk
        /// (<see cref="TemporalFollowLinesGpuTests"/>) over its start phases, in thousandths, against the fact's 0.85.
        /// Every phase for the lines a quarter texel wide at 1 to 2 display pixels a frame on Quality, and four phases
        /// for the rest. By default the walk at 1.5 on Quality a quarter texel wide and at 2 three eighths
        /// wide.</summary>
        [GpuFact]
        public void The_follow_lines_hold_their_share_from_every_start_phase()
        {
            double before = lines.Seconds;
            Header(output, "still lines beside the orthographic follow walk over jitter start phases (#1207), worst "
                + "share in thousandths");
            var sweeps = new List<PhaseSweep>();
            foreach (TemporalUpscale preset in new[] { N, Q })
                foreach (float speed in Speeds)
                    foreach (float texels in new[] { ThreeEighths, OneQuarter })
                    {
                        if (!TemporalStabilityRuns.FullTable
                            && !(preset == Q && (speed == 1.5f && texels == OneQuarter
                                || speed == 2f && texels == ThreeEighths))) continue;
                        int[] phases = Phases(preset, preset == Q && texels == OneQuarter && speed is >= 1f and <= 2f);
                        var measures = phases.Select(p => TemporalFollowLinesGpuTests.Run(lines, false, preset, speed,
                            texels, p)).ToArray();
                        foreach (var (name, pick) in new (string, Func<(LineMeasure, LineMeasure), LineMeasure>)[]
                            { ("across", m => m.Item1), ("along", m => m.Item2) })
                        {
                            int[] shares = measures.Select(m =>
                            {
                                double worst = pick(m).Worst;
                                Assert.False(double.IsNaN(worst), $"{preset} {speed} {texels} {name}: no line pixels");
                                return (int)Math.Floor(worst * 1000);
                            }).ToArray();
                            sweeps.Add(new PhaseSweep($"{preset} {speed} {texels} {name}", phases, shares,
                                phases.Select(_ => (int)Math.Round(TemporalFollowLinesGpuTests.MinShare * 1000))
                                    .ToArray(), AtLeast: true));
                            output.WriteLine(sweeps[^1].Row("lines"));
                        }
                    }
            Footer(output, sweeps, lines.Seconds - before);
            Hold(sweeps, "the follow lines");
        }

        /// <summary>The followed avatar's stop, damped stop and reversal (<see cref="TemporalFollowStopGpuTests"/>)
        /// over their start phases: every walk that fact holds, each set's worst frame over its control past the lead,
        /// as a share of the control's error in thousandths, against the share the fact allows. Every phase for the
        /// orthographic walk and the boot pitch away on Quality when they stop, and four phases for the rest. By
        /// default the orthographic stop and reversal on Quality.</summary>
        [GpuFact]
        public void The_follow_stop_holds_its_bounds_from_every_start_phase()
        {
            double before = stops.Seconds;
            Header(output, "the followed avatar's stop over jitter start phases (#1207), regression bounds, worst "
                + "margin over the control in thousandths of its error");
            var sweeps = new List<PhaseSweep>();
            foreach (var (label, preset, walk) in StopWalks())
            {
                if (!TemporalStabilityRuns.FullTable && label is not ("ortho Stop Quality" or "ortho Reversal Quality"))
                    continue;
                StopRun first = walk(0);
                bool every = preset == Q && first.Ending == FollowEnding.Stop && first.Surround == StopSurround.Ground
                    && !label.Contains("Sideways", StringComparison.Ordinal);
                int[] phases = Phases(preset, every);
                foreach (string set in new[] { "whole", "inner" })
                {
                    var values = new int[phases.Length];
                    for (int i = 0; i < phases.Length; i++)
                    {
                        StopRun r = walk(phases[i]);
                        StopRun control = stops.Control(r);
                        StopRun? without = r.Surround == StopSurround.Passer ? Without(label, phases[i]) : null;
                        StopMeasure m = set == "whole" ? r.Whole : r.Inner, c = set == "whole" ? control.Whole
                            : control.Inner;
                        StopMeasure? w = without is null ? null : set == "whole" ? without.Whole : without.Inner;
                        StopMeasure? wc = without is null ? null
                            : set == "whole" ? stops.Control(without).Whole : stops.Control(without).Inner;
                        double lead = TemporalFollowStopGpuTests.Lead(m, c, w, wc);
                        values[i] = (int)Math.Ceiling(1000 * TemporalFollowStopGpuTests.Worst(r, set, m, c, lead,
                            new List<string>()));
                    }
                    int bound = (int)Math.Round(1000 * TemporalFollowStopGpuTests.Share(first, set));
                    sweeps.Add(new PhaseSweep($"{label}, {set}", phases, values, phases.Select(_ => bound).ToArray()));
                    output.WriteLine(sweeps[^1].Row("stop"));
                }
            }
            Footer(output, sweeps, stops.Seconds - before);
            Hold(sweeps, "the follow stop");
        }

        // The same walk as the passer's, without the passer.
        StopRun Without(string label, int phase)
        {
            foreach (var (other, _, walk) in StopWalks())
                if (other == label.Replace(TemporalFollowStopRuns.PasserSuffix, "", StringComparison.Ordinal))
                    return walk(phase);
            throw new InvalidOperationException($"{label}: no walk without the passer");
        }

        // The walks of the stop fact, each by its start phase: the orthographic walk and the boot pitch away and
        // sideways at 1 display pixel a frame over the ground or wall, and the orthographic walk and the boot pitch
        // away over the clear colour and beside a passer, every ending but the passer's damped stop, at Native and
        // Quality.
        IEnumerable<(string Label, TemporalUpscale Preset, Func<int, StopRun> Walk)> StopWalks()
        {
            var surrounds = new[] { StopSurround.Ground, StopSurround.ClearColour, StopSurround.Passer };
            var endings = new[] { FollowEnding.Stop, FollowEnding.DampedStop, FollowEnding.Reversal };
            foreach (StopSurround surround in surrounds)
                foreach (FollowEnding ending in endings)
                    foreach (TemporalUpscale preset in new[] { N, Q })
                    {
                        if (surround == StopSurround.Passer && ending == FollowEnding.DampedStop) continue;
                        string suffix = surround switch
                        {
                            StopSurround.ClearColour => ", over the clear colour",
                            StopSurround.Passer => TemporalFollowStopRuns.PasserSuffix,
                            _ => "",
                        };
                        yield return ($"ortho {ending} {preset}{suffix}", preset,
                            p => stops.Orthographic(ending, preset, 1f, surround, p));
                        yield return ($"boot Away {ending} {preset}{suffix}", preset,
                            p => stops.Perspective(ending, preset, 1f, B, A, surround, p));
                        if (surround == StopSurround.Ground)
                            yield return ($"boot Sideways {ending} {preset}", preset,
                                p => stops.Perspective(ending, preset, 1f, B, S, surround, p));
                    }
        }
    }
}
