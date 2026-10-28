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
    /// valid history, a pixel the shading release dropped and a disoccluded pixel store an empty record, and the record
    /// moves at one over the jitter phase count. The line scene is <see cref="TemporalStillDetailGpuTests"/>' own.
    /// </summary>
    public sealed class TemporalShimmerRecordGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
    {
        const int DisplayW = TemporalStillDetailGpuTests.DisplayW, DisplayH = TemporalStillDetailGpuTests.DisplayH;
        const int Converge = TemporalStillDetailGpuTests.Converge;

        readonly record struct ShimmerRecord(float Drift, float Swing);

        [GpuFact]
        public void A_line_the_jitter_hits_once_per_cycle_builds_swing_without_drift()
        {
            // Averaged over the last whole jitter cycle: an average at one over the phase count reads a periodic hit
            // with a phase, so a single frame's drift says where in the cycle it was read, not whether the line drifts.
            int column = DisplayW / 2;
            ShimmerRecord[] cycle = RunNative(Converge, (jitter, n) =>
                TemporalStillDetailGpuTests.OncePerCycleLineScene(DisplayW, DisplayH, jitter, n), column);
            float ratio = 0f;
            foreach (ShimmerRecord record in cycle)
            {
                Assert.True(record.Swing >= 0.01f, $"{record}");
                ratio += MathF.Abs(record.Drift) / record.Swing / cycle.Length;
            }
            output.WriteLine($"mean |drift| / swing over the last {cycle.Length} frames {ratio}");
            Assert.True(ratio <= 0.25f, $"mean |drift| / swing {ratio}");
        }

        [GpuFact]
        public void A_steady_ramp_builds_drift_that_tracks_swing()
        {
            // The wall alone, its luma rising 0.004 a frame for 32 frames from frame Converge on, read on the last.
            const int Rising = 32;
            const float Step = 0.004f;
            static float Luma(int n) => TemporalStillDetailGpuTests.Wall + Step * Math.Clamp(n - Converge + 1, 0, Rising);
            ShimmerRecord record = RunNative(Converge + Rising, (_, n) => Grey(DisplayW, DisplayH, (_, _) => Luma(n)),
                DisplayW / 2)[^1];
            // A step is about 0.002 of weighted luma, and an empty record would pass the drift bound.
            float weightedStep = Weighted(Luma(Converge + Rising - 1)) - Weighted(Luma(Converge + Rising - 2));
            output.WriteLine($"weighted step {weightedStep}");
            Assert.True(record.Swing >= 0.5f * weightedStep, $"{record}");
            Assert.True(MathF.Abs(record.Drift) >= 0.8f * record.Swing, $"{record}");
        }

        [GpuFact]
        public void A_pixel_whose_shading_release_fires_writes_an_empty_shimmer_record()
        {
            // A still textured ramp under a history far brighter than its box, with no lock and no ridge, at Native: the
            // shading release fires and drops the accumulated weight, so the stored confidence is one sample's. A history
            // close to the ramp is the control, which keeps its weight and carries the record on.
            ShimmerRecord[] released = ResolveOverRecord(_ => Q(0.9f), SceneLinear, out float[] state);
            for (int p = 0; p < released.Length; p++)
            {
                Assert.Equal(1f / Max, state[Interior(p) * 4], 1e-3);
                Assert.Equal(default, released[p]);
            }
            ShimmerRecord[] kept = ResolveOverRecord(x => Q(Ramp(x) + 0.01f), SceneLinear, out state);
            for (int p = 0; p < kept.Length; p++)
            {
                Assert.Equal((Q(3f / Max) * Max + 1f) / Max, state[Interior(p) * 4], 1e-3);
                Assert.True(kept[p].Swing > 0.1f, $"control pixel {Interior(p)}: {kept[p]}");
            }
            output.WriteLine($"released {released[0]}, kept {kept[0]}");
        }

        [GpuFact]
        public void A_disoccluded_pixel_writes_an_empty_shimmer_record()
        {
            // Last frame's depth nearer than the wall everywhere disoccludes every pixel, as in
            // TemporalResolveRendererGpuTests.A_disoccluded_pixel_drops_its_history.
            ShimmerRecord[] dropped = ResolveOverRecord(_ => Q(0.9f), SceneLinear * 0.5f, out float[] state);
            for (int p = 0; p < dropped.Length; p++)
            {
                Assert.Equal(1f / Max, state[Interior(p) * 4], 1e-3);
                Assert.Equal(default, dropped[p]);
            }
            output.WriteLine($"disoccluded {dropped[0]}");
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

        // The interior pixels of the 8 by 8 rig, whose 3x3 lies inside the frame.
        static int Interior(int index) => (1 + index / (N - 2)) * N + 1 + index % (N - 2);

        // One resolve of the grey ramp over a history of the given grey, every state texel holding three samples' weight,
        // no lock and the record (0.5, 0.5), and the record each interior pixel stored.
        static ShimmerRecord[] ResolveOverRecord(Func<int, float> history, float previousDepth, out float[] state)
        {
            using var rig = new Rig();
            rig.BeginFrame();
            rig.Fill(Grey((x, _) => Ramp(x)), Grey((x, _) => Ramp(x)), Motion((_, _) => Vector2.Zero));
            rig.FillHistory(Grey((x, _) => history(x)),
                StateWithRecord(N, N, (_, _) => new Vector4(Q(3f / Max), 0f, 0.5f, 0.5f)), previousDepth);
            rig.Resolve(Uniforms(historyValid: true));
            state = rig.ReadState();
            var records = new ShimmerRecord[(N - 2) * (N - 2)];
            for (int p = 0; p < records.Length; p++)
                records[p] = new ShimmerRecord(state[Interior(p) * 4 + 2], state[Interior(p) * 4 + 3]);
            return records;
        }

        // Converge-style run at Native, the record read at the middle row's pixel in the given column on each frame of
        // the last jitter cycle, oldest first, each printed.
        ShimmerRecord[] RunNative(int frames, Func<Vector2, int, float[]> scene, int column)
        {
            int phases = TemporalJitter.PhaseCount(1f);
            Matrix4x4 projection = Perspective((float)DisplayW / DisplayH);
            TemporalViewInput still = View(Eye, projection);
            using var rig = new Rig(DisplayW, DisplayH, DisplayW, DisplayH, NdcDepth(projection, SceneMetres));
            float[] motion = Pairs(DisplayW, DisplayH, (_, _) => Vector2.Zero);
            int pixel = DisplayH / 2 * DisplayW + column;
            var cycle = new ShimmerRecord[phases];
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
                ShimmerRecord record = new(state[pixel * 4 + 2], state[pixel * 4 + 3]);
                cycle[n - (frames - phases)] = record;
                output.WriteLine($"frame {n}: {record}, |drift| / swing {MathF.Abs(record.Drift) / record.Swing}");
            }
            return cycle;
        }
    }
}
