using System;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Fixed-capacity numeric workspace admitted with one prepared membership buffer.</summary>
internal sealed class MapMembershipExact
{
    internal const int RegisterCount = 24;
    internal const long StorageBytes = RegisterCount * MapMembershipInteger.Words * sizeof(uint) + 64L;
    readonly uint[] _words = new uint[RegisterCount * MapMembershipInteger.Words];
    internal bool NotRepresentable;
    MapMembershipInteger R(int i) => new(_words.AsSpan(i * MapMembershipInteger.Words, MapMembershipInteger.Words));

    // Preparation can certify a triangle before allocating any retained numeric workspace. Clearing the six
    // coordinate denominators gives six degree-six terms, at most 6 * 64 + 3 magnitude bits.
    internal static int Orientation(MapExactXz a, MapExactXz b, MapExactXz c)
    {
        var ax = MapMembershipInterval.Point(a.X); var az = MapMembershipInterval.Point(a.Z);
        var bx = MapMembershipInterval.Point(b.X); var bz = MapMembershipInterval.Point(b.Z);
        var cx = MapMembershipInterval.Point(c.X); var cz = MapMembershipInterval.Point(c.Z);
        var interval = bx.Add(ax.Negate()).Multiply(cz.Add(az.Negate()))
            .Add(bz.Add(az.Negate()).Multiply(cx.Add(ax.Negate())).Negate());
        if (interval.Low > 0) return 1;
        if (interval.High < 0) return -1;
        Span<MapExactValue> values = stackalloc MapExactValue[] { a.X, a.Z, b.X, b.Z, c.X, c.Z };
        ReadOnlySpan<int> terms = [0, 3, 2, 5, 4, 1, 1, 2, 3, 4, 5, 0];
        Span<uint> words = stackalloc uint[5 * MapMembershipInteger.Words];
        var sum = new MapMembershipInteger(words[..MapMembershipInteger.Words]); sum.Clear();
        var next = new MapMembershipInteger(words.Slice(MapMembershipInteger.Words, MapMembershipInteger.Words));
        var term = new MapMembershipInteger(words.Slice(2 * MapMembershipInteger.Words, MapMembershipInteger.Words));
        var product = new MapMembershipInteger(words.Slice(3 * MapMembershipInteger.Words, MapMembershipInteger.Words));
        var factor = new MapMembershipInteger(words.Slice(4 * MapMembershipInteger.Words, MapMembershipInteger.Words));
        for (int t = 0; t < 6; t++)
        {
            term.Set(t < 3 ? 1 : -1);
            for (int i = 0; i < 6; i++)
            {
                factor.Set(i == terms[2 * t] || i == terms[2 * t + 1] ? values[i].Numerator : values[i].Denominator);
                MapMembershipInteger.Multiply(term, factor, product);
                product.CopyTo(term);
            }
            MapMembershipInteger.Add(sum, term, next); next.CopyTo(sum);
        }
        return sum.Sign;
    }

