using System;

namespace KhaozEngine.Physics.Bepu;

internal interface ICircularProjectionArithmetic<T>
{
    static abstract T Add(T a, T b);
    static abstract T Subtract(T a, T b);
    static abstract T Multiply(T a, T b);
    static abstract T Root(T a);
    static abstract T RadialScale(T radius, T radial);
    static abstract T Cap(T axial, T half);
}

/// <summary>The finite cylinder projection used by capsule proposals, with the same operation
/// order. Half length zero is the closed disc. Arithmetic policies also enclose its continuum.</summary>
internal static class CircularProjection
{
    internal static void Normal<T, A>(T px, T py, T pz, T ex, T ey, T ez, T radius, T half,
        out T x, out T y, out T z, out T radial) where A : ICircularProjectionArithmetic<T>
    {
        T axial = A.Add(A.Add(A.Multiply(px, ex), A.Multiply(py, ey)), A.Multiply(pz, ez));
        x = A.Subtract(px, A.Multiply(axial, ex));
        y = A.Subtract(py, A.Multiply(axial, ey));
        z = A.Subtract(pz, A.Multiply(axial, ez));
        radial = A.Root(A.Add(A.Add(A.Multiply(x, x), A.Multiply(y, y)), A.Multiply(z, z)));
        T scale = A.RadialScale(radius, radial), cap = A.Cap(axial, half);
        x = A.Subtract(px, A.Add(A.Multiply(x, scale), A.Multiply(ex, cap)));
        y = A.Subtract(py, A.Add(A.Multiply(y, scale), A.Multiply(ey, cap)));
        z = A.Subtract(pz, A.Add(A.Multiply(z, scale), A.Multiply(ez, cap)));
    }
}

internal readonly struct CircularDoubleArithmetic : ICircularProjectionArithmetic<double>
{
    public static double Add(double a, double b) => a + b;
    public static double Subtract(double a, double b) => a - b;
    public static double Multiply(double a, double b) => a * b;
    public static double Root(double a) => Math.Sqrt(a);
    public static double RadialScale(double radius, double radial) => radial > radius ? radius / radial : 1;
    public static double Cap(double axial, double half) => Math.Clamp(axial, -half, half);
}

internal readonly struct CircularIntervalArithmetic : ICircularProjectionArithmetic<GeometryInterval>
{
    public static GeometryInterval Add(GeometryInterval a, GeometryInterval b) => a.Add(b);
    public static GeometryInterval Subtract(GeometryInterval a, GeometryInterval b) => a.Subtract(b);
    public static GeometryInterval Multiply(GeometryInterval a, GeometryInterval b) => a.Multiply(b);
    public static GeometryInterval Root(GeometryInterval a) => a.IsResolved && a.Upper >= 0
        ? GeometryInterval.Enclose(Math.Max(0, a.Lower), a.Upper).Sqrt() : default;
    public static GeometryInterval RadialScale(GeometryInterval radius, GeometryInterval radial)
    {
        if (!radius.IsResolved || !radial.IsResolved || radius.Lower <= 0) return default;
        if (radial.Upper <= radius.Lower) return GeometryInterval.Exact(1);
        GeometryInterval ratio = radius.Divide(GeometryInterval.Enclose(Math.Max(radius.Lower, radial.Lower), radial.Upper));
        return ratio.IsResolved ? GeometryInterval.Enclose(Math.Min(1, ratio.Lower), Math.Min(1, ratio.Upper)) : default;
    }
    public static GeometryInterval Cap(GeometryInterval axial, GeometryInterval half) => axial.IsResolved && half.IsResolved &&
        half.Lower == half.Upper ? GeometryInterval.Enclose(Math.Clamp(axial.Lower, -half.Lower, half.Lower),
            Math.Clamp(axial.Upper, -half.Lower, half.Lower)) : default;
}

internal readonly struct CircularQuadraticArithmetic : ICircularProjectionArithmetic<ConvexQuadratic>
{
    public static ConvexQuadratic Add(ConvexQuadratic a, ConvexQuadratic b) => a + b;
    public static ConvexQuadratic Subtract(ConvexQuadratic a, ConvexQuadratic b) => a - b;
    public static ConvexQuadratic Multiply(ConvexQuadratic a, ConvexQuadratic b) => a * b;
    public static ConvexQuadratic Root(ConvexQuadratic a) => a.Rational ? ConvexQuadratic.Root(a.A) : throw new ConvexExactRefusalException();
    public static ConvexQuadratic RadialScale(ConvexQuadratic radius, ConvexQuadratic radial) =>
        (radial - radius).Sign > 0 ? radius / radial : ConvexQuadratic.From(1);
    public static ConvexQuadratic Cap(ConvexQuadratic axial, ConvexQuadratic half) =>
        (axial - half).Sign > 0 ? half : (axial + half).Sign < 0 ? -half : axial;
}
