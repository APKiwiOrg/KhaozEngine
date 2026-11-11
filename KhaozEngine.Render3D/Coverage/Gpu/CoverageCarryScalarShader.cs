namespace KhaozEngine.Render3D;

/// <summary>Corrected division and square root through the retained single scalar worker. Native results are seeds only.</summary>
internal static class CoverageCarryScalarShader
{
    internal const string Source = """

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
            uint phase = 0u, probe = candidate, lo = 0u, hi = end, step = 1u, result = 0u;
            bool searching = true;
            // One exit after the loop, because FXC refuses a function whose only returns are inside an endless loop.
            [[dont_unroll]] for (; searching;) {
                bool atOrBelow = covRoundedAtOrBelow(a,b,root,probe);
                if (phase == 0u) {
                    if (!atOrBelow) { lo = candidate; phase = 2u; probe = min(candidate+step,end); }
                    else if (candidate == 0u) { result = 0u; searching = false; }
                    else { phase = 1u; probe = candidate-1u; }
                } else if (phase == 1u) {
                    if (!atOrBelow) { result = candidate; searching = false; }
                    else {
                        hi = candidate-1u; phase = 3u;
                        probe = candidate > step ? candidate-step : 0u;
                    }
                } else if (phase == 2u) {
                    if (atOrBelow) { hi = probe; phase = 4u; }
                    else { lo = probe; step <<= 1; probe = min(candidate+step,end); }
                } else if (phase == 3u) {
                    if (!atOrBelow) { lo = probe; phase = 4u; }
                    else if (probe == 0u) { result = 0u; searching = false; }
                    else { hi = probe; step <<= 1; probe = candidate > step ? candidate-step : 0u; }
                } else {
                    if (atOrBelow) hi = probe; else lo = probe;
                    bisections++;
                }
                // Whole-domain bracket, fewer than 2^31 ordinal words. At most 31 bisections.
                if (phase == 4u) {
                    if (hi-lo <= 1u) { result = hi; searching = false; }
                    else probe = lo+((hi-lo) >> 1);
                }
            }
            return result;
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

        const int CovJobDiv = 3, CovJobRoot = 4;
        struct CovPredicate { float value; };
        struct CovExactJob { int kind; vec3 a; vec3 b; };
        struct CovExactResult { CovPredicate predicate; uint rounded; uint bisections; };
        CovExactResult covExactResultZero() { return CovExactResult(CovPredicate(0.0),0u,0u); }
        CovExactJob covScalarJob(float a, float b, bool root) {
            return CovExactJob(root ? CovJobRoot : CovJobDiv,vec3(a,0.0,0.0),vec3(b,0.0,0.0));
        }
        CovExactResult covExactWorker(CovExactJob job) {
            CovExactResult result = covExactResultZero();
            result.rounded = covRoundOperation(job.a.x,job.b.x,job.kind == CovJobRoot,false,0u,result.bisections);
            result.predicate.value = covRead(result.rounded);
            return result;
        }
        """;
}
