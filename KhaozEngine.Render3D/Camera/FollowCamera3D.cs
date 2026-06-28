using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Third-person follow camera: a perspective camera that orbits behind a moving <see cref="Target"/> at a
    /// clamped <see cref="Pitch"/> and <see cref="Distance"/>, always looking at the target. Sibling of
    /// <see cref="IsoCamera3D"/> (same Y-up right-handed convention, same Eye/Forward/ScreenToGround helpers) but
    /// perspective so scroll-zoom-via-distance reads naturally. Pure System.Numerics, no GPU and no input types;
    /// drive it with a <see cref="FollowCameraController"/> or set the fields directly.
    ///
    /// Convention (matches IsoCamera3D): dirToEye = normalize(cosP*sinYaw, sinP, cosP*cosYaw),
    /// Eye = Target + dirToEye*Distance + (0, HeightOffset, 0), looking at Target.
    /// </summary>
    public sealed class FollowCamera3D : IIsoCamera3D
    {
        /// <summary>World-space point the camera follows (the character position).</summary>
        public Vector3 Target = Vector3.Zero;
        /// <summary>Orbit angle about the Y (up) axis, radians. Yaw 0 puts the eye on +Z looking toward -Z.</summary>
        public float Yaw = 0f;

        /// <summary>
        /// Opt-in target damping. When true, the camera follows a smoothed <see cref="EffectiveTarget"/> that eases
        /// toward <see cref="Target"/> each <see cref="AdvanceTarget"/> call instead of snapping - belt-and-suspenders
        /// against residual avatar jitter on a remote server. Default OFF, so existing consumers (which read
        /// <see cref="Eye"/>/<see cref="View"/> without driving the damping) are completely unchanged.
        /// </summary>
        public bool EnableTargetDamping = false;
        /// <summary>Exponential follow rate (per second) used when <see cref="EnableTargetDamping"/> is on; higher is
        /// snappier. Frame-rate independent. Default 10.</summary>
        public float TargetDampingRate = 10f;

        Vector3 _dampedTarget;
        bool _dampedInit;

        /// <summary>
        /// The point the camera geometry actually uses: <see cref="Target"/> when damping is off (or before the first
        /// <see cref="AdvanceTarget"/> call), otherwise the smoothed target that eases toward <see cref="Target"/>.
        /// </summary>
        public Vector3 EffectiveTarget => EnableTargetDamping && _dampedInit ? _dampedTarget : Target;

        /// <summary>
        /// Advances the optional target damping by <paramref name="dt"/> seconds (call once per render frame, e.g. via
        /// <see cref="FollowCameraController.Update"/>). A no-op for the camera geometry while
        /// <see cref="EnableTargetDamping"/> is off (it just keeps the smoothed target synced so enabling later starts
        /// without a lurch). The first call after enabling locks the smoothed target onto the current
        /// <see cref="Target"/>; subsequent calls ease it in frame-rate-independently.
        /// </summary>
        public void AdvanceTarget(float dt)
        {
            if (!EnableTargetDamping || !_dampedInit)
            {
                _dampedTarget = Target;   // disabled, or first frame: lock onto the live target (no lurch)
                _dampedInit = true;
                return;
            }
            if (dt <= 0f || !(TargetDampingRate > 0f) || !float.IsFinite(TargetDampingRate))
                return;                   // nothing to advance, or a degenerate rate: hold the smoothed target
            float a = 1f - MathF.Exp(-TargetDampingRate * dt);   // exponential smoothing -> frame-rate independent
            _dampedTarget = Vector3.Lerp(_dampedTarget, Target, a);
        }

        /// <summary>Lower clamp for <see cref="Pitch"/>, radians (kept &gt; 0 so the view never goes flat). Default ~6 deg.</summary>
        public float MinPitch = MathF.PI / 30f;
        /// <summary>Upper clamp for <see cref="Pitch"/>, radians (kept &lt; 90 deg so LookAt never degenerates). Default ~80 deg.</summary>
        public float MaxPitch = MathF.PI * 0.45f;
        /// <summary>Nearest the eye may sit to the target. Default 2.</summary>
        public float MinDistance = 2f;
        /// <summary>Farthest the eye may sit from the target. Default 30.</summary>
        public float MaxDistance = 30f;
        /// <summary>Eye height added above the target so the camera looks slightly down at the character. Default 1.</summary>
        public float HeightOffset = 1f;

        /// <summary>Vertical field of view, radians. Default 60 deg.</summary>
        public float FieldOfView = MathF.PI / 3f;
        /// <summary>Viewport aspect (width/height). Set this from the framebuffer each frame.</summary>
        public float AspectRatio = 16f / 9f;
        public float NearPlane = 0.1f;
        public float FarPlane = 500f;

        /// <summary>
        /// Optional ground-height sampler. When set, <see cref="Eye"/> is kept at least <see cref="GroundClearance"/>
        /// above the ground at its own XZ, so the camera does not sink through terrain when the target is in a dip
        /// (the surrounding ground rises behind it). Terrain-agnostic: a plain delegate, no terrain dependency
        /// (mirrors how <c>CharacterController3D</c> takes ground height). Null (the default) leaves the eye purely
        /// geometric.
        /// </summary>
        public Func<float, float, float>? GroundHeight;
        /// <summary>Minimum gap kept between the eye and the ground when <see cref="GroundHeight"/> is set. Default 0.5.</summary>
        public float GroundClearance = 0.5f;

        float _pitch = MathF.PI / 6f;   // 30 deg, a comfortable default tilt
        float _distance = 8f;

        /// <summary>Tilt above the horizontal, radians, clamped to [<see cref="MinPitch"/>, <see cref="MaxPitch"/>].</summary>
        public float Pitch
        {
            get => _pitch;
            set => _pitch = Math.Clamp(value, MinPitch, MaxPitch);
        }

        /// <summary>Eye distance from the target, clamped to [<see cref="MinDistance"/>, <see cref="MaxDistance"/>].</summary>
        public float Distance
        {
            get => _distance;
            set => _distance = Math.Clamp(value, MinDistance, MaxDistance);
        }

        Vector3 DirToEye
        {
            get
            {
                float cP = MathF.Cos(_pitch), sP = MathF.Sin(_pitch);
                float cY = MathF.Cos(Yaw), sY = MathF.Sin(Yaw);
                return Vector3.Normalize(new Vector3(cP * sY, sP, cP * cY));
            }
        }

        public Vector3 Eye
        {
            get
            {
                Vector3 eye = EffectiveTarget + DirToEye * _distance + new Vector3(0f, HeightOffset, 0f);
                if (GroundHeight is { } ground)
                {
                    float floor = ground(eye.X, eye.Z) + GroundClearance;
                    if (eye.Y < floor) eye.Y = floor;   // keep the eye out of the terrain in a dip
                }
                return eye;
            }
        }

        public Vector3 Forward => Vector3.Normalize(EffectiveTarget - Eye);

        public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, EffectiveTarget, Vector3.UnitY);
        public Matrix4x4 Projection => Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, AspectRatio, NearPlane, FarPlane);
        public Matrix4x4 ViewProjection => View * Projection;

        /// <summary>Project a world point to a screen pixel (forward inverse of <see cref="ScreenToRay"/>); false
        /// when the point is not in front of the camera. See <see cref="IIsoCamera3D.WorldToScreen"/>.</summary>
        public bool WorldToScreen(Vector3 world, int viewportWidth, int viewportHeight, out Vector2 screenPixel) =>
            CameraProjection.WorldToScreen(ViewProjection, world, viewportWidth, viewportHeight, out screenPixel);

        /// <summary>Unproject a screen pixel (top-left origin, y-down) into a world ray (mirrors IsoCamera3D).</summary>
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
