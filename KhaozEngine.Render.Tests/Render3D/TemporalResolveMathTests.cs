using System;
using System.Numerics;
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
            Vector4 then = Vector4.Transform(world, previous.View);

            // The shader rebuilds (ndc.xy * w, linear depth, 1) and lands in last frame's view space.
            TemporalResolveUniforms u = Build(current, previous);
            var ndc = new Vector2(now.X / now.W, now.Y / now.W);
            float depth = TemporalResolveMath.LinearDepth(now.Z / now.W, u.CurrentDepth);
            Vector4 back = Vector4.Transform(new Vector4(ndc * depth, depth, 1f), u.CurrentToPrevious);

            Assert.Equal(then.X, back.X, 3);
            Assert.Equal(then.Y, back.Y, 3);
            Assert.Equal(then.Z, back.Z, 3);
            Assert.Equal(-then.Z, TemporalResolveMath.ExpectedPreviousDepth(u, ndc, depth), 3);
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
                historyValid: true).Jitter.W);
            Assert.Equal(0f, TemporalResolveMath.BuildUniforms(current, current, Vector2.Zero, 1280, 720, 1920, 1080,
                historyValid: false).Jitter.W);

            // The previous projection falls back to the current one, so the block stays well defined.
            Matrix4x4 zoomed = Matrix4x4.CreatePerspectiveFieldOfView(0.6f, 16f / 9f, 0.1f, 500f);
            var previous = new TemporalViewInput(current.View, zoomed, current.View * zoomed);
            Assert.Equal(Perspective, TemporalResolveMath.BuildUniforms(current, previous, Vector2.Zero, 1280, 720, 1920, 1080,
                historyValid: false).PreviousProjection);
            Assert.Equal(Perspective, TemporalResolveMath.BuildUniforms(current, null, Vector2.Zero, 1280, 720, 1920, 1080,
                historyValid: true).PreviousProjection);
        }

        [Fact]
        public void The_uniforms_carry_the_sizes_jitter_ratio_and_depth_and_leave_params_reserved()
        {
            TemporalViewInput current = View(new Vector3(0f, 2f, 10f), Vector3.Zero);
            TemporalResolveUniforms u = TemporalResolveMath.BuildUniforms(current, current, new Vector2(0.25f, -0.125f),
                1280, 720, 1920, 1080, historyValid: true);
            Assert.Equal(new Vector4(1280f, 720f, 1920f, 1080f), u.Sizes);
            Assert.Equal(new Vector4(0.25f, -0.125f, 1.5f, 1f), u.Jitter);
            Assert.Equal(Vector4.Zero, u.Params);
            Assert.Equal(TemporalResolveMath.DepthParams(current.Projection), u.CurrentDepth);
            Assert.Equal(u.CurrentDepth, u.PreviousDepth);
            Assert.Equal(current.Projection, u.PreviousProjection);
            Assert.Equal(u.CurrentDepth, TemporalResolveMath.BuildDepthStore(current.Projection).CurrentDepth);
        }

        [Fact]
        public void A_singular_current_view_or_projection_gives_no_reprojection_and_no_history()
        {
            TemporalViewInput good = View(new Vector3(0f, 2f, 10f), Vector3.Zero);
            var flatView = new TemporalViewInput(default, Perspective, default);
            var flatProjection = new TemporalViewInput(good.View, default, default);

            Assert.False(TemporalResolveMath.TryReprojection(flatView, good, out Matrix4x4 currentToPrevious,
                out Matrix4x4 backgroundToPrevious));
            Assert.Equal(Matrix4x4.Identity, currentToPrevious);
            Assert.Equal(Matrix4x4.Identity, backgroundToPrevious);
            Assert.False(TemporalResolveMath.TryReprojection(flatProjection, good, out _, out _));

            TemporalResolveUniforms u = TemporalResolveMath.BuildUniforms(flatView, good, Vector2.Zero, 64, 64, 64, 64,
                historyValid: true);
            Assert.Equal(0f, u.Jitter.W);
            Assert.Equal(Matrix4x4.Identity, u.CurrentToPrevious);
            Assert.Equal(Matrix4x4.Identity, u.BackgroundToPrevious);
        }

        [Fact]
        public void An_orthographic_view_reprojects_a_static_point_with_linear_depth()
        {
            Matrix4x4 ortho = Matrix4x4.CreateOrthographic(24f, 13.5f, 0.5f, 200f);
            Matrix4x4 viewNow = Matrix4x4.CreateLookAt(new Vector3(0f, 30f, 30f), Vector3.Zero, Vector3.UnitY);
            Matrix4x4 viewThen = Matrix4x4.CreateLookAt(new Vector3(0.4f, 30.2f, 29.7f), new Vector3(0.4f, 0f, 0.3f), Vector3.UnitY);
            TemporalResolveUniforms u = TemporalResolveMath.BuildUniforms(new TemporalViewInput(viewNow, ortho, viewNow * ortho),
                new TemporalViewInput(viewThen, ortho, viewThen * ortho), Vector2.Zero, 1280, 720, 1280, 720,
                historyValid: true);
            Assert.Equal(1f, u.Jitter.W);
            Assert.Equal(0f, u.CurrentDepth.X);
            Assert.Equal(0.5, u.CurrentDepth.Y, 4);
            Assert.Equal(200.0, u.CurrentDepth.Z, 2);
            Assert.Equal(u.CurrentDepth, u.PreviousDepth);

            var world = new Vector4(2.5f, 1f, -3f, 1f);
            Vector4 clip = Vector4.Transform(world, viewNow * ortho);   // w is 1
            Vector4 then = Vector4.Transform(world, viewThen);
            float depth = TemporalResolveMath.LinearDepth(clip.Z, u.CurrentDepth);
            Assert.Equal(-Vector4.Transform(world, viewNow).Z, depth, 3);

            // Under an orthographic projection the shader rebuilds (ndc.xy, linear depth, 1).
            Vector4 back = Vector4.Transform(new Vector4(clip.X, clip.Y, depth, 1f), u.CurrentToPrevious);
            Assert.Equal(then.X, back.X, 3);
            Assert.Equal(then.Y, back.Y, 3);
            Assert.Equal(-then.Z, TemporalResolveMath.ExpectedPreviousDepth(u, new Vector2(clip.X, clip.Y), depth), 3);
        }

        [Fact]
        public void A_static_point_lands_at_its_previous_UV_under_a_moving_perspective_camera_that_also_zooms()
        {
            TemporalViewInput current = View(new Vector3(0f, 2f, 10f), new Vector3(0.4f, 1.8f, 0f));
            Matrix4x4 zoomed = Matrix4x4.CreatePerspectiveFieldOfView(0.85f, 16f / 9f, 0.1f, 500f);
            Matrix4x4 viewThen = Matrix4x4.CreateLookAt(new Vector3(0.6f, 2.2f, 10.9f), new Vector3(0.1f, 1.9f, 0f), Vector3.UnitY);
            var previous = new TemporalViewInput(viewThen, zoomed, viewThen * zoomed);
            TemporalResolveUniforms u = Build(current, previous);
            Assert.Equal(zoomed, u.PreviousProjection);
            Assert.Equal(TemporalResolveMath.DepthParams(zoomed), u.PreviousDepth);

            foreach (Vector4 world in new[] { new Vector4(1.2f, 0.7f, -3f, 1f), new Vector4(-2.5f, 3.1f, 1f, 1f),
                new Vector4(0.3f, 0.1f, -40f, 1f), new Vector4(6f, -1f, -120f, 1f) })
            {
                AssertStaticPreviousUv(u, current, previous, world);
            }
        }

        [Fact]
        public void A_static_point_lands_at_its_previous_UV_under_a_moving_orthographic_camera()
        {
            Matrix4x4 ortho = Matrix4x4.CreateOrthographic(24f, 13.5f, 0.5f, 200f);
            Matrix4x4 viewNow = Matrix4x4.CreateLookAt(new Vector3(0f, 30f, 30f), Vector3.Zero, Vector3.UnitY);
            Matrix4x4 viewThen = Matrix4x4.CreateLookAt(new Vector3(0.9f, 30.2f, 29.4f), new Vector3(0.7f, 0f, 0.3f), Vector3.UnitY);
            var current = new TemporalViewInput(viewNow, ortho, viewNow * ortho);
            var previous = new TemporalViewInput(viewThen, ortho, viewThen * ortho);
            TemporalResolveUniforms u = Build(current, previous);
            Assert.Equal(ortho, u.PreviousProjection);

            foreach (Vector4 world in new[] { new Vector4(2.5f, 1f, -3f, 1f), new Vector4(-8f, 0f, 4f, 1f),
                new Vector4(5f, 2.5f, 3f, 1f) })
            {
                AssertStaticPreviousUv(u, current, previous, world);
            }
        }

        [Fact]
        public void A_static_point_on_or_behind_the_previous_camera_plane_has_no_previous_UV()
        {
            // The camera stepped back two metres past a point one metre ahead of it now, so the point sat behind the
            // previous camera. The motion target writes its off-screen value there, and the resolve treats the surface
            // as moving and skips the depth test.
            TemporalViewInput current = View(new Vector3(0f, 2f, 10f), new Vector3(0f, 2f, 0f));
            TemporalViewInput previous = View(new Vector3(0f, 2f, 8f), new Vector3(0f, 2f, -2f));
            TemporalResolveUniforms u = Build(current, previous);
            Vector4 now = Vector4.Transform(new Vector4(0.1f, 2.2f, 9f, 1f), current.ViewProjection);
            var ndc = new Vector2(now.X / now.W, now.Y / now.W);
            float depth = TemporalResolveMath.LinearDepth(now.Z / now.W, u.CurrentDepth);

            Assert.Null(TemporalResolveMath.StaticPreviousUv(u, ndc, depth));
            Assert.True(TemporalResolveMath.ExpectedPreviousDepth(u, ndc, depth) <= MotionMath.MinPreviousClipW);
        }

        [Fact]
        public void Luma_weighting_keeps_every_colour_under_one_and_round_trips()
        {
            Vector3[] colours =
            [
                Vector3.Zero, new(0.02f, 0.01f, 0.03f), new(0.5f), new(4f, 2f, 0.5f), new(1f, 0f, 0f), new(0f, 0f, 60f),
                new(500f, 800f, 300f),
            ];
            foreach (Vector3 c in colours)
            {
                Vector3 w = TemporalResolveMath.ToWeighted(c);
                Assert.True(TemporalResolveMath.Luma(w) < 1f, $"{c} weighs {TemporalResolveMath.Luma(w)}");
                Vector3 back = TemporalResolveMath.FromWeighted(w);
                float tolerance = 1e-4f * MathF.Max(1f, MathF.Max(c.X, MathF.Max(c.Y, c.Z)));
                Assert.True(Vector3.Distance(c, back) <= tolerance, $"{c} came back as {back}");
            }
            Assert.Equal(0.5 / 1.5, TemporalResolveMath.ToWeighted(new Vector3(0.5f)).X, 5);
        }

        // A world point through both frames' own matrices, against the mirror that sees only the uniforms, and the
        // camera-only motion it implies against the value the motion target writes for the same point.
        static void AssertStaticPreviousUv(in TemporalResolveUniforms u, in TemporalViewInput current,
            in TemporalViewInput previous, Vector4 world)
        {
            Vector4 now = Vector4.Transform(world, current.ViewProjection);
            Vector4 then = Vector4.Transform(world, previous.ViewProjection);
            var ndc = new Vector2(now.X / now.W, now.Y / now.W);
            float depth = TemporalResolveMath.LinearDepth(now.Z / now.W, u.CurrentDepth);
            var expected = new Vector2(then.X / then.W * 0.5f + 0.5f, 0.5f - then.Y / then.W * 0.5f);

            Vector2? previousUv = TemporalResolveMath.StaticPreviousUv(u, ndc, depth);
            Assert.True(previousUv.HasValue, $"{world} has no previous UV");
            Assert.Equal(expected.X, previousUv.Value.X, 1e-4);
            Assert.Equal(expected.Y, previousUv.Value.Y, 1e-4);

            var uvNow = new Vector2(ndc.X * 0.5f + 0.5f, 0.5f - ndc.Y * 0.5f);
            Vector2 written = MotionMath.UvMotion(now, then);
            Assert.Equal(written.X, uvNow.X - previousUv.Value.X, 1e-4);
            Assert.Equal(written.Y, uvNow.Y - previousUv.Value.Y, 1e-4);
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
                historyValid: true);
    }
}
