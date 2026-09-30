using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>One cell of a follow fact over a set of jitter start phases: the measure at each phase (a trail's
    /// excess, a line's worst share or a stop's worst margin in thousandths) and the fact's bound there.</summary>
    internal sealed record PhaseSweep(string Cell, int[] Phases, int[] Values, int[] Bounds, bool AtLeast = false)
    {
        /// <summary>The worst value over the phases: the most excess, or the least share.</summary>
        public int Worst => AtLeast ? Values.Min() : Values.Max();

        public int WorstPhase => Phases[Array.IndexOf(Values, Worst)];

        /// <summary>The phases whose value passes their bound.</summary>
        public int Over => Values.Where((v, i) => AtLeast ? v < Bounds[i] : v > Bounds[i]).Count();

        public string Row(string family) =>
            $"| {family} | {Cell} | {(AtLeast ? Bounds.Max() : Bounds.Min())} | {Phases.Length} | {Over} | {Worst} "
            + $"| {WorstPhase} | {Values[0]} | {string.Join(" ", Values)} |";

        public const string Header = "| Family | Cell | Bound | Phases | Over | Worst | Worst phase | Phase 0 "
            + "| By phase |";
    }

    /// <summary>
    /// The follow facts across the jitter phase a walk starts on
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1207">#1207</see>). The trail behind a followed
    /// edge at Quality and below depends on it, and each follow fact starts on one phase. So each cell here is run from
    /// a set of start phases (<see cref="TemporalFixture.SkipPhases"/>), every phase of its preset's jitter sequence
    /// (<see cref="PhaseCount"/>) for the cells a phase moves most and their neighbours, and four phases a quarter
    /// sequence apart for the rest, and read against the fact's own bound at each phase. Report only: each table
    /// prints every cell's worst value over its phases, the phases over its bound, and the value at each phase. By
    /// default each table runs a few cells, and with <c>KE_TEMPORAL_ACCEPTANCE_TABLE=1</c> every cell its fact holds.
    /// HDR is off, the sharpen is at its default, and the measured values are Metal on Apple silicon.
    /// </summary>
    public sealed partial class TemporalFollowPhaseGpuTests(TemporalFollowCameraRuns ortho,
        TemporalPerspectiveFollowRuns perspective, TemporalFollowLinesRuns lines, TemporalFollowStopRuns stops,
        ITestOutputHelper output)
        : IClassFixture<TemporalFollowCameraRuns>, IClassFixture<TemporalPerspectiveFollowRuns>,
            IClassFixture<TemporalFollowLinesRuns>, IClassFixture<TemporalFollowStopRuns>
    {
        const TemporalUpscale N = TemporalUpscale.Native, Q = TemporalUpscale.Quality,
            P = TemporalUpscale.Performance, U = TemporalUpscale.UltraPerformance;
        const float B = TemporalPerspectiveFollowGpuTests.BootPitch, L = TemporalPerspectiveFollowGpuTests.LowPitch;
        const FollowHeading A = FollowHeading.Away, S = FollowHeading.Sideways, T = FollowHeading.Towards;

        static readonly float[] Speeds = { 0.5f, 1f, 1.5f, 2f, 2.5f, 3f };
        static readonly TemporalUpscale[] Presets = { N, Q, P, U };
        static readonly FollowHeading[] Headings = { A, S, T };

        /// <summary>The length of <paramref name="preset"/>'s jitter sequence at 320 x 180: 8 at Native, 18 at Quality,
        /// 32 at Performance and 72 at UltraPerformance.</summary>
        internal static int PhaseCount(TemporalUpscale preset) =>
            TemporalJitter.PhaseCount(TemporalSettings.DisplayOverInternal(preset));

        /// <summary>Every start phase of <paramref name="preset"/>'s sequence, or four a quarter sequence
        /// apart.</summary>
        internal static int[] Phases(TemporalUpscale preset, bool every)
        {
            int count = PhaseCount(preset);
            return every ? Enumerable.Range(0, count).ToArray()
                : new[] { 0, count / 4, count / 2, 3 * count / 4 };
        }

        // The orthographic walks run from every phase: every Native walk, the Quality and Performance walks the phase
        // moves most and their neighbours, and two at UltraPerformance.
        static bool OrthoEvery(TemporalUpscale preset, float speed) => preset switch
        {
            N => true,
            Q or P => speed <= 2f,
            _ => speed is 1f or 2f,
        };

        // The perspective walks run from every phase: the Native walks over acceptance 3, and at the other presets the
        // walks the phase moves most and their neighbours.
        static readonly (float Pitch, FollowHeading Heading, float Speed, TemporalUpscale Preset)[] PerspectiveEvery =
        {
            (B, A, 1f, N), (L, S, 0.5f, N), (L, S, 1f, N),
            (B, A, 1f, Q), (B, T, 1f, Q), (B, S, 2f, Q), (L, A, 1f, Q), (L, A, 1.5f, Q),
            (B, T, 1f, P), (B, T, 1.5f, P), (B, T, 2f, P), (B, T, 2.5f, P), (B, S, 2.5f, P), (L, A, 0.5f, P),
            (L, A, 1.5f, P), (L, S, 3f, P),
            (B, A, 2f, U), (B, T, 1f, U), (L, S, 1.5f, U),
        };

        static void Header(ITestOutputHelper output, string what)
        {
            output.WriteLine(what);
            output.WriteLine(PhaseSweep.Header);
            output.WriteLine("|" + string.Concat(Enumerable.Repeat(" --- |", 9)));
        }

        static void Footer(ITestOutputHelper output, List<PhaseSweep> sweeps, double seconds)
        {
            int runs = sweeps.Sum(s => s.Phases.Length);
            output.WriteLine($"{sweeps.Count(s => s.Over == 0)} of {sweeps.Count} cells within their bound at every "
                + $"phase, {runs} runs in {seconds:0.0} s");
            output.WriteLine(TemporalStabilityRuns.FullTable ? "the full table"
                : $"the default cells only. Set {TemporalStabilityRuns.TableVariable}=1 for the full table");
        }

        /// <summary>Every orthographic follow walk (<see cref="TemporalFollowCameraRuns"/>) over its start phases,
        /// against the bound of <see cref="TemporalFollowCameraGpuTests.Walks"/>, and at UltraPerformance the trail
        /// past the reconstruction's reach against <see cref="TemporalFollowCameraGpuTests.SpillWalks"/>. By default
        /// the walk at 1 display pixel a frame at Native, Quality and Performance, and at half a pixel at
        /// Quality.</summary>
        [GpuFact]
        public void The_orthographic_follow_phase_table_prints_every_walk()
        {
            double before = ortho.Seconds;
            Header(output, "orthographic follow over jitter start phases (#1207), report only");
            var sweeps = new List<PhaseSweep>();
            foreach (TemporalUpscale preset in Presets)
                foreach (float speed in Speeds)
                {
                    bool every = OrthoEvery(preset, speed);
                    bool asserted = speed == 1f && preset != U || preset == Q && speed == 0.5f;
                    if (!TemporalStabilityRuns.FullTable && !asserted) continue;
                    int[] phases = Phases(preset, every);
                    CrossingTrail[] runs = phases.Select(p => ortho.Run(preset, speed, phase: p)).ToArray();
                    foreach (CrossingTrail c in runs)
                        Assert.True(c.Total.Checked > 0, $"{c.Name}: the trail region measured nothing");
                    sweeps.Add(new PhaseSweep($"{preset} {speed}", phases, runs.Select(c => c.Total.Excess).ToArray(),
                        runs.Select(c => TemporalFollowCameraGpuTests.Bound(preset, speed, c.Total.Checked)!.Value)
                            .ToArray()));
                    output.WriteLine(sweeps[^1].Row("ortho"));
                    if (preset != U || TemporalFollowCameraGpuTests.SpillBound(speed) is not int spill) continue;
                    int[] past = phases.Select(p => ortho.BeyondSpill(preset, speed, phase: p).Total.Excess).ToArray();
                    sweeps.Add(new PhaseSweep($"{preset} {speed} past the reach", phases, past,
                        phases.Select(_ => spill).ToArray()));
                    output.WriteLine(sweeps[^1].Row("ortho"));
                }
            Footer(output, sweeps, ortho.Seconds - before);
        }

        /// <summary>Every perspective follow walk at Native and Quality (<see cref="TemporalPerspectiveFollowRuns"/>)
        /// over its start phases, against the bound of the perspective follow fact. By default the low pitch walking
        /// away at 1 display pixel a frame on Quality and the boot pitch walking away at 1 on Native.</summary>
        [GpuFact]
        public void The_perspective_follow_phase_table_prints_Native_and_Quality() =>
            PerspectiveTable(new[] { N, Q }, (B, A, 1f, N), (L, A, 1f, Q));

        /// <summary>The same at Performance and UltraPerformance, with the UltraPerformance trail past the
        /// reconstruction's reach. By default the boot pitch walking towards the camera at 2 display pixels a frame on
        /// Performance, and walking away at 2 on UltraPerformance from four phases.</summary>
        [GpuFact]
        public void The_perspective_follow_phase_table_prints_Performance_and_UltraPerformance() =>
            PerspectiveTable(new[] { P, U }, (B, T, 2f, P), (B, A, 2f, U));

        void PerspectiveTable(TemporalUpscale[] presets,
            params (float Pitch, FollowHeading Heading, float Speed, TemporalUpscale Preset)[] defaults)
        {
            double before = perspective.Seconds;
            Header(output, $"perspective follow at {string.Join(" and ", presets)} over jitter start phases (#1207), "
                + "report only");
            var sweeps = new List<PhaseSweep>();
            foreach (TemporalUpscale preset in presets)
                foreach (float pitch in new[] { B, L })
                    foreach (FollowHeading heading in Headings)
                        foreach (float speed in Speeds)
                        {
                            var cell = (pitch, heading, speed, preset);
                            if (!TemporalStabilityRuns.FullTable && !defaults.Contains(cell)) continue;
                            int[] phases = Phases(preset, PerspectiveEvery.Contains(cell)
                                && (TemporalStabilityRuns.FullTable || preset != U));
                            CrossingTrail[] runs = phases
                                .Select(p => perspective.Run(preset, speed, pitch, heading, phase: p)).ToArray();
                            foreach (CrossingTrail c in runs)
                                Assert.True(c.Total.Checked > 0, $"{c.Name}: the trail region measured nothing");
                            string name = $"{(pitch == B ? "boot" : "low")} {heading} {speed} {preset}";
                            sweeps.Add(new PhaseSweep(name, phases, runs.Select(c => c.Total.Excess).ToArray(),
                                runs.Select(c => TemporalPerspectiveFollowGpuTests.Bound(pitch, heading, speed,
                                    preset, c.Total.Checked)).ToArray()));
                            output.WriteLine(sweeps[^1].Row("perspective"));
                            if (preset != U
                                || TemporalPerspectiveFollowGpuTests.ReachBound(pitch, heading, speed) is not int reach)
                                continue;
                            int[] past = phases.Select(p => perspective.BeyondSpill(preset, speed, pitch, heading,
                                phase: p).Total.Excess).ToArray();
                            sweeps.Add(new PhaseSweep($"{name} past the reach", phases, past,
                                phases.Select(_ => reach).ToArray()));
                            output.WriteLine(sweeps[^1].Row("perspective"));
                        }
            Footer(output, sweeps, perspective.Seconds - before);
        }
    }
}
