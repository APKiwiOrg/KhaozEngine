using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// What the temporal resolve's synthetic-input facts share: a still perspective camera, a wall
    /// <see cref="SceneMetres"/> ahead in every texel, grey and paired input builders, the camera-consistent motion of a
    /// static wall, and <see cref="Rig"/> on the 8 by 8 native target. Grey inputs keep YCoCg chroma at zero, so the
    /// expected output follows from the mirrors in <see cref="TemporalResolveMath"/>. The confidence target stores
    /// accumulated weight over <see cref="TemporalResolveTuning.MaxAccumulation"/>.
    /// <para>The uniforms come from <see cref="TemporalResolveMath.BuildUniforms"/> over a real camera, and the scene
    /// depth is the NDC depth that camera gives the wall. A surface whose motion disagrees with the camera's own
    /// reprojection of it reads as moving and skips the depth test, so under a still camera any nonzero motion skips it.
    /// Every fact that needs the depth test uses zero motion under a still camera or the camera-consistent motion of a
    /// moving one.</para>
    /// </summary>
    public abstract class TemporalResolveGpuFacts
    {
        private protected const int N = 8;
        private protected const float Max = TemporalResolveTuning.MaxAccumulation;
        private protected const float SceneMetres = 2f;
        private protected static readonly Vector3 Eye = new(0f, 1.7f, 10f);
        private protected static readonly Matrix4x4 Projection = Perspective(1f);
        private protected static readonly TemporalViewInput Still = View(Eye, Projection);
        private protected static readonly Vector4 Depth = TemporalResolveMath.DepthParams(Projection);
        private protected static readonly float SceneNdc = NdcDepth(Projection, SceneMetres);
        private protected static float SceneLinear => TemporalResolveMath.LinearDepth(SceneNdc, Depth);

        private protected static float Q(float v) => (float)(Half)v;   // what a half float texel holds
        private protected static float Weighted(float grey) => TemporalResolveMath.ToWeighted(new Vector3(grey)).X;
        private protected static float Unweighted(float y) => y / (1f - y);   // FromWeighted for grey
        private protected static int At(int x, int y) => y * N + x;

        // The still camera's uniforms: jitter in internal pixels as TemporalJitter.Apply receives it, and a
        // reprojection that returns every static point to itself.
        private protected static TemporalResolveUniforms Uniforms(bool historyValid, Vector2 jitter = default)
            => TemporalResolveMath.BuildUniforms(Still, Still, jitter, N, N, N, N, historyValid);

        // The accumulation cap step 8 applies at this much display motion a frame.
        private protected static float MotionCap(float motionPixels) => Max - (Max - TemporalResolveTuning.MovingAccumulation)
            * Math.Clamp(motionPixels / TemporalResolveTuning.MotionAccumulationPixels, 0f, 1f);

        private protected static Matrix4x4 Perspective(float aspect)
            => Matrix4x4.CreatePerspectiveFieldOfView(1f, aspect, 0.1f, 100f);

        private protected static TemporalViewInput View(Vector3 eye, Matrix4x4 projection)
        {
            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY);
            return new TemporalViewInput(view, projection, view * projection);
        }

        // What the depth target holds for a point this far ahead: the projection's NDC depth.
        private protected static float NdcDepth(in Matrix4x4 projection, float metres)
        {
            Vector4 clip = Vector4.Transform(new Vector4(0f, 0f, -metres, 1f), projection);
            return clip.Z / clip.W;
        }

        private protected static Vector2 Ndc(Vector2 uv) => new(uv.X * 2f - 1f, 1f - uv.Y * 2f);

        // The camera-consistent motion of a static wall at one linear depth, zero jitter: each texel's sample UV minus
        // the resolve's static previous UV of that sample. Each is held to what MotionMath writes for the world point,
        // within 0.05 pixels plus 1/1024 of the motion, so a flip in StaticPreviousUv against the motion target fails.
        private protected static Vector2[] CameraMotion(in TemporalResolveUniforms u, TemporalViewInput now,
            TemporalViewInput then, int width, int height, float linearDepth)
        {
            var size = new Vector2(width, height);
            Assert.True(Matrix4x4.Invert(now.View, out Matrix4x4 inverseView));
            var motion = new Vector2[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    Vector2 uv = TemporalResolveMath.UnjitteredSamplePosition(new Vector2(x, y), Vector2.Zero) / size;
                    Vector2? previous = TemporalResolveMath.StaticPreviousUv(u, Ndc(uv), linearDepth);
                    Assert.True(previous.HasValue, $"texel ({x}, {y}) has no previous position");
                    motion[y * width + x] = uv - previous.Value;

                    Vector2 ndc = Ndc(uv);
                    var viewPoint = new Vector4(ndc.X * linearDepth / now.Projection.M11,
                        ndc.Y * linearDepth / now.Projection.M22, -linearDepth, 1f);
                    Vector4 world = Vector4.Transform(viewPoint, inverseView);
                    Vector2 written = MotionMath.UvMotion(Vector4.Transform(world, now.ViewProjection),
                        Vector4.Transform(world, then.ViewProjection));
                    float apart = ((written - motion[y * width + x]) * size).Length();
                    float tolerance = 0.05f + (written * size).Length() / 1024f;
                    Assert.True(apart <= tolerance, $"texel ({x}, {y}): {apart} px from the written motion");
                }
            return motion;
        }

        private protected static float[] Grey(Func<int, int, float> value) => Grey(N, N, value);

        private protected static float[] Grey(int width, int height, Func<int, int, float> value)
        {
            var v = new float[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float g = value(x, y);
                    int i = (y * width + x) * 4;
                    v[i] = g; v[i + 1] = g; v[i + 2] = g; v[i + 3] = 1f;
                }
            return v;
        }

        private protected static float[] Motion(Func<int, int, Vector2> value) => Pairs(N, N, value);
        private protected static float[] State(Func<int, int, Vector2> value) => Pairs(N, N, value);

        private protected static float[] Pairs(int width, int height, Func<int, int, Vector2> value)
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

        /// <summary>The shared rig on the 8 by 8 native target, the wall <see cref="SceneMetres"/> ahead in every texel,
        /// unless a fact gives other sizes.</summary>
        private protected sealed class Rig : TemporalResolveRig
        {
            public Rig() : base(N, N, N, N, SceneNdc) { }

            public Rig(int internalWidth, int internalHeight, int displayWidth, int displayHeight, float sceneNdc)
                : base(internalWidth, internalHeight, displayWidth, displayHeight, sceneNdc) { }
        }
    }
}
