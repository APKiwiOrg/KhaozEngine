using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Render3D;

/// <summary>An exact dyadic rational, Numerator * 2^-Scale. Every finite double is an integer multiple of 2^-1074, so
/// decomposition is exact and products only add scales.</summary>
internal readonly struct CoverageExactValue(BigInteger numerator, int scale)
{
    internal const int DoubleScale = 1074;
    internal BigInteger Numerator { get; } = numerator;
    internal int Scale { get; } = scale;
    internal int Sign => Numerator.Sign;

    internal static CoverageExactValue FromDouble(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("The exact oracle accepts finite values only.", nameof(value));
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        int exponent = (int)(bits >> 52 & 0x7FF);
        BigInteger fraction = bits & 0xF_FFFF_FFFF_FFFF;
        // A normal value is (2^52 + fraction) * 2^(exponent - 1075) and a subnormal is fraction * 2^-1074.
        BigInteger scaled = exponent == 0 ? fraction : (fraction | BigInteger.One << 52) << (exponent - 1);
        return new(bits >> 63 == 0 ? scaled : -scaled, DoubleScale);
    }

    internal static CoverageExactValue PowerOfTwo(int exponent) => new(BigInteger.One << (DoubleScale + exponent), DoubleScale);

    internal static CoverageExactValue Sum(ReadOnlySpan<double> components)
    {
        CoverageExactValue sum = default;
        foreach (double component in components) sum += FromDouble(component);
        return sum;
    }

    internal CoverageExactValue Abs => new(BigInteger.Abs(Numerator), Scale);

    /// <summary>The exponent of the lowest set bit. Any expansion of this value has a component no larger than
    /// 2^(LowestBit + 53).</summary>
    internal long LowestBit => Numerator.IsZero ? throw new InvalidOperationException("Zero has no set bit.")
        : (long)BigInteger.TrailingZeroCount(BigInteger.Abs(Numerator)) - Scale;

    internal int CompareTo(CoverageExactValue other) => (this - other).Sign;
    internal int CompareTo(double other) => CompareTo(FromDouble(other));

    public static CoverageExactValue operator +(CoverageExactValue a, CoverageExactValue b)
    {
        int scale = Math.Max(a.Scale, b.Scale);
        return new((a.Numerator << (scale - a.Scale)) + (b.Numerator << (scale - b.Scale)), scale);
    }
    public static CoverageExactValue operator -(CoverageExactValue a) => new(-a.Numerator, a.Scale);
    public static CoverageExactValue operator -(CoverageExactValue a, CoverageExactValue b) => a + -b;
    public static CoverageExactValue operator *(CoverageExactValue a, CoverageExactValue b) => new(a.Numerator * b.Numerator, a.Scale + b.Scale);

    public override string ToString() => $"{Numerator} * 2^-{Scale}";
}

/// <summary>Independent exact references for the CoverageGeometry kernels. It reads input components through the
/// coordinate indexer only. It never calls CoverageGeometry, CoverageIntervals or another coverage helper to form an
/// expected value.</summary>
internal static class CoverageExactOracle
{
    /// <summary>Products below 2^-1022 * 2^53 are refused by the kernels to keep error-free residuals out of underflow.</summary>
    internal static CoverageExactValue MinExactProduct => CoverageExactValue.PowerOfTwo(-1022 + 53);

    /// <summary>Any exact product at or above 2^1024 rounds to infinity in double.</summary>
    internal static CoverageExactValue Overflow => CoverageExactValue.PowerOfTwo(1024);

    internal static CoverageExactValue Exact(CoverageCoordinate c)
    {
        CoverageExactValue sum = default;
        for (int i = 0; i < 4; i++) sum += CoverageExactValue.FromDouble(c[i]);
        return sum;
    }

    internal static CoverageExactValue Exact(double value) => CoverageExactValue.FromDouble(value);

    internal static CoverageExactValue Difference(CoverageCoordinate a, CoverageCoordinate b) => Exact(a) - Exact(b);

    /// <summary>cross(b - a, p - a), independent of the kernel's endpoint ordering.</summary>
    internal static CoverageExactValue Orient(CoverageAffinePoint a, CoverageAffinePoint b, CoverageAffinePoint p) =>
        Difference(b.X, a.X) * Difference(p.Y, a.Y) - Difference(b.Y, a.Y) * Difference(p.X, a.X);

    /// <summary>(b - a) dot (d - c) in XY.</summary>
    internal static CoverageExactValue Dot(CoverageAffinePoint a, CoverageAffinePoint b, CoverageAffinePoint c, CoverageAffinePoint d) =>
        Difference(b.X, a.X) * Difference(d.X, c.X) + Difference(b.Y, a.Y) * Difference(d.Y, c.Y);

    /// <summary>cross(b - a, c - a) dot axis.</summary>
    internal static CoverageExactValue ProjectedArea(CoverageAffinePoint a, CoverageAffinePoint b, CoverageAffinePoint c, Vector3 axis)
    {
        CoverageExactValue ux = Difference(b.X, a.X), uy = Difference(b.Y, a.Y), uz = Difference(b.Z, a.Z);
        CoverageExactValue vx = Difference(c.X, a.X), vy = Difference(c.Y, a.Y), vz = Difference(c.Z, a.Z);
        return (uy * vz - uz * vy) * Exact(axis.X) + (uz * vx - ux * vz) * Exact(axis.Y) + (ux * vy - uy * vx) * Exact(axis.Z);
    }

    internal static CoverageExactValue SquaredDistance(CoverageAffinePoint a, CoverageAffinePoint b)
    {
        CoverageExactValue x = Difference(a.X, b.X), y = Difference(a.Y, b.Y), z = Difference(a.Z, b.Z);
        return x * x + y * y + z * z;
    }

    /// <summary>Lexicographic exact XY order, the condition under which an edge swaps its endpoints.</summary>
    internal static bool After(CoverageAffinePoint a, CoverageAffinePoint b)
    {
        int x = Difference(a.X, b.X).Sign;
        return x > 0 || x == 0 && Difference(a.Y, b.Y).Sign > 0;
    }

    /// <summary>Outward bounds contain the exact value. Finite bounds are dyadic, so the comparison is exact.</summary>
    internal static bool Contains(CoverageScalarBounds bounds, CoverageExactValue value) =>
        value.CompareTo(bounds.Min) >= 0 && value.CompareTo(bounds.Max) <= 0;
}
