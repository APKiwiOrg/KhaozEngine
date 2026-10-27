using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Resolve-only still-detail failures and the lighting and motion behavior they must preserve.
    /// Geometric inputs use jittered point samples, and the synthetic line uses a fixed eight-frame cadence.
    /// All measurements use contrast shares in weighted luma.</summary>
    public sealed class TemporalStillDetailGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
    {
        internal const int DisplayW = 48, DisplayH = 24, Converge = 64, Measured = 32, RampFrames = 24;
        internal const float Line = 0.95f, Wall = 0.30f, LineWidth = 1f / 8f;

        internal readonly record struct StillRun(float MaxChange, float MaxRange, float Mean, float Expected);
        internal readonly record struct TrackRun(float MeanError);
        internal readonly record struct TrailRun(float MaxTrail);
        internal readonly record struct SettleRun(int Frames);

        [GpuTheory, InlineData(TemporalUpscale.Native), InlineData(TemporalUpscale.Quality)]
        public void A_still_shallow_diagonal_step_between_strong_contrasts_holds_steady(TemporalUpscale preset)
        {
            // A 0.4-texel trim at luma 0.75 separates the 0.60 tile from the 0.11 fascia.
            StillRun run = RunStillStep(preset, trim: true);
            output.WriteLine($"{preset}: {run}");
            Assert.True(run.MaxChange <= 0.1f, $"{preset}: {run}");
            Assert.True(run.MaxRange <= 0.15f, $"{preset}: {run}");
        }

        [GpuTheory, InlineData(TemporalUpscale.Native), InlineData(TemporalUpscale.Quality)]
        public void A_still_line_the_jitter_hits_once_per_cycle_holds_steady_at_its_coverage(TemporalUpscale preset)
        {
            // A line one eighth of a texel wide at luma 0.95 on 0.30, lit only on frames where n % 8 == 0.
            StillRun run = RunOncePerCycleLine(preset);
            output.WriteLine($"{preset}: {run}");
            Assert.True(run.MaxChange <= 0.1f, $"{preset}: {run}");
            Assert.True(preset == TemporalUpscale.Native
                ? MathF.Abs(run.Mean - run.Expected) <= 0.25f * run.Expected
                : run.Mean >= 0.75f * run.Expected, $"{preset}: {run}");
        }

        [GpuTheory, InlineData(TemporalUpscale.Native), InlineData(TemporalUpscale.Quality)]
        public void A_shadow_creeping_over_a_shimmering_line_tracks_like_one_over_a_plain_wall(TemporalUpscale preset)
        {
            // The wall's luma falls linearly from 0.80 to 0.30 over 24 frames after convergence, a shadow creeping across.
            TrackRun plain = RunRampUnderLine(preset, withLine: false), shimmering = RunRampUnderLine(preset, withLine: true);
            output.WriteLine($"{preset}: plain {plain}, shimmering {shimmering}");
            // Unchanged code lags here because the lock's hold keeps the old luma out of the clip, so this guards
            // against a regression from that lag and prints the plain-wall error beside it.
            Assert.True(shimmering.MeanError <= MainCreepingError(preset) + 0.02f,
                $"{preset}: plain {plain}, shimmering {shimmering}, main {MainCreepingError(preset)}");
        }

        [GpuTheory, InlineData(TemporalUpscale.Native), InlineData(TemporalUpscale.Quality)]
        public void A_line_drifting_a_fifth_of_a_texel_a_frame_leaves_no_trail(TemporalUpscale preset)
        {
            TrailRun run = RunDriftingLine(preset, texelsPerFrame: 0.2f);
            output.WriteLine($"{preset}: {run}");
            Assert.True(run.MaxTrail <= 0.1f, $"{preset}: {run}");
        }

        [GpuTheory, InlineData(TemporalUpscale.Native), InlineData(TemporalUpscale.Quality)]
        public void A_still_wall_whose_light_steps_settles_as_fast_as_before(TemporalUpscale preset)
        {
            // The wall steps from 0.80 to 0.40 at frame 64.
            SettleRun run = RunLightStep(preset);
            output.WriteLine($"{preset}: {run}");
            Assert.True(run.Frames <= MainSettleFrames(preset), $"{preset}: {run}");
        }

        // Measured against unchanged engine commit f099a6a9e6490df0c378b7872610d7bbd7c761bf on Metal.
        internal static int MainSettleFrames(TemporalUpscale preset) => preset switch
        {
            TemporalUpscale.Native => 1,
            TemporalUpscale.Quality => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };

        // Measured against unchanged engine commit f099a6a9e6490df0c378b7872610d7bbd7c761bf on Metal.
        internal static float MainCreepingError(TemporalUpscale preset) => preset switch
        {
            TemporalUpscale.Native => 0.11076953f,
            TemporalUpscale.Quality => 0.11169002f,
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };

        internal static StillRun RunStillStep(TemporalUpscale preset, bool trim)
        {
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(DisplayW / factor), ih = (int)(DisplayH / factor);
            var pixels = new List<int>();
            float expected = 0f;
            for (int y = 2; y < DisplayH - 2; y++)
                for (int x = 2; x < DisplayW - 2; x++)
                {
                    float edge = StepCoverage(x / factor, y / factor, 1f / factor, ih, trim: false);
                    if (edge <= 0.05f || edge >= 0.95f) continue;
                    pixels.Add(y * DisplayW + x);
                    expected += StepCoverage(x / factor, y / factor, 1f / factor, ih, trim);
                }
            Assert.NotEmpty(pixels);
            return RunStill(preset, (jitter, _) => StillStepScene(iw, ih, jitter, trim), pixels,
                0.11f, trim ? 0.75f : 0.60f, expected / pixels.Count);
        }

        internal static StillRun RunOncePerCycleLine(TemporalUpscale preset)
        {
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(DisplayW / factor), ih = (int)(DisplayH / factor);
            int column = iw / 2, centre = (int)MathF.Floor((column + 0.5f) * factor);
            var pixels = ColumnPixels(factor, column);
            return RunStill(preset, (jitter, frame) => OncePerCycleLineScene(iw, ih, jitter, frame),
                pixels, Wall, Line, LineWidth, DisplayH / 2 * DisplayW + centre);
        }

        static StillRun RunStill(TemporalUpscale preset, Func<Vector2, int, float[]> scene,
            List<int> pixels, float dark, float light, float expected, int centre = -1)
        {
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(DisplayW / factor), ih = (int)(DisplayH / factor), phases = TemporalJitter.PhaseCount(factor);
            Matrix4x4 projection = Perspective((float)DisplayW / DisplayH);
            TemporalViewInput still = View(Eye, projection);
            using var rig = new Rig(iw, ih, DisplayW, DisplayH, NdcDepth(projection, SceneMetres));
            float[] motion = Pairs(iw, ih, (_, _) => Vector2.Zero);
            var previous = new float[pixels.Count];
            var minimum = new float[pixels.Count];
            var maximum = new float[pixels.Count];
            Array.Fill(minimum, float.PositiveInfinity);
            Array.Fill(maximum, float.NegativeInfinity);
            float change = 0f, range = 0f, sum = 0f;
            int samples = 0;
            for (int n = 0; n < Converge + Measured; n++)
            {
                Vector2 jitter = TemporalJitter.Offset(n, phases);
                rig.BeginFrame();
                float[] input = scene(jitter, n);
                rig.Fill(input, input, motion);
                rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, jitter, iw, ih, DisplayW, DisplayH, n > 0));
                if (n < Converge - 1) continue;
                float[] color = rig.ReadColor();
                for (int i = 0; i < pixels.Count; i++)
                {
                    float value = Share(color[pixels[i] * 4], dark, light);
                    if (n >= Converge)
                    {
                        change = MathF.Max(change, MathF.Abs(value - previous[i]));
                        minimum[i] = MathF.Min(minimum[i], value);
                        maximum[i] = MathF.Max(maximum[i], value);
                        range = MathF.Max(range, maximum[i] - minimum[i]);
                        if (centre < 0 || pixels[i] == centre)
                        {
                            sum += value;
                            samples++;
                        }
                    }
                    previous[i] = value;
                }
            }
            Assert.True(samples > 0);
            return new StillRun(change, range, sum / samples, expected);
        }

        internal static TrackRun RunRampUnderLine(TemporalUpscale preset, bool withLine)
        {
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(DisplayW / factor), ih = (int)(DisplayH / factor), phases = TemporalJitter.PhaseCount(factor);
            Matrix4x4 projection = Perspective((float)DisplayW / DisplayH);
            TemporalViewInput still = View(Eye, projection);
            using var rig = new Rig(iw, ih, DisplayW, DisplayH, NdcDepth(projection, SceneMetres));
            float[] motion = Pairs(iw, ih, (_, _) => Vector2.Zero);
            List<int> pixels = ColumnPixels(factor, iw / 2);
            var coverage = new float[pixels.Count];
            float error = 0f;
            int samples = 0;
            for (int n = 0; n < Converge + Measured; n++)
            {
                Vector2 jitter = TemporalJitter.Offset(n, phases);
                float[] scene = RampUnderLineScene(iw, ih, jitter, n, withLine);
                rig.BeginFrame();
                rig.Fill(scene, scene, motion);
                rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, jitter, iw, ih, DisplayW, DisplayH, n > 0));
                if (n < Converge - 8 || n >= Converge + RampFrames) continue;
                float[] color = rig.ReadColor();
                for (int i = 0; i < pixels.Count; i++)
                {
                    float value = color[pixels[i] * 4];
                    if (n < Converge)
                    {
                        // Measure each pixel's held contrast before the ramp so static coverage bias is not lag.
                        if (withLine) coverage[i] += Share(value, 0.80f, Line) / 8f;
                        continue;
                    }
                    float wall = Weighted(RampWall(n));
                    float expected = (wall + coverage[i] * (Weighted(Line) - wall) - Weighted(Wall))
                        / (Weighted(Line) - Weighted(Wall));
                    error += MathF.Abs(Share(value, Wall, Line) - expected);
                    samples++;
                }
            }
            return new TrackRun(error / samples);
        }

        internal static TrailRun RunDriftingLine(TemporalUpscale preset, float texelsPerFrame)
        {
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(DisplayW / factor), ih = (int)(DisplayH / factor), phases = TemporalJitter.PhaseCount(factor);
            Matrix4x4 projection = Perspective((float)DisplayW / DisplayH);
            TemporalViewInput still = View(Eye, projection);
            using var rig = new Rig(iw, ih, DisplayW, DisplayH, NdcDepth(projection, SceneMetres));
            var lastCovered = new int[DisplayW];
            Array.Fill(lastCovered, -1);
            float trail = 0f;
            int samples = 0;
            for (int n = 0; n < Converge + Measured; n++)
            {
                Vector2 jitter = TemporalJitter.Offset(n, phases);
                float position = DriftingPosition(iw, n, texelsPerFrame);
                float[] scene = DriftingLineScene(iw, ih, jitter, n, texelsPerFrame);
                float[] motion = Pairs(iw, ih, (x, _) => InLine(x + 0.5f - jitter.X, position)
                    ? new Vector2(texelsPerFrame / iw, 0f) : Vector2.Zero);
                rig.BeginFrame();
                rig.Fill(scene, scene, motion);
                rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, jitter, iw, ih, DisplayW, DisplayH, n > 0));
                for (int x = 1; x < DisplayW - 1; x++)
                    if ((x + 1f) / factor > position - LineWidth * 0.5f && x / factor < position + LineWidth * 0.5f)
                        lastCovered[x] = n;
                if (n < Converge) continue;
                float[] color = rig.ReadColor();
                for (int x = 1; x < DisplayW - 1; x++)
                {
                    if (lastCovered[x] < 0 || n - lastCovered[x] <= 2) continue;
                    trail = MathF.Max(trail, MathF.Abs(Share(color[(DisplayH / 2 * DisplayW + x) * 4], Wall, Line)));
                    samples++;
                }
            }
            Assert.True(samples > 0, "The drifting line must leave measured pixels behind.");
            return new TrailRun(trail);
        }

        internal static SettleRun RunLightStep(TemporalUpscale preset)
        {
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(DisplayW / factor), ih = (int)(DisplayH / factor), phases = TemporalJitter.PhaseCount(factor);
            Matrix4x4 projection = Perspective((float)DisplayW / DisplayH);
            TemporalViewInput still = View(Eye, projection);
            using var rig = new Rig(iw, ih, DisplayW, DisplayH, NdcDepth(projection, SceneMetres));
            float[] motion = Pairs(iw, ih, (_, _) => Vector2.Zero);
            List<int> pixels = ColumnPixels(factor, iw / 2);
            int frames = 1;
            for (int n = 0; n < Converge + Measured; n++)
            {
                Vector2 jitter = TemporalJitter.Offset(n, phases);
                float[] scene = LightStepScene(iw, ih, jitter, n);
                rig.BeginFrame();
                rig.Fill(scene, scene, motion);
                rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, jitter, iw, ih, DisplayW, DisplayH, n > 0));
                if (n < Converge) continue;
                float[] color = rig.ReadColor();
                foreach (int pixel in pixels)
                    if (MathF.Abs(Share(color[pixel * 4], 0.40f, 0.80f)) > 0.05f)
                        frames = n - Converge + 2;
            }
            Assert.True(frames <= Measured, "The light step must settle inside the measured window.");
            return new SettleRun(frames);
        }

        // Internal frame builders are shared with the history-state facts so both exercise identical inputs.
        internal static float[] StillStepScene(int iw, int ih, Vector2 jitter, bool trim)
            => Grey(iw, ih, (x, y) =>
            {
                float distance = y + 0.5f - jitter.Y - StepY(x + 0.5f - jitter.X, ih);
                return trim && MathF.Abs(distance) < 0.2f ? 0.75f : distance < 0f ? 0.60f : 0.11f;
            });

        internal static float[] OncePerCycleLineScene(int iw, int ih, Vector2 jitter, int frame)
            => Grey(iw, ih, (x, _) => frame % 8 == 0 && x == iw / 2 ? Line : Wall);

        internal static float[] RampUnderLineScene(int iw, int ih, Vector2 jitter, int frame, bool withLine)
            => Grey(iw, ih, (x, _) => withLine && frame % 8 == 0 && x == iw / 2
                ? Line : RampWall(frame));

        internal static float[] DriftingLineScene(int iw, int ih, Vector2 jitter, int frame, float texelsPerFrame)
            => Grey(iw, ih, (x, _) => InLine(x + 0.5f - jitter.X, DriftingPosition(iw, frame, texelsPerFrame)) ? Line : Wall);

        internal static float[] LightStepScene(int iw, int ih, Vector2 jitter, int frame)
            => Grey(iw, ih, (_, _) => frame < Converge ? 0.80f : 0.40f);

        static float RampWall(int frame) => 0.80f + (Wall - 0.80f) * Math.Clamp((frame - Converge + 1f) / RampFrames, 0f, 1f);
        static float DriftingPosition(int iw, int frame, float texelsPerFrame) => iw / 4 + 0.5f + frame * texelsPerFrame;
        static bool InLine(float x, float position) => MathF.Abs(x - position) < LineWidth * 0.5f;
        static float StepY(float x, int ih) => ih / 4f + 0.2f * x;
        static float Share(float value, float dark, float light) => (Weighted(value) - Weighted(dark)) / (Weighted(light) - Weighted(dark));

        static List<int> ColumnPixels(float factor, int column)
        {
            var pixels = new List<int>();
            for (int x = 0; x < DisplayW; x++)
                if ((int)MathF.Floor((x + 0.5f) / factor) == column) pixels.Add(DisplayH / 2 * DisplayW + x);
            Assert.NotEmpty(pixels);
            return pixels;
        }

        // Integrate the clipped linear edge analytically over each display pixel, independently of jitter.
        static float StepCoverage(float x, float y, float side, int ih, bool trim)
        {
            float Below(float offset)
            {
                float a = StepY(x, ih) + offset - y;
                float b = a + 0.2f * side;
                static float Integral(float v, float height) => v <= 0f ? 0f
                    : v < height ? 0.5f * v * v : height * (v - 0.5f * height);
                return (Integral(b, side) - Integral(a, side)) / (0.2f * side * side);
            }
            if (!trim) return Below(0f);
            float tile = Below(-0.2f), strip = Below(0.2f) - tile;
            return tile * Share(0.60f, 0.11f, 0.75f) + strip;
        }
    }
}
