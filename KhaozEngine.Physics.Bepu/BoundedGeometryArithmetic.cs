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

    /// <summary>Encloses both this real interval and its possible binary32 encodings.
    /// It does not assert that the supplied values came from an installed shape or transform.</summary>
    public GeometryInterval EncloseSingleRounding()
    {
        if (!IsResolved) return default;
        float lower = (float)Lower, upper = (float)Upper;
        if (!float.IsFinite(lower) || !float.IsFinite(upper)) return default;
        return Enclose(Math.Min(Lower, MathF.BitDecrement(lower)), Math.Max(Upper, MathF.BitIncrement(upper)));
    }

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
        return Compare(Product(a, b), Product(c, d));
    }

    /// <summary>Exact sign of sum((a-b)^2)-sum((c-d)^2), with one to three coordinates per distance.
    /// Operands are supplied represented values. No preceding transform or geometry premise is inferred.</summary>
    internal static GeometrySign CompareSquaredDistances(ReadOnlySpan<double> a, ReadOnlySpan<double> b,
        ReadOnlySpan<double> c, ReadOnlySpan<double> d)
    {
        if (!TrySquaredDistance(a, b, out Dyadic left) || !TrySquaredDistance(c, d, out Dyadic right))
            return GeometrySign.Unresolved;
        return Compare(left, right);
    }

    static GeometrySign Compare(Dyadic left, Dyadic right)
    {
        int exponent = Math.Min(left.Exponent, right.Exponent);
        int leftShift = left.Exponent - exponent, rightShift = right.Exponent - exponent;
        if (BigInteger.Abs(left.Mantissa).GetBitLength() + leftShift > MaximumPredicateBits ||
            BigInteger.Abs(right.Mantissa).GetBitLength() + rightShift > MaximumPredicateBits)
            return GeometrySign.Unresolved;
        int comparison = (left.Mantissa << leftShift).CompareTo(right.Mantissa << rightShift);
        return comparison < 0 ? GeometrySign.Negative : comparison > 0 ? GeometrySign.Positive : GeometrySign.Zero;
    }

    static bool TrySquaredDistance(ReadOnlySpan<double> a, ReadOnlySpan<double> b, out Dyadic distance)
    {
        distance = default;
        if (a.Length is < 1 or > 3 || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!double.IsFinite(a[i]) || !double.IsFinite(b[i])) return false;
            Dyadic first = Split(a[i]), second = Split(b[i]);
            if (!TryAdd(first, new Dyadic(-second.Mantissa, second.Exponent), out Dyadic delta)) return false;
            // Refuse before allocating an over-budget product, even if a later operation might cancel it.
            if (2 * BigInteger.Abs(delta.Mantissa).GetBitLength() > MaximumPredicateBits) return false;
            var square = new Dyadic(delta.Mantissa * delta.Mantissa, delta.Mantissa.IsZero ? 0 : 2 * delta.Exponent);
            if (!TryAdd(distance, square, out Dyadic sum)) return false;
            distance = sum;
        }
        return true;
    }

    static bool TryAdd(Dyadic a, Dyadic b, out Dyadic sum)
    {
        sum = default;
        if (BigInteger.Abs(a.Mantissa).GetBitLength() > MaximumPredicateBits ||
            BigInteger.Abs(b.Mantissa).GetBitLength() > MaximumPredicateBits) return false;
        if (a.Mantissa.IsZero) { sum = b; return true; }
        if (b.Mantissa.IsZero) { sum = a; return true; }
        int exponent = Math.Min(a.Exponent, b.Exponent);
        int aShift = a.Exponent - exponent, bShift = b.Exponent - exponent;
        long bits = Math.Max(BigInteger.Abs(a.Mantissa).GetBitLength() + aShift,
            BigInteger.Abs(b.Mantissa).GetBitLength() + bShift);
        // Reserve the possible carry before shifting or adding. Conservative refusal is permitted.
        if (bits + 1 > MaximumPredicateBits) return false;
        BigInteger mantissa = (a.Mantissa << aShift) + (b.Mantissa << bShift);
        sum = new Dyadic(mantissa, mantissa.IsZero ? 0 : exponent);
        return true;
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
