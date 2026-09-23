using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The cross-cascade hand-off scene, shared by the committed golden
    /// (<c>CascadeShadowGpuTests.Golden3D_CascadeHandoff</c>) and the in-session blend A/B
    /// (<see cref="CascadeHandoffBlendGoldenTests"/>) so the two render exactly the same frame and cannot drift apart.
    /// A telephoto fly camera looks down a row of boxes receding in view depth (6 to 44 units), with
    /// ShadowNearDistance 8 pulling the cascade 0-to-1 hand-off onto visibly shadowed ground in the lower middle of the
    /// frame. It also mirrors <c>Scene3D.ComputeShadowCascades</c> on the CPU, so a test can ask which ground points
    /// sit inside a blend band without reading the shadow atlas back.
    /// </summary>
    internal static class CascadeHandoffScene
    {
        public const int W = 480, H = 320;
        public const float NearDist = 8f, MaxDist = 60f;

        /// <summary>The engine's default <see cref="ShadowSettings.ShadowCascadeBlend"/>, pinned explicit.</summary>
        public const float DefaultBlend = 0.15f;

        /// <summary>Shallow key light: long stripe shadows (cast toward +x/+z) crossing the band.</summary>
        public static readonly Vector3 Light = new(0.7f, -0.45f, 0.55f);

        /// <summary>Height of the floor tile's top face, the surface the camera actually sees.</summary>
        const float FloorTop = 0.1f;

        static readonly float[] RowZ = { 6f, 10f, 15f, 21f, 28f, 36f, 44f };

        /// <summary>
        /// Telephoto fly camera high behind the row: the zoom magnifies the seam-region ground so the hand-off band
        /// spans many pixels, and the steep pitch keeps the row's long shallow-light shadows on screen from near to
        /// far.
        /// </summary>
        public static FlyCamera3D Camera() => new()
        {
            Position = new Vector3(5f, 5.2f, 0.5f),
            Yaw = -0.42f,
            Pitch = -0.42f,
            FieldOfView = 0.42f,
            AspectRatio = (float)W / H,
            NearPlane = 0.5f,
            FarPlane = 160f,
        };

        /// <summary>Render the scene through <paramref name="camera"/> with the given cross-cascade blend width.
        /// </summary>
        public static byte[] Capture(FlyCamera3D camera, float blend)
        {
            MeshHandle floor = default, box = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(160f, 0.1f));
                    box = scene.LoadMesh(MeshPrimitives.Box(1.6f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    scene.CameraOverride = camera;
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    scene.Post.Quality.Shadows.ShadowNearDistance = NearDist;   // hand-off at view depth 8, mid-frame
                    scene.Post.Quality.Shadows.ShadowMaxDistance = MaxDist;
                    scene.Post.Quality.Shadows.ShadowCascadeBlend = blend;
                    scene.Post.LightDirection = Light;
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.Identity, new Color(0.60f, 0.61f, 0.63f, 1f));
                    foreach (float z in RowZ)
                        scene.Draw(box, Matrix4x4.CreateScale(1f, 2.0f, 1f) * Matrix4x4.CreateTranslation(0f, 1.6f, z),
                            new Color(0.80f, 0.35f, 0.20f, 1f));
                },
                frames: 2);
        }

        /// <summary>
        /// The golden's framing guard: scan the lower half of the screen on an 8-pixel grid, unproject each pixel to
        /// the ground plane and count the points whose cascade 0 UV sits within <paramref name="blend"/> of the map
        /// border while cascade 1 still covers them, exactly the fragments the shader blends. Measured 412 of 1200
        /// sampled points in-band as authored.
        /// </summary>
        public static int CountVisibleGroundPointsInCascade0BlendBand(FlyCamera3D cam, float blend)
        {
            Span<Matrix4x4> mats = stackalloc Matrix4x4[ShadowSettings.MaxCascades];
            if (FitCascades(cam, mats) < 2) return 0;

            int inBand = 0;
            for (int py = H / 2; py < H; py += 8)
                for (int px = 0; px < W; px += 8)
                {
                    Vector3 g = cam.ScreenToGround(new Vector2(px, py), W, H);
                    if (!ProjectToCascadeUv(mats[0], g, out float u0, out float v0)) continue;
                    float edge = MathF.Min(MathF.Min(u0, 1f - u0), MathF.Min(v0, 1f - v0));
                    if (edge < blend && ProjectToCascadeUv(mats[1], g, out _, out _)) inBand++;
                }
            return inBand;
        }

        /// <summary>
        /// Per-pixel mask of the visible floor that sits inside ANY inner cascade's blend band of width
        /// <paramref name="blend"/>: the tightest cascade containing the floor point (the shader's selection) is not
        /// the outermost, the point lies within the band of that cascade's border, and the next cascade covers it.
        /// Pixels whose ray never reaches the floor are false. Box faces are not modelled, so a caller dilates the
        /// mask rather than treating its edge as exact.
        /// </summary>
        public static bool[] BlendBandMask(FlyCamera3D cam, float blend)
        {
            var mask = new bool[W * H];
            Span<Matrix4x4> mats = stackalloc Matrix4x4[ShadowSettings.MaxCascades];
            int count = FitCascades(cam, mats);
            if (count < 2) return mask;

            for (int py = 0; py < H; py++)
                for (int px = 0; px < W; px++)
                {
                    Ray r = cam.ScreenToRay(new Vector2(px + 0.5f, py + 0.5f), W, H);
                    if (r.Direction.Y > -1e-6f) continue;   // at or above the horizon: never reaches the floor
                    Vector3 g = r.Origin + r.Direction * ((FloorTop - r.Origin.Y) / r.Direction.Y);
                    for (int i = 0; i < count; i++)
                    {
                        if (!ProjectToCascadeUv(mats[i], g, out float u, out float v)) continue;
                        float edge = MathF.Min(MathF.Min(u, 1f - u), MathF.Min(v, 1f - v));
                        mask[py * W + px] = i < count - 1 && edge < blend
                            && ProjectToCascadeUv(mats[i + 1], g, out _, out _);
                        break;
                    }
                }
            return mask;
        }

        /// <summary>
        /// Mirror <c>Scene3D.ComputeShadowCascades</c> for <paramref name="cam"/> (slice-sphere fit over the
        /// practical split, texel snap at the default resolution) and return the cascade count.
        /// </summary>
        static int FitCascades(FlyCamera3D cam, Span<Matrix4x4> mats)
        {
            Span<Vector3> corners = stackalloc Vector3[8];
            if (!KhaozEngine.Render3D.Internal.ShadowMapMath.FrustumCornersWorld(cam.ViewProjection, corners)) return 0;
            Vector3 eye = cam.Eye, fwd = cam.Forward;
            Vector3 nearC = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
            Vector3 farC = (corners[4] + corners[5] + corners[6] + corners[7]) * 0.25f;
            float camNear = Vector3.Dot(nearC - eye, fwd);
            float camFar = Vector3.Dot(farC - eye, fwd);
            float range = MathF.Max(camFar - camNear, 1e-3f);

            var defaults = new ShadowSettings();
            int res = defaults.ShadowMapResolution;
            int count = defaults.ResolvedCascadeCount;
            Span<float> splits = stackalloc float[ShadowSettings.MaxCascades];
            KhaozEngine.Render3D.Internal.ShadowMapMath.FillCascadeSplits(splits, count, NearDist, MaxDist);
            float prev = camNear;
            for (int i = 0; i < count; i++)
            {
                float d = Math.Clamp(splits[i], camNear, camFar);
                KhaozEngine.Render3D.Internal.ShadowMapMath.SliceBoundingSphere(corners,
                    (prev - camNear) / range, (d - camNear) / range, out Vector3 center, out float radius);
                mats[i] = KhaozEngine.Render3D.Internal.ShadowMapMath.BuildLightViewProj(Light, center, radius, res);
                prev = MathF.Max(d, prev);
            }
            return count;
        }

        // Project a world point through one cascade's light-clip matrix to map UV, returning false when the point
        // falls outside the map (or behind the light plane). The V flip the sampler applies is irrelevant here
        // because only border distance and coverage are read.
        static bool ProjectToCascadeUv(in Matrix4x4 mat, Vector3 p, out float u, out float v)
        {
            Vector4 lc = Vector4.Transform(new Vector4(p, 1f), mat);
            u = v = 0f;
            if (lc.W <= 0f) return false;
            u = lc.X / lc.W * 0.5f + 0.5f;
            v = lc.Y / lc.W * 0.5f + 0.5f;
            float z = lc.Z / lc.W;
            return u >= 0f && u <= 1f && v >= 0f && v <= 1f && z >= 0f && z <= 1f;
        }
    }
}
