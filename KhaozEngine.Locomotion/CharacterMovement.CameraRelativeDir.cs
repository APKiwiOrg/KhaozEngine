using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public static partial class CharacterMovement
{
    /// <summary>The COMMANDED camera-relative travel direction of <paramref name="cmd"/> as a unit vector in world XZ
    /// (<c>X</c> = world +X, <c>Y</c> = world +Z), or <see cref="Vector2.Zero"/> when the command is idle (its move
    /// axis is inside the 1e-6 length-squared dead-zone). This is the exact direction the authoritative and
    /// client-prediction
    /// <see cref="Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>
    /// resolves the command to before it moves, so a consumer driving EXPLICIT model facing (facing the model toward
    /// where it is COMMANDED to travel, distinct from the direction the measured render position drifts) shares the ONE
    /// camera basis instead of copying it by hand. A commanded-facing yaw is then just <c>MathF.Atan2(dir.X, dir.Y)</c>
    /// on the result (world radians about +Y, 0 = +Z), gated on the vector being non-zero for idle. Delegates to the
    /// same private <c>ResolveCameraRelative</c> the step uses, so the public facing and the resolved movement can
    /// never drift apart. Lives in its own partial file so the (already large) main <c>CharacterMovement.cs</c> does
    /// not grow.</summary>
    /// <param name="cmd">The movement command (camera-relative axis + camera yaw).</param>
    /// <returns>The unit world-space travel direction (XZ), or <see cref="Vector2.Zero"/> when idle.</returns>
    public static Vector2 CameraRelativeDir(in MoveCommand cmd) => ResolveCameraRelative(cmd).dir;
}
