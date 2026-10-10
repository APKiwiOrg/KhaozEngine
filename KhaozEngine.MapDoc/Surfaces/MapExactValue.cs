using System;
using System.Globalization;
using System.Numerics;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>An exact result cannot be represented by the bounded rational value.</summary>
public sealed class MapExactOverflowException : ArithmeticException
{
    public MapExactOverflowException() : base("exact value overflow") { }
}

/// <summary>A normalized rational with checked Int128 intermediates. Default is exactly zero.</summary>
public readonly struct MapExactValue : IEquatable<MapExactValue>, IComparable<MapExactValue>
{
    readonly long _denominatorMinusOne;
    public long Numerator { get; }
    public long Denominator => _denominatorMinusOne + 1;
    public int Sign => Math.Sign(Numerator);

    public MapExactValue(long numerator, long denominator) => this = Normalize(numerator, denominator);

    MapExactValue(long numerator, long denominator, bool normalized)
    {
        Numerator = numerator;
        _denominatorMinusOne = denominator - 1;
    }

    static MapExactValue Normalize(Int128 numerator, Int128 denominator)
    {
        if (denominator == 0) throw new ArgumentException("denominator must not be zero");
        if (denominator < 0) { numerator = -numerator; denominator = -denominator; }
        Int128 gcd = Gcd(numerator, denominator);
        numerator /= gcd;
        denominator /= gcd;
        if (numerator < long.MinValue || numerator > long.MaxValue || denominator > long.MaxValue)
            throw new MapExactOverflowException();
        return new((long)numerator, (long)denominator, normalized: true);
    }

    internal static Int128 Gcd(Int128 a, Int128 b)
    {
        if (a < 0) a = -a;
        if (b < 0) b = -b;
        while (b != 0) { Int128 next = a % b; a = b; b = next; }
        return a;
    }

    public MapExactValue Add(MapExactValue other) => Normalize(
        (Int128)Numerator * other.Denominator + (Int128)other.Numerator * Denominator,
        (Int128)Denominator * other.Denominator);
    public MapExactValue Subtract(MapExactValue other) => Normalize(
        (Int128)Numerator * other.Denominator - (Int128)other.Numerator * Denominator,
        (Int128)Denominator * other.Denominator);
    public MapExactValue Multiply(MapExactValue other) => Normalize(
        (Int128)Numerator * other.Numerator, (Int128)Denominator * other.Denominator);
    public MapExactValue Divide(MapExactValue other) => Normalize(
        (Int128)Numerator * other.Denominator, (Int128)Denominator * other.Numerator);
    public MapExactValue Negate() => Normalize(-(Int128)Numerator, Denominator);
    public long Floor() => Numerator / Denominator - (Numerator % Denominator < 0 ? 1 : 0);
    public long Ceiling() => Numerator / Denominator + (Numerator % Denominator > 0 ? 1 : 0);
    public double ToDouble() => (double)Numerator / Denominator;

    /// <summary>Rounds once to binary32, nearest with ties to even, without a binary64 intermediate.</summary>
    public float ToSingle()
    {
        if (Numerator == 0) return 0;
        UInt128 n = (UInt128)(Numerator < 0 ? -(Int128)Numerator : Numerator);
        UInt128 d = (UInt128)Denominator;
        int exponent = BitOperations.Log2((ulong)n) - BitOperations.Log2((ulong)d);
        if (exponent >= 0 ? n < (d << exponent) : (n << -exponent) < d) exponent--;
        int shift = 23 - exponent;
        // Bounded long ratios are normal floats. The scaled numerator/denominator need at most 87 bits.
        UInt128 scaledN = shift >= 0 ? n << shift : n;
        UInt128 scaledD = shift < 0 ? d << -shift : d;
        UInt128 significand = scaledN / scaledD;
        UInt128 remainder = scaledN % scaledD;
        if (remainder * 2 > scaledD || (remainder * 2 == scaledD && (significand & 1) != 0))
            significand++;
        if (significand == (1U << 24)) { significand >>= 1; exponent++; }
        uint bits = (Numerator < 0 ? 0x80000000U : 0) |
            ((uint)(exponent + 127) << 23) | ((uint)significand & 0x7fffffU);
        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }

    public static MapExactValue FromSingle(float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentException("value must be finite", nameof(value));
        uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(value));
        uint fraction = bits & 0x7fffffU;
        int encodedExponent = (int)((bits >> 23) & 255);
        uint significand = encodedExponent == 0 ? fraction : fraction | 0x800000U;
        if (significand == 0) return default;
        int exponent = encodedExponent == 0 ? -149 : encodedExponent - 127 - 23;
        while ((significand & 1) == 0) { significand >>= 1; exponent++; }
        if (exponent < -62 || exponent > 63) throw new MapExactOverflowException();
        Int128 numerator = significand;
        Int128 denominator = 1;
        if (exponent >= 0) numerator <<= exponent;
        else denominator <<= -exponent;
        if ((bits & 0x80000000U) != 0) numerator = -numerator;
        return Normalize(numerator, denominator);
    }

    public int CompareTo(MapExactValue other) => ((Int128)Numerator * other.Denominator)
        .CompareTo((Int128)other.Numerator * Denominator);
    public bool Equals(MapExactValue other) => Numerator == other.Numerator && Denominator == other.Denominator;
    public override bool Equals(object? obj) => obj is MapExactValue other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);
    public override string ToString() => Denominator == 1 ? Numerator.ToString(CultureInfo.InvariantCulture)
        : Numerator.ToString(CultureInfo.InvariantCulture) + "/" + Denominator.ToString(CultureInfo.InvariantCulture);
    public static bool operator ==(MapExactValue a, MapExactValue b) => a.Equals(b);
    public static bool operator !=(MapExactValue a, MapExactValue b) => !a.Equals(b);
}

public readonly record struct MapExactPoint(MapExactValue X, MapExactValue Y, MapExactValue Z);
public readonly record struct MapExactXz(MapExactValue X, MapExactValue Z) : IComparable<MapExactXz>
{
    public int CompareTo(MapExactXz other) => Z != other.Z ? Z.CompareTo(other.Z) : X.CompareTo(other.X);
}
