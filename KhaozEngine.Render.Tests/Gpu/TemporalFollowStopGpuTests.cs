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
    /// Report only. HDR is off, the sharpen is at its default, and the measured values are Metal on Apple silicon.
    /// </summary>
    public sealed class TemporalFollowStopGpuTests(TemporalFollowStopRuns runs, ITestOutputHelper output)
        : IClassFixture<TemporalFollowStopRuns>
    {
        const float BootPitch = TemporalPerspectiveFollowGpuTests.BootPitch;

        static readonly FollowEnding[] Endings = { FollowEnding.Stop, FollowEnding.DampedStop, FollowEnding.Reversal };

        static readonly TemporalUpscale[] Presets = { TemporalUpscale.Native, TemporalUpscale.Quality };

        // The walks: the boot pitch away and sideways and the orthographic walk at 1 display pixel a frame, and with
        // the table switch both pitches, every heading and 0.5 and 2 display pixels a frame too.
        IEnumerable<StopRun> Walks()
        {
            bool full = TemporalStabilityRuns.FullTable;
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

        /// <summary>Every walk's error on the frame before the turn, the turn frame and the 15 after, its flicker and
        /// added change over those frame steps, and the avatar's pixels on the turn frame.</summary>
        [GpuFact]
        public void The_follow_stop_table_prints_the_avatars_own_error_around_the_turn()
        {
            output.WriteLine("| Walk | Pixels | Before | Turn | "
                + string.Join(" | ", Enumerable.Range(1, TemporalFollowStopRuns.After).Select(k => $"+{k}"))
                + " | Flicker | Added |");
            output.WriteLine("|" + string.Concat(Enumerable.Repeat(" --- |", TemporalFollowStopRuns.After + 6)));
            foreach (StopRun r in Walks())
            {
                output.WriteLine($"| {r.Name} | {r.Pixels} | "
                    + string.Join(" | ", r.Errors.Select(e => $"{e:0.00000}"))
                    + $" | {r.Flicker:0.00000} | {r.Added:0.00000} |");
                Assert.True(r.Pixels > 0, $"{r.Name}: the avatar showed on no pixel");
            }
            output.WriteLine(TemporalStabilityRuns.FullTable ? "the full table"
                : $"the default walks only. Set {TemporalStabilityRuns.TableVariable}=1 for the full table");
            output.WriteLine($"every run: {runs.Seconds:0.0} s");
        }
    }
}
