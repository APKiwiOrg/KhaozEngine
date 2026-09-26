using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Why the resolve tells a moving surface from a static one at the dilated texel's own sample position. The motion
    /// target writes each texel's motion for the surface point its jittered sample shows, so the static reprojection of
    /// that same point, through <see cref="TemporalResolveMath.StaticPreviousUv"/>, agrees with it to float precision on a
    /// static surface. Pairing the display pixel's position with a neighbour's depth and motion does not: the motion field
    /// changes across the gap, and static ground near the camera would read as moving and lose its disocclusion test.
    /// </summary>
    public sealed class TemporalMovingSurfaceTests
    {
        const int Width = 1600, Height = 900;
        static readonly Vector2 Size = new(Width, Height);
        static readonly Matrix4x4 Perspective = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 16f / 9f, 0.1f, 500f);

        [Fact]
        public void A_static_point_two_metres_ahead_of_a_fast_step_matches_at_its_texel_and_not_at_a_display_pixel_beside_it()
        {
            // A 1.5 m step straight ahead, the fast end the reprojection supports, to a point 2 m ahead now.
            Matrix4x4 viewNow = Matrix4x4.CreateLookAt(new Vector3(0f, 1.7f, 10f), new Vector3(0f, 1.7f, 0f), Vector3.UnitY);
            Matrix4x4 viewThen = Matrix4x4.CreateLookAt(new Vector3(0f, 1.7f, 11.5f), new Vector3(0f, 1.7f, 1.5f), Vector3.UnitY);
            var current = new TemporalViewInput(viewNow, Perspective, viewNow * Perspective);
            var previous = new TemporalViewInput(viewThen, Perspective, viewThen * Perspective);
            var jitter = new Vector2(0.3f, -0.2f);
            TemporalResolveUniforms u = TemporalResolveMath.BuildUniforms(current, previous, jitter, Width, Height,
                Width, Height, historyValid: true, phaseCount: 8);

            // The static world point the texel's jittered sample shows at 2 m, and the motion the target writes for it.
            Vector2 sample = TemporalResolveMath.UnjitteredSamplePosition(new Vector2(1000f, 700f), jitter);
            Vector2 sampleUv = sample / Size;
            Vector2 sampleNdc = Ndc(sampleUv);
            Assert.True(Matrix4x4.Invert(viewNow, out Matrix4x4 inverseView));
            var viewPoint = new Vector4(sampleNdc.X * 2f / Perspective.M11, sampleNdc.Y * 2f / Perspective.M22, -2f, 1f);
            Vector4 world = Vector4.Transform(viewPoint, inverseView);
            Vector4 now = Vector4.Transform(world, current.ViewProjection);
            Vector2 motion = MotionMath.UvMotion(now, Vector4.Transform(world, previous.ViewProjection));
            float depth = TemporalResolveMath.LinearDepth(now.Z / now.W, u.CurrentDepth);
            Assert.Equal(2.0, depth, 3);

            // At the texel's own sample the two previous positions agree.
            float own = Gap(u, sampleUv, depth, motion);
            Assert.True(own < 0.01f, $"texel-own gap {own} px");

            // A display pixel centre 1.5 internal pixels to its right, the farthest a 3x3 neighbour sits, paired with this
            // texel's depth and motion, is off by more than the threshold on a surface that did not move.
            Vector2 pixelUv = (sample + new Vector2(1.5f, 0f)) / Size;
            float paired = Gap(u, pixelUv, depth, motion);
            Assert.True(paired > TemporalResolveTuning.MovingSurfaceInternalPixels, $"pixel-paired gap {paired} px");
        }

        // The resolve's moving-surface measure, in internal pixels: where a static point at this UV and depth was last
        // frame, against where the motion says it was.
        static float Gap(in TemporalResolveUniforms u, Vector2 uv, float depth, Vector2 motion)
        {
            Vector2? staticUv = TemporalResolveMath.StaticPreviousUv(u, Ndc(uv), depth);
            Assert.True(staticUv.HasValue);
            return ((staticUv.Value - (uv - motion)) * Size).Length();
        }

        static Vector2 Ndc(Vector2 uv) => new(uv.X * 2f - 1f, 1f - uv.Y * 2f);
    }
}
