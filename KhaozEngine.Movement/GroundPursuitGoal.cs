using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Exact body reach presented as a conservative, finite feet region for planning.</summary>
internal sealed class GroundPursuitGoal
{
    private readonly ReachTarget _target;
    private readonly float _range, _radius, _halfHeight;

    internal GroundPursuitGoal(in MoveTuning tuning, in ReachTarget target, float range)
    {
        _target = target;
        _range = range;
        _radius = tuning.CapsuleRadius;
        _halfHeight = tuning.CapsuleHalfHeight;
        Vector3 anchor = target.Centre;
        if (target.Kind == ReachTargetKind.Capsule) anchor.Y -= target.Body.HalfHeight;
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
        Region = new NavGoalRegion(anchor, outward, Contains);
    }

    internal NavGoalRegion Region { get; }

    private bool Contains(Vector3 feet)
        => ReachGeometry.Within(new MovementBody(feet + new Vector3(0, _halfHeight, 0),
            _radius, _halfHeight), _target, _range);
}
