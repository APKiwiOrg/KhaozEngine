using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>Decides whether a resolved candidate step may be taken by a range approach driver.</summary>
internal delegate bool StepAdmission(in MoveState from, in MoveState to, in MoveTuning tuning);

/// <summary>The target shape, range and capsule geometry whose change resets a range approach driver.
/// Target translation is not part of the key.</summary>
internal readonly record struct RangeShapeKey(ReachTargetKind Kind, float TargetRadius, float TargetHalfHeight,
    Vector3 HalfExtents, float Yaw, float Range, float Radius, float HalfHeight, float Slope, float Step)
{
    internal static RangeShapeKey From(in MoveTuning tuning, in ReachTarget target, float range)
        => new(target.Kind, target.Body.Radius, target.Body.HalfHeight, target.HalfExtents, target.YawRadians, range,
            tuning.CapsuleRadius, tuning.CapsuleHalfHeight, tuning.MaxSlopeRadians, tuning.StepHeight);
}

/// <summary>Reach, travel bound, closest point and stop ring rules shared by the range approach drivers.</summary>
internal static class RangeApproachCore
{
    internal static float TravelBound(in MoveState body, in MoveTuning tuning, bool run, float dt,
        GroundMoveContext context)
    {
        float wade = CharacterMovement.WadeSpeedScale(body.Position.X, body.Position.Z,
            body.Position.Y - tuning.CapsuleHalfHeight, tuning, context.Medium);
        if (!float.IsFinite(wade)) throw new ArgumentOutOfRangeException(nameof(context), "Medium pace must be finite.");
        float bound = (run ? tuning.RunSpeed : tuning.WalkSpeed) * (MathF.Max(1f, wade) * body.SpeedScale) * dt;
        if (!float.IsFinite(bound)) throw new ArgumentOutOfRangeException(nameof(dt), "Commanded travel must be finite.");
        return bound;
    }

    internal static Vector2 BoundedDirection(Vector2 offset, float bound)
    {
        double length = Math.Sqrt((double)offset.X * offset.X + (double)offset.Y * offset.Y);
        if (length == 0d) return Vector2.Zero;
        double divisor = Math.Max(bound, length);
        return new Vector2((float)(offset.X / divisor), (float)(offset.Y / divisor));
    }

    internal static Vector2 ClosestHorizontal(Vector3 position, in ReachTarget target)
    {
        if (target.Kind != ReachTargetKind.Box) return new Vector2(target.Centre.X, target.Centre.Z);
        double cos = Math.Cos(target.YawRadians), sin = Math.Sin(target.YawRadians);
        double dx = (double)position.X - target.Centre.X, dz = (double)position.Z - target.Centre.Z;
        double x = Math.Clamp(cos * dx - sin * dz, -target.HalfExtents.X, target.HalfExtents.X);
        double z = Math.Clamp(sin * dx + cos * dz, -target.HalfExtents.Z, target.HalfExtents.Z);
        return new Vector2((float)(target.Centre.X + cos * x + sin * z),
            (float)(target.Centre.Z - sin * x + cos * z));
    }

    internal static MovementBody Body(in MoveState body, in MoveTuning tuning)
        => new(body.Position, tuning.CapsuleRadius, tuning.CapsuleHalfHeight);

    internal static Vector3 Feet(in MoveState body, in MoveTuning tuning)
        => body.Position - new Vector3(0f, tuning.CapsuleHalfHeight, 0f);

    internal static void ValidateBody(in MoveState body)
    {
        if (!MovementBody.IsFinite(body.Position) || !float.IsFinite(body.SpeedScale) ||
            !float.IsFinite(body.VerticalVelocity) || !float.IsFinite(body.TimeSinceGrounded) ||
            !float.IsFinite(body.JumpBufferRemaining) || !float.IsFinite(body.FacingYaw) ||
            !float.IsFinite(body.HorizontalVelocity.X) || !float.IsFinite(body.HorizontalVelocity.Y) ||
            !float.IsFinite(body.ClimbRateEwma))
            throw new ArgumentOutOfRangeException(nameof(body), "Movement state must be finite.");
    }

    internal static Vector2 StopAtRange(in MoveState body, in MoveTuning tuning, in ReachTarget target, float range,
        bool run, float dt, GroundMoveContext context, Vector2 command, in MoveState predicted, StepAdmission admits)
    {
        if (!ReachGeometry.Within(Body(predicted, tuning), target, range)) return command;
        float low = 0f, high = 1f;
        // Each candidate resolves on a copy through the live shared core. This includes support height,
        // bounds, medium and collision, without a position snap or a gameplay reach epsilon.
        for (int iteration = 0; iteration < 32; iteration++)
        {
            float fraction = low + (high - low) * 0.5f;
            if (fraction == low || fraction == high) break;
            MoveState candidate = context.Step(body, command * fraction, run, dt, tuning);
            if (admits(body, candidate, tuning) && ReachGeometry.Within(Body(candidate, tuning), target, range))
                high = fraction;
            else
                low = fraction;
        }
        return command * high;
    }
}
