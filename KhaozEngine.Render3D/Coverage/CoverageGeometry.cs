using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>An approximate double view. Certification uses CoverageAffinePoint and scalar bounds.</summary>
public readonly record struct CoveragePoint3(double X, double Y, double Z);

internal readonly record struct CoverageScalar(int Sign, double Estimate, CoverageScalarBounds Bounds)
{
    internal bool IsFinite => double.IsFinite(Estimate) && Bounds.IsFinite;
    internal static CoverageScalar Invalid => new(0, double.NaN, CoverageIntervals.Invalid);
}

internal static class CoverageGeometry
{
    // Leave 53 bits of gradual-underflow headroom for an error-free product residual.
    const double MinExactProduct = 2.2250738585072014e-308 * 9007199254740992d;

    internal static Vector3 ToVector3(CoveragePoint3 point) => new((float)point.X, (float)point.Y, (float)point.Z);

    internal static int Compare(CoverageCoordinate a, CoverageCoordinate b) => Difference(a, b).Sign;
    internal static bool SameXY(CoverageAffinePoint a, CoverageAffinePoint b) => Compare(a.X, b.X) == 0 && Compare(a.Y, b.Y) == 0;
    internal static bool After(CoverageAffinePoint a, CoverageAffinePoint b)
    {
        int x = Compare(a.X, b.X);
        return x > 0 || x == 0 && Compare(a.Y, b.Y) > 0;
    }

    // The same authoritative edge uses identical endpoint order and arithmetic in either incident face.
    internal static CoverageScalar Edge(CoverageAffinePoint a, CoverageAffinePoint b, CoverageAffinePoint point)
    {
        if (After(a, b))
        {
            CoverageScalar result = Bilinear(a: b, b: a, c: b, d: point, determinant: true);
            return new(-result.Sign, -result.Estimate, CoverageIntervals.Negate(result.Bounds));
        }
        return Bilinear(a, b, a, point, determinant: true);
    }

    internal static CoverageScalar Dot(CoverageAffinePoint a, CoverageAffinePoint b, CoverageAffinePoint c, CoverageAffinePoint d) =>
        Bilinear(a, b, c, d, determinant: false);

    static CoverageScalar Bilinear(CoverageAffinePoint a, CoverageAffinePoint b, CoverageAffinePoint c, CoverageAffinePoint d, bool determinant)
    {
        Span<double> x = stackalloc double[8], y = stackalloc double[8], qx = stackalloc double[8], qy = stackalloc double[8];
        int nx = Difference(b.X, a.X, x), ny = Difference(b.Y, a.Y, y);
        int nqx = Difference(d.X, c.X, qx), nqy = Difference(d.Y, c.Y, qy);
        Span<double> expansion = stackalloc double[256];
        int length = 0;
        if (!Products(x[..nx], determinant ? qy[..nqy] : qx[..nqx], 1, expansion, ref length)
            || !Products(y[..ny], determinant ? qx[..nqx] : qy[..nqy], determinant ? -1 : 1, expansion, ref length))
            return CoverageScalar.Invalid;
        return Scalar(expansion[..length]);
    }

    // Exact cross(b-a,c-a) dot axis. Affine coordinates retain their four terms and differences up to eight.
    internal static CoverageScalar ProjectedArea(CoverageAffinePoint a, CoverageAffinePoint b, CoverageAffinePoint c, Vector3 axis)
    {
        var pa = CoverageBoundedVector.From(a);
        CoverageScalarBounds bound = CoverageBoundedVector.Dot(CoverageBoundedVector.Cross(CoverageBoundedVector.From(b) - pa,
            CoverageBoundedVector.From(c) - pa), CoverageBoundedVector.From(axis));
        if (!bound.IsFinite) return CoverageScalar.Invalid;
        if (bound.Min > 0 || bound.Max < 0) return new(bound.Min > 0 ? 1 : -1, bound.Midpoint, bound);
        if (bound == CoverageIntervals.Zero) return new(0, 0, bound);
        Span<double> differences = stackalloc double[48];
        Span<int> lengths = stackalloc int[6];
        for (int i = 0; i < 3; i++)
        {
            lengths[i] = Difference(Coordinate(b, i), Coordinate(a, i), differences.Slice(i * 8, 8));
            lengths[i + 3] = Difference(Coordinate(c, i), Coordinate(a, i), differences.Slice((i + 3) * 8, 8));
        }
        Span<double> normal = stackalloc double[256], expansion = stackalloc double[1536], direction = stackalloc double[1];
        int length = 0;
        for (int i = 0; i < 3; i++)
        {
            int j = (i + 1) % 3, k = (i + 2) % 3, count = 0;
            direction[0] = i switch { 0 => axis.X, 1 => axis.Y, _ => axis.Z };
            if (direction[0] == 0) continue;
            if (!Products(differences.Slice(j * 8, lengths[j]), differences.Slice((k + 3) * 8, lengths[k + 3]), 1, normal, ref count)
                || !Products(differences.Slice(k * 8, lengths[k]), differences.Slice((j + 3) * 8, lengths[j + 3]), -1, normal, ref count)
                || !Products(normal[..count], direction, 1, expansion, ref length)) return CoverageScalar.Invalid;
        }
        return Scalar(expansion[..length]);

        static CoverageCoordinate Coordinate(CoverageAffinePoint p, int i) => i switch { 0 => p.X, 1 => p.Y, _ => p.Z };
    }

