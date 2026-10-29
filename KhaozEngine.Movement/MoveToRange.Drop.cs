using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

public sealed partial class MoveToRange
{
    bool TickDrop(in MoveState body, in MoveTuning tuning, bool run, float dt,
        GroundMoveContext context, out RangeSteering steering)
    {
        steering = _drop!.Tick(body, tuning, run, dt, context, out bool complete, out bool replan);
        if (complete)
        {
            _follower.CompleteDrop();
            _drop = null;
            ClearStall();
            return false;
        }
        if (replan)
        {
            _drop = null;
            _follower.Reset();
            ClearStall();
            return false;
        }
        if (steering.Status == RangeMoveStatus.Dropping && !body.Grounded) ClearStall();
        else if ((steering.Status is RangeMoveStatus.Following or RangeMoveStatus.Dropping) &&
            RangeApproachCore.TravelBound(body, tuning, run, dt, context) > 0)
            steering = Counted(body, steering);
        if (steering.Status == RangeMoveStatus.Blocked) _stalled = true;
        return true;
    }
}
