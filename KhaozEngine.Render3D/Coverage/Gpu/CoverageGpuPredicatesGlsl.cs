using System.Text;

namespace KhaozEngine.Render3D;

/// <summary>Exact binary32 XY predicates and the B30 normalized crossing parameter.</summary>
internal static class CoverageGpuPredicatesGlsl
{
    internal static readonly string Source = BuildSource();
    const string Template = """

        struct CovPredicate { int sign; float value; };
        bool covPredicateFinite(vec3 p) { return covFinite(p.x) && covFinite(p.y); }
        CovPredicate covPredicateInvalid() { return CovPredicate(0, uintBitsToFloat(0x7fc00000u)); }
        // Each flushed finite coordinate is s*2^(E-1) in 2^-149 units, with s < 2^24.
        // A product needs only two words before its exact shift into the 557-bit accumulator.
        CovExact covPredicateProduct(float a, float b) {
            uint x = floatBitsToUint(covF(a)), y = floatBitsToUint(covF(b));
            uint ax = x & 0x7fffffffu, ay = y & 0x7fffffffu;
            if (ax == 0u || ay == 0u) return covExactZero();
            uint sx = (ax & 0x7fffffu) | 0x800000u, sy = (ay & 0x7fffffu) | 0x800000u;
            CovExact result = covExactZero();
            uint low, high; covExactMul32(sx, sy, low, high);
            int shift = int(ax >> 23) + int(ay >> 23) - 2, index = shift/32, part = shift%32;
            uint first = low << part, second = high, third = 0u;
            if (part != 0) { second = (high << part) | (low >> (32-part)); third = high >> (32-part); }
            /* PLACE */
            result.sign = ((x ^ y) & 0x80000000u) == 0u ? 1 : -1;
            return result;
        }
        // Expand before subtracting coordinates. No rounded difference or product participates.
        // |coordinate integer| < 2^277, so every signed partial sum below fits 557 bits.
        CovExact covEdgeExact(vec3 a, vec3 b, vec3 p) {
            CovExact result = covPredicateProduct(b.x, p.y);
            result = covExactSub(result, covPredicateProduct(b.x, a.y));
            result = covExactSub(result, covPredicateProduct(a.x, p.y));
            result = covExactSub(result, covPredicateProduct(b.y, p.x));
            result = covExactAdd(result, covPredicateProduct(b.y, a.x));
            return covExactAdd(result, covPredicateProduct(a.y, p.x));
        }
        CovExact covDotExact(vec3 a, vec3 b, vec3 c, vec3 d) {
            CovExact result = covPredicateProduct(b.x, d.x);
            result = covExactSub(result, covPredicateProduct(b.x, c.x));
            result = covExactSub(result, covPredicateProduct(a.x, d.x));
            result = covExactAdd(result, covPredicateProduct(a.x, c.x));
            result = covExactAdd(result, covPredicateProduct(b.y, d.y));
            result = covExactSub(result, covPredicateProduct(b.y, c.y));
            result = covExactSub(result, covPredicateProduct(a.y, d.y));
            return covExactAdd(result, covPredicateProduct(a.y, c.y));
        }
        CovPredicate covEdge(vec3 a, vec3 b, vec3 p) {
            if (!covPredicateFinite(a) || !covPredicateFinite(b) || !covPredicateFinite(p)) return covPredicateInvalid();
            CovExact exact = covEdgeExact(a,b,p);
            return CovPredicate(exact.sign, covRead(covRound32(exact,-298)));
        }
        CovPredicate covDot(vec3 a, vec3 b, vec3 c, vec3 d) {
            if (!covPredicateFinite(a) || !covPredicateFinite(b) || !covPredicateFinite(c) || !covPredicateFinite(d)) return covPredicateInvalid();
            CovExact exact = covDotExact(a,b,c,d);
            return CovPredicate(exact.sign, covRead(covRound32(exact,-298)));
        }
        float covCrossing(vec3 s, vec3 t, vec3 a, vec3 b) {
            if (!covPredicateFinite(s) || !covPredicateFinite(t) || !covPredicateFinite(a) || !covPredicateFinite(b)) return uintBitsToFloat(0x7fc00000u);
            CovExact ca = covEdgeExact(s,t,a), cb = covEdgeExact(s,t,b);
            if (ca.sign == 0 || cb.sign == 0 || ca.sign == cb.sign) return uintBitsToFloat(0x7fc00000u);
            int length = max(covExactBitLength(ca), covExactBitLength(cb));
            int k = 299 - length;
            precise float x = covRead(covRound32(ca,k-298)), y = covRead(covRound32(cb,k-298));
            return covDiv(x,covSub(x,y));
        }
        int covCell(float value, float minimum, float maximum) {
            precise float x = covMul(covDiv(covSub(value,minimum),covSub(maximum,minimum)),16.0);
            int cell = 0;
            for (int threshold = 1; threshold <= 15; threshold++) if (covGreaterOrEqual(x,float(threshold))) cell++;
            return cell;
        }

        """;

    static string BuildSource()
    {
        var placement = new StringBuilder();
        for (int i = 0; i < 18; i++)
            placement.AppendLine($"result.limb[{i}] = index == {i} ? first : (index+1 == {i} ? second : (index+2 == {i} ? third : 0u));");
        return Template.Replace("/* PLACE */", placement.ToString());
    }
}
