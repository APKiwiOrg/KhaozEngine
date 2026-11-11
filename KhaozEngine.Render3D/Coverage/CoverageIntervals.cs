using System;

namespace KhaozEngine.Render3D;

/// <summary>Finite outward bounds. A consumer must include their width in its safety margin or refuse.</summary>
public readonly record struct CoverageScalarBounds(double Min, double Max)
{
    public bool IsFinite => double.IsFinite(Min) && double.IsFinite(Max) && Min <= Max;
    public double Midpoint => Min + (Max - Min) * .5;
}

/// <summary>The nearest point is approximate. Distance and nearest boundary height retain certified outward bounds.</summary>
public readonly record struct CoverageBoundaryBounds(CoveragePoint3 ApproximateNearest,
    CoverageScalarBounds Distance, CoverageScalarBounds Height);

internal static class CoverageIntervals
{
    internal static CoverageScalarBounds Invalid => new(double.NaN, double.NaN);
    internal static CoverageScalarBounds Zero => new(0, 0);
    internal static CoverageScalarBounds One => new(1, 1);
    internal static CoverageScalarBounds FromCoordinate(CoverageCoordinate coordinate)
    {
        Span<double> values = stackalloc double[4];
        for (int i = 0; i < 4; i++) values[i] = coordinate[i];
        return FromExpansion(values);
    }
    internal static CoverageScalarBounds FromExpansion(ReadOnlySpan<double> values)
    {
        CoverageScalarBounds bounds = Zero;
        foreach (double value in values)
            if (value != 0) bounds = Add(bounds, new(value, value));
        return bounds;
    }
    internal static CoverageScalarBounds Add(CoverageScalarBounds a, CoverageScalarBounds b)
    {
        if (a == Zero) return b;
        if (b == Zero) return a;
        return new(Math.BitDecrement(a.Min + b.Min), Math.BitIncrement(a.Max + b.Max));
    }
    internal static CoverageScalarBounds Negate(CoverageScalarBounds value) => new(-value.Max, -value.Min);
    internal static CoverageScalarBounds Subtract(CoverageScalarBounds a, CoverageScalarBounds b) => Add(a, Negate(b));
    internal static CoverageScalarBounds Multiply(CoverageScalarBounds a, CoverageScalarBounds b)
    {
        if (a == Zero || b == Zero) return Zero;
        if (a == One) return b;
        if (b == One) return a;
        double p0 = a.Min * b.Min, p1 = a.Min * b.Max, p2 = a.Max * b.Min, p3 = a.Max * b.Max;
        return new(Math.BitDecrement(Math.Min(Math.Min(p0, p1), Math.Min(p2, p3))),
            Math.BitIncrement(Math.Max(Math.Max(p0, p1), Math.Max(p2, p3))));
    }
    internal static CoverageScalarBounds Divide(CoverageScalarBounds a, CoverageScalarBounds b)
    {
        if (b.Min <= 0 && b.Max >= 0) return Invalid;
        if (a == Zero) return Zero;
        if (b == One) return a;
        double p0 = a.Min / b.Min, p1 = a.Min / b.Max, p2 = a.Max / b.Min, p3 = a.Max / b.Max;
        return new(Math.BitDecrement(Math.Min(Math.Min(p0, p1), Math.Min(p2, p3))),
            Math.BitIncrement(Math.Max(Math.Max(p0, p1), Math.Max(p2, p3))));
    }
    internal static CoverageScalarBounds ClampUnit(CoverageScalarBounds value) =>
        new(Math.Clamp(value.Min, 0, 1), Math.Clamp(value.Max, 0, 1));
    internal static CoverageScalarBounds Square(CoverageScalarBounds value)
    {
        if (value == Zero) return Zero;
        double low = value.Min <= 0 && value.Max >= 0 ? 0 : Math.BitDecrement(Math.Min(value.Min * value.Min, value.Max * value.Max));
        return new(Math.Max(0, low), Math.BitIncrement(Math.Max(value.Min * value.Min, value.Max * value.Max)));
    }
    internal static CoverageScalarBounds Sqrt(CoverageScalarBounds value) =>
        new(value.Min <= 0 ? 0 : Math.Max(0, Math.BitDecrement(Math.Sqrt(value.Min))), Math.BitIncrement(Math.Sqrt(value.Max)));
}
