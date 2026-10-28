using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>The shimmer record the resolve stores in the history state's last two channels: drift, the average of
    /// this frame's luma less its reprojected history, and swing, the average of its size. A still line the jitter
    /// shows on one frame a cycle swings without drifting, a steady ramp drifts as far as it swings, a frame without
    /// valid history stores an empty record, and the record moves at one over the jitter phase count. The scenes are
    /// <see cref="TemporalStillDetailGpuTests"/>' own.</summary>
    public sealed class TemporalShimmerRecordGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
    {
        const int DisplayW = TemporalStillDetailGpuTests.DisplayW, DisplayH = TemporalStillDetailGpuTests.DisplayH;
        const int Converge = TemporalStillDetailGpuTests.Converge, RampFrames = TemporalStillDetailGpuTests.RampFrames;

        readonly record struct ShimmerRecord(float Drift, float Swing);

        [GpuFact]
        public void A_line_the_jitter_hits_once_per_cycle_builds_swing_without_drift()
        {
            int column = DisplayW / 2;
            ShimmerRecord record = RunNative(Converge, (jitter, n) =>
                TemporalStillDetailGpuTests.OncePerCycleLineScene(DisplayW, DisplayH, jitter, n), column);
            Assert.True(record.Swing >= 0.01f, $"{record}");
            Assert.True(MathF.Abs(record.Drift) <= 0.25f * record.Swing, $"{record}");
        }

        [GpuFact]
        public void A_steady_ramp_builds_drift_that_tracks_swing()
        {
            // The wall alone, its luma falling at a steady rate from frame Converge on, read on the ramp's last frame.
            ShimmerRecord record = RunNative(Converge + RampFrames, (jitter, n) =>
                TemporalStillDetailGpuTests.RampUnderLineScene(DisplayW, DisplayH, jitter, n, withLine: false),
                DisplayW / 2);
            // A ramp step is about 0.009 of weighted luma a frame, and an empty record would pass the drift bound.
            Assert.True(record.Swing >= 0.004f, $"{record}");
            Assert.True(MathF.Abs(record.Drift) >= 0.8f * record.Swing, $"{record}");
        }

        [GpuFact]
        public void Invalid_history_writes_an_empty_shimmer_record()
        {
            using var rig = new Rig();
            rig.BeginFrame();
            float[] grey = Grey((_, _) => 0.4f);
            rig.Fill(grey, grey, Motion((_, _) => Vector2.Zero));
            rig.FillHistory(grey, StateWithRecord(N, N, (_, _) => new Vector4(1f, 0f, 0.5f, 0.5f)), SceneLinear);
            rig.Resolve(Uniforms(historyValid: false));
            float[] state = rig.ReadState();
            Assert.Equal(N * N * 4, state.Length);
            for (int p = 0; p < N * N; p++)
            {
                Assert.Equal(0f, state[p * 4 + 2]);
                Assert.Equal(0f, state[p * 4 + 3]);
            }
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.Native), InlineData(TemporalUpscale.Quality), InlineData(TemporalUpscale.UltraPerformance)]
        public void The_record_moves_at_one_over_the_jitter_phase_count(TemporalUpscale preset)
        {
            // One frame of a flat wall brighter than its history over a known record. Flat ground neither clips the
            // record nor releases the shading, so drift and swing each step toward this frame's change by the rate.
            const int Display = 24;
            const float Current = 0.5f, Held = 0.3f, Drift = -0.01f, Swing = 0.01f;
            int size = (int)(Display / TemporalSettings.DisplayOverInternal(preset));
            using var rig = new Rig(size, size, Display, Display, SceneNdc);
            TemporalResolveUniforms uniforms = TemporalResolveMath.BuildUniforms(Still, Still, Vector2.Zero, size, size,
                Display, Display, historyValid: true);
            rig.BeginFrame();
            float[] current = Grey(size, size, (_, _) => Current);
            rig.Fill(current, current, Pairs(size, size, (_, _) => Vector2.Zero));
            rig.FillHistory(Grey(Display, Display, (_, _) => Held),
                StateWithRecord(Display, Display, (_, _) => new Vector4(1f, 0f, Drift, Swing)), SceneLinear);
            rig.Resolve(uniforms);
            float[] state = rig.ReadState();
            int pixel = (Display / 2 * Display + Display / 2) * 4;
            float change = Weighted(Current) - Weighted(Q(Held));
            float expected = TemporalShimmer.Rate(uniforms.Jitter.Z);
            float driftRate = (state[pixel + 2] - Q(Drift)) / (change - Q(Drift));
            float swingRate = (state[pixel + 3] - Q(Swing)) / (change - Q(Swing));
            output.WriteLine($"{preset}: ratio {uniforms.Jitter.Z}, expected {expected}, drift {state[pixel + 2]} "
                + $"rate {driftRate}, swing {state[pixel + 3]} rate {swingRate}");
            Assert.InRange(driftRate, 0.97f * expected, 1.03f * expected);
            Assert.InRange(swingRate, 0.97f * expected, 1.03f * expected);
        }

        // Converge-style run at Native, the record read at the middle row's pixel in the given column after the last
        // frame. Each frame of the last jitter cycle is printed.
        ShimmerRecord RunNative(int frames, Func<Vector2, int, float[]> scene, int column)
        {
            int phases = TemporalJitter.PhaseCount(1f);
            Matrix4x4 projection = Perspective((float)DisplayW / DisplayH);
            TemporalViewInput still = View(Eye, projection);
            using var rig = new Rig(DisplayW, DisplayH, DisplayW, DisplayH, NdcDepth(projection, SceneMetres));
            float[] motion = Pairs(DisplayW, DisplayH, (_, _) => Vector2.Zero);
            int pixel = DisplayH / 2 * DisplayW + column;
            ShimmerRecord record = default;
            for (int n = 0; n < frames; n++)
            {
                Vector2 jitter = TemporalJitter.Offset(n, phases);
                float[] input = scene(jitter, n);
                rig.BeginFrame();
                rig.Fill(input, input, motion);
                rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, jitter, DisplayW, DisplayH, DisplayW,
                    DisplayH, n > 0));
                if (n < frames - phases) continue;
                float[] state = rig.ReadState();
                record = new ShimmerRecord(state[pixel * 4 + 2], state[pixel * 4 + 3]);
                output.WriteLine($"frame {n}: {record}, |drift| / swing {MathF.Abs(record.Drift) / record.Swing}");
            }
            return record;
        }
    }
}
