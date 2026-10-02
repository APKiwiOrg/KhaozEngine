using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public static partial class CharacterMovement
{
    /// <summary>World-space movement through the shared collision core, optionally preserving nonzero direction
    /// magnitudes below the legacy dead zone. Precise input clamps speed fraction to 1 and rejects non-finite
    /// components. False retains the legacy resolver.</summary>
    public static MoveState StepTowards(in MoveState state, Vector2 worldDir, bool run, float dt,
        Func<float, float, float> groundHeight, in MoveTuning tuning, bool preserveSmallMagnitude,
        Func<float, float, Vector3>? groundNormal = null, IPhysicsWorld? world = null,
        Func<float, float, Vector2>? clampXz = null,
        Func<float, float, float, MovementMedium>? medium = null)
    {
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        (Vector2 moveDir, float speedFraction) = ResolveWorldDir(worldDir, preserveSmallMagnitude);
        return StepCore(state, moveDir, speedFraction, run, jump: false, dt, groundHeight, tuning,
            groundNormal, world, clampXz, medium);
    }

    private static (Vector2 dir, float fraction) ResolvePreciseCameraRelative(in MoveCommand cmd)
    {
        if (!float.IsFinite(cmd.CameraYaw)) return (Vector2.Zero, 0f);
        (Vector2 axis, float fraction) = ResolvePreciseDirection(cmd.Move);
        if (fraction == 0f) return (Vector2.Zero, 0f);

        float sY = MathF.Sin(cmd.CameraYaw), cY = MathF.Cos(cmd.CameraYaw);
        Vector2 worldDir = new(cY * axis.X - sY * axis.Y, -sY * axis.X - cY * axis.Y);
        // Rotate unit input so extreme axes cannot overflow the camera basis. The original axis owns speed.
        return (ResolvePreciseDirection(worldDir).dir, fraction);
    }

    private static (Vector2 dir, float fraction) ResolvePreciseDirection(Vector2 direction)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y)) return (Vector2.Zero, 0f);
        float largest = MathF.Max(MathF.Abs(direction.X), MathF.Abs(direction.Y));
        if (largest == 0f) return (Vector2.Zero, 0f);

        // One scaled component has magnitude 1, keeping length squared in [1,2] at either float extreme.
        Vector2 scaled = direction / largest;
        float length = MathF.Sqrt(scaled.LengthSquared());
        float fraction = largest >= 1f ? 1f : MathF.Min(largest * length, 1f);
        return (scaled / length, fraction);
    }
}
