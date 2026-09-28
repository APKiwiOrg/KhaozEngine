using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The perspective stage the follow facts use under a third-person camera: the engine's
    /// <see cref="FollowCamera3D"/> at the field of view, height offset and distance a game boots with, over a
    /// world-fixed ground plane at Y 0 textured with <see cref="FrontStage"/>'s noise. Each texel is
    /// <see cref="TexelMetres"/> across, about two display pixels where a 320 by 180 camera at the boot pitch looks,
    /// fewer farther out, where the mip chain and the mip bias decide what it shows. The camera looks at its target on
    /// the ground from <see cref="Distance"/> metres along its pitch and yaw, raised by <see cref="HeightOffset"/>.
    /// <see cref="Offset"/> moves the whole world, camera included, so a scene can stand far from the world origin.
    /// Every setup loads the same meshes in the same order. Nothing reads the clock or a random source.
    /// </summary>
    internal sealed class GroundStage
    {
        public const float FieldOfView = MathF.PI / 3f, HeightOffset = 1.2f, Distance = 12f, Yaw = 0.4f;

        /// <summary>The ground texel's side in metres.</summary>
        public const float TexelMetres = 0.16f;

        const float GroundHalf = 400f;

        public readonly int W, H;
        public readonly float Pitch;
        public readonly Vector3 Offset;
        public MeshHandle Box, Ground;

        public GroundStage(int w, int h, float pitch, Vector3 offset = default)
        {
            W = w; H = h; Pitch = pitch; Offset = offset;
        }

        /// <summary>The camera at <paramref name="target"/>, a point relative to <see cref="Offset"/>, turned
        /// <paramref name="yawOffset"/> from <see cref="Yaw"/>.</summary>
        public FollowCamera3D Camera(Vector3 target, float yawOffset = 0f)
        {
            var camera = new FollowCamera3D
            {
                Yaw = Yaw + yawOffset,
                FieldOfView = FieldOfView,
                HeightOffset = HeightOffset,
                AspectRatio = (float)W / H,
                Target = Offset + target,
            };
            camera.Pitch = Pitch;
            camera.Distance = Distance;
            return camera;
        }

        /// <summary>Away from the camera along the ground at <see cref="Yaw"/>, and to its right.</summary>
        public static Vector3 Forward => new(-MathF.Sin(Yaw), 0f, -MathF.Cos(Yaw));

        public static Vector3 Right => new(MathF.Cos(Yaw), 0f, -MathF.Sin(Yaw));

        public void Setup(Scene3D s, AntiAliasing aa, TemporalUpscale preset, FollowCamera3D camera)
        {
            s.Post.UseSmoothPreset();
            s.Post.RenderScale = RenderScale.MatchViewport;
            s.Post.Quality.AntiAliasing = aa;
            s.Post.Temporal.Upscale = preset;
            s.Post.Temporal.Sharpness = 0.25f;
            s.Post.TransparentBackground = false;
            s.Post.BackgroundColor = new Color(0.04f, 0.05f, 0.07f, 1f);
            s.Post.AmbientColor = new Color(0.9f, 0.9f, 0.9f, 1f);
            s.Post.Hdr.Enabled = false;
            s.CameraOverride = camera;
            Box = s.LoadMesh(MeshPrimitives.Box(1f));
            Ground = s.LoadMesh(GroundMesh(), s.LoadTexture(FrontStage.NoiseTexture(), FrontStage.TextureSize,
                FrontStage.TextureSize));
        }

        /// <summary>The ground, tinted a mid grey under the stage's light, so white and dark features both
        /// show.</summary>
        public void DrawGround(Scene3D s) =>
            s.Draw(Ground, Matrix4x4.CreateTranslation(Offset), new Color(0.55f, 0.55f, 0.55f, 1f));

        /// <summary>The world matrix of a box of <paramref name="size"/> standing on the ground at
        /// <paramref name="foot"/>, relative to <see cref="Offset"/>, turned to face the camera's yaw.</summary>
        public Matrix4x4 Standing(Vector3 foot, Vector3 size) => Matrix4x4.CreateScale(size)
            * Matrix4x4.CreateRotationY(Yaw)
            * Matrix4x4.CreateTranslation(Offset + foot + new Vector3(0f, size.Y / 2f, 0f));

        // Both windings, so culling cannot hide it. The texture coordinates are local, so a far offset keeps them fine.
        static GltfMesh GroundMesh()
        {
            float period = TexelMetres * FrontStage.TextureSize;
            var n = Vector3.UnitY;
            var c = Vector4.One;
            ModelVertex V(float x, float z) => new(new Vector3(x, 0f, z), n, c, new Vector2(x / period, z / period));
            var v = new[] { V(-GroundHalf, -GroundHalf), V(GroundHalf, -GroundHalf), V(GroundHalf, GroundHalf),
                V(-GroundHalf, GroundHalf) };
            return new GltfMesh(v, new ushort[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 });
        }
    }

    /// <summary>Screen geometry on the ground stage: where a pixel's ray meets the ground, and whether a box standing
    /// on the ground hides a ground point from an eye.</summary>
    internal static class GroundRays
    {
        /// <summary>The ground point pixel (<paramref name="x"/>, <paramref name="y"/>) shows through its centre, or
        /// null where its ray never meets the ground.</summary>
        public static Vector3? GroundPoint(FollowCamera3D camera, int w, int h, int x, int y)
        {
            Ray r = camera.ScreenToRay(new Vector2(x + 0.5f, y + 0.5f), w, h);
            if (!(r.Direction.Y < -1e-6f)) return null;
            return r.Origin + r.Direction * (-r.Origin.Y / r.Direction.Y);
        }

        /// <summary>Whether the segment from <paramref name="from"/> to <paramref name="to"/> passes through the box
        /// of <paramref name="size"/> standing at <paramref name="foot"/> and turned by <paramref name="yaw"/>, short
        /// of its far end.</summary>
        public static bool Hides(Vector3 from, Vector3 to, Vector3 foot, Vector3 size, float yaw)
        {
            Matrix4x4 toLocal = Matrix4x4.CreateTranslation(-(foot + new Vector3(0f, size.Y / 2f, 0f)))
                * Matrix4x4.CreateRotationY(-yaw);
            Vector3 a = Vector3.Transform(from, toLocal), b = Vector3.Transform(to, toLocal), d = b - a;
            Vector3 half = size * 0.5f;
            float t0 = 0f, t1 = 1f - 1e-4f;
            for (int axis = 0; axis < 3; axis++)
            {
                float o = axis == 0 ? a.X : axis == 1 ? a.Y : a.Z;
                float dir = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
                float lo = axis == 0 ? -half.X : axis == 1 ? -half.Y : -half.Z;
                float hi = -lo;
                if (MathF.Abs(dir) < 1e-9f)
                {
                    if (o < lo || o > hi) return false;
                    continue;
                }
                float ta = (lo - o) / dir, tb = (hi - o) / dir;
                t0 = MathF.Max(t0, MathF.Min(ta, tb));
                t1 = MathF.Min(t1, MathF.Max(ta, tb));
                if (t0 > t1) return false;
            }
            return true;
        }
    }
}
