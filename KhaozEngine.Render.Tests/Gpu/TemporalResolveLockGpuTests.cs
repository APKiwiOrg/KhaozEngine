using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Step 6 of TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3, thin feature retention, against both sides of risk 1: a
    /// moving thin feature must not trail (section 7 acceptance 3) and a still one narrower than a texel must not
    /// shimmer (acceptance 2). The camera is still, and the uniforms come from
    /// <see cref="TemporalResolveMath.BuildUniforms"/> as in <see cref="TemporalResolveRendererGpuTests"/>. The moving
    /// and still line facts write their measurements to the test output and into their failure messages.
    /// </summary>
    public sealed class TemporalResolveLockGpuTests
    {
        /// <summary>The share of a feature's contrast above the background at which a pixel it left counts as trail.</summary>
        const float TrailContrast = 0.1f;
        const float SceneMetres = 2f;
        /// <summary>How far ahead a keyed line stands, in front of the wall at <see cref="SceneMetres"/>, as a blade
        /// stands in front of the ground behind it. The dilation of step 1 then carries its motion.</summary>
        const float LineMetres = 1.5f;
        const int LineW = 96, LineH = 12, LineRow = LineH / 2;
        const float LineStart = 4f;
        static readonly Vector3 Eye = new(0f, 1.7f, 10f);
        static readonly float Bg = Q(0.1f), Line = Q(1f);
        static readonly float[] Speeds = { 0.3f, 0.6f, 0.9f };

        readonly ITestOutputHelper _output;

        public TemporalResolveLockGpuTests(ITestOutputHelper output) => _output = output;

        [GpuFact]
        public void A_thin_ridge_takes_a_lock_and_the_lock_holds_its_luma_against_the_clip()
        {
            const int N = 8;
            const float Max = TemporalResolveTuning.MaxAccumulation;
            Matrix4x4 projection = Perspective(1f);
            TemporalViewInput still = View(Eye, projection);
            float ndc = NdcDepth(projection, SceneMetres);
            float sceneLinear = TemporalResolveMath.LinearDepth(ndc, TemporalResolveMath.DepthParams(projection));
            TemporalResolveUniforms Uniforms(bool historyValid) => TemporalResolveMath.BuildUniforms(still, still,
                Vector2.Zero, N, N, N, N, historyValid);
            int At(int x, int y) => y * N + x;
            float[] zero = Pairs(N, N, (_, _) => Vector2.Zero);
            using var rig = new TemporalResolveRig(N, N, N, N, ndc);
            float bg = Q(0.1f), line = Q(1f), held = Q(0.55f);

            // Frame one: the line is in the current samples. The ridge takes a lock on its own column only.
            rig.BeginFrame();
            rig.Fill(Grey(N, N, (x, _) => x == 4 ? line : bg), Grey(N, N, (x, _) => x == 4 ? line : bg), zero);
            rig.Resolve(Uniforms(historyValid: false));
            float[] state = rig.ReadState();
            Assert.Equal(1f, state[At(4, 3) * 2 + 1], 1e-3);
            Assert.Equal(0f, state[At(3, 3) * 2 + 1], 1e-3);
            Assert.Equal(0f, state[At(5, 3) * 2 + 1], 1e-3);

            // Frames two and three: this frame's samples miss the line and the history holds its average. The flat box
            // would clip the history to the background. A lock one decay below a refresh is still at least
            // 1 / LockHoldGain, so its hold is whole and the history keeps all its luma. A lock that has run down below
            // that holds only its share.
            float accumulated = Q(8f / Max) * Max;
            float c = Weighted(bg), h = Weighted(held);
            foreach (float stored in new[] { 1f, Q(0.3f) })
            {
                rig.BeginFrame();
                rig.Fill(Grey(N, N, (_, _) => bg), Grey(N, N, (_, _) => bg), zero);
                rig.FillHistory(Grey(N, N, (x, _) => x == 4 ? held : bg),
                    Pairs(N, N, (x, _) => new Vector2(Q(8f / Max), x == 4 ? stored : 0f)), sceneLinear);
                rig.Resolve(Uniforms(historyValid: true));

                float lockAfter = stored - TemporalResolveTuning.LockDecay;
                float hold = MathF.Min(1f, lockAfter * TemporalResolveTuning.LockHoldGain);
                float kept = c + (h - c) * hold;
                float expected = Unweighted(kept + (c - kept) / (accumulated + 1f));
                float[] color = rig.ReadColor();
                state = rig.ReadState();
                Assert.Equal(lockAfter, state[At(4, 3) * 2 + 1], 1e-3);
                Assert.Equal(expected, color[At(4, 3) * 4], 1e-2);
                Assert.Equal(bg, color[At(2, 3) * 4], 1e-3);
                if (stored == 1f)
                    Assert.True(color[At(4, 3) * 4] > 0.35f, $"the locked line kept {color[At(4, 3) * 4]} of its luma");
            }
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.UltraPerformance)]
        [InlineData(TemporalUpscale.Quality)]
        public void A_keyed_thin_feature_leaves_no_trail_two_frames_after_it_passes(TemporalUpscale preset)
        {
            // Design section 7 acceptance 3. A bright line one internal pixel wide converges still in front of the wall,
            // then moves 0.3 to 0.9 display pixels a frame with its true motion on its own texels, as the foliage wind
            // writes it for a swaying blade. Two frames after it passes a pixel, no more than one display pixel may keep
            // TrailContrast of its contrast.
            (LineRun[] runs, string message) = RunKeyed(preset);
            _output.WriteLine(message);
            Assert.True(Array.TrueForAll(runs, r => r.Trail <= 1), message);
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.UltraPerformance)]
        [InlineData(TemporalUpscale.Quality)]
        public void A_keyed_thin_feature_keeps_half_its_still_contrast_while_it_moves(TemporalUpscale preset)
        {
            // The other side of risk 1 for the same keyed line: the history follows it, so from the eighth frame of
            // motion on it keeps, averaged over the window, at least half the contrast it converges to held still at
            // the same position. The mean is the measure and not the worst frame, because at Quality the line is 1.5
            // display pixels wide and straddles two pixels on some frames, where its peak drops in any render, so one
            // frame's peak measures where the line sits on the grid as much as what the resolve kept. Resampling blur
            // that accumulates lowers every frame of the window, so the mean still catches it. The worst frame is in
            // the message.
            (LineRun[] runs, string message) = RunKeyed(preset);
            _output.WriteLine(message);
            Assert.True(Array.TrueForAll(runs, r => r.KeptMean >= 0.5f), message);
        }

        // The keyed line at each speed. The motion steps 0.3 display pixels at a time and both presets' grids line up
        // again every three display pixels, so ten still runs give the still contrast at every position it takes.
        static (LineRun[] Runs, string Message) RunKeyed(TemporalUpscale preset)
        {
            const int StillFrames = 48, MovingFrames = 32, Offsets = 10;
            float factor = TemporalSettings.DisplayOverInternal(preset);
            var still = new float[Offsets];
            for (int m = 0; m < Offsets; m++)
                still[m] = RunLine(preset, LineStart + 0.3f * m / factor, 0f, keyed: true, StillFrames, StillFrames,
                    StillFrames, _ => 1f).Still;
            var runs = new LineRun[Speeds.Length];
            var report = new List<string> { "still " + string.Join(" ", Array.ConvertAll(still, v => v.ToString("P0"))) };
            for (int i = 0; i < Speeds.Length; i++)
            {
                int steps = (int)MathF.Round(Speeds[i] / 0.3f);
                runs[i] = RunLine(preset, LineStart, Speeds[i], keyed: true, StillFrames, StillFrames + MovingFrames,
                    StillFrames + 2, moved => still[moved * steps % Offsets]);
                report.Add($"{Speeds[i]} px/frame: {runs[i]}");
            }
            return (runs, $"{preset}: " + string.Join(", ", report));
        }

        [GpuFact]
        public void A_thin_feature_moving_without_motion_trails_no_more_than_one_internal_texel()
        {
            // Design section 3 step 6 and risk 1, at the preset whose texels are widest: UltraPerformance, a third of the
            // display per axis. Acceptance 3 is written for a keyed object, and the keyed facts above are its bar. This is
            // the worse unkeyed case: the same line crosses a still wall at the wall's depth with zero motion, as a
            // moving feature with no motion of its own renders. The depth test cannot help and the history cannot
            // follow it, so the lock may hold only what the current neighbourhood still supports. Two frames after the
            // line passes, no more than one internal texel of display pixels keeps TrailContrast of its contrast. How
            // much of the line shows where it is now is reported and not held to a floor: without motion it is set by
            // the accumulation, and measured the same with the lock off. A lock too weak to hold thin features fails
            // the still line fact below.
            const TemporalUpscale Preset = TemporalUpscale.UltraPerformance;
            int allowance = (int)TemporalSettings.DisplayOverInternal(Preset);
            var report = new List<string>();
            int worst = 0;
            foreach (float speed in Speeds)
            {
                LineRun run = RunLine(Preset, LineStart, speed, keyed: false, stillFrames: 0, frames: 64, measureFrom: 16,
                    _ => 1f);
                worst = Math.Max(worst, run.Trail);
                report.Add($"{speed} px/frame: {run}");
            }
            string message = $"Trail two frames after the line passed, allowance {allowance} px. " + string.Join(", ", report);
            _output.WriteLine(message);
            Assert.True(worst <= allowance, message);
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        [InlineData(TemporalUpscale.UltraPerformance)]
        public void A_still_line_narrower_than_a_texel_holds_steady_at_its_coverage(TemporalUpscale preset)
        {
            // Design section 7 acceptance 2, the owner's grass shimmer. A still vertical line three eighths of a texel
            // wide, centred in its texel column, is in that column's samples on 3 of every 8 frames and absent from the
            // whole neighbourhood on the rest. The pattern is the one the native jitter gives it: Halton x offsets 0,
            // 0.125 and -0.125, phases 0, 4 and 5, put the sample inside it. Without the lock a missed frame clips the
            // history to the wall. Once converged, the line's pixels change by at most a tenth of its contrast from frame
            // to frame. At Native the pixel is the texel, and it averages within a quarter of the coverage, 3/8 of the
            // contrast. Upscaled, the pixel at the line's centre is narrower than the texel and the line covers more of
            // it, and the reconstruction weights the frames whose sample lands near it, which are the frames that hit
            // the line, so there it only has to reach three quarters of the coverage. Values are read in the
            // luma-weighted space the resolve accumulates in, where coverage is linear.
            const int DisplayW = 48, DisplayH = 12, Row = DisplayH / 2, Converge = 64, Measured = 16;
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(DisplayW / factor), ih = (int)(DisplayH / factor), phases = TemporalJitter.PhaseCount(factor);
            int column = iw / 2;
            Matrix4x4 projection = Perspective((float)DisplayW / DisplayH);
            TemporalViewInput still = View(Eye, projection);
            float[] zero = Pairs(iw, ih, (_, _) => Vector2.Zero);
            float[] lit = Grey(iw, ih, (x, _) => x == column ? Line : Bg), missed = Grey(iw, ih, (_, _) => Bg);
            float wBg = Weighted(Bg), wContrast = Weighted(Line) - wBg;

            var pixels = new List<int>();
            for (int c = 0; c < DisplayW; c++)
                if ((int)MathF.Floor((c + 0.5f) / factor) == column) pixels.Add(c);
            int centre = (int)MathF.Floor((column + 0.5f) * factor);
            Assert.Contains(centre, pixels);

            using var rig = new TemporalResolveRig(iw, ih, DisplayW, DisplayH, NdcDepth(projection, SceneMetres));
            var previous = new float[DisplayW];
            float change = 0f, sum = 0f;
            int changeFrame = 0, changePixel = 0;
            for (int n = 0; n < Converge + Measured; n++)
            {
                Vector2 jitter = TemporalJitter.Offset(n, phases);
                bool hit = (n % 8) is 0 or 4 or 5;
                rig.BeginFrame();
                rig.Fill(hit ? lit : missed, hit ? lit : missed, zero);
                rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, jitter, iw, ih, DisplayW, DisplayH,
                    historyValid: n > 0));
                if (n < Converge - 1) continue;
                float[] color = rig.ReadColor();
                foreach (int c in pixels)
                {
                    float value = (Weighted(color[(Row * DisplayW + c) * 4]) - wBg) / wContrast;
                    if (n >= Converge && MathF.Abs(value - previous[c]) > change)
                        (change, changeFrame, changePixel) = (MathF.Abs(value - previous[c]), n, c);
                    if (n >= Converge && c == centre) sum += value;
                    previous[c] = value;
                }
            }
            float mean = sum / Measured, coverage = 3f / 8f;
            bool meanHolds = preset == TemporalUpscale.Native
                ? MathF.Abs(mean - coverage) <= 0.25f * coverage
                : mean >= 0.75f * coverage;
            string message = $"{preset}: largest frame to frame change {change:P1} of the contrast at pixel {changePixel} "
                + $"frame {changeFrame}, centre pixel {centre} mean {mean:P1} against coverage {coverage:P1}";
            _output.WriteLine(message);
            Assert.True(change <= 0.1f && meanHolds, message);
        }

        /// <summary>What one run of the moving line measured. Trail is the most display pixels, wholly left of the line's
        /// one internal pixel reach two frames before, that stay above <see cref="TrailContrast"/> of the contrast, and
        /// Trail5 the same at 5 percent. Still is the contrast the line showed over its last eight still frames.
        /// KeptLeast, at frame KeptLeastFrame, and KeptMean are the least and the mean the line shows at its true
        /// position over the measured frames, from the eighth frame of motion on, as a share of the reference contrast
        /// for that position.</summary>
        readonly record struct LineRun(int Trail, int TrailFrame, int Trail5, float Peak, float Still, float KeptLeast,
            int KeptLeastFrame, float KeptMean)
        {
            public override string ToString() => $"trail {Trail} px at frame {TrailFrame} (5%: {Trail5} px), trail peak "
                + $"{Peak:P0}, kept least {KeptLeast:P0} at frame {KeptLeastFrame} mean {KeptMean:P0}";
        }

        // A line one internal pixel wide, lit in each row at the texel whose jittered sample falls inside it, as a
        // rasteriser lights it. It holds at start for stillFrames, then moves speed display pixels a frame, keyed (its
        // texels carry its motion and stand at LineMetres) or unkeyed (zero motion at the wall's depth). One frame
        // lights display pixels only within one internal pixel of the line, so a display pixel wholly left of that reach
        // two frames ago shows history alone. reference gives the contrast to hold the line to after a number of frames
        // of motion. The reset frame must show the line, or the rig renders nothing and the measure proves nothing.
        static LineRun RunLine(TemporalUpscale preset, float start, float speed, bool keyed, int stillFrames, int frames,
            int measureFrom, Func<int, float> reference)
        {
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(LineW / factor), ih = (int)(LineH / factor), phases = TemporalJitter.PhaseCount(factor);
            Matrix4x4 projection = Perspective((float)LineW / LineH);
            TemporalViewInput still = View(Eye, projection);
            float wallNdc = NdcDepth(projection, SceneMetres), lineNdc = keyed ? NdcDepth(projection, LineMetres) : wallNdc;
            float threshold = Bg + TrailContrast * (Line - Bg), threshold5 = Bg + 0.05f * (Line - Bg);
            var motion = new Vector2(speed / LineW, 0f);

            using var rig = new TemporalResolveRig(iw, ih, LineW, LineH, wallNdc);
            int trail = 0, trailFrame = 0, trail5 = 0, stillCount = 0, keptCount = 0, keptLeastFrame = 0;
            float peak = 0f, stillSum = 0f, keptLeast = float.MaxValue, keptSum = 0f;
            for (int n = 0; n < frames; n++)
            {
                Vector2 jitter = TemporalJitter.Offset(n, phases);
                int moved = Math.Max(0, n - stillFrames);
                float left = start + moved * speed / factor;
                bool InLine(int x) => x + 0.5f - jitter.X >= left && x + 0.5f - jitter.X < left + 1f;
                bool carries = keyed && moved > 0;
                float[] feature = Grey(iw, ih, (x, _) => InLine(x) ? Line : Bg);
                rig.BeginFrame();
                rig.Fill(feature, feature, Pairs(iw, ih, (x, _) => carries && InLine(x) ? motion : Vector2.Zero));
                rig.FillDepth(Grey(iw, ih, 1, (x, _) => InLine(x) ? lineNdc : wallNdc));
                rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, jitter, iw, ih, LineW, LineH,
                    historyValid: n > 0));

                float[] color = rig.ReadColor();
                float shown = 0f;
                for (int c = (int)(factor * left); c < (int)MathF.Ceiling(factor * (left + 1f)); c++)
                    shown = MathF.Max(shown, color[(LineRow * LineW + c) * 4]);
                float contrast = (shown - Bg) / (Line - Bg);
                if (n == 0) Assert.True(shown > threshold, $"{speed} px/frame: the reset frame shows the line at {shown}");
                if (n < stillFrames && n >= stillFrames - 8) { stillSum += contrast; stillCount++; }
                if (n < measureFrom) continue;
                if (stillFrames == 0 || moved >= 8)
                {
                    float kept = contrast / reference(moved);
                    if (kept < keptLeast) (keptLeast, keptLeastFrame) = (kept, n);
                    keptSum += kept;
                    keptCount++;
                }

                float passed = factor * (start + Math.Max(0, moved - 2) * speed / factor - 1f);
                int count = 0, count5 = 0;
                for (int c = 0; c + 1 <= passed; c++)
                {
                    float value = color[(LineRow * LineW + c) * 4];
                    peak = MathF.Max(peak, (value - Bg) / (Line - Bg));
                    if (value > threshold) count++;
                    if (value > threshold5) count5++;
                }
                if (count > trail) (trail, trailFrame) = (count, n);
                trail5 = Math.Max(trail5, count5);
            }
            return new LineRun(trail, trailFrame, trail5, peak, stillCount > 0 ? stillSum / stillCount : 0f,
                keptCount > 0 ? keptLeast : 0f, keptLeastFrame, keptCount > 0 ? keptSum / keptCount : 0f);
        }

        static float Q(float v) => (float)(Half)v;                    // what a half float texel holds
        static float Weighted(float grey) => TemporalResolveMath.ToWeighted(new Vector3(grey)).X;
        static float Unweighted(float y) => y / (1f - y);             // FromWeighted for grey

        static Matrix4x4 Perspective(float aspect) => Matrix4x4.CreatePerspectiveFieldOfView(1f, aspect, 0.1f, 100f);

        static TemporalViewInput View(Vector3 eye, Matrix4x4 projection)
        {
            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY);
            return new TemporalViewInput(view, projection, view * projection);
        }

        // What the depth target holds for a point this far ahead: the projection's NDC depth.
        static float NdcDepth(in Matrix4x4 projection, float metres)
        {
            Vector4 clip = Vector4.Transform(new Vector4(0f, 0f, -metres, 1f), projection);
            return clip.Z / clip.W;
        }

        static float[] Grey(int width, int height, Func<int, int, float> value) => Grey(width, height, 4, value);

        // One value per texel repeated over its channels, the fourth of four set to 1 as an opaque alpha.
        static float[] Grey(int width, int height, int channels, Func<int, int, float> value)
        {
            var v = new float[width * height * channels];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * channels;
                    for (int ch = 0; ch < channels; ch++) v[i + ch] = ch == 3 ? 1f : value(x, y);
                }
            return v;
        }

        static float[] Pairs(int width, int height, Func<int, int, Vector2> value)
        {
            var v = new float[width * height * 2];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    Vector2 p = value(x, y);
                    v[(y * width + x) * 2] = p.X;
                    v[(y * width + x) * 2 + 1] = p.Y;
                }
            return v;
        }
    }
}
