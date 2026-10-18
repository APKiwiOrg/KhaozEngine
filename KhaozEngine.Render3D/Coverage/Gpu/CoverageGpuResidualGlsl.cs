namespace KhaozEngine.Render3D;

/// <summary>Exact scaled comparisons for 24-bit inputs and at most 50-bit midpoint products.</summary>
internal static class CoverageGpuResidualGlsl
{
    internal static string Source => CoverageGpuExactWordsGlsl.WordProductSource + """

        struct CovResidual { uint low; uint high; int exponent; };
        CovResidual covResidualFromWord(uint word) {
            uint exponent = word >> 23;
            return CovResidual((word & 0x7fffffu) | (exponent == 0u ? 0u : 0x800000u),
                0u, exponent == 0u ? -149 : int(exponent)-150);
        }
        // Adjacent words have midpoint (2*significand(lower)+1) * 2^(exponent(lower)-1).
        // At max finite this is the finite overflow midpoint, without decoding infinity.
        CovResidual covResidualMidpoint(uint lower) {
            CovResidual result = covResidualFromWord(lower);
            result.low = (result.low << 1) | 1u;
            result.exponent--;
            return result;
        }
        // Callers supply single-word factors. Their product has at most 49 (div) or 50 (sqrt) bits.
        CovResidual covResidualProduct(CovResidual a, CovResidual b) {
            CovResidual result;
            covExactMul32(a.low, b.low, result.low, result.high);
            result.exponent = a.exponent + b.exponent;
            return result;
        }
        int covResidualLength(CovResidual a) {
            return a.high == 0u ? findMSB(a.low)+1 : findMSB(a.high)+33;
        }
        CovResidual covResidualNormalize(CovResidual a, int length) {
            if (length <= 32) { a.high = a.low << (32-length); a.low = 0u; }
            else {
                int shift = 64-length;
                if (shift != 0) {
                    a.high = (a.high << shift) | (a.low >> (32-shift));
                    a.low <<= shift;
                }
            }
            return a;
        }
        int covResidualCompare(CovResidual a, CovResidual b) {
            int la = covResidualLength(a), lb = covResidualLength(b);
            // Zero and disjoint leading powers decide before any alignment.
            if (la == 0 || lb == 0) return la == lb ? 0 : (la == 0 ? -1 : 1);
            int topA = a.exponent+la, topB = b.exponent+lb;
            if (topA != topB) return topA < topB ? -1 : 1;
            // Equal leading powers permit lossless left alignment within two words.
            a = covResidualNormalize(a, la); b = covResidualNormalize(b, lb);
            if (a.high != b.high) return a.high < b.high ? -1 : 1;
            return a.low == b.low ? 0 : (a.low < b.low ? -1 : 1);
        }

        """;
}
