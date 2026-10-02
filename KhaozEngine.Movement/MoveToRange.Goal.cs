using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

public sealed partial class MoveToRange
{
    private readonly Func<Vector3, bool> _contains;
    private NavGoalRegion? _goal;
    private GoalShape _goalShape;
    private ReachTarget _target;
    private float _range;
    private float _radius;
    private float _halfHeight;

    private NavGoalRegion Goal(in MoveTuning tuning, in ReachTarget target, float range)
    {
        var key = new GoalShape(target.Kind, target.Body.Radius, target.Body.HalfHeight,
            target.HalfExtents, target.YawRadians, range, tuning.CapsuleRadius,
            tuning.CapsuleHalfHeight, tuning.MaxSlopeRadians, tuning.StepHeight);
        if (_goal is not null && key != _goalShape) Reset();
        _goalShape = key;
        _target = target;
        _range = range;
        _radius = tuning.CapsuleRadius;
        _halfHeight = tuning.CapsuleHalfHeight;
        Vector3 anchor = target.Centre;
        if (target.Kind == ReachTargetKind.Capsule) anchor.Y -= target.Body.HalfHeight;
        if (_goal is null || _goal.Anchor != anchor)
        {
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
            if (outward < extent) outward = MathF.BitIncrement(outward);
            _goal = new NavGoalRegion(anchor, outward, _contains);
        }
        return _goal;
    }

    private bool Contains(Vector3 feet)
        => ReachGeometry.Within(new MovementBody(feet + new Vector3(0f, _halfHeight, 0f),
            _radius, _halfHeight), _target, _range);

    private readonly record struct GoalShape(ReachTargetKind Kind, float TargetRadius, float TargetHalfHeight,
        Vector3 HalfExtents, float Yaw, float Range, float Radius, float HalfHeight, float Slope, float Step);
}
