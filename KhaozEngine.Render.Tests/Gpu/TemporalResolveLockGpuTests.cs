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
    /// shimmer (acceptance 2). A sub-texel feature must keep holding when its surface moves as a whole, against the sky
    /// and beside a nearer still surface, and a nearer surface crossing it must not carry it away. Same rig and camera
    /// as <see cref="TemporalResolveRendererGpuTests"/>, through <see cref="TemporalResolveGpuFacts"/>. The moving and
    /// still line facts write their measurements to the test output and into their failure messages.
    /// </summary>
    public sealed class TemporalResolveLockGpuTests : TemporalResolveGpuFacts
    {
        /// <summary>The share of a feature's contrast above the background at which a pixel it left counts as trail.</summary>
        const float TrailContrast = 0.1f;
        /// <summary>How far ahead a keyed line stands, in front of the wall at <see cref="TemporalResolveGpuFacts.SceneMetres"/>,
        /// as a blade stands in front of the ground behind it. The dilation of step 1 then carries its motion.</summary>
        const float LineMetres = 1.5f;
        const int LineW = 96, LineH = 12, LineRow = LineH / 2;
        const float LineStart = 4f;
        static readonly float Bg = Q(0.1f), Line = Q(1f), Occluder = Q(0.05f);
        static readonly float[] Speeds = { 0.3f, 0.6f, 0.9f };
        static readonly float[] OccluderSpeeds = { 0.1f, 0.3f, 0.6f, 0.9f };

        readonly ITestOutputHelper _output;

        public TemporalResolveLockGpuTests(ITestOutputHelper output) => _output = output;

        [GpuFact]
        public void A_thin_ridge_takes_a_lock_and_the_lock_holds_its_luma_against_the_clip()
        {
            float[] zero = Motion((_, _) => Vector2.Zero);
            using var rig = new Rig();
            float bg = Q(0.1f), line = Q(1f), held = Q(0.55f);

            // Frame one: the line is in the current samples. The ridge takes a lock on its own column only.
            rig.BeginFrame();
            rig.Fill(Grey((x, _) => x == 4 ? line : bg), Grey((x, _) => x == 4 ? line : bg), zero);
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
                rig.Fill(Grey((_, _) => bg), Grey((_, _) => bg), zero);
                rig.FillHistory(Grey((x, _) => x == 4 ? held : bg),
                    State((x, _) => new Vector2(Q(8f / Max), x == 4 ? stored : 0f)), SceneLinear);
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
            // that accumulates lowers every frame of the window, so the mean still catches it. At UltraPerformance the
            // line is 3 display pixels wide and always covers one whole pixel, so there the worst frame keeps half too.
            // The worst frame is in the message.
            (LineRun[] runs, string message) = RunKeyed(preset);
            _output.WriteLine(message);
            bool worstHolds = preset != TemporalUpscale.UltraPerformance || Array.TrueForAll(runs, r => r.KeptLeast >= 0.5f);
            Assert.True(Array.TrueForAll(runs, r => r.KeptMean >= 0.5f) && worstHolds, message);
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
            // history to the wall. HoldsLikeAStillLine sets the bounds.
            SubTexelRun run = RunSubTexelLine(preset, followStep: 0f);
            _output.WriteLine($"{preset}: {run}");
            Assert.True(HoldsLikeAStillLine(preset, run), $"{preset}: {run}");
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.Native, 0.3f)]
        [InlineData(TemporalUpscale.Native, 0.5f)]
        [InlineData(TemporalUpscale.UltraPerformance, 0.3f)]
        public void A_line_narrower_than_a_texel_on_a_swaying_surface_holds_like_a_still_one(TemporalUpscale preset,
            float speed)
        {
            // The same line on a surface that sways as the foliage wind sways a blade, a sine with a 24 frame period
            // peaking at speed internal pixels a frame, so it swings back and forth about its place and crosses a few
            // columns. The surface writes its true motion in every texel every frame, and the frames that see the line
            // light the column its centre is in. Its history is where the motion says, so it must hold as the still
            // line does. The line crosses pixels, so what is measured is its coverage summed over the pixels within
            // one and a half texels of its centre, in texels, which does not depend on where it sits on the grid. The
            // speeds stay under LockMotionStartPixels in display pixels, where the design keeps locks, so
            // UltraPerformance takes 0.3 alone. Quality is left out: there the pixels beside the line's centre share its
            // reconstruction but take a ridge only on the frames whose jitter puts their centre texel on it, and even
            // held still the line sums to 28 percent, at the floor.
            SubTexelRun run = RunSwayingLine(preset, speed);
            _output.WriteLine($"{preset} at {speed} px/frame: {run}");
            Assert.True(HoldsLikeAStillLine(preset, run), $"{preset} at {speed} px/frame: {run}");
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.UltraPerformance)]
        public void A_line_narrower_than_a_texel_on_a_surface_the_camera_follows_holds_like_a_still_one(TemporalUpscale preset)
        {
            // A follow camera. The camera steps sideways every frame and the surface carrying the line moves with it, as
            // an avatar's staff or blade does, so on screen it is still and its motion is zero, while the camera alone
            // would have moved a static point there 8 internal pixels. Its history is exactly where it was, so it must
            // hold as the still line does.
            SubTexelRun run = RunSubTexelLine(preset, followStep: 8f);
            _output.WriteLine($"{preset}: {run}");
            Assert.True(HoldsLikeAStillLine(preset, run), $"{preset}: {run}");
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        [InlineData(TemporalUpscale.UltraPerformance)]
        public void A_line_narrower_than_a_texel_against_the_sky_holds_like_one_on_a_wall(TemporalUpscale preset)
        {
            // The still line with the background sentinel around it in place of the wall, as a far blade stands against
            // the sky. Where a pixel's centre texel is sky and the line is the nearest surface around it, the edge
            // release compares the line's motion with the motion the camera's rotation gives the sky at that texel, zero
            // under a still camera. Without that, Quality's pixel at the line's centre averages 5.1 percent. The depth
            // stored where the line was missed is the background's, farther than any surface, and that must not release
            // the line's own lock either.
            SubTexelRun run = RunSubTexelLine(preset, followStep: 0f, overSky: true);
            _output.WriteLine($"{preset}: {run}");
            Assert.True(HoldsLikeAStillLine(preset, run), $"{preset}: {run}");
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.Native, -1)]
        [InlineData(TemporalUpscale.Native, 1)]
        [InlineData(TemporalUpscale.Quality, -1)]
        [InlineData(TemporalUpscale.Quality, 1)]
        [InlineData(TemporalUpscale.UltraPerformance, -1)]
        [InlineData(TemporalUpscale.UltraPerformance, 1)]
        public void A_line_narrower_than_a_texel_beside_a_still_nearer_surface_holds_like_a_still_one(TemporalUpscale preset,
            int side)
        {
            // The still line with a still dark surface standing LineMetres ahead in every texel on one side of its column,
            // as a blade stands beside a rock. The surface is the nearest in the line's neighbourhood, so step 1 takes its
            // depth and its zero motion, and the depth stored at the line is the wall's, farther. Nothing there moves, so
            // the line's history is its own and it must hold as the still line does. Releasing the lock wherever the
            // history lies farther than the dilated surface averaged 5.9 percent at Native and 5.4 at UltraPerformance
            // with the surface to the left, and 4.3 at Quality with it to the right.
            SubTexelRun run = RunSubTexelLine(preset, followStep: 0f, beside: side);
            _output.WriteLine($"{preset}, surface on side {side}: {run}");
            Assert.True(HoldsLikeAStillLine(preset, run), $"{preset}, surface on side {side}: {run}");
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.UltraPerformance)]
        public void A_blade_narrower_than_a_texel_swaying_over_still_ground_holds_like_a_still_one(TemporalUpscale preset)
        {
            // Only the line sways, a blade standing LineMetres ahead of still ground, and it writes its motion and its depth
            // on the frames that see it. The pixels beside it take its motion by the dilation of step 1 while their own
            // centre texel is the still ground, so the edge release acts on them. At 0.1 internal pixels a frame it holds
            // as the still line does. Quality is left out, where held still in front of the ground it sums to 28.4
            // percent, at the floor, and moving at 0.1 to 22.1. From 0.2 on the blade sums to 16 percent or less at every
            // preset with any lock tried, and the same with the edge release off: nothing carries its history through the
            // frames that miss it, so the history stays where the blade was last seen.
            SubTexelRun run = RunSwayingLine(preset, 0.1f, bladeOnly: true);
            _output.WriteLine($"{preset} at 0.1 px/frame: {run}");
            Assert.True(HoldsLikeAStillLine(preset, run), $"{preset} at 0.1 px/frame: {run}");
        }

        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        [InlineData(TemporalUpscale.UltraPerformance)]
        public void A_nearer_surface_crossing_a_held_line_carries_no_ghost_of_it(TemporalUpscale preset)
        {
            // Design section 7 acceptance 3 from the lock's side. The still line of the facts above holds its lock, and a
            // dark surface standing LineMetres ahead of it, as a trunk stands in front of a grass blade, crosses it at 0.1
            // to 0.9 internal pixels a frame with its true motion. The pixels ahead of the surface and on its leading edge
            // take its motion by the dilation of step 1 and read history the line left, and a lock carried with that
            // history would hold the line's luma where the line is not. No more than one display pixel may show the line
            // brighter than it should by TrailContrast of the contrast: on the surface once it has covered the pixel for
            // two frames, the bound the keyed line's trail takes, and off it anywhere brighter than the still image.
            var report = new List<string>();
            bool holds = true;
            foreach (float speed in OccluderSpeeds)
            {
                OccluderRun run = RunOccluder(preset, speed);
                holds &= run.Ghost <= 1;
                report.Add($"{speed} px/frame: {run}");
            }
            string message = $"{preset}: " + string.Join(", ", report);
            _output.WriteLine(message);
            Assert.True(holds, message);
        }

        /// <summary>What one crossing measured. Ghost is the most display pixels in one frame that show the line brighter
        /// than they should by <see cref="TrailContrast"/> of its contrast, and Ghost5 the same at 5 percent. Peak is the
        /// largest excess, as a share of the contrast, and where and when it was.</summary>
        readonly record struct OccluderRun(int Ghost, int GhostFrame, int Ghost5, float Peak, int PeakPixel, int PeakFrame)
        {
            public override string ToString() => $"ghost {Ghost} px at frame {GhostFrame} (5%: {Ghost5} px), peak {Peak:P0} "
                + $"at pixel {PeakPixel} frame {PeakFrame}";
        }

        // The line of RunSubTexelLine in the middle column, and a surface OccluderTexels wide and Occluder dark standing
        // LineMetres ahead, its leading edge two texels short of the line, rasterised at the texels whose jittered sample
        // it covers. Both converge for 64 frames, which also record the still image, the most each pixel showed over the
        // last eight. The surface then moves right at speed internal pixels a frame, its true motion on its own texels,
        // until its trailing edge is past the line. A pixel wholly on the surface now and two frames ago should show the
        // surface, and one within a texel of its edge is left out on a frame the line shows, whose reconstruction reaches
        // a texel. Any other pixel should show no more than the still image or the wall.
        static OccluderRun RunOccluder(TemporalUpscale preset, float speed)
        {
            const int DisplayW = 96, DisplayH = 12, Row = DisplayH / 2, Converge = 64, OccluderTexels = 6;
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(DisplayW / factor), ih = (int)(DisplayH / factor), phases = TemporalJitter.PhaseCount(factor);
            int column = iw / 2;
            float startLead = column - 2f, endLead = column + 2f + OccluderTexels;
            int frames = Converge + (int)MathF.Ceiling((endLead - startLead) / speed);
            float Lead(int n) => startLead + speed * Math.Max(0, n - Converge + 1);
            Matrix4x4 projection = Perspective((float)DisplayW / DisplayH);
            TemporalViewInput still = View(Eye, projection);
            float wallNdc = NdcDepth(projection, SceneMetres), occluderNdc = NdcDepth(projection, LineMetres);

            using var rig = new Rig(iw, ih, DisplayW, DisplayH, wallNdc);
            var stillImage = new float[DisplayW];
            int ghost = 0, ghostFrame = 0, ghost5 = 0, peakPixel = 0, peakFrame = 0;
            float peak = 0f;
            for (int n = 0; n < frames; n++)
            {
                Vector2 jitter = TemporalJitter.Offset(n, phases);
                float lead = Lead(n), trail = lead - OccluderTexels, leadBefore = Lead(n - 2);
                bool Covered(int x) => x + 0.5f - jitter.X >= trail && x + 0.5f - jitter.X < lead;
                bool hit = (n % 8) is 0 or 4 or 5;
                var motion = new Vector2((lead - Lead(n - 1)) / iw, 0f);
                float[] scene = Grey(iw, ih, (x, _) => Covered(x) ? Occluder : hit && x == column ? Line : Bg);
                rig.BeginFrame();
                rig.Fill(scene, scene, Pairs(iw, ih, (x, _) => Covered(x) ? motion : Vector2.Zero));
                rig.FillDepth(DepthMap(iw, ih, x => Covered(x) ? occluderNdc : wallNdc));
                rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, jitter, iw, ih, DisplayW, DisplayH,
                    historyValid: n > 0));
                if (n < Converge - 8) continue;

                float[] color = rig.ReadColor();
                if (n < Converge)
                {
                    for (int c = 0; c < DisplayW; c++)
                        stillImage[c] = MathF.Max(stillImage[c], MathF.Max(Bg, color[(Row * DisplayW + c) * 4]));
                    continue;
                }
                if (n < Converge + 2) continue;
                float reach = hit && !Covered(column) ? 1f : 0f;
                int count = 0, count5 = 0;
                for (int c = 0; c < DisplayW; c++)
                {
                    float left = c / factor, right = (c + 1) / factor, expected = stillImage[c];
                    if (left >= trail && right <= lead)
                    {
                        if (left < leadBefore - OccluderTexels || right > leadBefore) continue;
                        if (left < trail + reach || right > lead - reach) continue;
                        expected = Occluder;
                    }
                    float excess = (color[(Row * DisplayW + c) * 4] - expected) / (Line - Bg);
                    if (excess > peak) (peak, peakPixel, peakFrame) = (excess, c, n);
                    if (excess > TrailContrast) count++;
                    if (excess > 0.05f) count5++;
                }
                if (count > ghost) (ghost, ghostFrame) = (count, n);
                ghost5 = Math.Max(ghost5, count5);
            }
            return new OccluderRun(ghost, ghostFrame, ghost5, peak, peakPixel, peakFrame);
        }

        // The still line's bounds. Once converged, the line's pixels change by at most a tenth of its contrast from frame
        // to frame. At Native the pixel is the texel, and it averages within a quarter of the coverage, 3/8 of the
        // contrast. So does coverage summed around a moving line at every preset, which is 3/8 wherever the line sits.
        // Upscaled rows read at one pixel guard stability and not coverage: the pixel at the line's centre is narrower
        // than the texel, so the line covers about 53 percent of it at Quality and all of it at UltraPerformance, and
        // the reconstruction weights the frames whose sample lands near it, which are the frames that hit the line.
        // There the mean only has to show the lock held, three quarters of the texel coverage. Values are read in the
        // luma-weighted space the resolve accumulates in, where coverage is linear.
        static bool HoldsLikeAStillLine(TemporalUpscale preset, SubTexelRun run)
        {
            const float Coverage = 3f / 8f;
            bool meanHolds = preset == TemporalUpscale.Native || run.Centre < 0
                ? MathF.Abs(run.Mean - Coverage) <= 0.25f * Coverage
                : run.Mean >= 0.75f * Coverage;
            return run.Change <= 0.1f && meanHolds;
        }

        /// <summary>What one run of the sub-texel line measured: the largest frame to frame change over the pixels
        /// centred in its column, where and when, and the mean at the pixel nearest its centre, as shares of the
        /// contrast in the luma-weighted space. A swaying run sums its coverage instead and has no pixel, so its
        /// <see cref="Centre"/> and <see cref="ChangePixel"/> are -1.</summary>
        readonly record struct SubTexelRun(float Change, int ChangePixel, int ChangeFrame, int Centre, float Mean)
        {
            public override string ToString() => Centre < 0
                ? $"largest frame to frame change {Change:P1} of the contrast at frame {ChangeFrame}, summed coverage mean "
                    + $"{Mean:P1} against 37.5%"
                : $"largest frame to frame change {Change:P1} of the contrast at pixel {ChangePixel} frame {ChangeFrame}, "
                    + $"centre pixel {Centre} mean {Mean:P1} against coverage 37.5%";
        }

        // A line three eighths of a texel wide in the middle texel column, lit on the frames n % 8 of 0, 4 and 5.
        // followStep is how many internal pixels the camera alone moves a static point on the wall each frame, while the
        // wall and the line move with the camera and write zero motion. overSky writes the background sentinel in every
        // texel the line does not light. beside puts a still Occluder surface LineMetres ahead in every texel left of the
        // column when negative, or right of it when positive. Converges 64 frames and measures 16.
        static SubTexelRun RunSubTexelLine(TemporalUpscale preset, float followStep, bool overSky = false, int beside = 0)
        {
            const int DisplayW = 48, DisplayH = 12, Row = DisplayH / 2, Converge = 64, Measured = 16;
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(DisplayW / factor), ih = (int)(DisplayH / factor), phases = TemporalJitter.PhaseCount(factor);
            int column = iw / 2;
            Matrix4x4 projection = Perspective((float)DisplayW / DisplayH);
            float wallNdc = NdcDepth(projection, SceneMetres);
            // A sideways step of d metres moves a point SceneMetres ahead M11 * d / SceneMetres in NDC, half the
            // internal width per unit.
            float stepMetres = followStep * SceneMetres / (projection.M11 * iw / 2f);
            bool Near(int x) => beside < 0 ? x < column : beside > 0 && x > column;
            float[] lit = Grey(iw, ih, (x, _) => x == column ? Line : Near(x) ? Occluder : Bg),
                missed = Grey(iw, ih, (x, _) => Near(x) ? Occluder : Bg);
            var sky = new Vector2(MotionMath.Sentinel);
            float[] litMotion = Pairs(iw, ih, (x, _) => overSky && x != column ? sky : Vector2.Zero);
            float[] missedMotion = Pairs(iw, ih, (_, _) => overSky ? sky : Vector2.Zero);
            float wBg = Weighted(Bg), wContrast = Weighted(Line) - wBg;

            var pixels = new List<int>();
            for (int c = 0; c < DisplayW; c++)
                if ((int)MathF.Floor((c + 0.5f) / factor) == column) pixels.Add(c);
            int centre = (int)MathF.Floor((column + 0.5f) * factor);
            Assert.Contains(centre, pixels);

            using var rig = new Rig(iw, ih, DisplayW, DisplayH, wallNdc);
            float nearNdc = NdcDepth(projection, LineMetres);
            if (beside != 0) rig.FillDepth(DepthMap(iw, ih, x => Near(x) ? nearNdc : wallNdc));
            var previous = new float[DisplayW];
            float change = 0f, sum = 0f;
            int changeFrame = 0, changePixel = 0;
            for (int n = 0; n < Converge + Measured; n++)
            {
                Vector2 jitter = TemporalJitter.Offset(n, phases);
                bool hit = (n % 8) is 0 or 4 or 5;
                TemporalViewInput now = View(Eye + new Vector3(n * stepMetres, 0f, 0f), projection);
                TemporalViewInput then = View(Eye + new Vector3((n - 1) * stepMetres, 0f, 0f), projection);
                rig.BeginFrame();
                rig.Fill(hit ? lit : missed, hit ? lit : missed, hit ? litMotion : missedMotion);
                rig.Resolve(TemporalResolveMath.BuildUniforms(now, then, jitter, iw, ih, DisplayW, DisplayH,
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
            return new SubTexelRun(change, changePixel, changeFrame, centre, sum / Measured);
        }

        // The line on a wall that sways sideways as a whole, offset(n) = A sin(2 pi n / 24) internal pixels with
        // A = speed * 24 / (2 pi), every texel writing the wall's motion each frame, under a still camera. The frames
        // n % 8 of 0, 4 and 5 light the column the line's centre is in. With bladeOnly the wall stays still and only the
        // lit texel writes the sway, standing LineMetres ahead. Each frame's value is the weighted contrast summed over
        // the display pixels centred within 1.5 texels of the line's centre, over the display pixels per texel, so a
        // line of coverage 3/8 sums to 3/8 wherever it sits. Converges 64 frames and measures 16.
        static SubTexelRun RunSwayingLine(TemporalUpscale preset, float speed, bool bladeOnly = false)
        {
            const int DisplayW = 48, DisplayH = 12, Row = DisplayH / 2, Converge = 64, Measured = 16, Period = 24;
            float factor = TemporalSettings.DisplayOverInternal(preset);
            int iw = (int)(DisplayW / factor), ih = (int)(DisplayH / factor), phases = TemporalJitter.PhaseCount(factor);
            float amplitude = speed * Period / (2f * MathF.PI), rest = iw / 2 + 0.5f;
            float Offset(int n) => amplitude * MathF.Sin(2f * MathF.PI * n / Period);
            Matrix4x4 projection = Perspective((float)DisplayW / DisplayH);
            TemporalViewInput still = View(Eye, projection);
            float wBg = Weighted(Bg), wContrast = Weighted(Line) - wBg;

            float wallNdc = NdcDepth(projection, SceneMetres), bladeNdc = NdcDepth(projection, LineMetres);
            using var rig = new Rig(iw, ih, DisplayW, DisplayH, wallNdc);
            float change = 0f, sum = 0f, previous = 0f;
            int changeFrame = 0;
            for (int n = 0; n < Converge + Measured; n++)
            {
                float centreX = rest + Offset(n);
                int column = (int)MathF.Floor(centreX);
                bool hit = (n % 8) is 0 or 4 or 5;
                float[] scene = Grey(iw, ih, (x, _) => hit && x == column ? Line : Bg);
                var motion = new Vector2(n > 0 ? (Offset(n) - Offset(n - 1)) / iw : 0f, 0f);
                bool Blade(int x) => hit && x == column;
                rig.BeginFrame();
                rig.Fill(scene, scene, Pairs(iw, ih, (x, _) => !bladeOnly || Blade(x) ? motion : Vector2.Zero));
                if (bladeOnly) rig.FillDepth(DepthMap(iw, ih, x => Blade(x) ? bladeNdc : wallNdc));
                rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, TemporalJitter.Offset(n, phases), iw, ih,
                    DisplayW, DisplayH, historyValid: n > 0));
                if (n < Converge - 1) continue;
                float[] color = rig.ReadColor();
                float covered = 0f;
                for (int c = 0; c < DisplayW; c++)
                    if (MathF.Abs((c + 0.5f) / factor - centreX) <= 1.5f)
                        covered += (Weighted(color[(Row * DisplayW + c) * 4]) - wBg) / wContrast / factor;
                if (n >= Converge)
                {
                    if (MathF.Abs(covered - previous) > change) (change, changeFrame) = (MathF.Abs(covered - previous), n);
                    sum += covered;
                }
                previous = covered;
            }
            return new SubTexelRun(change, -1, changeFrame, -1, sum / Measured);
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

            using var rig = new Rig(iw, ih, LineW, LineH, wallNdc);
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
                rig.FillDepth(DepthMap(iw, ih, x => InLine(x) ? lineNdc : wallNdc));
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

        // One NDC depth per texel, by column, rows top to bottom.
        static float[] DepthMap(int width, int height, Func<int, float> ndcOfColumn)
        {
            var v = new float[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    v[y * width + x] = ndcOfColumn(x);
            return v;
        }
    }
}