    internal void Evaluate(MapMembershipAffine affine, MapExactXz point)
    {
        R(0).Set(point.X.Numerator); R(1).Set(point.X.Denominator);
        R(2).Set(point.Z.Numerator); R(3).Set(point.Z.Denominator);
        affine.Load(0, R(4)); affine.Load(1, R(5)); affine.Load(2, R(6));
        MapMembershipInteger.Multiply(R(4), R(0), R(10));
        MapMembershipInteger.Multiply(R(10), R(3), R(11));
        MapMembershipInteger.Multiply(R(5), R(2), R(10));
        MapMembershipInteger.Multiply(R(10), R(1), R(12));
        MapMembershipInteger.Add(R(11), R(12), R(13));
        MapMembershipInteger.Multiply(R(1), R(3), R(7));
        MapMembershipInteger.Multiply(R(6), R(7), R(10));
        MapMembershipInteger.Add(R(13), R(10), R(8));
        affine.Load(3, R(4));
        MapMembershipInteger.Multiply(R(4), R(7), R(9));
    }
    internal int Sign(MapMembershipAffine affine, MapExactXz point)
    {
        int? filtered = affine.Filter(point);
        if (filtered is { } sign) return sign;
        Evaluate(affine, point);
        return R(8).Sign;
    }
    internal double ValueDouble(MapMembershipAffine affine, MapExactXz point)
    {
        Evaluate(affine, point);
        if (R(8).Sign == 0) return 0;
        Gcd();
        DivideExact(R(8), R(20), R(16));
        DivideExact(R(9), R(20), R(17));
        return R(16).ToDouble() / R(17).ToDouble();
    }
    internal bool TryValue(MapMembershipAffine affine, MapExactXz point, out MapExactValue value)
    {
        if (affine.Constant is { } constant) { value = constant; return true; }
        Evaluate(affine, point);
        if (R(8).TryInt64(out long smallN) && R(9).TryInt64(out long smallD))
        { value = new(smallN, smallD); return true; }
        int sign = R(8).Sign;
        if (sign == 0) { value = default; return true; }
        Gcd();
        if (!DivideInt64(R(8), R(20), sign < 0, out long n) || !DivideInt64(R(9), R(20), false, out long d))
        { value = default; return false; }
        value = new(n, d);
        return true;
    }
    void Gcd()
    {
        R(8).CopyTo(R(18)); R(18).Sign = 1;
        R(9).CopyTo(R(19)); R(19).Sign = 1;
        var a = R(18); var b = R(19);
        int common = Math.Min(a.TrailingZeros, b.TrailingZeros);
        a.ShiftRight(a.TrailingZeros);
        while (b.Sign != 0)
        {
            b.ShiftRight(b.TrailingZeros);
            if (MapMembershipInteger.CompareMagnitude(a, b) > 0) { var swap = a; a = b; b = swap; }
            MapMembershipInteger.SubtractMagnitude(b, a, b);
        }
        MapMembershipInteger.ShiftLeft(a, common, R(20));
    }
    void DivideExact(MapMembershipInteger numerator, MapMembershipInteger divisor, MapMembershipInteger quotient)
    {
        quotient.Clear();
        int bits = numerator.BitLength - divisor.BitLength;
        numerator.CopyTo(R(21)); R(21).Sign = 1;
        for (int i = bits; i >= 0; i--)
        {
            MapMembershipInteger.ShiftLeft(divisor, i, R(22));
            if (MapMembershipInteger.CompareMagnitude(R(21), R(22)) < 0) continue;
            MapMembershipInteger.SubtractMagnitude(R(21), R(22), R(21));
            quotient.SetBit(i);
        }
        quotient.Length = Math.Max(0, (bits + 32) / 32);
        quotient.Sign = numerator.Sign;
        quotient.Trim();
    }
    bool DivideInt64(MapMembershipInteger numerator, MapMembershipInteger divisor, bool negative, out long value)
    {
        value = 0;
        int bits = numerator.BitLength - divisor.BitLength;
        if (bits > 63) return false;
        numerator.CopyTo(R(21)); R(21).Sign = numerator.Length == 0 ? 0 : 1;
        ulong quotient = 0;
        for (int i = bits; i >= 0; i--)
        {
            MapMembershipInteger.ShiftLeft(divisor, i, R(22));
            if (MapMembershipInteger.CompareMagnitude(R(21), R(22)) < 0) continue;
            MapMembershipInteger.SubtractMagnitude(R(21), R(22), R(21));
            quotient |= 1UL << i;
        }
        if (R(21).Sign != 0 || quotient > (negative ? 1UL << 63 : long.MaxValue)) return false;
        value = negative ? unchecked(-(long)quotient) : (long)quotient;
        return true;
    }
}

/// <summary>Outward-rounded interval operations. A filter answers only when zero is excluded.</summary>
internal readonly record struct MapMembershipInterval(double Low, double High)
{
    internal static MapMembershipInterval Uncertain => new(double.NegativeInfinity, double.PositiveInfinity);
    internal static MapMembershipInterval Integer(long value)
    {
        double d = value;
        Int128 represented = (Int128)d;
        return new(represented > value ? Math.BitDecrement(d) : d, represented < value ? Math.BitIncrement(d) : d);
    }
    internal static MapMembershipInterval Point(MapExactValue value)
    {
        var n = Integer(value.Numerator); var d = Integer(value.Denominator);
        double low = value.Sign < 0 ? n.Low / d.Low : n.Low / d.High;
        double high = value.Sign < 0 ? n.High / d.High : n.High / d.Low;
        return Enclose(low, high);
    }
    static MapMembershipInterval Enclose(double low, double high) => double.IsNaN(low) || double.IsNaN(high)
        ? Uncertain : new(Math.BitDecrement(low), Math.BitIncrement(high));
    internal MapMembershipInterval Add(MapMembershipInterval other) => Enclose(Low + other.Low, High + other.High);
    internal MapMembershipInterval Negate() => new(-High, -Low);
    internal MapMembershipInterval Multiply(MapMembershipInterval other)
    {
        double a = Low * other.Low, b = Low * other.High, c = High * other.Low, d = High * other.High;
        return Enclose(Math.Min(Math.Min(a, b), Math.Min(c, d)), Math.Max(Math.Max(a, b), Math.Max(c, d)));
    }
}

