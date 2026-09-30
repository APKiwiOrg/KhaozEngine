using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23 when a followed avatar stops or turns back
    /// (<see cref="TemporalFollowStopRuns"/>): under the perspective follow camera and the orthographic one, the box
    /// stops with the camera, stops while a damped camera eases on, or slows and walks back. On the turn frame its
    /// travel in the world is zero. Each row reads the avatar's own pixels, or the ring beside them, their mean luma
    /// error against the 4x reference on the frame before the turn, the turn frame and the 15 after, and the flicker
    /// over those frame steps. The gate holds every pixel showing the avatar to its control in the same run, the same
    /// walk with the avatar standing still in the world, which keeps its own history by construction. HDR is off, the
    /// sharpen is at its default, and the measured values are Metal on Apple silicon.
    /// </summary>
    public sealed partial class TemporalFollowStopGpuTests(TemporalFollowStopRuns runs, ITestOutputHelper output)
        : IClassFixture<TemporalFollowStopRuns>
    {
        const float BootPitch = TemporalPerspectiveFollowGpuTests.BootPitch;

        static readonly FollowEnding[] Endings = { FollowEnding.Stop, FollowEnding.DampedStop, FollowEnding.Reversal };

        static readonly TemporalUpscale[] Presets = { TemporalUpscale.Native, TemporalUpscale.Quality };

        /// <summary>How far every pixel showing the avatar may read over its control after a stop or a reversal, past
        /// the most it read over it before the turn, as a share of the control's error: the outline's pixels mix the
        /// ground or wall behind them, which passes in the walk and holds still in the control, so their histories
        /// differ. 0.008 at most on the first phase, and past it on some start phases at Quality and beside a passer
        /// (<see cref="SweptWholeShares"/>).</summary>
        const double WholeShare = 0.02;

        /// <summary>The same under the damped camera, which resamples the history at fractional offsets after the
        /// stop: 0.025 at most, measured.</summary>
        const double DampedWholeShare = 0.05;

        /// <summary>How far the inner pixels may read over their control under the damped camera, the same way:
        /// 0.003 at most, measured. After a stop or a reversal they read their control's error to the
        /// rounding.</summary>
        const double DampedInnerShare = 0.01;

        // The walks: the boot pitch away and sideways and the orthographic walk at 1 display pixel a frame, and with
        // the table switch both pitches, every heading and 0.5 and 2 display pixels a frame too.
        IEnumerable<StopRun> Walks() => Walks(TemporalStabilityRuns.FullTable);

        IEnumerable<StopRun> Walks(bool full)
        {
            float[] speeds = full ? new[] { 0.5f, 1f, 2f } : new[] { 1f };
            FollowHeading[] headings = full
                ? new[] { FollowHeading.Away, FollowHeading.Sideways, FollowHeading.Towards }
                : new[] { FollowHeading.Away, FollowHeading.Sideways };
            float[] pitches = full
                ? new[] { BootPitch, TemporalPerspectiveFollowGpuTests.LowPitch }
                : new[] { BootPitch };
            foreach (FollowEnding ending in Endings)
                foreach (TemporalUpscale preset in Presets)
                {
                    foreach (float speed in speeds)
                        yield return runs.Orthographic(ending, preset, speed);
                    foreach (float pitch in pitches)
                        foreach (FollowHeading heading in headings)
                            foreach (float speed in speeds)
                                yield return runs.Perspective(ending, preset, speed, pitch, heading);
                }
        }

        /// <summary>
        /// The followed avatar keeps its own history when it stops with the camera, stops while a damped camera eases
        /// on, or turns back through zero travel, over the ground or wall, over the clear colour and beside a passer:
        /// on the turn frame and each of the 15 after, at Native and Quality, every pixel showing it and those inside
        /// its outline read at most their control's error (<see cref="TemporalFollowStopRuns.Control"/>), past the
        /// most they read over it on the frames before the turn, and one luma step of 1/255 on one of their pixels.
        /// Beside a passer that lead is what they read over it on the frame before the turn, or the same walk's
        /// without the passer where that is more: the passer shows beside the walking avatar some frames before the
        /// turn, and the most read over the control since then left the outline up to 16 percent of the control's
        /// error to drop after it, where this leaves at most 5.
        /// Every pixel showing it may also read <see cref="WholeShare"/> of the control's error over it, and under the
        /// damped camera <see cref="DampedWholeShare"/> and <see cref="DampedInnerShare"/>. The control renders in the
        /// same run, so the bound holds on any backend. Dropped on the turn frame, the inner pixels read up to 3.7 and
        /// 3.0 times a kept history's error on the orthographic walk at Native and Quality, and with the background
        /// read as motion beside the outline, every pixel showing the avatar over the clear colour read up to 1.39
        /// times on a stop. The damped stop beside a passer is printed by the table only: the passer moves on screen
        /// otherwise than the easing avatar, so the outline drops its history where the passer shows beside it, and
        /// every pixel showing the avatar on the orthographic walk at Quality reads up to 0.061 of its control's error
        /// over it past the frames before the turn.
        /// </summary>
        [GpuFact]
        public void A_followed_avatar_keeps_its_own_history_when_it_stops_or_turns_back()
        {
            var over = new List<string>();
            int walks = 0;
            Dictionary<string, StopRun> plain = Walks(false).ToDictionary(r => r.Name);
            foreach (StopRun r in Walks(false).Concat(Surrounded(false)
                .Where(r => !(r.Surround == StopSurround.Passer && r.Ending == FollowEnding.DampedStop))))
            {
                Assert.True(r.Phases <= TemporalFollowStopRuns.Lead, $"{r.Name}: the jitter sequence outruns the lead");
                StopRun control = runs.Control(r);
                StopRun? without = r.Surround == StopSurround.Passer
                    ? plain[r.Name.Replace(TemporalFollowStopRuns.PasserSuffix, "", StringComparison.Ordinal)]
                    : null;
                Hold(r, "whole", r.Whole, control.Whole, Lead(r.Whole, control.Whole, without?.Whole,
                    without is null ? null : runs.Control(without).Whole), over);
                Hold(r, "inner", r.Inner, control.Inner, Lead(r.Inner, control.Inner, without?.Inner,
                    without is null ? null : runs.Control(without).Inner), over);
                walks++;
            }
            foreach (string line in over) output.WriteLine(line);
            output.WriteLine($"{walks} walks, {over.Count} frames over their bound");
            Assert.True(over.Count == 0, $"the avatar drops its own history: {string.Join(". ", over)}");
        }

        // The most a walk read over its control before the turn. A walk with a passer reads over its control from the
        // frame the passer first shows beside the avatar, while it still walks, and that is no licence for the outline
        // to drop more of its history from the turn on: its lead is what it read over the control on the frame before
        // the turn, or the lead of the same walk without the passer where that is more.
        internal static double Lead(StopMeasure m, StopMeasure control, StopMeasure? without,
            StopMeasure? withoutControl) =>
            without is null || withoutControl is null ? Lead(m.Cycle, control.Cycle)
                : Math.Max(Math.Max(0, m.Errors[0] - control.Errors[0]), Lead(without.Cycle, withoutControl.Cycle));

        static double Lead(double[] walk, double[] control) => Math.Max(0, walk.Zip(control, (a, b) => a - b).Max());

        // Each frame from the turn on against its control, past the lead, and the walk's worst margin printed as a
        // share of the control's error.
        void Hold(StopRun r, string set, StopMeasure m, StopMeasure control, double lead, List<string> over)
        {
            double share = Share(r, set);
            double worst = Worst(r, set, m, control, lead, over);
            output.WriteLine($"{r.Name}, {set}: {worst:+0.0000;-0.0000} of the kept error over it and the lead's "
                + $"{lead:0.00000}, allowed {share:0.00}");
        }

        /// <summary>How far every pixel showing the avatar (<paramref name="set"/> "whole") or its inner pixels may
        /// read over their control on <paramref name="r"/>'s walk, as a share of the control's error.</summary>
        internal static double Share(StopRun r, string set)
        {
            bool whole = set == "whole";
            if (whole && SweptWholeShare(r) is double swept) return swept;
            return r.Ending == FollowEnding.DampedStop ? whole ? DampedWholeShare : DampedInnerShare
                : whole ? WholeShare : 0;
        }

        /// <summary>The most any frame from the turn on reads over its control past the lead and a luma step, as a
        /// share of the control's error, with each frame over its bound added to <paramref name="over"/>.</summary>
        internal static double Worst(StopRun r, string set, StopMeasure m, StopMeasure control, double lead,
            List<string> over)
        {
            double rounding = 1.0 / (255.0 * m.Pixels);
            double share = Share(r, set);
            double worst = double.NegativeInfinity;
            for (int k = 1; k <= TemporalFollowStopRuns.After + 1; k++)
            {
                double kept = control.Errors[k];
                double bound = kept + lead + rounding + share * kept;
                worst = Math.Max(worst, (m.Errors[k] - kept - lead - rounding) / kept);
                if (m.Errors[k] > bound)
                    over.Add($"{r.Name}, {set}: frame +{k - 1} {m.Errors[k]:0.00000} against {bound:0.00000}");
            }
            return worst;
        }

        /// <summary>Every walk's error on the frame before the turn, the turn frame and the 15 after, its flicker and
        /// added change over those frame steps, and the set's pixels on the turn frame, for every pixel showing the
        /// avatar, for those inside the reconstruction's reach from its outline, and for the ring within that reach
        /// outside it, the ground or wall beside the avatar, whose band history drops when the avatar stops.</summary>
        [GpuFact]
        public void The_follow_stop_table_prints_the_avatars_own_error_around_the_turn()
        {
            output.WriteLine("| Walk | Pixels | Set | Before | Turn | "
                + string.Join(" | ", Enumerable.Range(1, TemporalFollowStopRuns.After).Select(k => $"+{k}"))
                + " | Flicker | Added |");
            output.WriteLine("|" + string.Concat(Enumerable.Repeat(" --- |", TemporalFollowStopRuns.After + 7)));
            foreach (StopRun r in Walks())
            {
                output.WriteLine(Row(r.Name, "whole", r.Whole));
                output.WriteLine(Row(r.Name, "inner", r.Inner));
                output.WriteLine(Row(r.Name, "ring", r.Ring));
                Assert.True(r.Inner.Pixels > 0, $"{r.Name}: the avatar's inner pixels are none");
            }
            output.WriteLine(TemporalStabilityRuns.FullTable ? "the full table"
                : $"the default walks only. Set {TemporalStabilityRuns.TableVariable}=1 for the full table");
            output.WriteLine($"every run: {runs.Seconds:0.0} s");
        }

        // The avatar over the clear colour and with a passer behind it: the orthographic walk and the boot pitch away
        // at 1 display pixel a frame, and with the table switch sideways and 0.5 and 2 display pixels a frame too.
        IEnumerable<StopRun> Surrounded() => Surrounded(TemporalStabilityRuns.FullTable);

        IEnumerable<StopRun> Surrounded(bool full)
        {
            float[] speeds = full ? new[] { 0.5f, 1f, 2f } : new[] { 1f };
            FollowHeading[] headings = full
                ? new[] { FollowHeading.Away, FollowHeading.Sideways }
                : new[] { FollowHeading.Away };
            foreach (StopSurround surround in new[] { StopSurround.ClearColour, StopSurround.Passer })
                foreach (FollowEnding ending in Endings)
                    foreach (TemporalUpscale preset in Presets)
                    {
                        foreach (float speed in speeds)
                            yield return runs.Orthographic(ending, preset, speed, surround);
                        foreach (FollowHeading heading in headings)
                            foreach (float speed in speeds)
                                yield return runs.Perspective(ending, preset, speed, BootPitch, heading, surround);
                    }
        }

        /// <summary>The table of <see cref="The_follow_stop_table_prints_the_avatars_own_error_around_the_turn"/>
        /// for the avatar stopping with the clear colour beside and behind its whole outline, and with a second keyed
        /// box walking past behind it as it stops, at <see cref="TemporalFollowStopRuns.PasserPixels"/> display
        /// pixels a frame, showing beside its outline.</summary>
        [GpuFact]
        public void The_follow_stop_table_prints_the_avatar_beside_the_clear_colour_and_a_passer()
        {
            output.WriteLine("| Walk | Pixels | Set | Before | Turn | "
                + string.Join(" | ", Enumerable.Range(1, TemporalFollowStopRuns.After).Select(k => $"+{k}"))
                + " | Flicker | Added |");
            output.WriteLine("|" + string.Concat(Enumerable.Repeat(" --- |", TemporalFollowStopRuns.After + 7)));
            foreach (StopRun r in Surrounded())
            {
                output.WriteLine(Row(r.Name, "whole", r.Whole));
                output.WriteLine(Row(r.Name, "inner", r.Inner));
                output.WriteLine(Row(r.Name, "ring", r.Ring));
                Assert.True(r.Whole.Pixels > 0 && r.Inner.Pixels > 0, $"{r.Name}: the avatar shows no pixels");
            }
            output.WriteLine($"every run: {runs.Seconds:0.0} s");
        }

        static string Row(string name, string set, StopMeasure m) =>
            $"| {name} | {m.Pixels} | {set} | " + string.Join(" | ", m.Errors.Select(e => $"{e:0.00000}"))
            + $" | {m.Flicker:0.00000} | {m.Added:0.00000} |";
    }
}
