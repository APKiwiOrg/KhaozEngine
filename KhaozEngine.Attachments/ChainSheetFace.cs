using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

// Each direction bounds the whole triangle, including its edges when the face collapses.
internal readonly struct ChainSheetFace
{
    private const float FloatRoundingMargin = 1f / (1 << 19);
    private const double DoubleMachineEpsilon = 2.2204460492503131e-16;
    private readonly Vector3 _min, _max;
    private readonly Offset _normal;
    private readonly double _low, _high;
    private readonly float _scale;
    private readonly bool _canSeparate;
    internal readonly ChainContactTriangle Triangle;

    internal ChainSheetFace((Vector3 A, Vector3 B, Vector3 C) points)
    {
        var (a, b, c) = points;
        Triangle = new ChainContactTriangle(a, b, c);
        _min = Vector3.Min(a, Vector3.Min(b, c));
        _max = Vector3.Max(a, Vector3.Max(b, c));
        _scale = MaxAbs(_min, _max);
        double condition = Triangle.ShapeCondition(out _normal);
        // Near collinearity amplifies roundoff in the exact query's barycentric tests.
        // Keep that query whenever its shape-dependent error could exceed the float padding.
        _canSeparate = 128 * DoubleMachineEpsilon * condition * condition <= FloatRoundingMargin;
        // Project all three corners so even a poorly conditioned normal gives an enclosing interval.
        double first = Offset.Dot(_normal, new Offset(a));
        double second = Offset.Dot(_normal, new Offset(b));
        double third = Offset.Dot(_normal, new Offset(c));
        _low = Math.Min(first, Math.Min(second, third));
        _high = Math.Max(first, Math.Max(second, third));
    }

    internal bool Separated(in ColliderCapsule capsule, in Axis axis, double reach)
    {
        if (!_canSeparate)
            return false;

        // Cover the float gap, conversion of the inflated radius and projections at world scale.
        float radius = (float)reach;
        float margin = radius + FloatRoundingMargin * (_scale + MaxAbs(capsule.Start, capsule.End) + radius);
        var limit = new Vector3(margin);
        if (Vector3.GreaterThanAny(Vector3.Min(capsule.Start, capsule.End) - _max, limit) ||
            Vector3.GreaterThanAny(_min - Vector3.Max(capsule.Start, capsule.End), limit))
            return true;

        double first = Offset.Dot(_normal, axis.Start);
        double last = Offset.Dot(_normal, axis.End);
        return Math.Min(first, last) - _high > margin || _low - Math.Max(first, last) > margin;
    }

    private static float MaxAbs(Vector3 a, Vector3 b)
    {
        var v = Vector3.Max(Vector3.Abs(a), Vector3.Abs(b));
        return MathF.Max(v.X, MathF.Max(v.Y, v.Z));
    }
}
