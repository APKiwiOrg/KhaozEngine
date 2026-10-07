using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A finite outward enclosure. Default carries no mathematical assertion.</summary>
internal readonly struct GeometryInterval
{
    public double Lower { get; }
    public double Upper { get; }
    public bool IsResolved { get; }

    GeometryInterval(double lower, double upper)
    {
        Lower = lower == 0 ? 0 : lower;
        Upper = upper == 0 ? 0 : upper;
        IsResolved = true;
    }

    public static GeometryInterval Exact(double value) => Enclose(value, value);

    public static GeometryInterval Enclose(double lower, double upper) =>
        double.IsFinite(lower) && double.IsFinite(upper) && lower <= upper ? new(lower, upper) : default;

    static GeometryInterval Rounded(double lower, double upper) =>
        double.IsFinite(lower) && double.IsFinite(upper)
            ? Enclose(Math.BitDecrement(lower), Math.BitIncrement(upper)) : default;

    public GeometryInterval Add(GeometryInterval other) => IsResolved && other.IsResolved
        ? Rounded(Lower + other.Lower, Upper + other.Upper) : default;

    public GeometryInterval Subtract(GeometryInterval other) => IsResolved && other.IsResolved
        ? Rounded(Lower - other.Upper, Upper - other.Lower) : default;

    public GeometryInterval Multiply(GeometryInterval other)
    {
        if (!IsResolved || !other.IsResolved) return default;
        return Corners(Lower * other.Lower, Lower * other.Upper, Upper * other.Lower, Upper * other.Upper);
    }

    public GeometryInterval Divide(GeometryInterval other)
    {
        if (!IsResolved || !other.IsResolved || (other.Lower <= 0 && other.Upper >= 0)) return default;
        return Corners(Lower / other.Lower, Lower / other.Upper, Upper / other.Lower, Upper / other.Upper);
    }

    static GeometryInterval Corners(double a, double b, double c, double d)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c) || !double.IsFinite(d))
            return default;
        return Rounded(Math.Min(Math.Min(a, b), Math.Min(c, d)), Math.Max(Math.Max(a, b), Math.Max(c, d)));
    }

    public GeometryInterval Square()
    {
        if (!IsResolved) return default;
        double a = Lower * Lower, b = Upper * Upper;
        if (!double.IsFinite(a) || !double.IsFinite(b)) return default;
        double lower = Lower <= 0 && Upper >= 0 ? 0 : Math.Max(0, Math.BitDecrement(Math.Min(a, b)));
        return Enclose(lower, Math.BitIncrement(Math.Max(a, b)));
    }

    public GeometryInterval Sqrt()
    {
        if (!IsResolved || Lower < 0) return default;
        double lower = Lower == 0 ? 0 : Math.BitDecrement(Math.Sqrt(Lower));
        double upper = Upper == 0 ? 0 : Math.BitIncrement(Math.Sqrt(Upper));
        if (!double.IsFinite(lower) || !double.IsFinite(upper) || lower < 0) return default;
        GeometrySign lo = BoundedGeometryArithmetic.CompareProducts(lower, lower, Lower, 1);
        GeometrySign hi = BoundedGeometryArithmetic.CompareProducts(upper, upper, Upper, 1);
        // Math.Sqrt proposes endpoints. Exact squared comparisons, not an observed rounding accuracy,
        // decide whether those endpoints enclose the mathematical root.
        return (lo is GeometrySign.Negative or GeometrySign.Zero) && (hi is GeometrySign.Positive or GeometrySign.Zero)
            ? Enclose(lower, upper) : default;
    }
}

internal enum GeometrySign : byte { Unresolved, Negative, Zero, Positive }

/// <summary>Exact, bounded arithmetic over represented binary64 operands. No shape or sweep certificate.</summary>
internal static class BoundedGeometryArithmetic
{
    internal const int MaximumPredicateBits = 4096;

    /// <summary>Sign of a*b-c*d without rounding either product. Refuses work beyond the fixed bit cap.</summary>
    internal static GeometrySign CompareProducts(double a, double b, double c, double d)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c) || !double.IsFinite(d))
            return GeometrySign.Unresolved;
        Dyadic left = Product(a, b), right = Product(c, d);
        int exponent = Math.Min(left.Exponent, right.Exponent);
        int leftShift = left.Exponent - exponent, rightShift = right.Exponent - exponent;
        if (BigInteger.Abs(left.Mantissa).GetBitLength() + leftShift > MaximumPredicateBits ||
            BigInteger.Abs(right.Mantissa).GetBitLength() + rightShift > MaximumPredicateBits)
            return GeometrySign.Unresolved;
        int comparison = (left.Mantissa << leftShift).CompareTo(right.Mantissa << rightShift);
        return comparison < 0 ? GeometrySign.Negative : comparison > 0 ? GeometrySign.Positive : GeometrySign.Zero;
    }

    static Dyadic Product(double a, double b)
    {
        Dyadic first = Split(a), second = Split(b);
        BigInteger mantissa = first.Mantissa * second.Mantissa;
        return new(mantissa, mantissa.IsZero ? 0 : first.Exponent + second.Exponent);
    }

    static Dyadic Split(double value)
    {
        ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        int exponentBits = (int)((bits >> 52) & 0x7ff);
        ulong fraction = bits & 0x000f_ffff_ffff_ffffUL;
        BigInteger mantissa = exponentBits == 0 ? fraction : fraction | (1UL << 52);
        if ((bits & (1UL << 63)) != 0) mantissa = -mantissa;
        int exponent = exponentBits == 0 ? -1074 : exponentBits - 1075;
        return new(mantissa, mantissa.IsZero ? 0 : exponent);
    }

    readonly record struct Dyadic(BigInteger Mantissa, int Exponent);
}
