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
        var result = Measure(body, target, float.MaxValue, 0f, includeDistance: true);
        if (!result.Within)
            throw new ArgumentOutOfRangeException(nameof(target), "Distance exceeds the finite float range.");
        return (float)result.Distance;
    }

    /// <summary>Compares retained squared-distance residuals to range plus tolerance, without a hidden epsilon.</summary>
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
        return Measure(body, target, range, tolerance, includeDistance: false).Within;
    }

    static (bool Within, double Distance) Measure(in MovementBody body, in ReachTarget target,
        float range, float tolerance, bool includeDistance)
    {
        body.Validate(nameof(body));
        Span<double> x = stackalloc double[9];
        Span<double> y = stackalloc double[6];
        Span<double> z = stackalloc double[9];
        float otherRadius = 0f;

        switch (target.Kind)
        {
            case ReachTargetKind.Capsule:
                otherRadius = target.Body.Radius;
                x = x[..Horizontal(body.Centre.X, target.Centre.X, x)];
                y = y[..Vertical(body.Centre.Y, target.Centre.Y, body.HalfHeight, body.Radius,
                    target.Body.HalfHeight, otherRadius, y)];
                z = z[..Horizontal(body.Centre.Z, target.Centre.Z, z)];
                break;
            case ReachTargetKind.Box:
                double cos = Math.Cos(target.YawRadians);
                double sin = Math.Sin(target.YawRadians);
                x = x[..Rotated(body.Centre.X, target.Centre.X, body.Centre.Z, target.Centre.Z,
                    cos, -sin, target.HalfExtents.X, x)];
                y = y[..Vertical(body.Centre.Y, target.Centre.Y, body.HalfHeight, body.Radius,
                    target.HalfExtents.Y, 0f, y)];
                z = z[..Rotated(body.Centre.X, target.Centre.X, body.Centre.Z, target.Centre.Z,
                    sin, cos, target.HalfExtents.Z, z)];
                break;
            case ReachTargetKind.Point:
                x = x[..Horizontal(body.Centre.X, target.Centre.X, x)];
                y = y[..Vertical(body.Centre.Y, target.Centre.Y, body.HalfHeight, body.Radius, 0f, 0f, y)];
                z = z[..Horizontal(body.Centre.Z, target.Centre.Z, z)];
                break;
            default:
                throw new ArgumentException("A default reach target is invalid.", nameof(target));
        }

        Span<double> boundary = stackalloc double[4];
        boundary = boundary[..Expand(stackalloc double[] { body.Radius, otherRadius, range, tolerance }, boundary)];
        bool within = !SquaredDifference(x, y, z, boundary).Positive;
        if (!includeDistance || !within) return (within, 0d);

        Span<double> radii = stackalloc double[2];
        radii = radii[..Expand(stackalloc double[] { body.Radius, otherRadius }, radii)];
        var numerator = SquaredDifference(x, y, z, radii);
        if (!numerator.Positive) return (true, 0d);

        // Rationalization retains a tiny positive edge after the squared terms have cancelled.
        double dx = Value(x);
        double dy = Value(y);
        double dz = Value(z);
        double norm = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return (true, numerator.Value / (norm + Value(radii)));
    }

    static int Horizontal(float first, float second, Span<double> expansion)
    {
        if (first < second) (first, second) = (second, first);
        return Expand(stackalloc double[] { first, -(double)second }, expansion);
    }

    static int Vertical(float first, float second, float height, float radius,
        float otherHeight, float otherRadius, Span<double> expansion)
    {
        if (first < second) (first, second) = (second, first);
        int count = Expand(stackalloc double[]
        {
            first, -(double)second, -(double)height, -(double)otherHeight, radius, otherRadius,
        }, expansion);
        return Positive(expansion[..count]) ? count : 0;
    }

    static int Rotated(float firstX, float secondX, float firstZ, float secondZ,
        double xFactor, double zFactor, float extent, Span<double> expansion)
    {
        Span<double> terms = stackalloc double[9];
        Product(terms, 0, xFactor, firstX);
        Product(terms, 2, -xFactor, secondX);
        Product(terms, 4, zFactor, firstZ);
        Product(terms, 6, -zFactor, secondZ);
        int count = Expand(terms[..8], expansion);
        if (!Positive(expansion[..count]))
            for (int i = 0; i < 8; i++) terms[i] = -terms[i];
        terms[8] = -(double)extent;
        count = Expand(terms, expansion);
        return Positive(expansion[..count]) ? count : 0;
    }

    static void Product(Span<double> terms, int index, double factor, float value)
    {
        double product = factor * value;
        terms[index] = product;
        terms[index + 1] = Math.FusedMultiplyAdd(factor, value, -product);
    }

    static (bool Positive, double Value) SquaredDifference(ReadOnlySpan<double> x, ReadOnlySpan<double> y,
        ReadOnlySpan<double> z, ReadOnlySpan<double> boundary)
    {
        // Each triangular product contributes its rounded value and its FMA residual.
        int capacity = 2 * (Triangle(x.Length) + Triangle(y.Length) + Triangle(z.Length) + Triangle(boundary.Length));
        Span<double> expansion = stackalloc double[capacity];
        int count = 0;
        AddSquare(x, 1d, expansion, ref count);
        AddSquare(y, 1d, expansion, ref count);
        AddSquare(z, 1d, expansion, ref count);
        AddSquare(boundary, -1d, expansion, ref count);
        return (Positive(expansion[..count]), Value(expansion[..count]));
    }

    static int Triangle(int count) => count * (count + 1) / 2;

    static void AddSquare(ReadOnlySpan<double> terms, double sign, Span<double> expansion, ref int count)
    {
        for (int i = 0; i < terms.Length; i++)
        {
            for (int j = i; j < terms.Length; j++)
            {
                double product = terms[i] * terms[j];
                double error = Math.FusedMultiplyAdd(terms[i], terms[j], -product);
                double factor = i == j ? sign : 2d * sign;
                Add(expansion, ref count, factor * product);
                Add(expansion, ref count, factor * error);
            }
        }
    }

    static int Expand(ReadOnlySpan<double> terms, Span<double> expansion)
    {
        int count = 0;
        foreach (double term in terms) Add(expansion, ref count, term);
        return count;
    }

    static void Add(Span<double> expansion, ref int count, double value)
    {
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

    static bool Positive(ReadOnlySpan<double> expansion) =>
        !expansion.IsEmpty && expansion[^1] > 0d;

    static double Value(ReadOnlySpan<double> expansion)
    {
        double result = 0d;
        foreach (double part in expansion) result += part;
        return result;
    }
}
