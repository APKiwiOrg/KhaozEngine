using System;
using System.Numerics;

namespace KhaozEngine.Tests.Gpu;

// Test-only rational arithmetic. No floating arithmetic or production exact-word helpers.
internal static class CoverageBinary32IntegerOracle
{
    internal const uint Infinity = 0x7f800000u, Invalid = 0xffc00000u;
    internal static uint Flush(uint word) => (word & 0x7fffffffu) <= 0x00800000u ? word & 0x80000000u : word;

    internal static BigInteger Units(uint word)
    {
        uint magnitude = word & 0x7fffffffu;
        int exponent = (int)(magnitude >> 23);
        return exponent == 0 ? magnitude : new BigInteger((magnitude & 0x7fffffu) | 0x800000u) << (exponent - 1);
    }

    internal static uint Round(BigInteger magnitude, int exponent, int sign)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(magnitude.Sign);
        uint signBit = sign < 0 ? 0x80000000u : 0u;
        if (magnitude.IsZero) return signBit;
        int leading = checked((int)magnitude.GetBitLength() - 1 + exponent);
        if (leading > 127) return signBit | Infinity;
        int quantum = Math.Max(leading - 23, -149), shift = quantum - exponent;
        BigInteger rounded;
        if (shift <= 0) rounded = magnitude << -shift;
        else
        {
            rounded = BigInteger.DivRem(magnitude, BigInteger.One << shift, out BigInteger remainder);
            int midpoint = (remainder << 1).CompareTo(BigInteger.One << shift);
            if (midpoint > 0 || (midpoint == 0 && !rounded.IsEven)) rounded++;
        }
        if (rounded.IsZero) return signBit;
        if (leading < -126) return signBit | (uint)rounded;
        if (rounded == (BigInteger.One << 24)) { rounded >>= 1; leading++; }
        return signBit | (leading > 127 ? Infinity : (uint)(leading + 127) << 23 | ((uint)rounded & 0x7fffffu));
    }

    internal static uint Divide(uint a, uint b)
    {
        a = Flush(a); b = Flush(b);
        uint x = a & 0x7fffffffu, y = b & 0x7fffffffu, sign = (a ^ b) & 0x80000000u;
        if (x > Infinity) return a | 0x00400000u;
        if (y > Infinity) return b | 0x00400000u;
        if ((x == 0 && y == 0) || (x == Infinity && y == Infinity)) return Invalid;
        if (x == Infinity || y == 0) return sign | Infinity;
        if (x == 0 || y == Infinity) return sign;
        // A 600-bit fixed-point quotient has a remainder sticky bit. This retains every
        // rounding decision down to 2^-150 even for the smallest/largest finite ratio.
        BigInteger quotient = BigInteger.DivRem(Units(x) << 600, Units(y), out BigInteger remainder);
        if (!remainder.IsZero) quotient |= BigInteger.One;
        return Round(quotient, -600, sign == 0 ? 1 : -1);
    }

    internal static uint Sqrt(uint a)
    {
        a = Flush(a);
        uint magnitude = a & 0x7fffffffu;
        if (magnitude > Infinity) return a | 0x00400000u;
        if (magnitude == 0) return a;
        if ((a & 0x80000000u) != 0) return Invalid;
        if (magnitude == Infinity) return Infinity;
        // sqrt(A * 2^-149) = sqrt(A << 451) * 2^-300. Integer Newton,
        // followed by a sticky bit, is independent of the device's midpoint search.
        BigInteger radicand = Units(a) << 451;
        BigInteger root = BigInteger.One << (int)((radicand.GetBitLength() + 1) / 2);
        while (true)
        {
            BigInteger next = (root + radicand / root) >> 1;
            if (next >= root) break;
            root = next;
        }
        if (root * root != radicand) root |= BigInteger.One;
        return Round(root, -300, 1);
    }
}
