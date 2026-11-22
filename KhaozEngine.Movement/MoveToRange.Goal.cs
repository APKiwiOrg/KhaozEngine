using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

public sealed partial class MoveToRange
{
    private NavGoalRegion? _goal;
    private ReachGoalSnapshot? _reach;
    private RangeShapeKey _goalShape;

    // A region owns the reach it was made for, so a query or request that outlives this call keeps its members.
    // Any change to the reach's bits makes a new region, so every query still tests the current reach. A target
    // translation normally moves the anchor too. Rounding can hide a small vertical move of a capsule target
    // in its anchor. The new region then has the same anchor bits. When only the sign of an anchor zero changes,
    // reusing the equal anchor keeps the previous region's bits.
    private NavGoalRegion Goal<TPlanning>(in MoveTuning tuning, in ReachTarget target, float range)
        where TPlanning : struct
    {
        var key = RangeShapeKey.From(tuning, target, range);
        if (_goal is not null && key != _goalShape)
        {
            _follower.Reset<TPlanning>();
            ClearRouteGoal();
        }
        _goalShape = key;
        if (_goal is not null && _reach is not null &&
            _reach.Matches(target, range, tuning.CapsuleRadius, tuning.CapsuleHalfHeight))
            return _goal;
        Vector3 anchor = ReachGoalSnapshot.AnchorFor(target);
        if (_goal is not null && _goal.Anchor == anchor) anchor = _goal.Anchor;
        var reach = new ReachGoalSnapshot(target, range, tuning.CapsuleRadius, tuning.CapsuleHalfHeight);
        _goal = reach.CreateRegion(anchor);
        _reach = reach;
        return _goal;
    }
}
