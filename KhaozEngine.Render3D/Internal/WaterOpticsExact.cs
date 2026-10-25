using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Construction-only exact arithmetic. Every binary32 value is an integer multiple of 2^-149.</summary>
internal readonly struct WaterOpticsExact : IEquatable<WaterOpticsExact>, IComparable<WaterOpticsExact>
{
    private readonly BigInteger _numerator;
    private readonly BigInteger _denominator;
    private BigInteger Denominator => _denominator.IsZero ? BigInteger.One : _denominator;

    internal WaterOpticsExact(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero) throw new DivideByZeroException();
        if (denominator.Sign < 0) (numerator, denominator) = (-numerator, -denominator);
        BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        _numerator = numerator / divisor;
        _denominator = denominator / divisor;
    }

    internal int Sign => _numerator.Sign;
    internal static WaterOpticsExact Abs(WaterOpticsExact value) => value.Sign < 0 ? -value : value;
    internal static WaterOpticsExact Max(WaterOpticsExact a, WaterOpticsExact b) => a.CompareTo(b) >= 0 ? a : b;
    internal static WaterOpticsExact Min(WaterOpticsExact a, WaterOpticsExact b) => a.CompareTo(b) <= 0 ? a : b;

    internal static BigInteger FloatInteger(float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        int exponent = (bits >> 23) & 255;
        int significand = bits & 0x7fffff;
        if (exponent != 0) significand |= 1 << 23;
        BigInteger integer = (BigInteger)significand << Math.Max(0, exponent - 1);
        return bits < 0 ? -integer : integer;
    }

    internal static WaterOpticsExact From(float value) => new(FloatInteger(value), BigInteger.One << 149);
    public static implicit operator WaterOpticsExact(int value) => new(value, BigInteger.One);
    public static WaterOpticsExact operator +(WaterOpticsExact a, WaterOpticsExact b) =>
        new(a._numerator * b.Denominator + b._numerator * a.Denominator, a.Denominator * b.Denominator);
    public static WaterOpticsExact operator -(WaterOpticsExact a) => new(-a._numerator, a.Denominator);
    public static WaterOpticsExact operator -(WaterOpticsExact a, WaterOpticsExact b) => a + -b;
    public static WaterOpticsExact operator *(WaterOpticsExact a, WaterOpticsExact b) =>
        new(a._numerator * b._numerator, a.Denominator * b.Denominator);
    public static WaterOpticsExact operator /(WaterOpticsExact a, WaterOpticsExact b) =>
        new(a._numerator * b.Denominator, a.Denominator * b._numerator);

    public int CompareTo(WaterOpticsExact other) => (_numerator * other.Denominator).CompareTo(other._numerator * Denominator);
    public bool Equals(WaterOpticsExact other) => _numerator == other._numerator && Denominator == other.Denominator;
    public override bool Equals(object? obj) => obj is WaterOpticsExact other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_numerator, Denominator);
}

internal readonly record struct WaterOpticsExactPoint(WaterOpticsExact X, WaterOpticsExact Z)
{
    internal static WaterOpticsExactPoint From(Vector3 vertex) => new(WaterOpticsExact.From(vertex.X), WaterOpticsExact.From(vertex.Z));
    internal static WaterOpticsExact Cross(WaterOpticsExactPoint a, WaterOpticsExactPoint b, WaterOpticsExactPoint c) =>
        (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
}

/// <summary>Temporary exact geometry and height plane used by both coverage and error certification.</summary>
internal sealed class WaterOpticsExactTriangle
{
    internal readonly WaterOpticsTriangle Source;
    internal readonly WaterOpticsExactPoint A, B, C;
    internal readonly WaterOpticsExact Determinant, SlopeX, SlopeZ, Intercept;

    internal WaterOpticsExactTriangle(WaterOpticsTriangle triangle)
    {
        Source = triangle;
        A = WaterOpticsExactPoint.From(triangle.A);
        B = WaterOpticsExactPoint.From(triangle.B);
        C = WaterOpticsExactPoint.From(triangle.C);
        Determinant = WaterOpticsExactPoint.Cross(A, B, C);
        if (Determinant.Sign == 0) throw new ArgumentException("Projected triangles must have nonzero area.", nameof(triangle));
        WaterOpticsExact qa = WaterOpticsExact.From(triangle.A.Y);
        WaterOpticsExact db = WaterOpticsExact.From(triangle.B.Y) - qa;
        WaterOpticsExact dc = WaterOpticsExact.From(triangle.C.Y) - qa;
        SlopeX = (db * (C.Z - A.Z) - dc * (B.Z - A.Z)) / Determinant;
        SlopeZ = ((B.X - A.X) * dc - (C.X - A.X) * db) / Determinant;
        Intercept = qa - SlopeX * A.X - SlopeZ * A.Z;
    }

    internal WaterOpticsExact Height(WaterOpticsExactPoint p) => SlopeX * p.X + SlopeZ * p.Z + Intercept;
    internal bool SamePlane(WaterOpticsExactTriangle other) =>
        SlopeX.Equals(other.SlopeX) && SlopeZ.Equals(other.SlopeZ) && Intercept.Equals(other.Intercept);
}
