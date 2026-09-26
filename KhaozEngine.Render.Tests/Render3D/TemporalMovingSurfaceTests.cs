using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Why the resolve tells a moving surface from a static one at the dilated texel's own sample position, and why its
    /// threshold grows with the motion. The motion target writes each texel's motion for the surface point its jittered
    /// sample shows, so the static reprojection of that same point, through
    /// <see cref="TemporalResolveMath.StaticPreviousUv"/>, agrees with it on a static surface up to float precision and
    /// the target's own half-float rounding. Pairing the display pixel's position with a neighbour's depth and motion
    /// does not: the motion field changes across the gap, and static ground near the camera would read as moving and lose
    /// its disocclusion test.
    /// </summary>
    public sealed class TemporalMovingSurfaceTests
    {
        static readonly Matrix4x4 Perspective = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 16f / 9f, 0.1f, 500f);
        static readonly Vector2 Jitter = new(0.3f, -0.2f);

        [Fact]
        public void A_static_point_two_metres_ahead_of_a_fast_step_matches_at_its_texel_and_not_at_a_display_pixel_beside_it()
        {
            // A 1.5 m step straight ahead, the fast end the reprojection supports, to a point 2 m ahead now.
            var size = new Vector2(1600f, 900f);
            TemporalResolveUniforms u = Uniforms(new Vector3(0f, 1.7f, 10f), new Vector3(0f, 1.7f, 11.5f), size);

            // The static world point the texel's jittered sample shows at 2 m, and the motion the target writes for it.
            Vector2 sample = TemporalResolveMath.UnjitteredSamplePosition(new Vector2(880f, 560f), Jitter);
            (float depth, Vector2 motion) = Observe(u, new Vector3(0f, 1.7f, 10f), new Vector3(0f, 1.7f, 11.5f), sample,
                size, 2f);
            Assert.Equal(2.0, depth, 3);

            // At the texel's own sample the two previous positions agree.
            float own = Gap(u, sample / size, depth, motion, size);
            Assert.True(own < 0.01f, $"texel-own gap {own} px");

            // A display pixel centre 1.5 internal pixels to its right, the farthest a 3x3 neighbour sits, paired with this
            // texel's depth and motion, is off by more than the threshold on a surface that did not move.
            float paired = Gap(u, (sample + new Vector2(1.5f, 0f)) / size, depth, motion, size);
            Assert.True(paired > Threshold(motion, size), $"pixel-paired gap {paired} px, threshold {Threshold(motion, size)} px");
        }

        [Fact]
        public void The_half_float_rounding_of_a_fast_motion_stays_inside_the_motion_scaled_threshold()
        {
            // A 1.5 m strafe past points 1 to 1.5 m away moves them between about 0.5 and 0.8 UV. RG16F rounds a channel
            // there by up to 2.4e-4 UV, 0.94 internal pixels on a 3840 wide target, which alone would pass the fixed half
            // pixel on a surface that did not move.
            var size = new Vector2(3840f, 2160f);
            var eyeNow = new Vector3(0f, 1.7f, 10f);
            var eyeThen = new Vector3(-1.5f, 1.7f, 10f);
            TemporalResolveUniforms u = Uniforms(eyeNow, eyeThen, size);
            float worst = 0f;
            for (int column = 200; column < 3800; column += 200)
            {
                foreach (float row in new[] { 1080f, 1500f })
                {
                    for (float metres = 1f; metres <= 1.5f; metres += 0.1f)
                    {
                        Vector2 sample = TemporalResolveMath.UnjitteredSamplePosition(new Vector2(column, row), Jitter);
                        (float depth, Vector2 motion) = Observe(u, eyeNow, eyeThen, sample, size, metres);
                        Assert.InRange(MathF.Abs(motion.X), 0.5f, 1f);
                        var stored = new Vector2((float)(Half)motion.X, (float)(Half)motion.Y);
                        float gap = Gap(u, sample / size, depth, stored, size);
                        Assert.True(gap <= Threshold(stored, size),
                            $"texel {column},{row} at {metres} m: gap {gap} px, threshold {Threshold(stored, size)} px");
                        worst = MathF.Max(worst, gap);
                    }
                }
            }
            Assert.True(worst > TemporalResolveTuning.MovingSurfaceInternalPixels,
                $"the worst rounding gap {worst} px should need the motion term");
        }

        // The uniforms of a camera that moved from eyeThen to eyeNow, both looking down negative z.
        static TemporalResolveUniforms Uniforms(Vector3 eyeNow, Vector3 eyeThen, Vector2 size)
        {
            TemporalViewInput current = View(eyeNow), previous = View(eyeThen);
            return TemporalResolveMath.BuildUniforms(current, previous, Jitter, (int)size.X, (int)size.Y, (int)size.X,
                (int)size.Y, historyValid: true, phaseCount: 8);
        }

        // The static world point a sample shows at a view depth, the linear depth the depth target gives back for it, and
        // the motion MotionMath writes for it.
        static (float Depth, Vector2 Motion) Observe(in TemporalResolveUniforms u, Vector3 eyeNow, Vector3 eyeThen,
            Vector2 sample, Vector2 size, float metres)
        {
            TemporalViewInput current = View(eyeNow), previous = View(eyeThen);
            Vector2 ndc = Ndc(sample / size);
            Assert.True(Matrix4x4.Invert(current.View, out Matrix4x4 inverseView));
            var viewPoint = new Vector4(ndc.X * metres / Perspective.M11, ndc.Y * metres / Perspective.M22, -metres, 1f);
            Vector4 world = Vector4.Transform(viewPoint, inverseView);
            Vector4 now = Vector4.Transform(world, current.ViewProjection);
            Vector2 motion = MotionMath.UvMotion(now, Vector4.Transform(world, previous.ViewProjection));
            return (TemporalResolveMath.LinearDepth(now.Z / now.W, u.CurrentDepth), motion);
        }

        // The resolve's moving-surface measure, in internal pixels: where a static point at this UV and depth was last
        // frame, against where the motion says it was.
        static float Gap(in TemporalResolveUniforms u, Vector2 uv, float depth, Vector2 motion, Vector2 size)
        {
            Vector2? staticUv = TemporalResolveMath.StaticPreviousUv(u, Ndc(uv), depth);
            Assert.True(staticUv.HasValue);
            return ((staticUv.Value - (uv - motion)) * size).Length();
        }

        // The resolve's threshold for that measure: a fixed half pixel plus a share of the motion for its rounding.
        static float Threshold(Vector2 motion, Vector2 size) => TemporalResolveTuning.MovingSurfaceInternalPixels
            + (motion * size).Length() * TemporalResolveTuning.MovingSurfaceMotionFraction;

        static TemporalViewInput View(Vector3 eye)
        {
            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY);
            return new TemporalViewInput(view, Perspective, view * Perspective);
        }

        static Vector2 Ndc(Vector2 uv) => new(uv.X * 2f - 1f, 1f - uv.Y * 2f);
    }
}
