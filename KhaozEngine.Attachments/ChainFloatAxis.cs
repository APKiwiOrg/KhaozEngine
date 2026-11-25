using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

// A float rejection bound. Unrepresentable axes and distances stay in the solve.
internal readonly struct ChainFloatAxis
{
    // Cover the float subtraction, projection and squared-distance operations, as
    // well as conversion of a double query endpoint to its float bounding sphere.
    private const float RoundingMargin = 1f / (1 << 17);
    private const float SmallestNormal = 1.17549435e-38f;
    private readonly Vector3 _start;
    private readonly Vector3 _direction;
    private readonly float _inverseSquared;
    private readonly float _radius;
    private readonly float _axisScale;
    private readonly bool _valid;

    internal ChainFloatAxis(Vector3 start, Vector3 end, float radius)
    {
        _start = start;
        _direction = end - start;
        _radius = radius;
        _axisScale = MathF.Max(MaxAbs(start), MaxAbs(end));
        float squared = _direction.LengthSquared();
        _valid = float.IsFinite(squared) && float.IsFinite(_axisScale) && float.IsFinite(radius) &&
            (squared >= SmallestNormal || _direction == Vector3.Zero);
        _inverseSquared = squared > 0 ? 1 / squared : 0;
    }

    internal static ChainFloatAxis Sweep(in ColliderCapsule current, in ColliderCapsule opening)
    {
        var start = 0.5f * current.Start + 0.5f * opening.Start;
        var end = 0.5f * current.End + 0.5f * opening.End;
        // The midpoint axis plus the largest endpoint excursion contains the
        // convex hull of all four endpoints, including a turn or full crossing.
        float excursion = MathF.Max(Vector3.Distance(0.5f * current.Start, 0.5f * opening.Start),
            Vector3.Distance(0.5f * current.End, 0.5f * opening.End));
        return new ChainFloatAxis(start, end, MathF.Max(current.Radius, opening.Radius) + excursion);
    }

    internal bool Reaches(Vector3 center, float reach)
    {
        if (!_valid)
            return true;

        var fromStart = center - _start;
        float along = _inverseSquared == 0 ? 0 : Vector3.Dot(fromStart, _direction) * _inverseSquared;
        if (!float.IsFinite(along))
            return true;

        var residual = fromStart - Math.Clamp(along, 0, 1) * _direction;
        float radius = reach + _radius;
        float limit = radius + RoundingMargin * (_axisScale + MaxAbs(center) + radius);
        float squared = residual.LengthSquared();
        return !float.IsFinite(squared) || !(squared > limit * limit);
    }

    private static float MaxAbs(Vector3 point)
    {
        point = Vector3.Abs(point);
        return MathF.Max(point.X, MathF.Max(point.Y, point.Z));
    }
}
