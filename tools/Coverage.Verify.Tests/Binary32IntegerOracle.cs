using System;
using System.Numerics;

namespace KhaozEngine.Tests.Tools.Coverage;

internal static class Binary32IntegerOracle
{
    // Integer units of 2^-149. Products and determinants therefore have units of 2^-298.
    internal static BigInteger Integer(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        int exponent = (int)((bits >> 23) & 255);
        BigInteger significand = bits & 0x7fffff;
        if (exponent != 0) significand = (significand + (1 << 23)) << (exponent - 1);
        return (bits & 0x80000000) == 0 ? significand : -significand;
    }

    internal static BigInteger Edge(Vector3 a, Vector3 b, Vector3 p) =>
        ((Integer(b.X) - Integer(a.X)) * (Integer(p.Y) - Integer(a.Y)))
        - ((Integer(b.Y) - Integer(a.Y)) * (Integer(p.X) - Integer(a.X)));

    internal static float Round(BigInteger exact)
    {
        BigInteger magnitude = BigInteger.Abs(exact);
        int shift = Math.Max(149, (int)magnitude.GetBitLength() - 24);
        BigInteger significand = magnitude >> shift;
        BigInteger remainder = magnitude - (significand << shift), half = BigInteger.One << (shift - 1);
        if (remainder > half || remainder == half && !significand.IsEven) significand++;
        float rounded = MathF.ScaleB((float)(int)significand, shift - 298);
        return exact.Sign < 0 ? -rounded : rounded;
    }
}
