using System;
using System.Numerics;
using SweepInteger = KhaozEngine.Physics.Bepu.SweepExactWorkspace.Integer;

namespace KhaozEngine.Physics.Bepu;

internal static partial class BoundedGeometryArithmetic
{
    // Every shift, carry and multiplication is checked before writing a 4097th bit.
    internal static bool TryAddSweepInteger(SweepInteger a, SweepInteger b, out SweepInteger result)
    {
        result = default;
        if (a.Sign == 0) { result = b; return true; }
        if (b.Sign == 0) { result = a; return true; }
        int exponent = Math.Min(a.Exponent, b.Exponent);
        long shiftA = (long)a.Exponent - exponent, shiftB = (long)b.Exponent - exponent;
        long required = Math.Max(SweepBits(a) + shiftA, SweepBits(b) + shiftB);
        if (required + (a.Sign == b.Sign ? 1 : 0) > MaximumPredicateBits) return false;
        int sa = (int)shiftA, sb = (int)shiftB, bits = (int)required;
        int length = (bits + 31) / 32;
        int comparison = 0;
        for (int i = length - 1; i >= 0 && comparison == 0; i--)
            comparison = Shifted(a, sa, i).CompareTo(Shifted(b, sb, i));
        if (a.Sign != b.Sign && comparison == 0) return true;
        result.Sign = a.Sign == b.Sign || comparison > 0 ? a.Sign : b.Sign;
        result.Exponent = exponent;
        ulong carry = 0;
        for (int i = 0; i < length; i++)
        {
            uint av = Shifted(a, sa, i), bv = Shifted(b, sb, i);
            if (a.Sign == b.Sign)
            {
                ulong sum = (ulong)av + bv + carry;
                result.Digits[i] = (uint)sum;
                carry = sum >> 32;
            }
            else
            {
                ulong larger = comparison > 0 ? av : bv;
                ulong smaller = (comparison > 0 ? (ulong)bv : av) + carry;
                result.Digits[i] = unchecked((uint)(larger - smaller));
                carry = larger < smaller ? 1UL : 0;
            }
        }
        result.Length = length;
        if (a.Sign == b.Sign && carry != 0)
        {
            if (length == 128) return false;
            result.Digits[result.Length++] = (uint)carry;
        }
        return TryNormalizeSweepInteger(ref result);
    }

    internal static bool TryMultiplySweepInteger(SweepInteger a, SweepInteger b, out SweepInteger result)
    {
        result = default;
        if (a.Sign == 0 || b.Sign == 0) return true;
        if (SweepBits(a) + SweepBits(b) > MaximumPredicateBits) return false;
        long exponent = (long)a.Exponent + b.Exponent;
        if (exponent < int.MinValue || exponent > int.MaxValue) return false;
        result.Sign = a.Sign * b.Sign;
        result.Exponent = (int)exponent;
        for (int i = 0; i < a.Length; i++)
        {
            ulong carry = 0;
            for (int j = 0; j < b.Length; j++)
            {
                ulong product = (ulong)a.Digits[i] * b.Digits[j] + result.Digits[i + j] + carry;
                result.Digits[i + j] = (uint)product;
                carry = product >> 32;
            }
            if (carry != 0)
            {
                if (i + b.Length >= 128) return false;
                result.Digits[i + b.Length] = (uint)carry;
            }
        }
        result.Length = Math.Min(128, a.Length + b.Length);
        return TryNormalizeSweepInteger(ref result);
    }

    static int SweepBits(SweepInteger value) => value.Length == 0 ? 0 :
        32 * (value.Length - 1) + 32 - BitOperations.LeadingZeroCount(value.Digits[value.Length - 1]);

    static uint Shifted(SweepInteger value, int shift, int digit)
    {
        int index = digit - shift / 32, offset = shift % 32;
        uint low = index >= 0 && index < value.Length ? value.Digits[index] : 0;
        uint high = index > 0 && index <= value.Length ? value.Digits[index - 1] : 0;
        return offset == 0 ? low : low << offset | high >> (32 - offset);
    }

    internal static bool TryNormalizeSweepInteger(ref SweepInteger value)
    {
        while (value.Length > 0 && value.Digits[value.Length - 1] == 0) value.Length--;
        if (value.Length == 0) { value = default; return true; }
        int words = 0;
        while (value.Digits[words] == 0) words++;
        int shift = BitOperations.TrailingZeroCount(value.Digits[words]);
        long exponent = (long)value.Exponent + words * 32 + shift;
        if (exponent < int.MinValue || exponent > int.MaxValue) return false;
        int length = value.Length - words;
        for (int i = 0; i < length; i++)
        {
            uint lo = value.Digits[i + words], hi = i + words + 1 < value.Length ? value.Digits[i + words + 1] : 0;
            value.Digits[i] = shift == 0 ? lo : lo >> shift | hi << (32 - shift);
        }
        value.Exponent = (int)exponent;
        value.Length = length;
        while (value.Length > 0 && value.Digits[value.Length - 1] == 0) value.Length--;
        return true;
    }
}
