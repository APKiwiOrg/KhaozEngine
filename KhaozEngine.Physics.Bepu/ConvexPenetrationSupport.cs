using System;
using System.Numerics;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Exact surface points for the plane and rational surface proposals for its inner hull.</summary>
internal static class ConvexPenetrationSupport
{
    internal static P Exact(in ConvexQueryShape shape, P n)
    {
        Q radial = n.X * n.X + n.Z * n.Z;
        Q lower = Q.From(shape.LowerY), ring = Q.From(shape.RingY), upper = Q.From(shape.UpperY);
        P apex = new(default, lower, default);
        if (radial.Sign == 0) return n.Y.Sign > 0 ? new(default, upper, default) : apex;
        Q r = Q.From(shape.Radius), length = Q.Root(radial.A);
        Q y = n.Y.Sign > 0 ? upper : n.Y.Sign < 0 ? ring : (ring + upper) / Q.From(2);
        P circle = new(n.X * r / length, y, n.Z * r / length);
        int sign = P.Dot(n, circle - apex).Sign;
        return sign < 0 ? apex : sign == 0 ? (circle + apex) * Q.From(0.5) : circle;
    }

    internal static bool TryRational(in ConvexQueryShape shape, P direction, ref ConvexQueryWork work, out P q)
    {
        q = default;
        if (!work.Feature()) return false;
        GeometryVector bounds = direction.Bounds;
        if (!bounds.IsResolved) return false;
        double scale = Math.Max(Math.Max(Abs(bounds.X), Abs(bounds.Y)), Abs(bounds.Z));
        if (!(scale > 0) || !double.IsFinite(scale)) return false;
        double x = Mid(bounds.X) / scale, y = Mid(bounds.Y) / scale, z = Mid(bounds.Z) / scale;
        double radial = Math.Sqrt(x * x + z * z);
        // The chart has no pole near the proposed direction. Its rounded parameter is only
        // a proposal. The rational identity proves the point lies on the original circle.
        if (radial > 0)
        {
            Q t = Q.From((float)(z / (radial + Math.Abs(x))));
            Q den = Q.From(1) + t * t, r = Q.From(shape.Radius);
            q = new(r * Q.From(x < 0 ? -1 : 1) * (Q.From(1) - t * t) / den,
                Q.From(y > 0 ? shape.UpperY : shape.RingY), r * Q.From(2) * t / den);
        }
        else q = new(default, Q.From(y > 0 ? shape.UpperY : shape.LowerY), default);
        P apex = new(default, Q.From(shape.LowerY), default);
        if (P.Dot(direction, apex - q).Sign > 0) q = apex;
        if (!ConvexPlanarFeatures.Member(shape, q)) return false;
        if (shape.Margin > 0)
        {
            if (!work.Feature()) return false;
            double length = Math.Sqrt(x * x + y * y + z * z);
            int pivot = Math.Abs(x) >= Math.Abs(y) && Math.Abs(x) >= Math.Abs(z) ? 0 : Math.Abs(y) >= Math.Abs(z) ? 1 : 2;
            double primary = pivot == 0 ? x : pivot == 1 ? y : z;
            double first = pivot == 0 ? y : x, second = pivot == 2 ? y : z;
            Q u = Q.From((float)(first / (length + Math.Abs(primary))));
            Q v = Q.From((float)(second / (length + Math.Abs(primary))));
            Q sum = u * u + v * v, den = Q.From(1) + sum;
            Q a = Q.From(primary < 0 ? -1 : 1) * (Q.From(1) - sum) / den;
            Q b = Q.From(2) * u / den, c = Q.From(2) * v / den;
            P sphere = pivot == 0 ? new(a, b, c) : pivot == 1 ? new(b, a, c) : new(b, c, a);
            Q margin = Q.From(shape.Margin);
            sphere *= margin;
            if ((P.Dot(sphere, sphere) - margin * margin).Sign != 0) return false;
            q += sphere;
        }
        return q.X.Rational && q.Y.Rational && q.Z.Rational;
    }

    static double Abs(GeometryInterval a) => Math.Max(Math.Abs(a.Lower), Math.Abs(a.Upper));
    static double Mid(GeometryInterval a) => a.Lower * 0.5 + a.Upper * 0.5;
}
