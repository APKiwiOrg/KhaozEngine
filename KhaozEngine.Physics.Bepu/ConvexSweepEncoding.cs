using System;
using System.Numerics;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Outward path-distance, attained point and unit-normal encodings share the existing
/// evidence budget. These routines require geometric existence and prefix proofs from their caller.</summary>
internal static class ConvexSweepEncoding
{
    internal static GeometryInterval Length(Vector3 displacement)
    {
        P d = P.From(displacement);
        return Q.RootBounds(P.Dot(d, d));
    }

    internal static bool TryHit(in ConvexQueryRequest request, Q time, double lower, double upper,
        P point, P normal, ConvexShellFeature queryFeature, ConvexTargetFeature targetFeature, int id,
        out ConvexLeafEvidence evidence)
    {
        evidence = default;
        GeometryInterval length = Length(request.Displacement);
        if (!length.IsResolved) return false;
        GeometryInterval lo = Distance(length, lower), hi = Distance(length, upper);
        float from = InstalledSweepBounds.Down(Math.Max(0, lo.Lower)), to = InstalledSweepBounds.Up(hi.Upper);
        if (!float.IsFinite(from) || !float.IsFinite(to) || to < from) return false;
        GeometryVector worldPoint = (point + P.From(request.Pose.Position) + P.From(request.Displacement) * time).Bounds;
        GeometryInterval norm = Q.RootBounds(P.Dot(normal, normal));
        if (!norm.IsResolved || norm.Lower <= 0) return false;
        var n = normal.Bounds;
        GeometryVector unit = new(n.X.Divide(norm), n.Y.Divide(norm), n.Z.Divide(norm));
        if (!TryEncode(worldPoint, out var p, out double pointError) || !TryEncode(unit, out var toward, out double normalError) || toward == Vector3.Zero) return false;
        GeometryInterval error = GeometryInterval.Exact((double)to - from).Add(GeometryInterval.Exact(pointError))
            .Add(GeometryInterval.Exact(pointError)).Add(GeometryInterval.Exact(normalError));
        float encodedError = InstalledSweepBounds.Up(error.Upper);
        if (!error.IsResolved || encodedError > 0.001f) return false;
        // The operational hit uses the outward upper bound. Its unchanged skin subtraction must
        // land strictly before the proved prefix endpoint, except a zero-distance initial hit.
        float advance = MathF.Max(0, to - 0.001f);
        if (advance > 0 && (Q.From(from) - Q.From(advance)).Sign <= 0) return false;
        evidence = new(true, true, true, from, to, p, p, -toward, queryFeature, targetFeature,
            id, worldPoint, worldPoint, unit, ConvexSweepStatus.Hit, encodedError);
        return true;
    }

    static GeometryInterval Distance(GeometryInterval length, double fraction) => fraction == 0 ? GeometryInterval.Exact(0)
        : length.Lower == length.Upper ? (Q.From(length.Lower) * Q.From(fraction)).Bounds
        : length.Multiply(GeometryInterval.Exact(fraction));

    internal static bool TryClear(in ConvexQueryRequest request, out ConvexLeafEvidence evidence)
    {
        evidence = default;
        var length = Length(request.Displacement);
        if (!length.IsResolved) return false;
        float extent = InstalledSweepBounds.Up(length.Upper);
        if (!float.IsFinite(extent)) return false;
        evidence = new(true, false, false, extent, extent, default, default, default, default, default,
            0, default, default, default, ConvexSweepStatus.Clear);
        return true;
    }

    static bool TryEncode(GeometryVector bounds, out Vector3 point, out double error)
    {
        point = default; error = 0;
        if (!bounds.IsResolved) return false;
        point = new((float)(bounds.X.Lower * 0.5 + bounds.X.Upper * 0.5),
            (float)(bounds.Y.Lower * 0.5 + bounds.Y.Upper * 0.5), (float)(bounds.Z.Lower * 0.5 + bounds.Z.Upper * 0.5));
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) return false;
        Q Residual(GeometryInterval a, float p)
        {
            Q x = Q.From(a.Lower) - Q.From(p), y = Q.From(a.Upper) - Q.From(p);
            x *= x; y *= y;
            return (x - y).Sign >= 0 ? x : y;
        }
        var e = Q.RootBounds(Residual(bounds.X, point.X) + Residual(bounds.Y, point.Y) + Residual(bounds.Z, point.Z));
        if (!e.IsResolved) return false;
        error = e.Upper;
        return true;
    }
}
