using System;
using System.Text;

namespace KhaozEngine.Render3D;

/// <summary>Signed magnitude integers in 18 little-endian 32-bit limbs. No floating arithmetic.</summary>
internal static class CoverageGpuExactWordsGlsl
{
    // Shared with the compact residual layer, which never includes the wide helpers.
    internal const string WordProductSource = """

        #ifndef KE_COVERAGE_MUL32_DEFINED
        #define KE_COVERAGE_MUL32_DEFINED
        void covExactMul32(uint a, uint b, out uint low, out uint high) {
            uint a0 = a & 65535u, a1 = a >> 16, b0 = b & 65535u, b1 = b >> 16;
            uint p = a0 * b0;
            uint t = a1 * b0 + (p >> 16);
            uint middle = t & 65535u, upper = t >> 16;
            t = a0 * b1 + middle;
            low = (t << 16) | (p & 65535u);
            high = a1 * b1 + upper + (t >> 16);
        }
        #endif

        """;

    internal static readonly string WorkerSource = BuildSource();
    internal static string Source => WorkerSource + CoverageGpuWideDiagnosticGlsl.Source;

    const string Template = """

        struct CovExact { uint limb[18]; int sign; };
        CovExact covExactZero() {
            CovExact result;
            /* ZERO */
            result.sign = 0;
            return result;
        }
        int covExactBitLength(CovExact a) {
            /* LENGTH */
            return 0;
        }
        int covExactMagnitudeCompare(CovExact a, CovExact b) {
            /* COMPARE */
            return 0;
        }
        int covExactCompare(CovExact a, CovExact b) {
            int sa = covExactBitLength(a) == 0 ? 0 : a.sign;
            int sb = covExactBitLength(b) == 0 ? 0 : b.sign;
            if (sa != sb) return sa < sb ? -1 : 1;
            return sa * covExactMagnitudeCompare(a, b);
        }
        uint covExactWord(CovExact a, int index) {
            /* WORD */
            return 0u;
        }
        // The caller proves the shifted result fits 576 bits. Every limb access is constant.
        CovExact covExactShift(CovExact a, int bits) {
            CovExact result = covExactZero();
            int words = bits / 32, part = bits % 32;
            /* SHIFT */
            result.sign = a.sign;
            return result;
        }
        // Finite words are exact signed integers times 2^-149. Infinity is used only
        // as the virtual finite successor 2^128 when constructing the overflow midpoint.
        CovExact covExactFromWord(uint word) {
            CovExact result = covExactZero();
            uint exponent = (word >> 23) & 255u;
            result.limb[0] = (word & 0x7fffffu) | (exponent == 0u ? 0u : 0x800000u);
            result.sign = result.limb[0] == 0u ? 0 : ((word & 0x80000000u) == 0u ? 1 : -1);
            return covExactShift(result, exponent == 0u ? 0 : int(exponent)-1);
        }
        CovExact covExactMagnitudeAdd(CovExact a, CovExact b, out uint overflow) {
            CovExact result = covExactZero();
            uint carry = 0u;
            /* ADD */
            overflow = carry;
            result.sign = covExactBitLength(result) == 0 ? 0 : 1;
            return result;
        }
        // Requires |a| >= |b|.
        CovExact covExactMagnitudeSub(CovExact a, CovExact b) {
            CovExact result = covExactZero();
            uint borrow = 0u;
            /* SUBTRACT */
            result.sign = covExactBitLength(result) == 0 ? 0 : 1;
            return result;
        }
        CovExact covExactAdd(CovExact a, CovExact b, out uint overflow) {
            overflow = 0u;
            if (a.sign == 0) return b;
            if (b.sign == 0) return a;
            CovExact result;
            int sign;
            if (a.sign == b.sign) { result = covExactMagnitudeAdd(a, b, overflow); sign = a.sign; }
            else if (covExactMagnitudeCompare(a, b) >= 0) { result = covExactMagnitudeSub(a, b); sign = a.sign; }
            else { result = covExactMagnitudeSub(b, a); sign = b.sign; }
            result.sign = covExactBitLength(result) == 0 ? 0 : sign;
            return result;
        }
        CovExact covExactAdd(CovExact a, CovExact b) { uint overflow; return covExactAdd(a, b, overflow); }
        CovExact covExactSub(CovExact a, CovExact b, out uint overflow) {
            b.sign = -b.sign;
            return covExactAdd(a, b, overflow);
        }
        CovExact covExactSub(CovExact a, CovExact b) { uint overflow; return covExactSub(a, b, overflow); }
        uint covExactRightWord(CovExact a, int shift) {
            int index = shift / 32, part = shift % 32;
            if (index >= 18) return 0u;
            uint result = covExactWord(a, index) >> part;
            if (part != 0 && index < 17) result |= covExactWord(a, index+1) << (32-part);
            return result;
        }
        bool covExactBit(CovExact a, int bit) {
            return bit >= 0 && bit < 576 && (covExactWord(a, bit/32) & (1u << (bit%32))) != 0u;
        }
        bool covExactAnyBelow(CovExact a, int count) {
            /* STICKY */
            return false;
        }
        // RN-even only. Inventory callers apply F after this exact conversion.
        uint covRound32(CovExact a, int powerOfTwo) {
            uint sign = a.sign < 0 ? 0x80000000u : 0u;
            int length = covExactBitLength(a);
            if (length == 0) return sign;
            int exponent = length - 1 + powerOfTwo;
            if (exponent > 127) return sign | 0x7f800000u;
            if (exponent < -150) return sign;
            int shift = max(exponent - 23, -149) - powerOfTwo;
            uint rounded = shift <= 0 ? a.limb[0] << (-shift) : covExactRightWord(a, shift);
            if (shift > 0 && covExactBit(a, shift-1) &&
                (covExactAnyBelow(a, shift-1) || (rounded & 1u) != 0u)) rounded++;
            if (exponent < -126) return sign | rounded;
            if (rounded == 0x1000000u) { rounded >>= 1; exponent++; }
            if (exponent > 127) return sign | 0x7f800000u;
            return sign | (uint(exponent+127) << 23) | (rounded & 0x7fffffu);
        }

        """;

