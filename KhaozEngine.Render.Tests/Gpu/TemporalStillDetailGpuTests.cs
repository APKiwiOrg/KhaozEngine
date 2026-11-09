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
    /// Geometric inputs use jittered point samples, and synthetic lines use fixed hit cadences.
    /// All measurements use contrast shares in weighted luma.</summary>
    public sealed class TemporalStillDetailGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
    {
        internal const int DisplayW = 48, DisplayH = 24, Converge = 64, Measured = 32, RampFrames = 24;
        internal const int SparseHitInterval = 12;
        internal const float Line = 0.95f, Wall = 0.30f, LineWidth = 1f / 8f;

        internal readonly record struct StillRun(float MaxChange, float MaxRange, float Mean, float Expected);
        internal readonly record struct TrackRun(float MeanError);
        internal readonly record struct TrailRun(float MaxTrail);
        internal readonly record struct SettleRun(int Frames);

        [GpuTheory, InlineData(TemporalUpscale.Native), InlineData(TemporalUpscale.Quality)]
        public void A_still_line_hit_less_often_than_the_lock_lasts_holds_steady(TemporalUpscale preset)
        {
            StillRun run = RunSparseHitLine(preset);
            output.WriteLine($"{preset}: {run}");
            if (preset == TemporalUpscale.Quality)
                output.WriteLine($"Balanced: {RunSparseHitLine(TemporalUpscale.Balanced)}");
            Assert.True(run.MaxChange <= (preset == TemporalUpscale.Native ? MainSparseChange(preset) + 0.02f : 0.1f),
                $"{preset}: {run}");
            if (preset == TemporalUpscale.Quality)
                Assert.True(run.Mean >= 0.75f * run.Expected, $"{preset}: {run}");
        }

        [GpuTheory, InlineData(TemporalUpscale.Native), InlineData(TemporalUpscale.Quality)]
        public void A_slow_ramp_under_a_sparsely_hit_line_tracks_no_later_than_its_baseline(TemporalUpscale preset)
        {
            TrackRun shimmering = RunRampUnderLine(preset, withLine: true, hitInterval: SparseHitInterval);
            output.WriteLine($"{preset}: {shimmering}, main {MainSparseCreepingError(preset)}");
            if (preset == TemporalUpscale.Quality)
                output.WriteLine($"Balanced: {RunRampUnderLine(TemporalUpscale.Balanced, withLine: true, hitInterval: SparseHitInterval)}");
            Assert.True(shimmering.MeanError <= MainSparseCreepingError(preset) + 0.02f,
                $"{preset}: {shimmering}, main {MainSparseCreepingError(preset)}");
        }

        [GpuTheory, InlineData(TemporalUpscale.Native), InlineData(TemporalUpscale.Quality)]
        public void A_still_shallow_diagonal_step_between_strong_contrasts_holds_steady(TemporalUpscale preset)
        {
            // A 0.4-texel trim at luma 0.75 separates the 0.60 tile from the 0.11 fascia.
            StillRun run = RunStillStep(preset, trim: true);
            output.WriteLine($"{preset}: {run}, pixels {StepPixels(preset, trim: true).Count}");
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
        public void A_still_feature_the_release_keeps_tripping_holds_its_history(TemporalUpscale preset)
        {
            // Static half-texel stripes alias with the jitter over a checker whose 3x3 has enough contrast to release.
            // Zero motion, constant depth and equal scene/opaque inputs exclude disocclusion and reactive rejection.
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(DisplayW / factor), ih = (int)(DisplayH / factor), phases = TemporalJitter.PhaseCount(factor);
            int x = (int)MathF.Floor((iw / 2 + 0.5f) * factor);
            int y = (int)MathF.Floor((ih / 2 + 0.5f) * factor), pixel = (y * DisplayW + x) * 4;
            Matrix4x4 projection = Perspective((float)DisplayW / DisplayH);
            TemporalViewInput still = View(Eye, projection);
            using var rig = new Rig(iw, ih, DisplayW, DisplayH, NdcDepth(projection, SceneMetres));
            float[] motion = Pairs(iw, ih, (_, _) => Vector2.Zero);
            float lowestConfidence = float.PositiveInfinity, maxChange = 0f, previous = 0f;
            for (int n = 0; n < Converge + Measured; n++)
            {
                Vector2 jitter = TemporalJitter.Offset(n, phases);
                float[] scene = Grey(iw, ih, (sx, sy) =>
                {
                    float sampleX = sx + 0.5f - jitter.X;
                    float stripe = sampleX - MathF.Floor(sampleX) < 0.5f ? 0.80f : 0.15f;
                    return stripe + ((sx + sy) % 2 == 0 ? 0.10f : -0.10f);
                });
                rig.BeginFrame();
                rig.Fill(scene, scene, motion);
                rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, jitter, iw, ih, DisplayW, DisplayH, n > 0));
                if (n < Converge + Measured - phases - 1) continue;
                float value = Share(rig.ReadColor()[pixel], 0.05f, 0.90f);
                if (n >= Converge + Measured - phases)
                {
                    float[] state = rig.ReadState();
                    lowestConfidence = MathF.Min(lowestConfidence, state[pixel]);
                    maxChange = MathF.Max(maxChange, MathF.Abs(value - previous));
                    output.WriteLine($"{preset} frame {n}: confidence {state[pixel]}, lock {state[pixel + 1]}, "
                        + $"drift {state[pixel + 2]}, swing {state[pixel + 3]}, contrast share {value}");
                }
                previous = value;
            }
            output.WriteLine($"{preset}: lowest confidence {lowestConfidence}, MaxChange {maxChange}");
            Assert.True(lowestConfidence > 4f * (1f / Max), $"{preset}: lowest confidence {lowestConfidence}");
            Assert.True(maxChange <= 0.1f, $"{preset}: MaxChange {maxChange}");
        }

        [GpuTheory, InlineData(TemporalUpscale.Native), InlineData(TemporalUpscale.Quality)]
        public void A_shadow_creeping_over_a_shimmering_line_tracks_no_later_than_its_baseline(TemporalUpscale preset)
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

        [GpuTheory, InlineData(TemporalUpscale.Native), InlineData(TemporalUpscale.Quality)]
        public void A_light_step_under_a_flickering_line_settles_no_slower_than_today(TemporalUpscale preset)
        {
            SettleRun run = RunLightStepOverLine(preset, hitInterval: 8);
            output.WriteLine($"{preset}: {run}, main {MainLightStepOverLineFrames(preset)}");
            Assert.True(run.Frames <= MainLightStepOverLineFrames(preset), $"{preset}: {run}");
        }

        // Measured against unchanged engine commit 306408694f2e445e3f3b22eec4cbee4c2a3770e9 on Metal.
        internal static int MainLightStepOverLineFrames(TemporalUpscale preset) => preset switch
        {
            TemporalUpscale.Native => 19,
            TemporalUpscale.Quality => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };

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

        // Measured against unchanged engine commit 55256bd3ae6fa75ca2055c9fd348360b8236933c on Metal.
        internal static float MainSparseChange(TemporalUpscale preset) => preset switch
        {
            TemporalUpscale.Native => 0.059092026f,
            TemporalUpscale.Quality => 0.053124707f,
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };

        // Measured against unchanged engine commit 55256bd3ae6fa75ca2055c9fd348360b8236933c on Metal.
        internal static float MainSparseCreepingError(TemporalUpscale preset) => preset switch
        {
            TemporalUpscale.Native => 0.056105882f,
            TemporalUpscale.Quality => 0.056118146f,
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };

        internal static StillRun RunStillStep(TemporalUpscale preset, bool trim)
        {
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(DisplayW / factor), ih = (int)(DisplayH / factor);
            List<int> pixels = StepPixels(preset, trim);
            float expected = 0f;
            foreach (int p in pixels)
                expected += StepCoverage(p % DisplayW / factor, p / DisplayW / factor, 1f / factor, ih, trim);
            return RunStill(preset, (jitter, _) => StillStepScene(iw, ih, jitter, trim), pixels,
                0.11f, trim ? 0.75f : 0.60f, expected / pixels.Count);
        }

        /// <summary>The display pixels a boundary of the step crosses, the pixels the step facts measure.</summary>
        internal static List<int> StepPixels(TemporalUpscale preset, bool trim)
        {
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int ih = (int)(DisplayH / factor);
            var pixels = new List<int>();
            for (int y = 2; y < DisplayH - 2; y++)
                for (int x = 2; x < DisplayW - 2; x++)
                    if (Transitions(x / factor, y / factor, 1f / factor, ih, trim)) pixels.Add(y * DisplayW + x);
            Assert.NotEmpty(pixels);
            return pixels;
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

        internal static StillRun RunSparseHitLine(TemporalUpscale preset)
        {
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(DisplayW / factor), ih = (int)(DisplayH / factor);
            int column = iw / 2, centre = (int)MathF.Floor((column + 0.5f) * factor);
            var pixels = ColumnPixels(factor, column);
            return RunStill(preset, (jitter, frame) => SparseHitLineScene(iw, ih, jitter, frame),
                pixels, Wall, Line, 1f / SparseHitInterval, DisplayH / 2 * DisplayW + centre);
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

        internal static TrackRun RunRampUnderLine(TemporalUpscale preset, bool withLine, int hitInterval = 8)
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
                float[] scene = RampUnderLineScene(iw, ih, jitter, n, withLine, hitInterval);
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

        internal static SettleRun RunLightStepOverLine(TemporalUpscale preset, int hitInterval)
        {
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(DisplayW / factor), ih = (int)(DisplayH / factor), phases = TemporalJitter.PhaseCount(factor);
            Matrix4x4 projection = Perspective((float)DisplayW / DisplayH);
            TemporalViewInput still = View(Eye, projection);
            List<int> pixels = ColumnPixels(factor, iw / 2);
            // The reference has always had the post-step lighting. Match its jitter phases to the stepped run,
            // because even settled coverage varies through the cycle. Both the wall and line halve at the step.
            float[][] Trajectory(bool stepped)
            {
                using var rig = new Rig(iw, ih, DisplayW, DisplayH, NdcDepth(projection, SceneMetres));
                float[] motion = Pairs(iw, ih, (_, _) => Vector2.Zero);
                var values = new float[Measured][];
                for (int n = 0; n < Converge + Measured; n++)
                {
                    Vector2 jitter = TemporalJitter.Offset(n, phases);
                    float scale = !stepped || n >= Converge ? 0.5f : 1f;
                    float[] scene = Grey(iw, ih, (x, _) =>
                        scale * (n % hitInterval == 0 && x == iw / 2 ? Line : Wall));
                    rig.BeginFrame();
                    rig.Fill(scene, scene, motion);
                    rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, jitter, iw, ih, DisplayW, DisplayH, n > 0));
                    if (n < Converge) continue;
                    float[] color = rig.ReadColor();
                    var sample = new float[pixels.Count];
                    for (int p = 0; p < pixels.Count; p++) sample[p] = color[pixels[p] * 4];
                    values[n - Converge] = sample;
                }
                return values;
            }
            float[][] step = Trajectory(stepped: true), target = Trajectory(stepped: false);
            float contrast = Weighted(Wall) - Weighted(Wall * 0.5f);
            int frames = 1;
            for (int n = 0; n < Measured; n++)
                for (int p = 0; p < pixels.Count; p++)
                    if (MathF.Abs(Weighted(step[n][p]) - Weighted(target[n][p])) > 0.05f * contrast)
                        frames = n + 2;
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

        internal static float[] SparseHitLineScene(int iw, int ih, Vector2 jitter, int frame)
            => Grey(iw, ih, (x, _) => frame % SparseHitInterval == 0 && x == iw / 2 ? Line : Wall);

        internal static float[] RampUnderLineScene(int iw, int ih, Vector2 jitter, int frame, bool withLine,
            int hitInterval = 8)
            => Grey(iw, ih, (x, _) => withLine && frame % hitInterval == 0 && x == iw / 2
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

        // A pixel is a transition pixel when any boundary of the step crosses it: the slab edge, and with the trim
        // also the tile-to-trim and trim-to-fascia edges either side of it.
        static bool Transitions(float x, float y, float side, int ih, bool trim)
        {
            foreach (float offset in trim ? TrimBoundaries : SlabBoundary)
            {
                float edge = Below(x, y, side, ih, offset);
                if (edge > 0.05f && edge < 0.95f) return true;
            }
            return false;
        }

        static readonly float[] SlabBoundary = { 0f }, TrimBoundaries = { -0.2f, 0f, 0.2f };

        // Integrate the clipped linear edge analytically over each display pixel, independently of jitter.
        static float StepCoverage(float x, float y, float side, int ih, bool trim)
        {
            if (!trim) return Below(x, y, side, ih, 0f);
            float tile = Below(x, y, side, ih, -0.2f), strip = Below(x, y, side, ih, 0.2f) - tile;
            return tile * Share(0.60f, 0.11f, 0.75f) + strip;
        }

        // The share of the pixel on the tile side of the boundary offset from StepY by the given texels.
        static float Below(float x, float y, float side, int ih, float offset)
        {
            float a = StepY(x, ih) + offset - y;
            float b = a + 0.2f * side;
            static float Integral(float v, float height) => v <= 0f ? 0f
                : v < height ? 0.5f * v * v : height * (v - 0.5f * height);
            return (Integral(b, side) - Integral(a, side)) / (0.2f * side * side);
        }
    }
}
