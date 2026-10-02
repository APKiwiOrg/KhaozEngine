namespace KhaozEngine.Render3D;

/// <summary>Opt-in configuration bundles for a fresh <see cref="FollowCamera3D"/>.</summary>
public static class FollowCamera3DPresets
{
    /// <summary>Creates a mouse-look camera with pitch stops at -80 and about 78 degrees, distance stops at
    /// 1.5 and 22 metres, a 1.5 metre pivot lift, no eye-only height offset, 0.3 metre ground clearance and
    /// boom recovery rate 4 per second. Starts at pitch 0.75 radians and distance 12 metres.</summary>
    /// <remarks>Bind the target, ground sampler and occlusion world or boom probe. Pivot height is relative to
    /// Target, so subtract a capsule half-height when following its centre instead of its feet. Gesture policy,
    /// target damping and frame-clock binding belong to the caller. Existing constructor defaults are unchanged.</remarks>
    public static FollowCamera3D CreateMouseLook()
    {
        var camera = new FollowCamera3D
        {
            MinPitch = -1.3962634f,
            MaxPitch = 1.36f,
            MinDistance = 1.5f,
            MaxDistance = 22f,
            PivotHeight = 1.5f,
            HeightOffset = 0f,
            GroundClearance = 0.3f,
            BoomRecoveryRate = 4f,
        };
        camera.Pitch = 0.75f;
        camera.Distance = 12f;
        return camera;
    }
}
