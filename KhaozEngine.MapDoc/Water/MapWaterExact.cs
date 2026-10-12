using System;
using System.Globalization;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>An unbounded normalized rational for water geometry. Every operation reduces by the greatest common
/// divisor, so wide intermediates cancel before <see cref="TryNarrow"/>, which is the only step that can fail.
/// Default is exactly zero.</summary>
internal readonly struct MapWideRational : IEquatable<MapWideRational>, IComparable<MapWideRational>
{
    readonly BigInteger _numerator;
    readonly BigInteger _denominatorMinusOne;

    MapWideRational(BigInteger numerator, BigInteger denominator)
    {
        _numerator = numerator;
        _denominatorMinusOne = denominator - BigInteger.One;
    }

    public BigInteger Numerator => _numerator;
    public BigInteger Denominator => _denominatorMinusOne + BigInteger.One;
    public int Sign => _numerator.Sign;
    internal double ToDouble() => (double)_numerator / (double)Denominator;

    public static MapWideRational From(MapExactValue value) => new(value.Numerator, value.Denominator);
    public static MapWideRational From(long value) => new(value, BigInteger.One);

    /// <summary>Captures a finite binary64 grid bound exactly, without narrowing its numerator or denominator.</summary>
    public static MapWideRational FromDouble(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("value must be finite", nameof(value));
        ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        ulong fraction = bits & 0x000fffffffffffffUL;
        int encodedExponent = (int)((bits >> 52) & 2047);
        ulong significand = encodedExponent == 0 ? fraction : fraction | 0x0010000000000000UL;
        if (significand == 0) return default;
        int exponent = encodedExponent == 0 ? -1074 : encodedExponent - 1023 - 52;
        BigInteger numerator = significand, denominator = BigInteger.One;
        if (exponent >= 0) numerator <<= exponent;
        else denominator <<= -exponent;
        if ((bits & 0x8000000000000000UL) != 0) numerator = -numerator;
        return Normalize(numerator, denominator);
    }

    static MapWideRational Normalize(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero) throw new DivideByZeroException("wide rational denominator is zero");
        if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
        BigInteger gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        return gcd.IsOne ? new(numerator, denominator) : new(numerator / gcd, denominator / gcd);
    }

    public MapWideRational Add(MapWideRational other) => Denominator == other.Denominator
        ? Normalize(_numerator + other._numerator, Denominator)
        : Normalize(_numerator * other.Denominator + other._numerator * Denominator, Denominator * other.Denominator);
    public MapWideRational Subtract(MapWideRational other) => Add(other.Negate());
    public MapWideRational Multiply(MapWideRational other) =>
        Normalize(_numerator * other._numerator, Denominator * other.Denominator);
    public MapWideRational Divide(MapWideRational other) =>
        Normalize(_numerator * other.Denominator, Denominator * other._numerator);
    public MapWideRational Negate() => new(-_numerator, Denominator);
    public MapWideRational Abs() => _numerator.Sign < 0 ? Negate() : this;

    /// <summary>Narrows to the bounded stored type. False when the reduced numerator or denominator leaves
    /// <see cref="long"/>, so the caller reports <c>NotRepresentable</c> rather than rounding.</summary>
    public bool TryNarrow(out MapExactValue value)
    {
        BigInteger denominator = Denominator;
        if (_numerator < long.MinValue || _numerator > long.MaxValue || denominator > long.MaxValue)
        {
            value = default;
            return false;
        }
        value = new MapExactValue((long)_numerator, (long)denominator);
        return true;
    }

    public int CompareTo(MapWideRational other) => Denominator == other.Denominator
        ? _numerator.CompareTo(other._numerator)
        : (_numerator * other.Denominator).CompareTo(other._numerator * Denominator);
    public bool Equals(MapWideRational other) =>
        _numerator == other._numerator && _denominatorMinusOne == other._denominatorMinusOne;
    public override bool Equals(object? obj) => obj is MapWideRational other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_numerator, _denominatorMinusOne);
    public override string ToString() => Denominator.IsOne ? _numerator.ToString(CultureInfo.InvariantCulture)
        : _numerator.ToString(CultureInfo.InvariantCulture) + "/" + Denominator.ToString(CultureInfo.InvariantCulture);

    public static MapWideRational Min(MapWideRational a, MapWideRational b) => a.CompareTo(b) <= 0 ? a : b;
    public static MapWideRational Max(MapWideRational a, MapWideRational b) => a.CompareTo(b) >= 0 ? a : b;
}

internal readonly record struct MapWideXz(MapWideRational X, MapWideRational Z) : IComparable<MapWideXz>
{
    public int CompareTo(MapWideXz other) => !Z.Equals(other.Z) ? Z.CompareTo(other.Z) : X.CompareTo(other.X);

    public static MapWideXz From(MapExactXz point) => new(MapWideRational.From(point.X), MapWideRational.From(point.Z));

    public bool TryNarrow(out MapExactXz point)
    {
        if (X.TryNarrow(out MapExactValue x) && Z.TryNarrow(out MapExactValue z))
        {
            point = new(x, z);
            return true;
        }
        point = default;
        return false;
    }
}

internal readonly record struct MapWidePoint(MapWideRational X, MapWideRational Y, MapWideRational Z)
{
    public MapWideXz Xz => new(X, Z);
    public static MapWidePoint From(MapExactPoint point) => new(MapWideRational.From(point.X), MapWideRational.From(point.Y), MapWideRational.From(point.Z));
}
