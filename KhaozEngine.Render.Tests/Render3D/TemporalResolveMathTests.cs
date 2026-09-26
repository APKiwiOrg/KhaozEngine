using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The CPU half of the temporal resolve: the reprojection matrices it hands the shader, the depth linearisation, the
    /// Lanczos kernel and the luma weighting it mirrors, and the sample-position convention against group A's jitter.
    /// </summary>
    public sealed class TemporalResolveMathTests
    {
        static readonly Matrix4x4 Perspective = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 16f / 9f, 0.1f, 500f);

        [Fact]
        public void Lanczos2_is_one_at_the_centre_zero_at_the_integers_and_negative_between_one_and_two()
        {
            Assert.Equal(1.0, TemporalResolveMath.Lanczos2(0f), 6);
            Assert.Equal(0.0, TemporalResolveMath.Lanczos2(1f), 5);
            Assert.Equal(0.0, TemporalResolveMath.Lanczos2(2f), 6);
            Assert.Equal(0.0, TemporalResolveMath.Lanczos2(-2.5f), 6);
            Assert.Equal(0.57316, TemporalResolveMath.Lanczos2(0.5f), 4);
            Assert.Equal(TemporalResolveMath.Lanczos2(0.5f), TemporalResolveMath.Lanczos2(-0.5f));
            Assert.Equal(-0.06368, TemporalResolveMath.Lanczos2(1.5f), 4);
        }

        [Fact]
        public void The_sample_position_convention_undoes_TemporalJitter_Apply()
        {
            // The rasteriser sees a point at its unjittered pixel position plus the jitter (x right, y down). The resolve
            // assumes a texel centred at c shows the unjittered content at c - jitter. Both must agree or every
            // reconstruction is misplaced by twice the jitter.
            const int w = 320, h = 180;
            var jitter = new Vector2(0.3f, -0.2f);
            Matrix4x4 jittered = TemporalJitter.Apply(Perspective, jitter, w, h);
            var point = new Vector4(0.7f, -0.4f, -6f, 1f);
            Vector2 plain = ToPixels(Vector4.Transform(point, Perspective), w, h);
            Vector2 shifted = ToPixels(Vector4.Transform(point, jittered), w, h);
            Assert.Equal(plain.X + jitter.X, shifted.X, 3);
            Assert.Equal(plain.Y + jitter.Y, shifted.Y, 3);

            Vector2 recovered = TemporalResolveMath.UnjitteredSamplePosition(shifted - new Vector2(0.5f), jitter);
            Assert.Equal(plain.X, recovered.X, 3);
            Assert.Equal(plain.Y, recovered.Y, 3);
        }

        [Fact]
        public void Perspective_depth_parameters_linearise_a_projected_point_to_its_view_distance()
        {
            Vector4 p = TemporalResolveMath.DepthParams(Perspective);
            Assert.Equal(1f, p.X);
            Assert.Equal(0.1, p.Y, 4);
            Assert.InRange(p.Z, 499f, 501f);
            Vector4 clip = Vector4.Transform(new Vector4(0.3f, -0.2f, -12f, 1f), Perspective);
            Assert.Equal(12.0, TemporalResolveMath.LinearDepth(clip.Z / clip.W, p), 2);
            Assert.Equal(OutlineMath.LinearizeDepth(0.9f, p.Y, p.Z), TemporalResolveMath.LinearDepth(0.9f, p), 4);
        }

        [Fact]
        public void Orthographic_depth_parameters_are_linear_between_near_and_far()
        {
            Vector4 p = TemporalResolveMath.DepthParams(Matrix4x4.CreateOrthographic(10f, 10f, 0.5f, 80f));
            Assert.Equal(0f, p.X);
            Assert.Equal(0.5, p.Y, 4);
            Assert.Equal(80.0, p.Z, 3);
            Assert.Equal(40.25, TemporalResolveMath.LinearDepth(0.5f, p), 3);
        }

        [Fact]
        public void A_static_point_reprojects_to_where_the_previous_camera_saw_it()
        {
            TemporalViewInput current = View(new Vector3(0f, 2f, 10f), new Vector3(0.4f, 1.8f, 0f));
            TemporalViewInput previous = View(new Vector3(0.3f, 2.1f, 10.4f), new Vector3(0.2f, 1.9f, 0f));
            var world = new Vector4(1.2f, 0.7f, -3f, 1f);
            Vector4 now = Vector4.Transform(world, current.ViewProjection);
            Vector4 then = Vector4.Transform(world, previous.ViewProjection);

            TemporalResolveUniforms u = Build(current, previous);
            Vector4 back = Vector4.Transform(new Vector4(now.X / now.W, now.Y / now.W, now.Z / now.W, 1f), u.CurrentToPrevious);

            Assert.Equal(then.X / then.W, back.X / back.W, 3);
            Assert.Equal(then.Y / then.W, back.Y / back.W, 3);
            Assert.Equal(then.Z / then.W, back.Z / back.W, 4);
            Assert.Equal(1f, u.Jitter.W);
        }

        [Fact]
        public void The_background_matrix_follows_rotation_and_ignores_translation()
        {
            TemporalViewInput current = View(new Vector3(0f, 2f, 10f), new Vector3(0f, 2f, 0f));
            TemporalViewInput moved = View(new Vector3(40f, 7f, -25f), new Vector3(40f, 7f, -35f));   // same heading, far away
            Vector4 far = Vector4.Transform(new Vector4(0.35f, -0.4f, 1f, 1f), Build(current, moved).BackgroundToPrevious);
            Assert.Equal(0.35, far.X / far.W, 3);
            Assert.Equal(-0.4, far.Y / far.W, 3);

            TemporalViewInput turned = View(new Vector3(0f, 2f, 10f), new Vector3(-3f, 2f, 0f));   // looked left of here
            Vector4 ahead = Vector4.Transform(new Vector4(0f, 0f, 1f, 1f), Build(current, turned).BackgroundToPrevious);
            Assert.True(ahead.X / ahead.W > 0.1f,
                "straight ahead now sits right of centre for a previous camera that looked left of it");
        }

        [Fact]
        public void Without_a_previous_view_or_a_valid_history_the_shader_is_told_to_ignore_history()
        {
            TemporalViewInput current = View(new Vector3(0f, 2f, 10f), Vector3.Zero);
            Assert.Equal(0f, TemporalResolveMath.BuildUniforms(current, null, Vector2.Zero, 1280, 720, 1920, 1080,
                historyValid: true, phaseCount: 18).Jitter.W);
            Assert.Equal(0f, TemporalResolveMath.BuildUniforms(current, current, Vector2.Zero, 1280, 720, 1920, 1080,
                historyValid: false, phaseCount: 18).Jitter.W);
        }

        [Fact]
        public void The_uniforms_carry_the_sizes_jitter_ratio_depth_and_lock_decay()
        {
            TemporalViewInput current = View(new Vector3(0f, 2f, 10f), Vector3.Zero);
            TemporalResolveUniforms u = TemporalResolveMath.BuildUniforms(current, current, new Vector2(0.25f, -0.125f),
                1280, 720, 1920, 1080, historyValid: true, phaseCount: 18);
            Assert.Equal(new Vector4(1280f, 720f, 1920f, 1080f), u.Sizes);
            Assert.Equal(new Vector4(0.25f, -0.125f, 1.5f, 1f), u.Jitter);
            Assert.Equal(1f / 18f, u.Params.X);
            Assert.Equal(TemporalResolveMath.DepthParams(current.Projection), u.CurrentDepth);
            Assert.Equal(u.CurrentDepth, u.PreviousDepth);
            Assert.Equal(1f / 8f, TemporalResolveMath.BuildUniforms(current, current, Vector2.Zero, 64, 64, 64, 64,
                historyValid: true, phaseCount: 4).Params.X);
            Assert.Equal(u.CurrentDepth, TemporalResolveMath.BuildDepthStore(current.Projection).CurrentDepth);
        }

        [Theory]
        [InlineData(TemporalUpscale.Quality, 3456, 2234, 18)]
        [InlineData(TemporalUpscale.UltraPerformance, 2560, 1600, 72)]
        public void The_jitter_cycle_comes_from_the_preset_ratio_never_from_the_rounded_target_sizes(
            TemporalUpscale preset, int displayWidth, int displayHeight, int phases)
        {
            // The internal size is the display size times the ratio rounded to whole pixels, so one axis can land a hair
            // above the preset's exact ratio, and ceil(8 * r * r) then takes one phase more. The cycle reads the preset's
            // ratio. The size ratio only scales the resolve's sampling footprint.
            var s = new PixelPostProcessSettings();
            s.Quality.AntiAliasing = AntiAliasing.Temporal;
            s.Temporal.Upscale = preset;
            var (iw, ih) = Scene3D.ComputeTargetSize(s, displayWidth, displayHeight);
            float presetRatio = TemporalSettings.DisplayOverInternal(preset);
            float sizeRatio = TemporalResolveMath.DisplayOverInternal(displayWidth, displayHeight, iw, ih);

            Assert.True(sizeRatio > presetRatio, $"{iw}x{ih} gives {sizeRatio}, not just above {presetRatio}");
            Assert.Equal(phases, TemporalJitter.PhaseCount(presetRatio));
            Assert.NotEqual(phases, TemporalJitter.PhaseCount(sizeRatio));

            TemporalViewInput current = View(new Vector3(0f, 2f, 10f), Vector3.Zero);
            TemporalResolveUniforms u = TemporalResolveMath.BuildUniforms(current, current, Vector2.Zero, iw, ih,
                displayWidth, displayHeight, historyValid: true, phaseCount: TemporalJitter.PhaseCount(presetRatio));
            Assert.Equal(1f / phases, u.Params.X);
            Assert.Equal(sizeRatio, u.Jitter.Z);
        }

        [Fact]
        public void Luma_weighting_keeps_every_colour_under_one_and_round_trips()
        {
            var c = new Vector3(4f, 2f, 0.5f);
            Vector3 w = TemporalResolveMath.ToWeighted(c);
            Assert.True(TemporalResolveMath.Luma(w) < 1f);
            Vector3 back = TemporalResolveMath.FromWeighted(w);
            Assert.Equal(c.X, back.X, 3);
            Assert.Equal(c.Y, back.Y, 3);
            Assert.Equal(c.Z, back.Z, 3);
            Assert.Equal(0.5 / 1.5, TemporalResolveMath.ToWeighted(new Vector3(0.5f)).X, 5);
        }

        static Vector2 ToPixels(Vector4 clip, int w, int h)
            => new((clip.X / clip.W * 0.5f + 0.5f) * w, (0.5f - clip.Y / clip.W * 0.5f) * h);

        static TemporalViewInput View(Vector3 eye, Vector3 target)
        {
            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
            return new TemporalViewInput(view, Perspective, view * Perspective);
        }

        static TemporalResolveUniforms Build(in TemporalViewInput current, in TemporalViewInput previous)
            => TemporalResolveMath.BuildUniforms(current, previous, Vector2.Zero, 1280, 720, 1280, 720,
                historyValid: true, phaseCount: 8);
    }
}
