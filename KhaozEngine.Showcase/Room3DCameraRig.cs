using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase;

/// <summary>Room3D's terrain-bound mouse-look setup around its presented capsule-centre target.</summary>
internal static class Room3DCameraRig
{
    internal static FollowCameraController Create(Vector3 target, float capsuleHalfHeight,
        Func<float, float, float> groundHeight, IPhysicsWorld? physics)
    {
        ArgumentNullException.ThrowIfNull(groundHeight);
        FollowCamera3D camera = FollowCamera3DPresets.CreateMouseLook();
        camera.Target = target;
        camera.Yaw = MathF.PI;
        camera.PivotHeight -= capsuleHalfHeight;
        camera.Distance = 9f;
        camera.GroundHeight = groundHeight;
        camera.Occlusion = physics;
        camera.EnableTargetDamping = true;
        return new FollowCameraController(camera)
        {
            OrbitGesture = new PointerGesture(MouseButton.Left),
            LookGesture = new PointerGesture(MouseButton.Right),
        };
    }
}
