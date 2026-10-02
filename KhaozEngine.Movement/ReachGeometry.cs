using System;

namespace KhaozEngine.Movement;

/// <summary>Shortest 3D edge distance from an upright capsule, with caller-owned reach tolerance.</summary>
public static class ReachGeometry
{
    /// <summary>Returns zero for overlap. Refuses a distance greater than <see cref="float.MaxValue"/>.</summary>
    /// <exception cref="ArgumentException">The body or target is a default value.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The distance cannot be represented as a finite float.</exception>
    public static float Distance(in MovementBody body, in ReachTarget target)
    {
        double distance = Measure(body, target);
        if (distance > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(target), "Distance exceeds the finite float range.");
        return (float)distance;
    }

    /// <summary>Compares the wider distance directly to range plus tolerance, without a hidden epsilon.</summary>
    /// <exception cref="ArgumentException">The body or target is a default value.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Range or tolerance is invalid, or their sum exceeds the finite float range.</exception>
    public static bool Within(in MovementBody body, in ReachTarget target, float range, float tolerance = 0f)
    {
        if (!float.IsFinite(range) || range < 0f)
            throw new ArgumentOutOfRangeException(nameof(range), "Range must be finite and nonnegative.");
        if (!float.IsFinite(tolerance) || tolerance < 0f)
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be finite and nonnegative.");
        float larger = Math.Max(range, tolerance);
        float smaller = Math.Min(range, tolerance);
        if (smaller > (double)float.MaxValue - larger)
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Range plus tolerance exceeds the finite float range.");
        return Measure(body, target) <= (double)range + tolerance;
    }

    static double Measure(in MovementBody body, in ReachTarget target)
    {
        body.Validate(nameof(body));
        Gap x;
        Gap y;
        Gap z;

        switch (target.Kind)
        {
            case ReachTargetKind.Capsule:
                x = Horizontal(body.Centre.X, target.Centre.X, body.Radius, target.Body.Radius);
                y = Vertical(body.Centre.Y, target.Centre.Y, body.HalfHeight, body.Radius,
                    target.Body.HalfHeight, target.Body.Radius);
                z = Horizontal(body.Centre.Z, target.Centre.Z, body.Radius, target.Body.Radius);
                break;
            case ReachTargetKind.Box:
                double cos = Math.Cos(target.YawRadians);
                double sin = Math.Sin(target.YawRadians);
                x = Rotated(body.Centre.X, target.Centre.X, body.Centre.Z, target.Centre.Z,
                    cos, -sin, target.HalfExtents.X, body.Radius);
                y = Vertical(body.Centre.Y, target.Centre.Y, body.HalfHeight, body.Radius,
                    target.HalfExtents.Y, 0f);
                z = Rotated(body.Centre.X, target.Centre.X, body.Centre.Z, target.Centre.Z,
                    sin, cos, target.HalfExtents.Z, body.Radius);
                break;
            case ReachTargetKind.Point:
                x = Horizontal(body.Centre.X, target.Centre.X, body.Radius, 0f);
                y = Vertical(body.Centre.Y, target.Centre.Y, body.HalfHeight, body.Radius, 0f, 0f);
                z = Horizontal(body.Centre.Z, target.Centre.Z, body.Radius, 0f);
                break;
            default:
                throw new ArgumentException("A default reach target is invalid.", nameof(target));
        }

        if (x.Length < y.Length) (x, y) = (y, x);
        if (x.Length < z.Length) (x, z) = (z, x);
        if (x.Length == 0d) return 0d;

        // Rationalize norm minus the dominant component, then add its already-cancelled radius gap.
        double otherSquared = y.Length * y.Length + z.Length * z.Length;
        double norm = Math.Sqrt(x.Length * x.Length + otherSquared);
        return Math.Max(0d, x.Edge + otherSquared / (norm + x.Length));
    }

    // Edge retains the component minus both radii before a large norm can round that gap away.
    readonly record struct Gap(double Length, double Edge);

    static Gap Horizontal(float first, float second, float radius, float otherRadius)
    {
        if (first < second) (first, second) = (second, first);
        ReadOnlySpan<double> terms = stackalloc double[] { first, -(double)second, -(double)radius, -(double)otherRadius };
        return new Gap(Sum(terms[..2]), Sum(terms));
    }

    static Gap Vertical(float first, float second, float height, float radius, float otherHeight, float otherRadius)
    {
        if (first < second) (first, second) = (second, first);
        ReadOnlySpan<double> terms = stackalloc double[]
        {
            first, -(double)second, -(double)height, -(double)otherHeight, radius, otherRadius,
        };
        return new Gap(Math.Max(0d, Sum(terms)), Sum(terms[..4]));
    }

    static Gap Rotated(float firstX, float secondX, float firstZ, float secondZ,
        double xFactor, double zFactor, float extent, float radius)
    {
        Span<double> terms = stackalloc double[10];
        Product(terms, 0, xFactor, firstX);
        Product(terms, 2, -xFactor, secondX);
        Product(terms, 4, zFactor, firstZ);
        Product(terms, 6, -zFactor, secondZ);
        if (Sum(terms[..8]) < 0d)
            for (int i = 0; i < 8; i++) terms[i] = -terms[i];
        terms[8] = -(double)extent;
        terms[9] = -(double)radius;
        return new Gap(Math.Max(0d, Sum(terms[..9])), Sum(terms));
    }

    static void Product(Span<double> terms, int index, double factor, float value)
    {
        double product = factor * value;
        terms[index] = product;
        terms[index + 1] = Math.FusedMultiplyAdd(factor, value, -product);
    }

    static double Sum(ReadOnlySpan<double> terms)
    {
        // Keep every addition residual until all common position, height and radius terms cancel.
        Span<double> expansion = stackalloc double[terms.Length];
        int count = 0;
        foreach (double term in terms)
        {
            double value = term;
            int nextCount = 0;
            for (int i = 0; i < count; i++)
            {
                double part = expansion[i];
                double sum = value + part;
                double partInSum = sum - value;
                double error = (value - (sum - partInSum)) + (part - partInSum);
                if (error != 0d) expansion[nextCount++] = error;
                value = sum;
            }
            if (value != 0d || nextCount == 0) expansion[nextCount++] = value;
            count = nextCount;
        }
        double result = 0d;
        for (int i = 0; i < count; i++) result += expansion[i];
        return result;
    }
}