    /// <summary>Compares exact squared distance with the squared float budget. Affine-sum and difference tails remain
    /// in the expansion, so certifiable equality is accepted without widening the budget. Unsupported products refuse.</summary>
    internal static bool SquaredDistanceWithinBudget(CoverageAffinePoint a, CoverageAffinePoint b, float budget)
    {
        if (!float.IsFinite(budget) || budget < 0) return false;
        // Three squared eight-term differences emit at most 384 product terms, plus the exact float budget square.
        Span<double> expansion = stackalloc double[512], difference = stackalloc double[8];
        int length = 0;
        Grow(expansion, ref length, -(double)budget * budget);
        for (int i = 0; i < 3; i++)
        {
            CoverageCoordinate first = i switch { 0 => a.X, 1 => a.Y, _ => a.Z };
            CoverageCoordinate second = i switch { 0 => b.X, 1 => b.Y, _ => b.Z };
            int count = Difference(first, second, difference);
            if (!Products(difference[..count], difference[..count], 1, expansion, ref length)) return false;
        }
        CoverageScalar result = Scalar(expansion[..length]);
        return result.IsFinite && result.Sign <= 0;
    }

    internal static CoverageScalar Difference(CoverageCoordinate a, CoverageCoordinate b)
    {
        Span<double> values = stackalloc double[8];
        int length = Difference(a, b, values);
        return Scalar(values[..length]);
    }
    static int Difference(CoverageCoordinate a, CoverageCoordinate b, Span<double> values)
    {
        int length = 0;
        for (int i = 0; i < 4; i++) { Grow(values, ref length, a[i]); Grow(values, ref length, -b[i]); }
        return length;
    }
    static CoverageScalar Scalar(ReadOnlySpan<double> values)
    {
        double estimate = 0;
        int sign = 0;
        foreach (double value in values)
        {
            if (!double.IsFinite(value)) return CoverageScalar.Invalid;
            estimate += value;
            if (value != 0) sign = Math.Sign(value);
        }
        return new(sign, estimate, CoverageIntervals.FromExpansion(values));
    }
    static bool Products(ReadOnlySpan<double> a, ReadOnlySpan<double> b, int sign, Span<double> expansion, ref int length)
    {
        foreach (double first in a)
            foreach (double second in b)
            {
                if (first == 0 || second == 0) continue;
                double product = first * second;
                if (!double.IsFinite(product) || Math.Abs(product) < MinExactProduct) return false;
                Grow(expansion, ref length, sign * Math.FusedMultiplyAdd(first, second, -product));
                Grow(expansion, ref length, sign * product);
            }
        return true;
    }

    // Error-free expansion addition. Components retain the exact sum, including affine translation tails.
    internal static void Grow(Span<double> expansion, ref int length, double value)
    {
        if (value == 0) return;
        int written = 0;
        for (int i = 0; i < length; i++)
        {
            double component = expansion[i], sum = value + component;
            double virtualComponent = sum - value;
            double error = (value - (sum - virtualComponent)) + (component - virtualComponent);
            if (error != 0) expansion[written++] = error;
            value = sum;
        }
        if (value != 0) expansion[written++] = value;
        length = written;
    }

    internal static (Vector3 Min, Vector3 Max) Bounds(CoverageAffinePoint a, CoverageAffinePoint b, CoverageAffinePoint c, bool projected) =>
        (new(Lower(Min(a.X, Min(b.X, c.X))), Lower(Min(a.Y, Min(b.Y, c.Y))), projected ? 0 : Lower(Min(a.Z, Min(b.Z, c.Z)))),
         new(Upper(Max(a.X, Max(b.X, c.X))), Upper(Max(a.Y, Max(b.Y, c.Y))), projected ? 0 : Upper(Max(a.Z, Max(b.Z, c.Z)))));
    internal static CoverageCoordinate Min(CoverageCoordinate a, CoverageCoordinate b) => Compare(a, b) <= 0 ? a : b;
    internal static CoverageCoordinate Max(CoverageCoordinate a, CoverageCoordinate b) => Compare(a, b) >= 0 ? a : b;
    internal static bool FitsFloat(CoverageCoordinate coordinate) =>
        Compare(coordinate, CoverageCoordinate.FromDouble(-float.MaxValue)) >= 0 && Compare(coordinate, CoverageCoordinate.FromDouble(float.MaxValue)) <= 0;
    static float Lower(CoverageCoordinate value)
    {
        float rounded = (float)value.Value;
        return Compare(CoverageCoordinate.FromDouble(rounded), value) > 0 ? MathF.BitDecrement(rounded) : rounded;
    }
    static float Upper(CoverageCoordinate value)
    {
        float rounded = (float)value.Value;
        return Compare(CoverageCoordinate.FromDouble(rounded), value) < 0 ? MathF.BitIncrement(rounded) : rounded;
    }
}
