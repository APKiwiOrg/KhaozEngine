using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Double-precision 3-vector for certification arithmetic over float inputs.</summary>
internal readonly record struct CoverageVector(double X, double Y, double Z)
{
    internal static CoverageVector From(Vector3 v) => new(v.X, v.Y, v.Z);
    internal double Length => Math.Sqrt(Dot(this, this));
    internal double MaxAbs => Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
    internal static double Dot(in CoverageVector a, in CoverageVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    internal static CoverageVector Cross(in CoverageVector a, in CoverageVector b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static CoverageVector operator +(CoverageVector a, CoverageVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static CoverageVector operator -(CoverageVector a, CoverageVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static CoverageVector operator *(CoverageVector a, double s) => new(a.X * s, a.Y * s, a.Z * s);
}

/// <summary>Conservative triangle predicates used by every-pose certification. Lower bounds subtract an explicit
/// rounding allowance, so a positive result is a certificate and an ambiguous configuration reports no separation.</summary>
internal static class CoverageTriangleGeometry
{
    // Generous multiple of double unit roundoff for a handful of dependent products and sums over the input magnitude.
    const double RoundingAllowance = 64 * (1.0 / (1L << 53));

    /// <summary>A certified lower bound of the Euclidean distance between two closed triangles. Any unit axis gives a
    /// valid bound as the gap between the projected intervals. The approximate closest-point direction is the optimal
    /// axis for disjoint convex sets, and both face normals are tried too. Intersecting or touching triangles return a
    /// value of at most zero on every axis.</summary>
    internal static double SeparationLowerBound(in CoverageVector a0, in CoverageVector a1, in CoverageVector a2,
        in CoverageVector b0, in CoverageVector b1, in CoverageVector b2)
    {
        double scale = Math.Max(Math.Max(Math.Max(a0.MaxAbs, a1.MaxAbs), Math.Max(a2.MaxAbs, b0.MaxAbs)), Math.Max(b1.MaxAbs, b2.MaxAbs));
        ClosestPair(a0, a1, a2, b0, b1, b2, out CoverageVector p, out CoverageVector q);
        double best = Gap(q - p, a0, a1, a2, b0, b1, b2);
        best = Math.Max(best, Gap(CoverageVector.Cross(a1 - a0, a2 - a0), a0, a1, a2, b0, b1, b2));
        best = Math.Max(best, Gap(CoverageVector.Cross(b1 - b0, b2 - b0), a0, a1, a2, b0, b1, b2));
        return best - RoundingAllowance * 8 * (scale + double.Epsilon);
    }

    static double Gap(CoverageVector axis, in CoverageVector a0, in CoverageVector a1, in CoverageVector a2,
        in CoverageVector b0, in CoverageVector b1, in CoverageVector b2)
    {
        double length = axis.Length;
        if (!(length > 0) || !double.IsFinite(length)) return double.NegativeInfinity;
        CoverageVector n = axis * (1 / length);
        double a = CoverageVector.Dot(n, a0), b = CoverageVector.Dot(n, a1), c = CoverageVector.Dot(n, a2);
        double d = CoverageVector.Dot(n, b0), e = CoverageVector.Dot(n, b1), f = CoverageVector.Dot(n, b2);
        double aMin = Math.Min(a, Math.Min(b, c)), aMax = Math.Max(a, Math.Max(b, c));
        double bMin = Math.Min(d, Math.Min(e, f)), bMax = Math.Max(d, Math.Max(e, f));
        return Math.Max(bMin - aMax, aMin - bMax);
    }

    static void ClosestPair(in CoverageVector a0, in CoverageVector a1, in CoverageVector a2,
        in CoverageVector b0, in CoverageVector b1, in CoverageVector b2, out CoverageVector p, out CoverageVector q)
    {
        double best = double.PositiveInfinity;
        p = a0; q = b0;
        Point(a0, b0, b1, b2, false, ref best, ref p, ref q); Point(a1, b0, b1, b2, false, ref best, ref p, ref q);
        Point(a2, b0, b1, b2, false, ref best, ref p, ref q); Point(b0, a0, a1, a2, true, ref best, ref p, ref q);
        Point(b1, a0, a1, a2, true, ref best, ref p, ref q); Point(b2, a0, a1, a2, true, ref best, ref p, ref q);
        Edges(a0, a1, b0, b1, b2, ref best, ref p, ref q); Edges(a1, a2, b0, b1, b2, ref best, ref p, ref q);
        Edges(a2, a0, b0, b1, b2, ref best, ref p, ref q);
    }

    static void Point(in CoverageVector point, in CoverageVector t0, in CoverageVector t1, in CoverageVector t2, bool swap,
        ref double best, ref CoverageVector p, ref CoverageVector q)
    {
        CoverageVector closest = ClosestOnTriangle(point, t0, t1, t2);
        CoverageVector delta = closest - point;
        double distance = CoverageVector.Dot(delta, delta);
        if (!(distance < best)) return;
        best = distance;
        if (swap) { p = closest; q = point; }
        else { p = point; q = closest; }
    }

    static void Edges(in CoverageVector a, in CoverageVector b, in CoverageVector b0, in CoverageVector b1, in CoverageVector b2,
        ref double best, ref CoverageVector p, ref CoverageVector q)
    {
        Segment(a, b, b0, b1, ref best, ref p, ref q); Segment(a, b, b1, b2, ref best, ref p, ref q);
        Segment(a, b, b2, b0, ref best, ref p, ref q);
    }

    // Ericson, Real-Time Collision Detection 5.1.9, with degenerate segments handled by clamping.
    static void Segment(in CoverageVector p1, in CoverageVector q1, in CoverageVector p2, in CoverageVector q2,
        ref double best, ref CoverageVector p, ref CoverageVector q)
    {
        CoverageVector d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        double a = CoverageVector.Dot(d1, d1), e = CoverageVector.Dot(d2, d2), f = CoverageVector.Dot(d2, r);
        double s, t;
        if (a <= 0 && e <= 0) { s = 0; t = 0; }
        else if (a <= 0) { s = 0; t = Math.Clamp(f / e, 0, 1); }
        else
        {
            double c = CoverageVector.Dot(d1, r);
            if (e <= 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
            else
            {
                double b = CoverageVector.Dot(d1, d2), denominator = a * e - b * b;
                s = denominator > 0 ? Math.Clamp((b * f - c * e) / denominator, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        CoverageVector c1 = p1 + d1 * s, c2 = p2 + d2 * t, delta = c2 - c1;
        double distance = CoverageVector.Dot(delta, delta);
        if (!(distance < best)) return;
        best = distance; p = c1; q = c2;
    }

    // Ericson 5.1.5 Voronoi-region closest point on a triangle.
    static CoverageVector ClosestOnTriangle(in CoverageVector p, in CoverageVector a, in CoverageVector b, in CoverageVector c)
    {
        CoverageVector ab = b - a, ac = c - a, ap = p - a;
        double d1 = CoverageVector.Dot(ab, ap), d2 = CoverageVector.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        CoverageVector bp = p - b;
        double d3 = CoverageVector.Dot(ab, bp), d4 = CoverageVector.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 - d3 == 0 ? 0 : d1 / (d1 - d3));
        CoverageVector cp = p - c;
        double d5 = CoverageVector.Dot(ab, cp), d6 = CoverageVector.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 - d6 == 0 ? 0 : d2 / (d2 - d6));
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            double w = (d4 - d3) + (d5 - d6);
            return b + (c - b) * (w == 0 ? 0 : (d4 - d3) / w);
        }
        double sum = va + vb + vc;
        if (!(sum > 0) || !double.IsFinite(sum)) return a;
        return a + ab * (vb / sum) + ac * (vc / sum);
    }

    /// <summary>Twice the signed XY area.</summary>
    internal static double Orient(double ax, double ay, double bx, double by, double cx, double cy) =>
        (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

    /// <summary>True when the open XY interiors of two triangles with the given common orientation sign overlap beyond a
    /// relative tolerance. Exactly collinear and shared corners count as touching, not overlapping.</summary>
    internal static bool ProjectedInteriorsOverlap(in CoverageVector a0, in CoverageVector a1, in CoverageVector a2,
        in CoverageVector b0, in CoverageVector b1, in CoverageVector b2, int sign)
    {
        double minX = Math.Min(Math.Min(Math.Min(a0.X, a1.X), Math.Min(a2.X, b0.X)), Math.Min(b1.X, b2.X));
        double maxX = Math.Max(Math.Max(Math.Max(a0.X, a1.X), Math.Max(a2.X, b0.X)), Math.Max(b1.X, b2.X));
        double minY = Math.Min(Math.Min(Math.Min(a0.Y, a1.Y), Math.Min(a2.Y, b0.Y)), Math.Min(b1.Y, b2.Y));
        double maxY = Math.Max(Math.Max(Math.Max(a0.Y, a1.Y), Math.Max(a2.Y, b0.Y)), Math.Max(b1.Y, b2.Y));
        double extent = Math.Max(maxX - minX, maxY - minY);
        double tolerance = 1e-10 * extent * extent;
        return !(Separates(a0, a1, b0, b1, b2) || Separates(a1, a2, b0, b1, b2) || Separates(a2, a0, b0, b1, b2)
            || Separates(b0, b1, a0, a1, a2) || Separates(b1, b2, a0, a1, a2) || Separates(b2, b0, a0, a1, a2));

        bool Separates(in CoverageVector u, in CoverageVector v, in CoverageVector p, in CoverageVector q, in CoverageVector r) =>
            sign * Orient(u.X, u.Y, v.X, v.Y, p.X, p.Y) <= tolerance && sign * Orient(u.X, u.Y, v.X, v.Y, q.X, q.Y) <= tolerance
            && sign * Orient(u.X, u.Y, v.X, v.Y, r.X, r.Y) <= tolerance;
    }

    /// <summary>Signed solid angle of a triangle seen from the origin, Van Oosterom and Strackee. Divide a closed
    /// surface's sum by 4 pi for its generalized winding number. Returns NaN when the origin is on a corner.</summary>
    internal static double SolidAngle(in CoverageVector a, in CoverageVector b, in CoverageVector c)
    {
        double la = a.Length, lb = b.Length, lc = c.Length;
        if (!(la > 0) || !(lb > 0) || !(lc > 0)) return double.NaN;
        double numerator = CoverageVector.Dot(a, CoverageVector.Cross(b, c));
        double denominator = la * lb * lc + CoverageVector.Dot(a, b) * lc + CoverageVector.Dot(a, c) * lb + CoverageVector.Dot(b, c) * la;
        return 2 * Math.Atan2(numerator, denominator);
    }

    /// <summary>Clips a convex XY polygon, stored as x,y,z triples where z is carried linearly, against one triangle.
    /// Returns the new vertex count. Scratch must hold at least 9 vertices.</summary>
    internal static int ClipToTriangle(Span<CoverageVector> polygon, int count, Span<CoverageVector> scratch,
        in CoverageVector t0, in CoverageVector t1, in CoverageVector t2)
    {
        int sign = Math.Sign(Orient(t0.X, t0.Y, t1.X, t1.Y, t2.X, t2.Y));
        if (sign == 0) return 0;
        count = ClipEdge(polygon, count, scratch, t0, t1, sign); scratch[..count].CopyTo(polygon);
        count = ClipEdge(polygon, count, scratch, t1, t2, sign); scratch[..count].CopyTo(polygon);
        count = ClipEdge(polygon, count, scratch, t2, t0, sign); scratch[..count].CopyTo(polygon);
        return count;
    }

    static int ClipEdge(ReadOnlySpan<CoverageVector> polygon, int count, Span<CoverageVector> output, in CoverageVector u, in CoverageVector v, int sign)
    {
        int written = 0;
        for (int i = 0; i < count; i++)
        {
            CoverageVector p = polygon[i], q = polygon[(i + 1) % count];
            double dp = sign * Orient(u.X, u.Y, v.X, v.Y, p.X, p.Y), dq = sign * Orient(u.X, u.Y, v.X, v.Y, q.X, q.Y);
            if (dp >= 0) output[written++] = p;
            if ((dp < 0) != (dq < 0)) output[written++] = p + (q - p) * (dp / (dp - dq));
        }
        return written;
    }

    /// <summary>Height of the plane through a triangle at an XY point, by barycentric interpolation.</summary>
    internal static double PlaneHeight(in CoverageVector a, in CoverageVector b, in CoverageVector c, double x, double y)
    {
        double area = Orient(a.X, a.Y, b.X, b.Y, c.X, c.Y);
        return (a.Z * Orient(b.X, b.Y, c.X, c.Y, x, y) + b.Z * Orient(c.X, c.Y, a.X, a.Y, x, y) + c.Z * Orient(a.X, a.Y, b.X, b.Y, x, y)) / area;
    }
}
