using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public static partial class CharacterMovement
{
    /// <summary>Measures the released down pass, including its conditional tread search.</summary>
    internal static float MeasureSupportDownPass(IPhysicsWorld world, in CapsuleShape capsule, in Vector3 pos,
        in Vector3 startPos, bool wasGrounded, in MoveTuning tuning, float halfHeight, float terrainGroundY,
        float groundY, bool steppedUp) =>
        PropSupportFloor(world, capsule, pos, startPos, wasGrounded, tuning, halfHeight, terrainGroundY,
            groundY, steppedUp);
}
