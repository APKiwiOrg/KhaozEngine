using System;
using I = KhaozEngine.Render3D.CoverageFloatInterval;
using F = KhaozEngine.Render3D.CoverageFloatIntervals;
using V = KhaozEngine.Render3D.CoverageVertexEnclosure;
using D = KhaozEngine.Render3D.CoverageScalarBounds;

namespace KhaozEngine.Render3D;

internal readonly record struct CoverageEnclosurePredicate(I Exact, I Value, int MinSign, int MaxSign)
{
    internal bool Zero => MinSign <= 0 && MaxSign >= 0;
    internal bool Positive => MaxSign > 0;
    internal bool Negative => MinSign < 0;
}

/// <summary>B30 separates an exact sign range from the stored RN32 arithmetic value.</summary>
internal static class CoverageEnclosurePredicates
{
    internal static CoverageEnclosurePredicate Edge(V a, V b, V p)
    {
        if (!CoverageEnclosureMath.Finite(a) || !CoverageEnclosureMath.Finite(b) || !CoverageEnclosureMath.Finite(p))
            return new(F.Unbounded, F.Unbounded, -1, 1);
        if (Point(a) && Point(b) && Point(p))
        {
            // Exact on point boxes, even when a determinant rounds to zero. No store or flush term touches its sign.
            var exact = CoverageGeometry.Edge(CoverageAffinePoint.FromDouble(a.X.Min, a.Y.Min),
                CoverageAffinePoint.FromDouble(b.X.Min, b.Y.Min), CoverageAffinePoint.FromDouble(p.X.Min, p.Y.Min));
            if (!exact.IsFinite) return new(F.Unbounded, F.Unbounded, -1, 1);
            I range = new(exact.Bounds.Min, exact.Bounds.Max);
            return new(range, Rounded(range), exact.Sign, exact.Sign);
        }
        // An outward bilinear range is sound for boxes. This costs four differences, two interval products and
        // one difference per site, with no device widening inside the exact predicate. No vertex enumeration.
        D dx = CoverageIntervals.Subtract(B(b.X), B(a.X)), dy = CoverageIntervals.Subtract(B(b.Y), B(a.Y));
        D px = CoverageIntervals.Subtract(B(p.X), B(a.X)), py = CoverageIntervals.Subtract(B(p.Y), B(a.Y));
        D determinant = CoverageIntervals.Subtract(CoverageIntervals.Multiply(dx, py), CoverageIntervals.Multiply(dy, px));
        I result = new(determinant.Min, determinant.Max);
        if (result.Contains(0)) return Vertices(a, b, p);
        return new(result, Rounded(result), Math.Sign(result.Min), Math.Sign(result.Max));
    }

    static CoverageEnclosurePredicate Vertices(V a, V b, V p)
    {
        ReadOnlySpan<I> coordinates = stackalloc I[6] { a.X, a.Y, b.X, b.Y, p.X, p.Y };
        Span<int> varying = stackalloc int[6];
        Span<double> values = stackalloc double[6];
        int count = 0;
        for (int i = 0; i < 6; i++)
        {
            values[i] = coordinates[i].Min;
            if (coordinates[i].Min != coordinates[i].Max) varying[count++] = i;
        }
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        int minSign = 1, maxSign = -1;
        // Multilinearity makes the box vertices exact extrema. Resolve cancellation only when the cheap range
        // contains zero. At most 64 exact determinants, fewer when any coordinate is a point input.
        for (int mask = 0; mask < 1 << count; mask++)
        {
            for (int i = 0; i < count; i++)
            {
                int coordinate = varying[i];
                values[coordinate] = (mask & 1 << i) == 0 ? coordinates[coordinate].Min : coordinates[coordinate].Max;
            }
            var exact = CoverageGeometry.Edge(CoverageAffinePoint.FromDouble(values[0], values[1]),
                CoverageAffinePoint.FromDouble(values[2], values[3]), CoverageAffinePoint.FromDouble(values[4], values[5]));
            if (!exact.IsFinite) return new(F.Unbounded, F.Unbounded, -1, 1);
            min = Math.Min(min, exact.Bounds.Min); max = Math.Max(max, exact.Bounds.Max);
            minSign = Math.Min(minSign, exact.Sign); maxSign = Math.Max(maxSign, exact.Sign);
        }
        I range = new(min, max);
        return new(range, Rounded(range), minSign, maxSign);
    }

    internal static I Dot(V a, V b, V c, V d)
    {
        D dx = CoverageIntervals.Subtract(B(b.X), B(a.X)), dy = CoverageIntervals.Subtract(B(b.Y), B(a.Y));
        D x = CoverageIntervals.Subtract(B(d.X), B(c.X)), y = CoverageIntervals.Subtract(B(d.Y), B(c.Y));
        D exact = CoverageIntervals.Add(CoverageIntervals.Multiply(dx, x), CoverageIntervals.Multiply(dy, y));
        return Rounded(new(exact.Min, exact.Max));
    }

    internal static bool SameStrictSide(CoverageEnclosurePredicate a, CoverageEnclosurePredicate b) =>
        a.MinSign > 0 && b.MinSign > 0 || a.MaxSign < 0 && b.MaxSign < 0;

    internal static bool OppositePossible(CoverageEnclosurePredicate a, CoverageEnclosurePredicate b) =>
        a.Positive && b.Negative || a.Negative && b.Positive;

    internal static bool InsidePossible(V a, V b, V c, V p, out bool certain)
    {
        var area = Edge(a, b, c);
        var u = Edge(a, b, p); var v = Edge(b, c, p); var w = Edge(c, a, p);
        bool positive = area.Positive && u.MaxSign >= 0 && v.MaxSign >= 0 && w.MaxSign >= 0;
        bool negative = area.Negative && u.MinSign <= 0 && v.MinSign <= 0 && w.MinSign <= 0;
        certain = area.MinSign > 0 && u.MinSign >= 0 && v.MinSign >= 0 && w.MinSign >= 0
            || area.MaxSign < 0 && u.MaxSign <= 0 && v.MaxSign <= 0 && w.MaxSign <= 0;
        return positive || negative;
    }

    static bool Point(V p) => p.X.Min == p.X.Max && p.Y.Min == p.Y.Max;
    static D B(I x) => new(x.Min, x.Max);
    static I Rounded(I exact) => exact.IsFinite ? F.Store32(new((float)exact.Min, (float)exact.Max)) : F.Unbounded;
}
