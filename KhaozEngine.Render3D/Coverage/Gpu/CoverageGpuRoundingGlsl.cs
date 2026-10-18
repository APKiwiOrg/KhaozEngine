namespace KhaozEngine.Render3D;

/// <summary>Native seeds with exact integer midpoint correction, followed by the inventory flush.</summary>
internal static class CoverageGpuRoundingGlsl
{
    internal static string Source => """

        // True iff RN32(exact result) is at or below 'lower'. Both endpoint values
        // are expressed in units of 2^-149, so their sum is the midpoint in 2^-150.
        bool covRoundedAtOrBelow(CovExact a, CovExact b, bool root, uint lower) {
            if (lower == 0x7f800000u) return true;
            CovExact midpoint = covExactAdd(covExactFromWord(lower), covExactFromWord(lower+1u));
            CovExact left = covExactShift(a, root ? 151 : 150);
            CovExact right;
            if (root) right = covExactProduct(midpoint, midpoint);
            else right = covExactProduct(b, midpoint);
            int residual = covExactMagnitudeCompare(left, right);
            return residual < 0 || (residual == 0 && (lower & 1u) == 0u);
        }
        uint covCorrectPositive(CovExact a, CovExact b, bool root, uint seed, out uint bisections) {
            const uint end = 0x7f800000u;
            uint candidate = min(seed & 0x7fffffffu, end);
            bisections = 0u;
            bool upper = covRoundedAtOrBelow(a, b, root, candidate);
            bool lower = candidate == 0u || !covRoundedAtOrBelow(a, b, root, candidate-1u);
            if (upper && lower) return candidate;
            uint lo, hi, step = 1u;
            if (!upper) {
                lo = candidate;
                for (;;) {
                    hi = min(candidate + step, end);
                    if (covRoundedAtOrBelow(a, b, root, hi)) break;
                    lo = hi;
                    step <<= 1;
                }
            } else {
                hi = candidate-1u;
                for (;;) {
                    lo = candidate > step ? candidate-step : 0u;
                    if (!covRoundedAtOrBelow(a, b, root, lo)) break;
                    if (lo == 0u) return 0u;
                    hi = lo;
                    step <<= 1;
                }
            }
            // lo is below the selected word and hi is at or above it. The ordinal
            // domain has fewer than 2^31 words, hence at most 31 bisections.
            while (hi-lo > 1u) {
                uint middle = lo + ((hi-lo) >> 1);
                if (covRoundedAtOrBelow(a, b, root, middle)) hi = middle;
                else lo = middle;
                bisections++;
            }
            return hi;
        }
        // Seed-taking helpers expose the exact correction separately. Production
        // callers below always supply a native operation, never a guessed result.
        uint covDivSeeded(float a, float b, uint seed, out uint bisections) {
            a = covF(a); b = covF(b);
            uint x = floatBitsToUint(a), y = floatBitsToUint(b);
            uint ax = x & 0x7fffffffu, by = y & 0x7fffffffu, sign = (x ^ y) & 0x80000000u;
            bisections = 0u;
            if (ax > 0x7f800000u) return x | 0x00400000u;
            if (by > 0x7f800000u) return y | 0x00400000u;
            if ((ax == 0u && by == 0u) || (ax == 0x7f800000u && by == 0x7f800000u)) return 0xffc00000u;
            if (ax == 0x7f800000u || by == 0u) return sign | 0x7f800000u;
            if (ax == 0u || by == 0x7f800000u) return sign;
            return sign | covCorrectPositive(covExactFromWord(ax), covExactFromWord(by), false, seed, bisections);
        }
        uint covSqrtSeeded(float a, uint seed, out uint bisections) {
            a = covF(a);
            uint x = floatBitsToUint(a), magnitude = x & 0x7fffffffu;
            bisections = 0u;
            if (magnitude > 0x7f800000u) return x | 0x00400000u;
            if (magnitude == 0u) return x;
            if ((x & 0x80000000u) != 0u) return 0xffc00000u;
            if (magnitude == 0x7f800000u) return magnitude;
            return covCorrectPositive(covExactFromWord(x), covExactZero(), true, seed, bisections);
        }
        uint covDivRn32(float a, float b, out uint bisections) {
            a = covF(a); b = covF(b);
            precise float candidate = a / b;
            return covDivSeeded(a, b, floatBitsToUint(candidate), bisections);
        }
        uint covSqrtRn32(float a, out uint bisections) {
            a = covF(a);
            precise float candidate = sqrt(a);
            return covSqrtSeeded(a, floatBitsToUint(candidate), bisections);
        }
        float covDiv(float a, float b) {
            uint bisections;
            return covF(uintBitsToFloat(covDivRn32(a, b, bisections)));
        }
        float covSqrt(float a) {
            uint bisections;
            return covF(uintBitsToFloat(covSqrtRn32(a, bisections)));
        }

        """;
}
