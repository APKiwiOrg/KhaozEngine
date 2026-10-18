namespace KhaozEngine.Render3D;

/// <summary>Signed magnitude integers in 18 little-endian 32-bit limbs. No floating arithmetic.</summary>
internal static class CoverageGpuExactWordsGlsl
{
    internal static string Source => """

        struct CovExact { uint limb[18]; int sign; };
        CovExact covExactZero() {
            CovExact result;
            for (int i = 0; i < 18; i++) result.limb[i] = 0u;
            result.sign = 0;
            return result;
        }
        int covExactBitLength(CovExact a) {
            for (int i = 17; i >= 0; i--)
                if (a.limb[i] != 0u) return i * 32 + findMSB(a.limb[i]) + 1;
            return 0;
        }
        int covExactMagnitudeCompare(CovExact a, CovExact b) {
            for (int i = 17; i >= 0; i--) {
                if (a.limb[i] < b.limb[i]) return -1;
                if (a.limb[i] > b.limb[i]) return 1;
            }
            return 0;
        }
        int covExactCompare(CovExact a, CovExact b) {
            int sa = covExactBitLength(a) == 0 ? 0 : a.sign;
            int sb = covExactBitLength(b) == 0 ? 0 : b.sign;
            if (sa != sb) return sa < sb ? -1 : 1;
            return sa * covExactMagnitudeCompare(a, b);
        }
        // The caller proves the shifted result fits 576 bits.
        CovExact covExactShift(CovExact a, int bits) {
            CovExact result = covExactZero();
            int words = bits / 32, part = bits % 32;
            for (int i = 17; i >= words; i--) {
                result.limb[i] = a.limb[i-words] << part;
                if (part != 0 && i > words) result.limb[i] |= a.limb[i-words-1] >> (32-part);
            }
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
            for (int i = 0; i < 18; i++) {
                uint sum = a.limb[i] + b.limb[i];
                uint next = sum + carry;
                carry = uint(sum < a.limb[i]) | uint(next < sum);
                result.limb[i] = next;
            }
            overflow = carry;
            result.sign = covExactBitLength(result) == 0 ? 0 : 1;
            return result;
        }
        // Requires |a| >= |b|.
        CovExact covExactMagnitudeSub(CovExact a, CovExact b) {
            CovExact result = covExactZero();
            uint borrow = 0u;
            for (int i = 0; i < 18; i++) {
                uint difference = a.limb[i] - b.limb[i];
                uint next = difference - borrow;
                borrow = uint(a.limb[i] < b.limb[i]) | uint(difference < borrow);
                result.limb[i] = next;
            }
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
        // Shader Model 5.0 needs no int64 lowering. Each partial product is 16 by 16.
        void covExactMul32(uint a, uint b, out uint low, out uint high) {
            uint a0 = a & 65535u, a1 = a >> 16, b0 = b & 65535u, b1 = b >> 16;
            uint p = a0 * b0;
            uint t = a1 * b0 + (p >> 16);
            uint middle = t & 65535u, upper = t >> 16;
            t = a0 * b1 + middle;
            low = (t << 16) | (p & 65535u);
            high = a1 * b1 + upper + (t >> 16);
        }
        CovExact covExactProduct(CovExact a, CovExact b, out uint overflow) {
            CovExact result = covExactZero();
            overflow = 0u;
            int na = (covExactBitLength(a)+31)/32, nb = (covExactBitLength(b)+31)/32;
            for (int i = 0; i < na; i++) {
                uint carry = 0u;
                for (int j = 0; j < nb; j++) {
                    uint low, high;
                    covExactMul32(a.limb[i], b.limb[j], low, high);
                    if (i+j >= 18) { overflow |= low | high; continue; }
                    uint sum = result.limb[i+j] + low;
                    uint next = sum + carry;
                    carry = high + uint(sum < low) + uint(next < sum);
                    result.limb[i+j] = next;
                }
                if (i+nb < 18) result.limb[i+nb] = carry;
                else overflow |= carry;
            }
            overflow = uint(overflow != 0u);
            result.sign = covExactBitLength(result) == 0 ? 0 : a.sign * b.sign;
            return result;
        }
        CovExact covExactProduct(CovExact a, CovExact b) { uint overflow; return covExactProduct(a, b, overflow); }
        uint covExactRightWord(CovExact a, int shift) {
            int index = shift / 32, part = shift % 32;
            if (index >= 18) return 0u;
            uint result = a.limb[index] >> part;
            if (part != 0 && index < 17) result |= a.limb[index+1] << (32-part);
            return result;
        }
        bool covExactBit(CovExact a, int bit) {
            return bit >= 0 && bit < 576 && (a.limb[bit/32] & (1u << (bit%32))) != 0u;
        }
        bool covExactAnyBelow(CovExact a, int count) {
            for (int i = 0; i < 18; i++) {
                int bits = count - i*32;
                if (bits <= 0) return false;
                if (bits >= 32) { if (a.limb[i] != 0u) return true; }
                else return (a.limb[i] & ((1u << bits)-1u)) != 0u;
            }
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
}
