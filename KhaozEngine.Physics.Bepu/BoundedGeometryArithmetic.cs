using System;
using System.Numerics;
using System.Runtime.CompilerServices;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GeometryInterval Exact(double value) => Enclose(value, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static GeometryInterval Rounded(double lower, double upper) =>
        double.IsFinite(lower) && double.IsFinite(upper)
            ? Enclose(Math.BitDecrement(lower), Math.BitIncrement(upper)) : default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public GeometryInterval Add(GeometryInterval other) => IsResolved && other.IsResolved
        ? Rounded(Lower + other.Lower, Upper + other.Upper) : default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

/// <summary>Exact, bounded arithmetic over represented binary64 operands. No shape or sweep certificate. Operands
/// inside the <see cref="ExactExpansion"/> domain take its allocation-free path, which returns the exact sign the
/// dyadic path returns there. Every other operand takes the dyadic path and its 4096 bit refusal.</summary>
[SkipLocalsInit]
internal static class BoundedGeometryArithmetic
{
    internal const int MaximumPredicateBits = 4096;

    /// <summary>Sign of a*b-c*d without rounding either product. Refuses work beyond the fixed bit cap.</summary>
    internal static GeometrySign CompareProducts(double a, double b, double c, double d)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c) || !double.IsFinite(d))
            return GeometrySign.Unresolved;
        if (ExactExpansion.InRange(a) && ExactExpansion.InRange(b) && ExactExpansion.InRange(c) &&
            ExactExpansion.InRange(d))
        {
            // c*1 is c exactly, and fma rounds a*b-c once. Every operand is a multiple of 2^-252 below 2^200, so a
            // nonzero a*b-c is at least 2^-504 and keeps its sign through that rounding.
            if (d == 1) return Sign(Math.FusedMultiplyAdd(a, b, -c));
            var sum = new ExactExpansion(stackalloc double[5]);
            sum.AddProduct(a, b);
            sum.AddProduct(-c, d);
            return sum.Sign;
        }
        // A zero product leaves the sign of the other. The dyadic path aligns a zero at exponent 0, at most 2148
        // bits from any product of finite operands, so it never refuses that comparison.
        if (a == 0 || b == 0 || c == 0 || d == 0)
            return Sign((a == 0 || b == 0 ? 0 : Math.Sign(a) * Math.Sign(b)) -
                (c == 0 || d == 0 ? 0 : Math.Sign(c) * Math.Sign(d)));
        // Scaling by powers of two is exact for finite operands, subnormal ones included, and keeps the sign. Each
        // operand is brought to [1, 2) and the products' exponent difference, at most 100, is moved onto d, so all
        // four land in the expansion domain. Their dyadic products then lie within about 300 bits of each other,
        // far inside the cap, so both paths decide the same exact sign.
        int left = Math.ILogB(a) + Math.ILogB(b), right = Math.ILogB(c) + Math.ILogB(d);
        if (Math.Abs(right - left) <= 100)
        {
            var scaled = new ExactExpansion(stackalloc double[5]);
            scaled.AddProduct(Math.ScaleB(a, -Math.ILogB(a)), Math.ScaleB(b, -Math.ILogB(b)));
            scaled.AddProduct(-Math.ScaleB(c, -Math.ILogB(c)), Math.ScaleB(d, right - left - Math.ILogB(d)));
            return scaled.Sign;
        }
        return CompareProductsDyadic(a, b, c, d);
    }

    /// <summary>The dyadic path of <see cref="CompareProducts"/>, the reference its expansion path is proved
    /// against.</summary>
    internal static GeometrySign CompareProductsDyadic(double a, double b, double c, double d)
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
        if (a.Length is < 1 or > 3 || a.Length != b.Length || c.Length is < 1 or > 3 || c.Length != d.Length)
            return GeometrySign.Unresolved;
        if (InRange(a) && InRange(b) && InRange(c) && InRange(d))
        {
            // Eight terms per coordinate, at most three coordinates on each side.
            var sum = new ExactExpansion(stackalloc double[49]);
            AddSquaredDistance(ref sum, a, b, 1);
            AddSquaredDistance(ref sum, c, d, -1);
            return sum.Sign;
        }
        return CompareSquaredDistancesDyadic(a, b, c, d);
    }

    /// <summary>The dyadic path of <see cref="CompareSquaredDistances"/>, the reference its expansion path is
    /// proved against.</summary>
    internal static GeometrySign CompareSquaredDistancesDyadic(ReadOnlySpan<double> a, ReadOnlySpan<double> b,
        ReadOnlySpan<double> c, ReadOnlySpan<double> d)
    {
        if (!TrySquaredDistance(a, b, out Dyadic left) || !TrySquaredDistance(c, d, out Dyadic right))
            return GeometrySign.Unresolved;
        return Compare(left, right);
    }

    // Each coordinate difference is s + t exactly by Two-Sum, and its square is s*s + 2*s*t + t*t.
    static void AddSquaredDistance(ref ExactExpansion sum, ReadOnlySpan<double> a, ReadOnlySpan<double> b,
        double sign)
    {
        for (int i = 0; i < a.Length; i++)
        {
            ExactExpansion.TwoSum(a[i], -b[i], out double s, out double t);
            sum.AddProduct(sign * s, s);
            sum.AddProduct(sign * s, t);
            sum.AddProduct(sign * s, t);
            sum.AddProduct(sign * t, t);
        }
    }

    // Finite, and zero or a magnitude in [2^-200, 2^200).
    static bool InRange(ReadOnlySpan<double> values)
    {
        foreach (double value in values)
            if (!double.IsFinite(value) || !ExactExpansion.InRange(value)) return false;
        return true;
    }

    /// <summary>Exact sign of x*x+y*y+z*z+w*w-expected. This fixed four-component
    /// operation does not extend the one-to-three-coordinate distance contract.</summary>
    internal static GeometrySign CompareSumOfFourSquares(double x, double y, double z, double w, double expected)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) ||
            !double.IsFinite(w) || !double.IsFinite(expected)) return GeometrySign.Unresolved;
        if (ExactExpansion.InRange(x) && ExactExpansion.InRange(y) && ExactExpansion.InRange(z) &&
            ExactExpansion.InRange(w) && ExactExpansion.InRange(expected))
        {
            var exact = new ExactExpansion(stackalloc double[10]);
            exact.AddProduct(x, x);
            exact.AddProduct(y, y);
            exact.AddProduct(z, z);
            exact.AddProduct(w, w);
            exact.Add(-expected);
            return exact.Sign;
        }
        return CompareSumOfFourSquaresDyadic(x, y, z, w, expected);
    }

    /// <summary>The dyadic path of <see cref="CompareSumOfFourSquares"/>, the reference its expansion path is
    /// proved against.</summary>
    internal static GeometrySign CompareSumOfFourSquaresDyadic(double x, double y, double z, double w,
        double expected)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) ||
            !double.IsFinite(w) || !double.IsFinite(expected)) return GeometrySign.Unresolved;
        Dyadic sum = default;
        ReadOnlySpan<double> values = [x, y, z, w];
        foreach (double value in values)
        {
            Dyadic operand = Split(value);
            if (!TryMultiply(operand, operand, out Dyadic square) ||
                !TryAdd(sum, square, out Dyadic next)) return GeometrySign.Unresolved;
            sum = next;
        }
        return Compare(sum, Split(expected));
    }

    /// <summary>Exact oriented area sign for three supplied binary32 points. Finite binary32 values lie in the
    /// <see cref="ExactExpansion"/> domain, and the dyadic path never refuses them.</summary>
    internal static GeometrySign Orient2D(float ax, float ay, float bx, float by, float cx, float cy)
    {
        if (!float.IsFinite(ax) || !float.IsFinite(ay) || !float.IsFinite(bx) ||
            !float.IsFinite(by) || !float.IsFinite(cx) || !float.IsFinite(cy))
            return GeometrySign.Unresolved;
        var area = new ExactExpansion(stackalloc double[17]);
        ExactExpansion.TwoSum(bx, -(double)ax, out double x1, out double x1Error);
        ExactExpansion.TwoSum(cy, -(double)ay, out double y2, out double y2Error);
        ExactExpansion.TwoSum(by, -(double)ay, out double y1, out double y1Error);
        ExactExpansion.TwoSum(cx, -(double)ax, out double x2, out double x2Error);
        AddProduct(ref area, x1, x1Error, y2, y2Error, 1);
        AddProduct(ref area, y1, y1Error, x2, x2Error, -1);
        return area.Sign;
    }

    /// <summary>The dyadic path of <see cref="Orient2D"/>, the reference its expansion path is proved against.</summary>
    internal static GeometrySign Orient2DDyadic(float ax, float ay, float bx, float by, float cx, float cy)
    {
        if (!float.IsFinite(ax) || !float.IsFinite(ay) || !float.IsFinite(bx) ||
            !float.IsFinite(by) || !float.IsFinite(cx) || !float.IsFinite(cy) ||
            !TryDifference(bx, ax, out Dyadic x1) || !TryDifference(by, ay, out Dyadic y1) ||
            !TryDifference(cx, ax, out Dyadic x2) || !TryDifference(cy, ay, out Dyadic y2) ||
            !TryProductDifference(x1, y2, y1, x2, out Dyadic area))
            return GeometrySign.Unresolved;
        return Sign(area);
    }

    /// <summary>Exact sign of ((b-a) cross (c-a)) dot (point-a), with no rounded differences. Finite binary32 values
    /// lie in the <see cref="ExactExpansion"/> domain, and the dyadic path never refuses them.</summary>
    internal static GeometrySign Orient3D(Vector3 a, Vector3 b, Vector3 c, Vector3 point)
    {
        if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(point)) return GeometrySign.Unresolved;
        Span<double> ab = stackalloc double[6], ac = stackalloc double[6], ap = stackalloc double[6];
        Differences(b, a, ab);
        Differences(c, a, ac);
        Differences(point, a, ap);
        // Six signed triple products of two-term differences: at most 6 * 32 terms.
        var volume = new ExactExpansion(stackalloc double[193]);
        for (int axis = 0; axis < 3; axis++)
        {
            int next = (axis + 1) % 3, last = (axis + 2) % 3;
            AddTriple(ref volume, ab, next, ac, last, ap, axis, 1);
            AddTriple(ref volume, ab, last, ac, next, ap, axis, -1);
        }
        return volume.Sign;
    }

    /// <summary>The dyadic path of <see cref="Orient3D"/>, the reference its expansion path is proved against.</summary>
    internal static GeometrySign Orient3DDyadic(Vector3 a, Vector3 b, Vector3 c, Vector3 point)
    {
        if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(point) ||
            !TryDifference(b, a, out DyadicVector ab) || !TryDifference(c, a, out DyadicVector ac) ||
            !TryDifference(point, a, out DyadicVector ap) ||
            !TryProductDifference(ab.Y, ac.Z, ab.Z, ac.Y, out Dyadic x) ||
            !TryProductDifference(ab.Z, ac.X, ab.X, ac.Z, out Dyadic y) ||
            !TryProductDifference(ab.X, ac.Y, ab.Y, ac.X, out Dyadic z) ||
            !TryMultiply(x, ap.X, out Dyadic px) || !TryMultiply(y, ap.Y, out Dyadic py) ||
            !TryMultiply(z, ap.Z, out Dyadic pz) || !TryAdd(px, py, out Dyadic xy) ||
            !TryAdd(xy, pz, out Dyadic volume))
            return GeometrySign.Unresolved;
        return Sign(volume);
    }

    readonly record struct DyadicVector(Dyadic X, Dyadic Y, Dyadic Z);

    static GeometrySign Sign(double value) =>
        value > 0 ? GeometrySign.Positive : value < 0 ? GeometrySign.Negative : GeometrySign.Zero;

    // (a + aError)(b + bError) times sign, as four exact products.
    static void AddProduct(ref ExactExpansion sum, double a, double aError, double b, double bError, double sign)
    {
        sum.AddProduct(sign * a, b);
        sum.AddProduct(sign * a, bError);
        sum.AddProduct(sign * aError, b);
        sum.AddProduct(sign * aError, bError);
    }

    // Each coordinate difference of two binary32 points as a value and its exact Two-Sum error.
    static void Differences(Vector3 to, Vector3 from, Span<double> output)
    {
        ExactExpansion.TwoSum(to.X, -(double)from.X, out output[0], out output[1]);
        ExactExpansion.TwoSum(to.Y, -(double)from.Y, out output[2], out output[3]);
        ExactExpansion.TwoSum(to.Z, -(double)from.Z, out output[4], out output[5]);
    }

    // sign * u[i] * v[j] * w[k] over the two terms of each difference. Each pairwise product is split into its
    // rounded value and error, and each of those times a w term is added exactly.
    static void AddTriple(ref ExactExpansion sum, ReadOnlySpan<double> u, int i, ReadOnlySpan<double> v, int j,
        ReadOnlySpan<double> w, int k, double sign)
    {
        for (int p = 0; p < 2; p++)
        {
            double up = u[2 * i + p];
            if (up == 0) continue;
            for (int q = 0; q < 2; q++)
            {
                double vq = v[2 * j + q];
                if (vq == 0) continue;
                double product = sign * up * vq;
                double error = Math.FusedMultiplyAdd(sign * up, vq, -product);
                for (int r = 0; r < 2; r++)
                {
                    double wr = w[2 * k + r];
                    if (wr == 0) continue;
                    sum.AddProduct(product, wr);
                    sum.AddProduct(error, wr);
                }
            }
        }
    }

    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    static GeometrySign Sign(Dyadic value) => value.Mantissa.Sign switch
    {
        < 0 => GeometrySign.Negative,
        > 0 => GeometrySign.Positive,
        _ => GeometrySign.Zero,
    };

    static bool TryDifference(Vector3 a, Vector3 b, out DyadicVector result)
    {
        result = default;
        if (!TryDifference(a.X, b.X, out Dyadic x) || !TryDifference(a.Y, b.Y, out Dyadic y) ||
            !TryDifference(a.Z, b.Z, out Dyadic z)) return false;
        result = new(x, y, z);
        return true;
    }

    static bool TryDifference(float a, float b, out Dyadic result)
    {
        Dyadic second = Split(b);
        return TryAdd(Split(a), new(-second.Mantissa, second.Exponent), out result);
    }

    static bool TryMultiply(Dyadic a, Dyadic b, out Dyadic result)
    {
        result = default;
        if (BigInteger.Abs(a.Mantissa).GetBitLength() + BigInteger.Abs(b.Mantissa).GetBitLength() > MaximumPredicateBits)
            return false;
        BigInteger mantissa = a.Mantissa * b.Mantissa;
        result = new(mantissa, mantissa.IsZero ? 0 : a.Exponent + b.Exponent);
        return true;
    }

    static bool TryProductDifference(Dyadic a, Dyadic b, Dyadic c, Dyadic d, out Dyadic result)
    {
        result = default;
        return TryMultiply(a, b, out Dyadic first) && TryMultiply(c, d, out Dyadic second) &&
            TryAdd(first, new(-second.Mantissa, second.Exponent), out result);
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
