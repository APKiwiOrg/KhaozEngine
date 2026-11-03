using System;
using System.Numerics;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>Cold exact rank decisions for represented halfspace normals. No determinant threshold.</summary>
internal static class TileWaterLinearRank
{
    readonly record struct Dyadic(BigInteger Mantissa, int Exponent)
    {
        internal static Dyadic From(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            int exponent = (int)((bits >> 52) & 2047);
            long mantissa = bits & 0xFFFFFFFFFFFFFL;
            if (exponent != 0) mantissa |= 1L << 52;
            return new(bits < 0 ? -mantissa : mantissa, exponent == 0 ? -1074 : exponent - 1075);
        }
        public static Dyadic operator +(Dyadic a, Dyadic b)
        {
            int exponent = Math.Min(a.Exponent, b.Exponent);
            return new((a.Mantissa << (a.Exponent - exponent)) + (b.Mantissa << (b.Exponent - exponent)), exponent);
        }
        public static Dyadic operator -(Dyadic a, Dyadic b) => a + new Dyadic(-b.Mantissa, b.Exponent);
        public static Dyadic operator *(Dyadic a, Dyadic b) => new(a.Mantissa * b.Mantissa, a.Exponent + b.Exponent);
    }

    internal static bool Independent(ReadOnlySpan<TileWaterConvexShape.Plane> planes, int a, int b, int c)
    {
        var x = planes[a].Normal;
        if (b < 0) return x.X.Low != 0 || x.Y.Low != 0 || x.Z.Low != 0;
        var y = planes[b].Normal;
        var cross = TileWaterConvexShape.Vector.Cross(x, y);
        if (c < 0)
        {
            if (cross.X.Low > 0 || cross.X.High < 0 || cross.Y.Low > 0 || cross.Y.High < 0 ||
                cross.Z.Low > 0 || cross.Z.High < 0) return true;
            if (cross == default) return false;
            return !(D(x.Y.Low) * D(y.Z.Low) - D(x.Z.Low) * D(y.Y.Low)).Mantissa.IsZero ||
                !(D(x.Z.Low) * D(y.X.Low) - D(x.X.Low) * D(y.Z.Low)).Mantissa.IsZero ||
                !(D(x.X.Low) * D(y.Y.Low) - D(x.Y.Low) * D(y.X.Low)).Mantissa.IsZero;
        }
        var z = planes[c].Normal;
        var determinant = TileWaterConvexShape.Vector.Dot(cross, z);
        if (determinant.Low > 0 || determinant.High < 0) return true;
        if (determinant.Low == 0 && determinant.High == 0) return false;
        Dyadic cx = D(x.Y.Low) * D(y.Z.Low) - D(x.Z.Low) * D(y.Y.Low);
        Dyadic cy = D(x.Z.Low) * D(y.X.Low) - D(x.X.Low) * D(y.Z.Low);
        Dyadic cz = D(x.X.Low) * D(y.Y.Low) - D(x.Y.Low) * D(y.X.Low);
        return !(cx * D(z.X.Low) + cy * D(z.Y.Low) + cz * D(z.Z.Low)).Mantissa.IsZero;
    }

    static Dyadic D(double value) => Dyadic.From(value);
}