    // Generate the fixed-width arithmetic before GLSL compilation. FXC never needs to unroll a
    // data-dependent loop or turn a private array's dynamic l-value into a register address.
    static string BuildSource()
    {
        string source = Template
            .Replace("/* ZERO */", Limbs(i => $"result.limb[{i}] = 0u;"))
            .Replace("/* LENGTH */", Limbs(i => $"if (a.limb[{i}] != 0u) return {i * 32} + findMSB(a.limb[{i}]) + 1;", reverse: true))
            .Replace("/* COMPARE */", Limbs(i => $"if (a.limb[{i}] < b.limb[{i}]) return -1; if (a.limb[{i}] > b.limb[{i}]) return 1;", reverse: true))
            .Replace("/* WORD */", Limbs(i => $"if (index == {i}) return a.limb[{i}];"))
            .Replace("/* SHIFT */", Limbs(i => $$"""
                if ({{i}} >= words) {
                    result.limb[{{i}}] = covExactWord(a, {{i}}-words) << part;
                    if (part != 0 && {{i}} > words) result.limb[{{i}}] |= covExactWord(a, {{i}}-words-1) >> (32-part);
                }
                """))
            .Replace("/* ADD */", Limbs(i => $$"""
                { uint sum = a.limb[{{i}}] + b.limb[{{i}}]; uint next = sum + carry;
                  carry = uint(sum < a.limb[{{i}}]) | uint(next < sum); result.limb[{{i}}] = next; }
                """))
            .Replace("/* SUBTRACT */", Limbs(i => $$"""
                { uint difference = a.limb[{{i}}] - b.limb[{{i}}]; uint next = difference - borrow;
                  borrow = uint(a.limb[{{i}}] < b.limb[{{i}}]) | uint(difference < borrow); result.limb[{{i}}] = next; }
                """))
            .Replace("/* STICKY */", Limbs(i => $$"""
                { int bits = count - {{i * 32}}; if (bits <= 0) return false;
                  if (bits >= 32) { if (a.limb[{{i}}] != 0u) return true; }
                  else return (a.limb[{{i}}] & ((1u << bits)-1u)) != 0u; }
                """));
        return WordProductSource + source;
    }

    static string Limbs(Func<int, string> emit, bool reverse = false)
    {
        var source = new StringBuilder();
        for (int i = 0; i < 18; i++) source.AppendLine(emit(reverse ? 17 - i : i));
        return source.ToString();
    }
}
