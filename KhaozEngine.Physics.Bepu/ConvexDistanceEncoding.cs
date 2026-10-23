using System;
using System.Numerics;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Encode the complete bracket and attained witness expressions. Encoding error never
/// enlarges the shape. Every component enclosure and the whole bracket share the existing budget.</summary>
internal static class ConvexDistanceEncoding
{
    internal static bool TryCreate(in ConvexQueryRequest request, P q, P t, ConvexTargetFeature targetFeature,
        int localFeatureId, bool overlap, out ConvexLeafEvidence evidence)
    {
        evidence = default;
        P delta = t - q;
        Q squared = P.Dot(delta, delta);
        GeometryInterval length = Q.RootBounds(squared);
        if (!length.IsResolved) return false;
        bool grownOverlap = overlap || (squared - Q.From(request.Shape.Margin) * Q.From(request.Shape.Margin)).Sign <= 0;
        GeometryInterval distance = grownOverlap ? GeometryInterval.Exact(0) : length.Subtract(GeometryInterval.Exact(request.Shape.Margin));
        GeometryVector query = q.Bounds, target = t.Bounds;
        GeometryVector normal = new(GeometryInterval.Exact(0), GeometryInterval.Exact(0), GeometryInterval.Exact(0));
        if (!grownOverlap)
        {
            GeometryVector d = delta.Bounds;
            normal = new(d.X.Divide(length), d.Y.Divide(length), d.Z.Divide(length));
            if (request.Shape.Margin != 0)
            {
                GeometryInterval m = GeometryInterval.Exact(request.Shape.Margin);
                query = new(query.X.Add(normal.X.Multiply(m)), query.Y.Add(normal.Y.Multiply(m)), query.Z.Add(normal.Z.Multiply(m)));
            }
        }
        else query = target;
        GeometryVector feet = P.From(request.Pose.Position).Bounds;
        query = Translate(query, feet); target = Translate(target, feet);
        if (!distance.IsResolved || !TryEncode(query, out var queryWitness, out double queryError) || !TryEncode(target, out var targetWitness, out double targetError) ||
            !TryEncode(normal, out var towardTarget, out _)) return false;
        float lower = Down(Math.Max(0, distance.Lower)), upper = Up(Math.Max(0, distance.Upper));
        if (!float.IsFinite(lower) || !float.IsFinite(upper) || GeometryInterval.Exact((double)upper - lower).Add(GeometryInterval.Exact(queryError)).Add(GeometryInterval.Exact(targetError)).Upper > 0.001f ||
            (!grownOverlap && towardTarget == Vector3.Zero)) return false;
        evidence = new(true, true, true, lower, upper, queryWitness, targetWitness, -towardTarget,
            overlap ? ConvexShellFeature.Interior : ConvexPlanarFeatures.Classify(request.Shape, q), targetFeature, localFeatureId,
            query, target, normal);
        return true;
    }

    static GeometryVector Translate(GeometryVector a, GeometryVector b) => new(a.X.Add(b.X), a.Y.Add(b.Y), a.Z.Add(b.Z));

    static bool TryEncode(GeometryVector point, out Vector3 result, out double error)
    {
        result = default;
        error = 0;
        if (!point.IsResolved) return false;
        result = new((float)(point.X.Lower * 0.5 + point.X.Upper * 0.5),
            (float)(point.Y.Lower * 0.5 + point.Y.Upper * 0.5), (float)(point.Z.Lower * 0.5 + point.Z.Upper * 0.5));
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z)) return false;
        // Compare rational endpoint residuals before any rounding. Exact zero must stay zero,
        // including the zero normal on an attained overlap. Interval sqrt of a widened zero
        // would have a negative lower endpoint and incorrectly refuse that certificate.
        Q Error(GeometryInterval interval, float value)
        {
            Q a = Q.From(interval.Lower) - Q.From(value), b = Q.From(interval.Upper) - Q.From(value);
            a *= a; b *= b;
            return (a - b).Sign >= 0 ? a : b;
        }
        Q squared = Error(point.X, result.X) + Error(point.Y, result.Y) + Error(point.Z, result.Z);
        GeometryInterval bound = Q.RootBounds(squared);
        if (!bound.IsResolved) return false;
        error = bound.Upper;
        return error <= 0.001f;
    }
    static float Down(double x) { float r = (float)x; return r > x ? MathF.BitDecrement(r) : r; }
    static float Up(double x) { float r = (float)x; return r < x ? MathF.BitIncrement(r) : r; }
}
