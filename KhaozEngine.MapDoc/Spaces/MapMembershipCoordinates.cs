using System;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Non-throwing bounded rational conversions for prepared input checks and observer motion.</summary>
internal static class MapMembershipCoordinates
{
    internal static bool Observer(MapFramePoint point, out MapExactPoint world)
    {
        world = default;
        if (!Single(point.Local.X, out var x) || !Single(point.Local.Z, out var z) || !Single(point.Local.Y, out var y) ||
            !Add(new((long)point.Frame.X * (long)WorldFrame.Grid, 1), x, out x) ||
            !Add(new((long)point.Frame.Z * (long)WorldFrame.Grid, 1), z, out z)) return false;
        world = new(x, y, z); return true;
    }
    internal static bool Freeze(double value, out MapExactValue exact)
    {
        exact = default;
        if (!double.IsFinite(value) || Math.Abs(value) > MapResourceTransform.MaxCoordinateMetres) return false;
        exact = new((long)Math.Round(value * MapResourceTransform.Quantum, MidpointRounding.ToEven), MapResourceTransform.Quantum);
        return true;
    }
    internal static bool Xz(MapEffectiveTransform? pose, MapExactXz local, out MapExactXz point)
    {
        point = local;
        if (pose is not { } p) return true;
        var (s, c) = Math.SinCos(p.YawRadians);
        double x = local.X.ToDouble() * p.Scale, z = local.Z.ToDouble() * p.Scale;
        if (!Freeze(p.X + c * x + s * z, out var wx) || !Freeze(p.Z - s * x + c * z, out var wz)) return false;
        point = new(wx, wz); return true;
    }
    internal static bool Height(MapEffectiveTransform? pose, MapExactValue local, out MapExactValue height)
    {
        height = local;
        if (pose is not { } p) return true;
        if (!Freeze(p.Y + local.ToDouble() * p.Scale, out var frozen)) return false;
        return Single(frozen.ToSingle(), out height);
    }
    internal static bool Point(MapEffectiveTransform? pose, MapExactPoint local, out MapExactPoint point)
    {
        point = default;
        if (!Xz(pose, new(local.X, local.Z), out var xz) || !Height(pose, local.Y, out var height)) return false;
        point = new(xz.X, height, xz.Z); return true;
    }
    internal static bool Single(float value, out MapExactValue exact)
    {
        exact = default;
        if (!float.IsFinite(value)) return false;
        uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(value)), significand = bits & 0x7fffff;
        int exponent = (int)((bits >> 23) & 255);
        if (exponent != 0) significand |= 0x800000;
        if (significand == 0) return true;
        exponent = exponent == 0 ? -149 : exponent - 150;
        while ((significand & 1) == 0) { significand >>= 1; exponent++; }
        if (exponent < -62 || exponent > 63) return false;
        Int128 n = significand, d = 1;
        if (exponent >= 0) n <<= exponent; else d <<= -exponent;
        return Rational((bits & 0x80000000) != 0 ? -n : n, d, out exact);
    }
    internal static bool World(MapLatticeFrame frame, MapLatticeAddress address, out MapExactXz point)
    {
        point = default;
        if (address.Denominator is < 1 or > 64 || !Rational((Int128)address.X * frame.CellUnitMetres.Numerator,
            (Int128)address.Denominator * frame.CellUnitMetres.Denominator, out var x) ||
            !Rational((Int128)address.Z * frame.CellUnitMetres.Numerator * (frame.RowDirection == MapRowDirection.NegativeZ ? -1 : 1),
                (Int128)address.Denominator * frame.CellUnitMetres.Denominator, out var z)) return false;
        point = new(x, z); return true;
    }
    internal static bool Rational(Int128 n, Int128 d, out MapExactValue value)
    {
        value = default;
        if (d <= 0) return false;
        Int128 gcd = MapExactValue.Gcd(n, d); n /= gcd; d /= gcd;
        if (n < long.MinValue || n > long.MaxValue || d > long.MaxValue) return false;
        value = new((long)n, (long)d); return true;
    }
    internal static bool Add(MapExactValue a, MapExactValue b, out MapExactValue value, bool subtract = false)
    {
        Int128 left = (Int128)a.Numerator * b.Denominator, right = (Int128)b.Numerator * a.Denominator;
        if (subtract) right = -right;
        value = default;
        if (right > 0 && left > Int128.MaxValue - right || right < 0 && left < Int128.MinValue - right) return false;
        return Rational(left + right, (Int128)a.Denominator * b.Denominator, out value);
    }
    internal static bool Divide(MapExactValue value, int denominator, out MapExactValue result) => Rational(value.Numerator, (Int128)value.Denominator * denominator, out result);
}
