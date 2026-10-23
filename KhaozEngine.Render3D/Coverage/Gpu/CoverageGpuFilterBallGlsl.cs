namespace KhaozEngine.Render3D;

/// <summary>Normal-domain TwoSum/4097 TwoProduct balls. Unproved signs, scales and RN32 cells refuse.</summary>
internal static class CoverageGpuFilterBallGlsl
{
    internal const string Source = """
        bool CovFilterRefused = false;
        bool CovFilterSignOnly = false;
        struct CovFiltered { float hi; float lo; float radius; int sign; };
        CovFiltered covFilteredZero() { return CovFiltered(0.0,0.0,0.0,0); }
        float covFilterAbs(float a) { return uintBitsToFloat(floatBitsToUint(a) & 0x7fffffffu); }
        float covFilterUp(float a) { return a == 0.0 ? 0.0 : uintBitsToFloat(floatBitsToUint(a)+1u); }
        float covFilterAddUp(float a, float b) {
            if (a == 0.0) return b;
            if (b == 0.0) return a;
            precise float sum = a+b;
            return covFilterUp(sum);
        }
        vec2 covFilterSum(float a, float b) {
            precise float sum = a+b;
            precise float bv = sum-a;
            precise float av = sum-bv;
            precise float br = b-bv;
            precise float ar = a-av;
            precise float tail = ar+br;
            return vec2(sum,tail);
        }
        CovFiltered covFilterAdd(CovFiltered a, float hi, float lo) {
            precise vec2 s = covFilterSum(a.hi,hi);
            precise vec2 t = covFilterSum(a.lo,lo);
            precise vec2 v = covFilterSum(t.x,s.y);
            precise vec2 h = covFilterSum(s.x,v.x);
            float radius = covFilterAddUp(a.radius,covFilterAddUp(covFilterAbs(t.y),covFilterAbs(v.y)));
            return CovFiltered(h.x,h.y,radius,0);
        }
        bool covFilterSafe(float a) {
            uint word = floatBitsToUint(a) & 0x7fffffffu;
            int e = int(word >> 23)-127;
            return word == 0u || e >= -100 && e <= 100;
        }
        bool covFilterProductSafe(float a, float b) {
            if (!covFilterSafe(a) || !covFilterSafe(b)) return false;
            if (a == 0.0 || b == 0.0) return true;
            int e = int((floatBitsToUint(a) & 0x7fffffffu) >> 23)
                +int((floatBitsToUint(b) & 0x7fffffffu) >> 23)-254;
            return e >= -78 && e <= 100;
        }
        vec2 covFilterProduct(float a, float b) {
            precise float product = a*b;
            precise float ac = 4097.0*a;
            precise float bc = 4097.0*b;
            precise float ah = ac-(ac-a);
            precise float bh = bc-(bc-b);
            precise float al = a-ah;
            precise float bl = b-bh;
            precise float error = ((ah*bh-product)+ah*bl+al*bh)+al*bl;
            return vec2(product,error);
        }
        // 2 means unknown. Every nonzero EFT term is on a lattice at least 2^-124.
        // Radius arithmetic is outward and never uses the inventory's inclusive flush.
        int covFilterSign(CovFiltered a) {
            if (a.hi == 0.0 && a.lo == 0.0 && a.radius == 0.0) return 0;
            float bound = covFilterAddUp(covFilterAbs(a.lo),a.radius);
            if (covFilterAbs(a.hi) > bound) return a.hi < 0.0 ? -1 : 1;
            return 2;
        }
        int covFilterCompare(CovFiltered a, float hi, float lo) {
            a = covFilterAdd(a,-hi,-lo);
            return covFilterSign(a);
        }
        float covFilterPower(int exponent) { return uintBitsToFloat(uint(exponent+127) << 23); }
        CovFiltered covFilterMagnitude(CovFiltered a) {
            if (a.sign < 0) { a.hi = -a.hi; a.lo = -a.lo; }
            return a;
        }
        int covFilteredBitLength(CovFiltered a) {
            if (a.sign == 0) return 0;
            a = covFilterMagnitude(a);
            int exponent = int((floatBitsToUint(a.hi) & 0x7fffffffu) >> 23)-127;
            // The original crossing graph's integer base is 2^-298. Return its equivalent length.
            if (exponent < -100 || exponent > 100) { CovFilterRefused = true; return 0; }
            int lower = covFilterCompare(a,covFilterPower(exponent),0.0);
            int upper = covFilterCompare(a,covFilterPower(exponent+1),0.0);
            if ((lower != 0 && lower != 1) || upper != -1) { CovFilterRefused = true; return 0; }
            return exponent+299;
        }
        float covFilterScaleWord(float a, int power) {
            uint bits = floatBitsToUint(a), magnitude = bits & 0x7fffffffu;
            if (magnitude == 0u) return a;
            int exponent = int(magnitude >> 23)+power;
            if (exponent < 27 || exponent > 227) { CovFilterRefused = true; return 0.0; }
            return uintBitsToFloat((bits & 0x807fffffu) | (uint(exponent) << 23));
        }
        uint covFilterRound(CovFiltered a, int power) {
            if (a.sign == 0) return 0u;
            a = covFilterMagnitude(a);
            if (power != 0) {
                a.hi = covFilterScaleWord(a.hi,power);
                a.lo = covFilterScaleWord(a.lo,power);
                a.radius = covFilterScaleWord(a.radius,power);
            }
            precise float center = a.hi+a.lo;
            uint word = floatBitsToUint(center);
            int exponent = int(word >> 23)-127;
            if (exponent < -100 || exponent > 100 || (word & 0x80000000u) != 0u) { CovFilterRefused = true; return 0u; }
            float up = covFilterPower(exponent-24);
            float down = (word & 0x7fffffu) == 0u ? covFilterPower(exponent-25) : up;
            int lower = covFilterCompare(a,center,-down);
            int upper = covFilterCompare(a,center,up);
            bool even = (word & 1u) == 0u;
            if (!(lower == 1 || lower == 0 && even) || !(upper == -1 || upper == 0 && even)) {
                CovFilterRefused = true; return 0u;
            }
            return word | (a.sign < 0 ? 0x80000000u : 0u);
        }
        """;
}
