namespace KhaozEngine.Render3D;

/// <summary>Native seeds with exact integer midpoint correction, followed by the inventory flush.</summary>
internal static class CoverageGpuRoundingGlsl
{
    internal static string Source => CoverageGpuResidualGlsl.Source + """

        #extension GL_EXT_control_flow_attributes : require

        // True iff RN32(exact result) is at or below 'lower'. Both endpoint values
        // use a 25-bit midpoint significand and an explicit power of two.
        bool covRoundedAtOrBelow(CovResidual a, CovResidual b, bool root, uint lower) {
            if (lower == 0x7f800000u) return true;
            CovResidual midpoint = covResidualMidpoint(lower);
            CovResidual factor = root ? midpoint : b;
            CovResidual right = covResidualProduct(factor, midpoint);
            int residual = covResidualCompare(a, right);
            return residual < 0 || (residual == 0 && (lower & 1u) == 0u);
        }
        uint covCorrectPositive(CovResidual a, CovResidual b, bool root, uint seed, out uint bisections) {
            const uint end = 0x7f800000u;
            uint candidate = min(seed & 0x7fffffffu, end);
            bisections = 0u;
            // One residual site services the seed bracket, expansion and bisection.
            // phase 0 tests the seed, 1 its predecessor, 2/3 expand, and 4 bisects.
            uint phase = 0u, probe = candidate, lo = 0u, hi = end, step = 1u;
            [[dont_unroll]] for (;;) {
                bool atOrBelow = covRoundedAtOrBelow(a,b,root,probe);
                if (phase == 0u) {
                    if (!atOrBelow) { lo = candidate; phase = 2u; probe = min(candidate+step,end); }
                    else if (candidate == 0u) return 0u;
                    else { phase = 1u; probe = candidate-1u; }
                } else if (phase == 1u) {
                    if (!atOrBelow) return candidate;
                    hi = candidate-1u; phase = 3u;
                    probe = candidate > step ? candidate-step : 0u;
                } else if (phase == 2u) {
                    if (atOrBelow) { hi = probe; phase = 4u; }
                    else { lo = probe; step <<= 1; probe = min(candidate+step,end); }
                } else if (phase == 3u) {
                    if (!atOrBelow) { lo = probe; phase = 4u; }
                    else if (probe == 0u) return 0u;
                    else { hi = probe; step <<= 1; probe = candidate > step ? candidate-step : 0u; }
                } else {
                    if (atOrBelow) hi = probe; else lo = probe;
                    bisections++;
                }
                // Whole-domain bracket, fewer than 2^31 ordinal words. At most 31 bisections.
                if (phase == 4u) {
                    if (hi-lo <= 1u) return hi;
                    probe = lo+((hi-lo) >> 1);
                }
            }
        }
        // Shared seeded/native entry. Every physical worker calls this once, including
        // mixed division/root graphs. Native results are seeds, never final answers.
        uint covRoundOperation(float a, float b, bool root, bool injected, uint seed, out uint bisections) {
            a = covF(a); b = covF(b);
            uint x = floatBitsToUint(a), y = floatBitsToUint(b);
            uint ax = x & 0x7fffffffu, by = y & 0x7fffffffu, sign = (x ^ y) & 0x80000000u;
            bisections = 0u;
            if (ax > 0x7f800000u) return x | 0x00400000u;
            if (root) {
                if (ax == 0u) return x;
                if ((x & 0x80000000u) != 0u) return 0xffc00000u;
                if (ax == 0x7f800000u) return ax;
                sign = 0u;
            } else {
                if (by > 0x7f800000u) return y | 0x00400000u;
                if ((ax == 0u && by == 0u) || (ax == 0x7f800000u && by == 0x7f800000u)) return 0xffc00000u;
                if (ax == 0x7f800000u || by == 0u) return sign | 0x7f800000u;
                if (ax == 0u || by == 0x7f800000u) return sign;
            }
            if (!injected) {
                precise float candidate;
                if (root) candidate = sqrt(a); else candidate = a / b;
                seed = floatBitsToUint(candidate);
            }
            return sign | covCorrectPositive(covResidualFromWord(ax),covResidualFromWord(by),root,seed,bisections);
        }
        uint covDivSeeded(float a, float b, uint seed, out uint bisections) {
            return covRoundOperation(a,b,false,true,seed,bisections);
        }
        uint covSqrtSeeded(float a, uint seed, out uint bisections) {
            return covRoundOperation(a,0.0,true,true,seed,bisections);
        }
        uint covDivRn32(float a, float b, out uint bisections) {
            return covRoundOperation(a,b,false,false,0u,bisections);
        }
        uint covSqrtRn32(float a, out uint bisections) {
            return covRoundOperation(a,0.0,true,false,0u,bisections);
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
