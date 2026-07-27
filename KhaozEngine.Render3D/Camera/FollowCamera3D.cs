using System;
using System.Numerics;
using KhaozEngine.Physics;

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
    public sealed class FollowCamera3D : IIsoCamera3D, IRenderOriginAware
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

        /// <summary>
        /// Hard-cuts the camera onto <paramref name="target"/>, bypassing target damping: sets <see cref="Target"/>
        /// and forces the smoothed target so <see cref="EffectiveTarget"/> equals <paramref name="target"/> THIS
        /// frame with zero trailing; normal damping resumes on the next <see cref="AdvanceTarget"/>. The 3D
        /// counterpart of <c>Render2D.CameraFollow.Warp</c> - use it on a teleport (login/reconnect placement,
        /// self-rescue, fast-travel) so the follow camera does not ease ("fly") across the jump. While
        /// <see cref="EnableTargetDamping"/> is off the effective target already tracks <see cref="Target"/>, so the
        /// cut is invisible, but it still updates <see cref="Target"/> and arms the smoothed state so enabling
        /// damping later starts without a lurch.
        /// </summary>
        /// <param name="target">The world-space point to cut the camera onto (also the new follow point).</param>
        public void Warp(Vector3 target)
        {
            Target = target;
            _dampedTarget = target;
            _dampedInit = true;
        }

        /// <summary>
        /// Collapses any in-flight target damping onto the current <see cref="Target"/> without moving the follow
        /// point, so <see cref="EffectiveTarget"/> equals <see cref="Target"/> this frame. Equivalent to
        /// <c>Warp(Target)</c>; use it to kill a residual ease after <see cref="Target"/> was set directly.
        /// </summary>
        public void SnapToTarget() => Warp(Target);

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

        /// <summary>
        /// Optional occlusion sweep. When set, <see cref="Eye"/> sweeps a sphere probe from
        /// <see cref="EffectiveTarget"/> toward the geometric eye (the boom) and pulls the eye in to the first
        /// static hit, so the follow camera never clips through a wall or ceiling between the target and the
        /// desired eye. Mirrors <c>CharacterMovement</c>'s own swept collide-and-slide: a zero-length capsule
        /// (a sphere of radius <see cref="OcclusionRadius"/>) is swept via <see cref="IPhysicsWorld.SweepCapsule"/>
        /// against statics only (<see cref="QueryFilter.StaticsOnly"/>). Applied BEFORE <see cref="GroundHeight"/>
        /// clearance, so a ground dip can still lift the (already pulled-in) eye clear of the terrain. Null (the
        /// default) leaves the eye purely geometric - existing consumers that never set this are unchanged.
        /// </summary>
        public IPhysicsWorld? Occlusion;
        /// <summary>Sphere-probe radius (metres) used by the <see cref="Occlusion"/> sweep. Default 0.25.</summary>
        public float OcclusionRadius = 0.25f;
        /// <summary>Clearance (metres) kept between the pulled-in eye and the occluding surface (mirrors the swept
        /// collide-and-slide skin-width convention), so the eye sits just off the wall rather than flush against
        /// it. Default 0.05.</summary>
        public float OcclusionSkin = 0.05f;
        /// <summary>The closest the <see cref="Occlusion"/> sweep is ever allowed to pull the boom (metres), so the
        /// eye never collapses onto the target and leave <see cref="Forward"/>/<see cref="View"/> degenerate (a
        /// zero-length look direction). A static within a skin of the target (e.g. a character pressed flush against
        /// a wall) is clamped to this floor instead. Default 0.2.</summary>
        public float MinOcclusionDistance = 0.2f;

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
                Vector3 target = EffectiveTarget;
                Vector3 eye = target + DirToEye * _distance + new Vector3(0f, HeightOffset, 0f);
                if (Occlusion is { } world)
                {
                    // Sweep a sphere probe (a zero-length capsule) from the target toward the desired eye along the
                    // boom. The first static hit clamps how far out the boom can extend, mirroring the
                    // hit.Distance - skin convention CharacterMovement uses for its own swept collide-and-slide. The
                    // pull-in is floored at MinOcclusionDistance so a static right at the target never collapses the
                    // eye onto it (which would leave Forward/View with a zero-length look direction).
                    Vector3 toEye = eye - target;
                    float dist = toEye.Length();
                    if (dist > 1e-6f)
                    {
                        Vector3 dir = toEye / dist;
                        if (world.SweepCapsule(new CapsuleShape(OcclusionRadius, 0f), Pose.At(target), dir, dist,
                                out SweepHit hit, QueryFilter.StaticsOnly))
                            eye = target + dir * MathF.Max(MinOcclusionDistance, hit.Distance - OcclusionSkin);
                    }
                }
                if (GroundHeight is { } ground)
                {
                    float floor = ground(eye.X, eye.Z) + GroundClearance;
                    if (eye.Y < floor) eye.Y = floor;   // keep the eye out of the terrain in a dip
                }
                return eye;
            }
        }

        public Vector3 Forward => Vector3.Normalize(EffectiveTarget - Eye);

        /// <summary>The render origin eye and target are expressed against when building <see cref="View"/>. See
        /// <see cref="IRenderOriginAware"/>. <see cref="Vector3.Zero"/> (the default) is the pre-floating-origin
        /// camera, bit for bit.</summary>
        public Vector3 RenderOrigin { get; set; }

        public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye - RenderOrigin, EffectiveTarget - RenderOrigin, Vector3.UnitY);
        public Matrix4x4 Projection => Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, AspectRatio, NearPlane, FarPlane);
        public Matrix4x4 ViewProjection => View * Projection;

        /// <summary>The pre-shift view-projection. See <see cref="IRenderOriginAware.AbsoluteViewProjection"/>.</summary>
        public Matrix4x4 AbsoluteViewProjection =>
            Matrix4x4.CreateLookAt(Eye, EffectiveTarget, Vector3.UnitY) * Projection;

        /// <summary>Project a world point to a screen pixel (forward inverse of <see cref="ScreenToRay"/>); false
        /// when the point is not in front of the camera. See <see cref="IIsoCamera3D.WorldToScreen(Vector3, int, int, out Vector2)"/>.</summary>
        public bool WorldToScreen(Vector3 world, int viewportWidth, int viewportHeight, out Vector2 screenPixel) =>
            CameraProjection.WorldToScreen(ViewProjection, world - RenderOrigin, viewportWidth, viewportHeight, out screenPixel);

        /// <summary>Unproject a screen pixel (top-left origin, y-down) into a world ray (mirrors IsoCamera3D).</summary>
        public Ray ScreenToRay(Vector2 screenPixel, int viewportWidth, int viewportHeight)
        {
            float ndcX = screenPixel.X / viewportWidth * 2f - 1f;
            float ndcY = 1f - screenPixel.Y / viewportHeight * 2f;
            Matrix4x4.Invert(ViewProjection, out var inv);
            Vector3 near = Unproject(new Vector3(ndcX, ndcY, 0f), inv);
            Vector3 far = Unproject(new Vector3(ndcX, ndcY, 1f), inv);
            // The unprojection lands in the RENDER frame, so add the origin back: the ray this returns is absolute
            // world, as it always was. The direction is a difference and is frame-invariant.
            return new Ray(near + RenderOrigin, far - near);
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
