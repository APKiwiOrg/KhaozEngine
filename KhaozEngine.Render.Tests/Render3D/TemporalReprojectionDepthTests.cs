using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The expected previous depth the resolve's disocclusion test compares against, at the town's depth range and far
    /// from the render origin. The matrix is built on the CPU and rounded to float, then applied in float as the shader
    /// applies it. Both roundings must leave a static point's expected depth well inside
    /// <see cref="TemporalResolveTuning.DisocclusionTolerance"/>, or distant static geometry drops its history every frame.
    /// </summary>
    public sealed class TemporalReprojectionDepthTests
    {
        static readonly Matrix4x4 TownPerspective = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 16f / 9f, 0.1f, 600f);

        /// <summary>A tenth of the disocclusion tolerance.</summary>
        const double MaxRelativeError = 0.002;

        [Theory]
        [InlineData(0f)]      // a still camera
        [InlineData(0.15f)]   // a walk
        [InlineData(1.5f)]    // a fast mount, or a slow frame, well under the automatic cut
        public void A_static_point_keeps_its_expected_depth_to_590_metres_from_a_camera_far_from_the_origin(float step)
        {
            var eye = new Vector3(60f, 4f, 60f);
            float[] distances = [50f, 150f, 300f, 590f];
            var worstByDistance = new double[distances.Length];
            double worst = 0;
            string worstAt = "";
            for (int heading = 0; heading < 8; heading++)
            {
                float yaw = heading * MathF.PI / 4f;
                var forward = new Vector3(MathF.Sin(yaw), -0.05f, -MathF.Cos(yaw));
                Vector3 eyeThen = eye + Vector3.Normalize(new Vector3(0.8f, 0.13f, -0.6f)) * step;
                Vector3 forwardThen = step > 0f ? Vector3.TransformNormal(forward, Matrix4x4.CreateRotationY(0.006f)) : forward;
                Matrix4x4 viewNow = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY);
                Matrix4x4 viewThen = Matrix4x4.CreateLookAt(eyeThen, eyeThen + forwardThen, Vector3.UnitY);
                TemporalResolveUniforms u = TemporalResolveMath.BuildUniforms(
                    new TemporalViewInput(viewNow, TownPerspective),
                    new TemporalViewInput(viewThen, TownPerspective),
                    Vector2.Zero, 1600, 900, 1600, 900, historyValid: true);
                Assert.Equal(1f, u.Jitter.W);

                for (int d = 0; d < distances.Length; d++)
                {
                    float metres = distances[d];
                    foreach (Vector2 at in new[] { Vector2.Zero, new(0.9f, 0.85f), new(-0.9f, -0.85f), new(0.9f, -0.85f), new(-0.9f, 0.85f) })
                    {
                        // The world point this pixel shows at that view depth, then what the depth target stores for it
                        // (float NDC) and the view depth last frame's camera really had, both from the float matrices
                        // taken exactly.
                        (double wx, double wy, double wz) = WorldAt(viewNow, eye, at, metres);
                        var clip = Apply(wx, wy, wz, viewNow * TownPerspective);
                        var ndc = new Vector3((float)(clip.X / clip.W), (float)(clip.Y / clip.W), (float)(clip.Z / clip.W));
                        double truth = -Apply(wx, wy, wz, viewThen).Z;

                        double error = Math.Abs(ShaderExpectedDepth(u, ndc) - truth) / truth;
                        worstByDistance[d] = Math.Max(worstByDistance[d], error);
                        if (error > worst)
                        {
                            worst = error;
                            worstAt = $"heading {heading * 45} deg, {metres} m, ndc {at}";
                        }
                    }
                }
            }
            Assert.True(worst < MaxRelativeError, $"worst relative depth error {worst:P4} at {worstAt}. Worst at 50, 150, "
                + $"300 and 590 m: {string.Join(", ", Array.ConvertAll(worstByDistance, e => e.ToString("P4")))}");
        }

        /// <summary>What the shader computes from the stored NDC: this frame's linear depth in float, then the float
        /// matrix applied to a float vector by <see cref="TemporalResolveMath.ExpectedPreviousDepth"/>.</summary>
        static double ShaderExpectedDepth(in TemporalResolveUniforms u, Vector3 ndc) => TemporalResolveMath.ExpectedPreviousDepth(
            u, new Vector2(ndc.X, ndc.Y), TemporalResolveMath.LinearDepth(ndc.Z, u.CurrentDepth));

        /// <summary>The world point at NDC <paramref name="at"/> and view depth <paramref name="metres"/>, in double, from
        /// the view's rotation and the eye.</summary>
        static (double X, double Y, double Z) WorldAt(in Matrix4x4 view, Vector3 eye, Vector2 at, float metres)
        {
            double vx = at.X * (double)metres / TownPerspective.M11;
            double vy = at.Y * (double)metres / TownPerspective.M22;
            double vz = -metres;
            return (eye.X + vx * view.M11 + vy * view.M12 + vz * view.M13,
                eye.Y + vx * view.M21 + vy * view.M22 + vz * view.M23,
                eye.Z + vx * view.M31 + vy * view.M32 + vz * view.M33);
        }

        /// <summary>A world point through a float matrix, row-vector convention, in double.</summary>
        static (double X, double Y, double Z, double W) Apply(double x, double y, double z, in Matrix4x4 m) =>
            (x * m.M11 + y * m.M21 + z * m.M31 + m.M41,
             x * m.M12 + y * m.M22 + z * m.M32 + m.M42,
             x * m.M13 + y * m.M23 + z * m.M33 + m.M43,
             x * m.M14 + y * m.M24 + z * m.M34 + m.M44);
    }
}
