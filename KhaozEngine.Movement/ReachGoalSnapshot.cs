using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>
/// Exact body reach to one target, frozen when a goal region is made: the target, the range and the mover's
/// capsule radius and half-height. <see cref="Contains"/> is the region's pure feet predicate, so a query or a
/// request begun from the region keeps its members after the mover is given another target, range or tuning.
/// </summary>
internal sealed class ReachGoalSnapshot : NavGoalPredicate
{
    private readonly ReachTarget _target;
    private readonly float _range;
    private readonly float _radius;
    private readonly float _halfHeight;

    internal ReachGoalSnapshot(in ReachTarget target, float range, float capsuleRadius, float capsuleHalfHeight)
    {
        _target = target;
        _range = range;
        _radius = capsuleRadius;
        _halfHeight = capsuleHalfHeight;
    }

    internal static Vector3 AnchorFor(in ReachTarget target)
    {
        Vector3 anchor = target.Centre;
        if (target.Kind == ReachTargetKind.Capsule) anchor.Y -= target.Body.HalfHeight;
        return anchor;
    }

    internal NavGoalRegion CreateRegion(Vector3 anchor)
        => new(this, anchor, HorizontalExtent(anchor, _target, _range, _radius));

    private static float HorizontalExtent(Vector3 anchor, in ReachTarget target, float range, float radius)
    {
        double extent = target.Kind switch
        {
            ReachTargetKind.Capsule => target.Body.Radius,
            ReachTargetKind.Box => Math.Sqrt((double)target.HalfExtents.X * target.HalfExtents.X +
                (double)target.HalfExtents.Z * target.HalfExtents.Z),
            _ => 0d,
        };
        extent += radius + (double)range;
        if (!MovementBody.IsFinite(anchor) || extent > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(target), "Goal extent and feet must be finite.");
        float outward = (float)extent;
        return outward < extent ? FloatUlp.Up(outward) : outward;
    }

    /// <summary>Whether a capsule standing on <paramref name="feet"/> is within the frozen range of the frozen
    /// target. The centre is the float sum of the feet and the half-height, as the mover seats its own body.</summary>
    internal override bool Contains(Vector3 feet)
        => ReachGeometry.Within(new MovementBody(feet + new Vector3(0f, _halfHeight, 0f), _radius, _halfHeight),
            _target, _range);

    /// <summary>Whether this snapshot holds the same bits as the given reach, so its predicate answers exactly as
    /// one made from them would.</summary>
    internal bool Matches(in ReachTarget target, float range, float capsuleRadius, float capsuleHalfHeight)
        => target.Kind == _target.Kind && Same(target.Centre, _target.Centre) &&
            Same(target.Body.Centre, _target.Body.Centre) && Same(target.Body.Radius, _target.Body.Radius) &&
            Same(target.Body.HalfHeight, _target.Body.HalfHeight) && Same(target.HalfExtents, _target.HalfExtents) &&
            Same(target.YawRadians, _target.YawRadians) && Same(range, _range) && Same(capsuleRadius, _radius) &&
            Same(capsuleHalfHeight, _halfHeight);

    private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    private static bool Same(Vector3 a, Vector3 b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
}
