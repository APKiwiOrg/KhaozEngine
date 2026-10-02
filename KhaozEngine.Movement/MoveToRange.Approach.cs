using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

public sealed partial class MoveToRange
{
    private static float TravelBound(in MoveState body, in MoveTuning tuning, bool run, float dt,
        GroundMoveContext context)
    {
        float wade = CharacterMovement.WadeSpeedScale(body.Position.X, body.Position.Z,
            body.Position.Y - tuning.CapsuleHalfHeight, tuning, context.Medium);
        if (!float.IsFinite(wade)) throw new ArgumentOutOfRangeException(nameof(context), "Medium pace must be finite.");
        float bound = (run ? tuning.RunSpeed : tuning.WalkSpeed) * (MathF.Max(1f, wade) * body.SpeedScale) * dt;
        if (!float.IsFinite(bound)) throw new ArgumentOutOfRangeException(nameof(dt), "Commanded travel must be finite.");
        return bound;
    }

    private static Vector2 BoundedDirection(Vector2 offset, float bound)
    {
        double length = Math.Sqrt((double)offset.X * offset.X + (double)offset.Y * offset.Y);
        if (length == 0d) return Vector2.Zero;
        double divisor = Math.Max(bound, length);
        return new Vector2((float)(offset.X / divisor), (float)(offset.Y / divisor));
    }

    private bool TryApproach(in MoveState body, in MoveTuning tuning, in ReachTarget target, float range,
        bool run, float dt, GroundMoveContext context, float bound, out Vector2 command)
    {
        Vector2 closest = ClosestHorizontal(body.Position, target);
        command = BoundedDirection(closest - new Vector2(body.Position.X, body.Position.Z), bound);
        if (command == Vector2.Zero) return false;
        MoveState predicted = context.Step(body, command, run, dt, tuning);
        // The actual shape at the core's predicted support height must intersect this tick's segment.
        if (!AllowsStep(body, predicted, tuning) || !ReachGeometry.Within(Body(predicted, tuning), target, range))
            return false;
        command = StopAtRange(body, tuning, target, range, run, dt, context, command, predicted);
        MoveState accepted = context.Step(body, command, run, dt, tuning);
        return AllowsStep(body, accepted, tuning);
    }

    private bool AllowsStep(in MoveState body, in MoveState predicted, in MoveTuning tuning)
        => predicted.Grounded && !predicted.Swimming && MovementBody.IsFinite(predicted.Position) &&
            _allowsSegment(Feet(body, tuning), Feet(predicted, tuning));

    private static Vector2 ClosestHorizontal(Vector3 position, in ReachTarget target)
    {
        if (target.Kind != ReachTargetKind.Box) return new Vector2(target.Centre.X, target.Centre.Z);
        double cos = Math.Cos(target.YawRadians), sin = Math.Sin(target.YawRadians);
        double dx = (double)position.X - target.Centre.X, dz = (double)position.Z - target.Centre.Z;
        double x = Math.Clamp(cos * dx - sin * dz, -target.HalfExtents.X, target.HalfExtents.X);
        double z = Math.Clamp(sin * dx + cos * dz, -target.HalfExtents.Z, target.HalfExtents.Z);
        return new Vector2((float)(target.Centre.X + cos * x + sin * z),
            (float)(target.Centre.Z - sin * x + cos * z));
    }

    private Vector2 StopAtRange(in MoveState body, in MoveTuning tuning, in ReachTarget target, float range,
        bool run, float dt, GroundMoveContext context, Vector2 command, in MoveState predicted)
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
            if (AllowsStep(body, candidate, tuning) && ReachGeometry.Within(Body(candidate, tuning), target, range))
                high = fraction;
            else
                low = fraction;
        }
        return command * high;
    }
}
