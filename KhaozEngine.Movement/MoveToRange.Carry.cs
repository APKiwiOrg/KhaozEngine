using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;
using static KhaozEngine.Movement.RangeApproachCore;

namespace KhaozEngine.Movement;

public sealed partial class MoveToRange
{
    // On a straight run, aiming at the run end and at the next waypoint is the same direction, so the body may
    // travel the full bound past cell centres. The follower then consumes the passed collinear waypoints. A corner
    // is never a pass-through, so it is still landed on exactly.
    private bool TryCarry(in MoveState body, in MoveTuning tuning, bool run, float dt, GroundMoveContext context,
        Vector2 waypoint, float bound, out Vector2 command)
    {
        command = Vector2.Zero;
        if (!_options.CarryThroughStraightRuns) return false;
        NavPath? path = _follower.ActivePath;
        if (path is null) return false;
        var position = new Vector2(body.Position.X, body.Position.Z);
        double dx = (double)waypoint.X - position.X, dz = (double)waypoint.Y - position.Y;
        int index = _follower.ActiveWaypointIndex;
        if (!(Math.Sqrt(dx * dx + dz * dz) < bound) || !path.IsCollinearPassThrough(index)) return false;
        // The last waypoint is never a pass-through, so the run end always exists.
        do index++;
        while (path.IsCollinearPassThrough(index));
        Vector2 carried = BoundedDirection(path.Waypoints[index].Position - position, bound);
        if (carried == Vector2.Zero) return false;
        MoveState predicted = context.Step(body, carried, run, dt, tuning);
        if (!AllowsStep(body, predicted, tuning)) return false;
        command = carried;
        return true;
    }
}
