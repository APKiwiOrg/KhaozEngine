using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Free-fly perspective camera for editor viewports: a world <see cref="Position"/> plus a
    /// <see cref="Yaw"/>/<see cref="Pitch"/> look direction, no orbit target. Implements
    /// <see cref="IIsoCamera3D"/> so it drops into <c>Scene3D.CameraOverride</c> exactly like
    /// <see cref="FollowCamera3D"/>. Pure System.Numerics, no GPU and no input types; drive it with a
    /// <see cref="FlyCameraController"/> or set the fields directly.
    ///
    /// Convention (reuses <see cref="FollowCamera3D"/>'s yaw/pitch basis formula): the look direction is
    /// <see cref="Forward"/> = normalize(cosPitch*sinYaw, sinPitch, cosPitch*cosYaw), so Yaw 0 with Pitch 0
    /// looks along +Z and a positive <see cref="Pitch"/> tilts the view up (+Y). Pitch is clamped just shy
    /// of straight up/down so the world-up LookAt view never degenerates.
    /// </summary>
    public sealed class FlyCamera3D : IIsoCamera3D
    {
        /// <summary>Just under 90 degrees: the pitch clamp keeps <see cref="Forward"/> off the vertical so the
        /// world-up LookAt stays well conditioned.</summary>
        const float PitchLimit = MathF.PI / 2f - 0.017f;

        float _pitch;

        /// <summary>World-space eye position. Exposed through <see cref="IIsoCamera3D.Eye"/> as <see cref="Eye"/>.</summary>
        public Vector3 Position { get; set; } = Vector3.Zero;

        /// <summary>Heading about the Y (up) axis, radians. Yaw 0 (with <see cref="Pitch"/> 0) looks along +Z.</summary>
        public float Yaw { get; set; }

        /// <summary>Tilt above the horizon, radians, clamped to +-(PI/2 - 0.017) so the view never looks fully
        /// vertical. A positive value looks up.</summary>
        public float Pitch
        {
            get => _pitch;
            set => _pitch = Math.Clamp(value, -PitchLimit, PitchLimit);
        }

        /// <summary>Vertical field of view, radians. Default 60 deg (matches <see cref="FollowCamera3D"/>).</summary>
        public float FieldOfView { get; set; } = MathF.PI / 3f;
        /// <summary>Viewport aspect (width/height). Set this from the framebuffer each frame. Default 16:9.</summary>
        public float AspectRatio { get; set; } = 16f / 9f;
        /// <summary>Near clip plane. Default 0.1 (matches <see cref="FollowCamera3D"/>).</summary>
        public float NearPlane { get; set; } = 0.1f;
        /// <summary>Far clip plane. Default 500 (matches <see cref="FollowCamera3D"/>).</summary>
        public float FarPlane { get; set; } = 500f;

        /// <summary>The eye position (== <see cref="Position"/>), satisfying <see cref="IIsoCamera3D.Eye"/>.</summary>
        public Vector3 Eye => Position;

        /// <summary>Unit look direction from <see cref="Yaw"/>/<see cref="Pitch"/>, using
        /// <see cref="FollowCamera3D"/>'s basis formula: normalize(cosPitch*sinYaw, sinPitch, cosPitch*cosYaw).</summary>
        public Vector3 Forward
        {
            get
            {
                float cP = MathF.Cos(_pitch), sP = MathF.Sin(_pitch);
                float cY = MathF.Cos(Yaw), sY = MathF.Sin(Yaw);
                return Vector3.Normalize(new Vector3(cP * sY, sP, cP * cY));
            }
        }

        public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);
        public Matrix4x4 Projection => Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, AspectRatio, NearPlane, FarPlane);
        public Matrix4x4 ViewProjection => View * Projection;

        /// <summary>Project a world point to a screen pixel (forward inverse of <see cref="ScreenToRay"/>); false
        /// when the point is not in front of the camera. See <see cref="IIsoCamera3D.WorldToScreen"/>.</summary>
        public bool WorldToScreen(Vector3 world, int viewportWidth, int viewportHeight, out Vector2 screenPixel) =>
            CameraProjection.WorldToScreen(ViewProjection, world, viewportWidth, viewportHeight, out screenPixel);

        /// <summary>Unproject a screen pixel (top-left origin, y-down) into a world ray (mirrors <see cref="FollowCamera3D"/>).</summary>
        public Ray ScreenToRay(Vector2 screenPixel, int viewportWidth, int viewportHeight)
        {
            float ndcX = screenPixel.X / viewportWidth * 2f - 1f;
            float ndcY = 1f - screenPixel.Y / viewportHeight * 2f;
            Matrix4x4.Invert(ViewProjection, out var inv);
            Vector3 near = Unproject(new Vector3(ndcX, ndcY, 0f), inv);
            Vector3 far = Unproject(new Vector3(ndcX, ndcY, 1f), inv);
            return new Ray(near, far - near);
        }

        /// <summary>Pick the world point under a screen pixel on the horizontal plane y = <paramref name="groundY"/>.</summary>
        public Vector3 ScreenToGround(Vector2 screenPixel, int viewportWidth, int viewportHeight, float groundY = 0f)
        {
            Ray r = ScreenToRay(screenPixel, viewportWidth, viewportHeight);
            float t = MathF.Abs(r.Direction.Y) < 1e-6f ? 0f : (groundY - r.Origin.Y) / r.Direction.Y;
            return r.Origin + r.Direction * t;
        }

        static Vector3 Unproject(Vector3 ndc, Matrix4x4 invViewProj)
        {
            var p = Vector4.Transform(new Vector4(ndc, 1f), invViewProj);
            return new Vector3(p.X, p.Y, p.Z) / p.W;
        }
    }
}
