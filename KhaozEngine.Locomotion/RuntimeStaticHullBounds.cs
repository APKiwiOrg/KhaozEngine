using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Bounds the hull recentring operand without depending on backend hull topology or allocation.
/// Cancellation in a translated or thin hull must be proved bounded before installing it.</summary>
internal static class RuntimeStaticHullBounds
{
    internal static float CentreMagnitude(Vector3[] points)
    {
        double magnitude = 0;
        foreach (Vector3 p in points)
            magnitude = Math.Max(magnitude, Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))));
        Vector3 a = points[0], b = a, c = a;
        double distance = 0, area = 0, determinant = 0;
        foreach (Vector3 p in points)
        {
            double x = (double)p.X - a.X, y = (double)p.Y - a.Y, z = (double)p.Z - a.Z;
            double squared = x * x + y * y + z * z;
            if (squared > distance) { distance = squared; b = p; }
        }
        foreach (Vector3 p in points)
        {
            var cross = Cross(a, b, p);
            double squared = cross.X * cross.X + cross.Y * cross.Y + cross.Z * cross.Z;
            if (squared > area) { area = squared; c = p; }
        }
        var normal = Cross(a, b, c);
        foreach (Vector3 p in points)
            determinant = Math.Max(determinant, Math.Abs(normal.X * ((double)p.X - a.X) +
                normal.Y * ((double)p.Y - a.Y) + normal.Z * ((double)p.Z - a.Z)));
        // The contained tetrahedron supplies a lower bound on the hull's volume.
        // 32 binary64 roundings enclose the edge subtraction, cross and dot evaluations above.
        double determinantError = Product(Gamma(32, Math.ScaleB(1d, -53)), 48, magnitude, magnitude, magnitude);
        double lower = Math.BitDecrement(Math.BitDecrement(determinant - determinantError) / 6);
        RuntimeStaticGeometry.Require(lower > 0);
        // A closed convex hull triangulates into at most 2V-4 triangles. Bound the absolute
        // tetrahedral sums, including 64 per-triangle operations and the running accumulators.
        // This covers the backend's signed volume and weighted-centre formula, not a guessed padding.
        double triangles = 2d * points.Length - 4;
        double volumeTerms = Product(triangles, 6, magnitude, magnitude, magnitude);
        double numeratorTerms = Product(triangles, 18, magnitude, magnitude, magnitude, magnitude);
        if (volumeTerms > float.MaxValue || numeratorTerms > float.MaxValue)
            throw new NotSupportedException("The hull's backend volume or centre arithmetic may overflow.");
        double gamma = Gamma(64 + triangles, Math.ScaleB(1d, -24));
        double underflow = Product(64 + triangles, triangles, Math.ScaleB(1d, -149));
        double volumeError = Sum(Product(gamma, volumeTerms), underflow);
        double numeratorError = Sum(Product(gamma, numeratorTerms), Product(underflow, Sum(1, Product(3, magnitude))));
        if (lower <= volumeError)
            throw new NotSupportedException("The hull's installed recentring cannot be conservatively bounded.");
        double error = Math.BitIncrement(Sum(Product(magnitude, volumeError), numeratorError * 0.25) /
            Math.BitDecrement(lower - volumeError));
        double bound = Sum(Math.BitIncrement(Sum(magnitude, error) / (1 - Math.ScaleB(1d, -24))), Math.ScaleB(1d, -149));
        float result = (float)bound;
        if (result < bound) result = MathF.BitIncrement(result);
        if (!float.IsFinite(result)) throw new NotSupportedException("The hull recentring bound is not finite.");
        return result;
    }

    static double Gamma(double operations, double unitRoundoff) =>
        Math.BitIncrement(operations * unitRoundoff / (1 - operations * unitRoundoff));

    static double Product(params double[] values)
    {
        double product = 1;
        foreach (double value in values) product = Math.BitIncrement(product * value);
        return product;
    }

    static double Sum(double a, double b) => Math.BitIncrement(a + b);

    static (double X, double Y, double Z) Cross(Vector3 origin, Vector3 a, Vector3 b)
    {
        double ax = (double)a.X - origin.X, ay = (double)a.Y - origin.Y, az = (double)a.Z - origin.Z;
        double bx = (double)b.X - origin.X, by = (double)b.Y - origin.Y, bz = (double)b.Z - origin.Z;
        return (ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx);
    }
}
