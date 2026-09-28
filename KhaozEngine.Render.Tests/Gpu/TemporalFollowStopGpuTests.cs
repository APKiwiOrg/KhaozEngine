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
    /// travel in the world is zero. Each row reads the avatar's own pixels, their mean luma error against the 4x
    /// reference on the frame before the turn, the turn frame and the 15 after, and the flicker over those frame steps.
    /// The default walks hold the avatar's inner pixels to the error a history kept through the turn gives. HDR is
    /// off, the sharpen is at its default, and the measured values are Metal on Apple silicon.
    /// </summary>
    public sealed class TemporalFollowStopGpuTests(TemporalFollowStopRuns runs, ITestOutputHelper output)
        : IClassFixture<TemporalFollowStopRuns>
    {
        const float BootPitch = TemporalPerspectiveFollowGpuTests.BootPitch;

        static readonly FollowEnding[] Endings = { FollowEnding.Stop, FollowEnding.DampedStop, FollowEnding.Reversal };

        static readonly TemporalUpscale[] Presets = { TemporalUpscale.Native, TemporalUpscale.Quality };

        /// <summary>How far over the kept history's error a frame may read: a luma step of 1/255 on one pixel in
        /// twenty of the inner set. Under the damped camera the history is resampled at fractional offsets, so the
        /// inner pixels read a little of the outline's, where the rules for the ground beside and under the avatar
        /// act: 0.00011 over at most, measured.</summary>
        const double Allowance = 0.0002;

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
        /// on, or turns back through zero travel: on the turn frame and each of the 15 after, at Native and Quality,
        /// its inner pixels' error against the 4x reference is at most a kept history's
        /// (<see cref="TemporalFollowStopKeptHistory"/>) and <see cref="Allowance"/>. Dropped on the turn frame, it
        /// read up to 3.7 and 3.0 times that on the orthographic walk at Native and Quality, 1.2 and 1.5 on the
        /// perspective one, and settled over the 15 frames. Every pixel showing the avatar is printed by the table,
        /// and reads up to 2.4 percent over the kept history after a stop or a reversal and 4.5 under the damped
        /// camera, on the outline's pixels, where the rules for the ground beside and under the avatar act.
        /// </summary>
        [GpuFact]
        public void A_followed_avatar_keeps_its_own_history_when_it_stops_or_turns_back()
        {
            var over = new List<string>();
            int walks = 0;
            foreach (StopRun r in Walks(false))
            {
                double[] kept = TemporalFollowStopKeptHistory.InnerErrors[r.Name];
                for (int k = 0; k < kept.Length; k++)
                    if (r.Inner.Errors[k + 1] > kept[k] + Allowance)
                        over.Add($"{r.Name}: frame +{k} {r.Inner.Errors[k + 1]:0.00000} against {kept[k]:0.00000}");
                walks++;
            }
            foreach (string line in over) output.WriteLine(line);
            output.WriteLine($"{walks} walks, {over.Count} frames over a kept history's error");
            Assert.True(walks == TemporalFollowStopKeptHistory.InnerErrors.Count, "a default walk has no bound");
            Assert.True(over.Count == 0, $"the avatar drops its own history: {string.Join(". ", over)}");
        }

        /// <summary>Every walk's error on the frame before the turn, the turn frame and the 15 after, its flicker and
        /// added change over those frame steps, and the avatar's pixels on the turn frame, for every pixel showing the
        /// avatar and for those inside the reconstruction's reach from its outline.</summary>
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
                Assert.True(r.Inner.Pixels > 0, $"{r.Name}: the avatar's inner pixels are none");
            }
            output.WriteLine(TemporalStabilityRuns.FullTable ? "the full table"
                : $"the default walks only. Set {TemporalStabilityRuns.TableVariable}=1 for the full table");
            output.WriteLine($"every run: {runs.Seconds:0.0} s");
        }

        // The avatar over the clear colour and with a passer behind it: the orthographic walk and the boot pitch away
        // at 1 display pixel a frame, and with the table switch sideways and 0.5 and 2 display pixels a frame too.
        IEnumerable<StopRun> Surrounded()
        {
            bool full = TemporalStabilityRuns.FullTable;
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
                Assert.True(r.Whole.Pixels > 0 && r.Inner.Pixels > 0, $"{r.Name}: the avatar shows no pixels");
            }
            output.WriteLine($"every run: {runs.Seconds:0.0} s");
        }

        static string Row(string name, string set, StopMeasure m) =>
            $"| {name} | {m.Pixels} | {set} | " + string.Join(" | ", m.Errors.Select(e => $"{e:0.00000}"))
            + $" | {m.Flicker:0.00000} | {m.Added:0.00000} |";
    }
}