/// <summary>Cleared-denominator affine numerator, with a positive constant denominator for heights.</summary>
internal readonly struct MapMembershipAffine
{
    readonly MapMembershipCoefficientStore _store;
    readonly int _first;
    internal MapMembershipCoefficientStore Store => _store;
    internal MapMembershipAffine(MapMembershipCoefficientStore store, int first) { _store = store; _first = first; }
    internal MapMembershipCoefficient X => _store[_first];
    internal MapMembershipCoefficient Z => _store[_first + 1];
    internal MapMembershipCoefficient C => _store[_first + 2];
    internal MapMembershipCoefficient D => _store[_first + 3];
    internal MapExactValue? Constant => X.Sign == 0 && Z.Sign == 0 && C.Kind == 0 && D.Kind == 0
        ? new MapExactValue(C.Inline, D.Inline) : null;
    internal int Direction => X.Sign != 0 ? X.Sign : Z.Sign;
    internal void Load(int coefficient, MapMembershipInteger target) => _store.Load(_store[_first + coefficient], target);
    internal MapMembershipAffine(BigInteger x, BigInteger z, BigInteger c, BigInteger d)
    {
        using var arithmetic = MapMembershipBuild.Kernel();
        if (d.IsZero) throw new MapDocumentException("invalid prepared plane");
        if (d.Sign < 0) { x = -x; z = -z; c = -c; d = -d; }
        BigInteger gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(x), BigInteger.Abs(z));
        gcd = BigInteger.GreatestCommonDivisor(gcd, BigInteger.Abs(c));
        gcd = BigInteger.GreatestCommonDivisor(gcd, d);
        x /= gcd; z /= gcd; c /= gcd; d /= gcd;
        _store = MapMembershipCoefficientStore.Current ?? MapMembershipCoefficientStore.Create(4);
        _first = _store.Count;
        _store.Append(x); _store.Append(z); _store.Append(c); _store.Append(d);
    }
    internal int? Filter(MapExactXz point)
    {
        var interval = _store.Interval(X).Multiply(MapMembershipInterval.Point(point.X))
            .Add(_store.Interval(Z).Multiply(MapMembershipInterval.Point(point.Z))).Add(_store.Interval(C));
        return interval.Low > 0 ? 1 : interval.High < 0 ? -1 : null;
    }
    internal static BigInteger Lcm(BigInteger a, BigInteger b) => a / BigInteger.GreatestCommonDivisor(a, b) * b;
    internal static MapMembershipAffine Edge(MapExactXz a, MapExactXz b)
    {
        using var arithmetic = MapMembershipBuild.Kernel();
        BigInteger l = Lcm(Lcm(a.X.Denominator, a.Z.Denominator), Lcm(b.X.Denominator, b.Z.Denominator));
        BigInteger ax = a.X.Numerator * (l / a.X.Denominator), az = a.Z.Numerator * (l / a.Z.Denominator);
        BigInteger bx = b.X.Numerator * (l / b.X.Denominator), bz = b.Z.Numerator * (l / b.Z.Denominator);
        BigInteger dx = bx - ax, dz = bz - az;
        return new(-dz * l, dx * l, dz * ax - dx * az, BigInteger.One);
    }
    internal static MapMembershipAffine Plane(MapExactTriangle triangle)
    {
        using var arithmetic = MapMembershipBuild.Kernel();
        MapExactPoint a = triangle.A, b = triangle.B, c = triangle.C;
        BigInteger l = BigInteger.One;
        foreach (var p in new[] { a, b, c })
        { l = Lcm(l, p.X.Denominator); l = Lcm(l, p.Y.Denominator); l = Lcm(l, p.Z.Denominator); }
        BigInteger ax = a.X.Numerator * (l / a.X.Denominator), ay = a.Y.Numerator * (l / a.Y.Denominator), az = a.Z.Numerator * (l / a.Z.Denominator);
        BigInteger bx = b.X.Numerator * (l / b.X.Denominator), by = b.Y.Numerator * (l / b.Y.Denominator), bz = b.Z.Numerator * (l / b.Z.Denominator);
        BigInteger cx = c.X.Numerator * (l / c.X.Denominator), cy = c.Y.Numerator * (l / c.Y.Denominator), cz = c.Z.Numerator * (l / c.Z.Denominator);
        BigInteger nx = (by - ay) * (cz - az) - (bz - az) * (cy - ay);
        BigInteger ny = (bz - az) * (cx - ax) - (bx - ax) * (cz - az);
        BigInteger nz = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        return new(-nx * l, -nz * l, nx * ax + ny * ay + nz * az, ny * l);
    }
}
