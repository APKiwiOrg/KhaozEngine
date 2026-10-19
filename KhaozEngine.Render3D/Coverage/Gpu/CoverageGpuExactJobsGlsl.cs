using System.Text;

namespace KhaozEngine.Render3D;

/// <summary>Finite typed jobs with one product, signed-add, RN32 and corrected scalar site.</summary>
internal static class CoverageGpuExactJobsGlsl
{
    internal static readonly string Source = BuildSource();
    const string Template = """

        #extension GL_EXT_control_flow_attributes : require
        const int CovJobEdge = 0, CovJobDot = 1, CovJobRound = 2, CovJobDiv = 3, CovJobRoot = 4;
        struct CovPredicate { int sign; float value; };
        struct CovExactJob { int kind; vec3 a; vec3 b; vec3 c; vec3 d; CovExact wide; int power; bool convert; };
        struct CovExactResult { CovExact exact; CovPredicate predicate; uint rounded; uint bisections; };
        CovExactResult covExactResultZero() { return CovExactResult(covExactZero(),CovPredicate(0,0.0),0u,0u); }
        bool covPredicateFinite(vec3 p) { return covFinite(p.x) && covFinite(p.y); }
        CovExactJob covJob(int kind, vec3 a, vec3 b, vec3 c, vec3 d) {
            return CovExactJob(kind,a,b,c,d,covExactZero(),-298,true);
        }
        CovExactJob covEdgeJob(vec3 a, vec3 b, vec3 c) { return covJob(CovJobEdge,a,b,c,vec3(0.0)); }
        CovExactJob covDotJob(vec3 a, vec3 b, vec3 c, vec3 d) { return covJob(CovJobDot,a,b,c,d); }
        CovExactJob covScalarJob(float a, float b, bool root) { return covJob(root ? CovJobRoot : CovJobDiv,vec3(a,0.0,0.0),vec3(b,0.0,0.0),vec3(0.0),vec3(0.0)); }
        CovExactJob covRoundJob(CovExact value, int power) {
            CovExactJob job = covJob(CovJobRound,vec3(0.0),vec3(0.0),vec3(0.0),vec3(0.0));
            job.wide = value; job.power = power; return job;
        }
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
        CovExactResult covExactWorker(CovExactJob job) {
            CovExactResult result = covExactResultZero();
            if (job.kind <= CovJobDot) {
                if (!covPredicateFinite(job.a) || !covPredicateFinite(job.b) || !covPredicateFinite(job.c)
                    || job.kind == CovJobDot && !covPredicateFinite(job.d)) {
                    result.predicate.value = uintBitsToFloat(0x7fc00000u); return result;
                }
                int count = job.kind == CovJobEdge ? 6 : 8;
                [[dont_unroll]] for (int term = 0; term < count; term++) {
                    float x, y; bool negative;
                    if (job.kind == CovJobEdge) {
                        if (term == 0) { x = job.b.x; y = job.c.y; }
                        else if (term == 1) { x = job.b.x; y = job.a.y; }
                        else if (term == 2) { x = job.a.x; y = job.c.y; }
                        else if (term == 3) { x = job.b.y; y = job.c.x; }
                        else if (term == 4) { x = job.b.y; y = job.a.x; }
                        else { x = job.a.y; y = job.c.x; }
                        negative = term >= 1 && term <= 3;
                    } else {
                        if (term == 0) { x = job.b.x; y = job.d.x; }
                        else if (term == 1) { x = job.b.x; y = job.c.x; }
                        else if (term == 2) { x = job.a.x; y = job.d.x; }
                        else if (term == 3) { x = job.a.x; y = job.c.x; }
                        else if (term == 4) { x = job.b.y; y = job.d.y; }
                        else if (term == 5) { x = job.b.y; y = job.c.y; }
                        else if (term == 6) { x = job.a.y; y = job.d.y; }
                        else { x = job.a.y; y = job.c.y; }
                        negative = term == 1 || term == 2 || term == 5 || term == 6;
                    }
                    CovExact product = covPredicateProduct(x,y);
                    if (negative) product.sign = -product.sign;
                    uint overflow;
                    result.exact = covExactAdd(result.exact,product,overflow);
                }
            } else if (job.kind == CovJobRound) result.exact = job.wide;
            if (job.kind <= CovJobRound) {
                result.predicate.sign = result.exact.sign;
                if (job.convert) result.rounded = covRound32(result.exact,job.power);
            } else result.rounded = covRoundOperation(job.a.x,job.b.x,job.kind == CovJobRoot,false,0u,result.bisections);
            result.predicate.value = covRead(result.rounded);
            return result;
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
