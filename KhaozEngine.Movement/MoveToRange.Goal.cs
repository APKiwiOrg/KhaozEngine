using System;
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
    // translation normally moves the anchor too. Rounding can hide a small vertical move of a capsule target in
    // its anchor, and a signed zero compares equal. The replacement then keeps the old anchor bits, as the region
    // it replaces would have.
    private NavGoalRegion Goal(in MoveTuning tuning, in ReachTarget target, float range)
    {
        var key = RangeShapeKey.From(tuning, target, range);
        if (_goal is not null && key != _goalShape) Reset();
        _goalShape = key;
        if (_goal is not null && _reach is not null &&
            _reach.Matches(target, range, tuning.CapsuleRadius, tuning.CapsuleHalfHeight))
            return _goal;
        Vector3 anchor = target.Centre;
        if (target.Kind == ReachTargetKind.Capsule) anchor.Y -= target.Body.HalfHeight;
        if (_goal is not null && _goal.Anchor == anchor) anchor = _goal.Anchor;
        double extent = target.Kind switch
        {
            ReachTargetKind.Capsule => target.Body.Radius,
            ReachTargetKind.Box => Math.Sqrt((double)target.HalfExtents.X * target.HalfExtents.X +
                (double)target.HalfExtents.Z * target.HalfExtents.Z),
            _ => 0d,
        };
        extent += tuning.CapsuleRadius + (double)range;
        if (!MovementBody.IsFinite(anchor) || extent > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(target), "Goal extent and feet must be finite.");
        float outward = (float)extent;
        if (outward < extent) outward = FloatUlp.Up(outward);
        _reach = new ReachGoalSnapshot(target, range, tuning.CapsuleRadius, tuning.CapsuleHalfHeight);
        _goal = new NavGoalRegion(anchor, outward, _reach.Contains);
        return _goal;
    }
}
