using System.Numerics;
using KhaozEngine.Locomotion;
using static KhaozEngine.Movement.RangeApproachCore;

namespace KhaozEngine.Movement;

public sealed partial class MoveToRange
{
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
        command = StopAtRange(body, tuning, target, range, run, dt, context, command, predicted, _admits);
        MoveState accepted = context.Step(body, command, run, dt, tuning);
        return AllowsStep(body, accepted, tuning);
    }

    private bool AllowsStep(in MoveState body, in MoveState predicted, in MoveTuning tuning)
        => predicted.Grounded && !predicted.Swimming && MovementBody.IsFinite(predicted.Position) &&
            _allowsSegment(Feet(body, tuning), Feet(predicted, tuning));
}
