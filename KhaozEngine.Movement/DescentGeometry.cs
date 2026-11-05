using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>Exact rational predicates over the captured binary coordinates. No epsilon manufactures coverage.</summary>
internal readonly struct DescentNumber : IComparable<DescentNumber>, IEquatable<DescentNumber>
{
    readonly BigInteger _n, _d;
    BigInteger D => _d.IsZero ? BigInteger.One : _d;
    DescentNumber(BigInteger n, BigInteger d)
    {
        if (d.Sign < 0) { n = -n; d = -d; }
        if (d.IsZero) throw new DivideByZeroException();
        BigInteger gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(n), d);
        _n = n / gcd; _d = d / gcd;
    }
    internal DescentNumber(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        long bits = BitConverter.DoubleToInt64Bits(value);
        int exponent = (int)((bits >> 52) & 2047);
        long mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent != 0) mantissa |= 1L << 52;
        int shift = (exponent == 0 ? -1022 : exponent - 1023) - 52;
        BigInteger n = bits < 0 ? -new BigInteger(mantissa) : new BigInteger(mantissa);
        BigInteger numerator = shift < 0 ? n : n << shift;
        BigInteger denominator = shift < 0 ? BigInteger.One << -shift : BigInteger.One;
        BigInteger gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        _n = numerator / gcd;
        _d = denominator / gcd;
    }
    internal int Sign => _n.Sign;
    internal double Value => (double)_n / (double)D;
    public int CompareTo(DescentNumber b) => (_n * b.D).CompareTo(b._n * D);
    public bool Equals(DescentNumber b) => CompareTo(b) == 0;
    public override bool Equals(object? obj) => obj is DescentNumber q && Equals(q);
    public override int GetHashCode() { var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(_n), D); return HashCode.Combine(_n / gcd, D / gcd); }
    public static DescentNumber operator +(DescentNumber a, DescentNumber b) => new(a._n * b.D + b._n * a.D, a.D * b.D);
    public static DescentNumber operator -(DescentNumber a, DescentNumber b) => new(a._n * b.D - b._n * a.D, a.D * b.D);
    public static DescentNumber operator *(DescentNumber a, DescentNumber b) => new(a._n * b._n, a.D * b.D);
    public static DescentNumber operator /(DescentNumber a, DescentNumber b) => new(a._n * b.D, a.D * b._n);
    public static bool operator <(DescentNumber a, DescentNumber b) => a.CompareTo(b) < 0;
    public static bool operator >(DescentNumber a, DescentNumber b) => a.CompareTo(b) > 0;
    public static bool operator <=(DescentNumber a, DescentNumber b) => a.CompareTo(b) <= 0;
    public static bool operator >=(DescentNumber a, DescentNumber b) => a.CompareTo(b) >= 0;
}

internal readonly record struct DescentPoint(DescentNumber X, DescentNumber Z)
{
    internal DescentPoint(double x, double z) : this(new DescentNumber(x), new DescentNumber(z)) { }
    internal static DescentPoint Mix(DescentPoint a, DescentPoint b, DescentNumber t) =>
        new(a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);
}

internal static class DescentGeometry
{
    internal static List<DescentPoint> Project(ReadOnlySpan<PhysicsEvidencePoint> points)
    {
        var result = new List<DescentPoint>(points.Length);
        foreach (var p in points) result.Add(new(p.X, p.Z));
        return result;
    }
    internal static DescentNumber Area2(IReadOnlyList<DescentPoint> polygon)
    {
        DescentNumber area = default;
        for (int i = 0; i < polygon.Count; i++)
        { var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count]; area += a.X * b.Z - b.X * a.Z; }
        return area.Sign < 0 ? default(DescentNumber) - area : area;
    }
    static DescentNumber Cross(DescentPoint a, DescentPoint b, DescentPoint p) =>
        (b.X - a.X) * (p.Z - a.Z) - (b.Z - a.Z) * (p.X - a.X);
    internal static List<DescentPoint> Clip(List<DescentPoint> input, DescentPoint a, DescentPoint b)
    {
        var output = new List<DescentPoint>();
        if (input.Count == 0) return output;
        var previous = input[^1];
        var last = Cross(a, b, previous);
        foreach (var point in input)
        {
            var current = Cross(a, b, point);
            if ((last.Sign < 0) != (current.Sign < 0)) output.Add(DescentPoint.Mix(previous, point, last / (last - current)));
            if (current.Sign >= 0) output.Add(point);
            previous = point; last = current;
        }
        return output;
    }
    internal static List<DescentPoint> Cell(ReadOnlySpan<PhysicsEvidencePoint> points, double x, double z, double size)
    {
        var polygon = Project(points);
        polygon = Clip(polygon, new(x, z), new(x + size, z));
        polygon = Clip(polygon, new(x + size, z), new(x + size, z + size));
        polygon = Clip(polygon, new(x + size, z + size), new(x, z + size));
        return Clip(polygon, new(x, z + size), new(x, z));
    }
    internal static List<DescentPoint> Overlap(List<DescentPoint> first, List<DescentPoint> second)
    {
        if (second.Count < 3) return [];
        DescentNumber signed = default;
        for (int i = 0; i < second.Count; i++)
        { var a = second[i]; var b = second[(i + 1) % second.Count]; signed += a.X * b.Z - b.X * a.Z; }
        var result = first;
        for (int i = 0; i < second.Count; i++)
        {
            var a = second[i]; var b = second[(i + 1) % second.Count];
            result = signed.Sign >= 0 ? Clip(result, a, b) : Clip(result, b, a);
        }
        return result;
    }
    internal static bool Contains(ReadOnlySpan<PhysicsEvidencePoint> points, double x, double z) =>
        ContainsPoint(Project(points), new(x, z));
    static bool ContainsPoint(List<DescentPoint> polygon, DescentPoint point)
    {
        int side = 0;
        for (int i = 0; i < polygon.Count; i++)
        { int sign = Cross(polygon[i], polygon[(i + 1) % polygon.Count], point).Sign; if (sign == 0) continue; if (side != 0 && side != sign) return false; side = sign; }
        return side != 0;
    }
    internal static DescentNumber Height(ReadOnlySpan<PhysicsEvidencePoint> points, DescentPoint p)
    {
        var a = points[0]; var b = points[1]; var c = points[2];
        var x = new DescentNumber(a.X); var z = new DescentNumber(a.Z); var y = new DescentNumber(a.Y);
        var ux = new DescentNumber(b.X) - x; var uz = new DescentNumber(b.Z) - z; var uy = new DescentNumber(b.Y) - y;
        var vx = new DescentNumber(c.X) - x; var vz = new DescentNumber(c.Z) - z; var vy = new DescentNumber(c.Y) - y;
        var determinant = ux * vz - uz * vx;
        return y + ((p.X - x) * vz - (p.Z - z) * vx) / determinant * uy +
            (ux * (p.Z - z) - uz * (p.X - x)) / determinant * vy;
    }
}
