namespace KhaozEngine.Render3D;

/// <summary>A narrow geometry ball and compact scalar worker, with no wide integer payload.</summary>
internal static class CoverageGpuFilterJobsGlsl
{
    internal const string Source = """
        const int CovJobEdge = 0, CovJobDot = 1, CovJobRound = 2, CovJobDiv = 3, CovJobRoot = 4;
        struct CovPredicate { int sign; float value; };
        struct CovFilteredJob { int kind; vec3 a; vec3 b; vec3 c; vec3 d; CovFiltered wide; int power; bool convert; };
        struct CovFilteredResult { CovFiltered exact; CovPredicate predicate; uint rounded; uint bisections; };
        CovFilteredResult covFilteredResultZero() { return CovFilteredResult(covFilteredZero(),CovPredicate(0,0.0),0u,0u); }
        bool covPredicateFinite(vec3 p) { return covFinite(p.x) && covFinite(p.y); }
        CovFilteredJob covJob(int kind, vec3 a, vec3 b, vec3 c, vec3 d) {
            return CovFilteredJob(kind,a,b,c,d,covFilteredZero(),-298,true);
        }
        CovFilteredJob covEdgeJob(vec3 a, vec3 b, vec3 c) { return covJob(CovJobEdge,a,b,c,vec3(0.0)); }
        CovFilteredJob covDotJob(vec3 a, vec3 b, vec3 c, vec3 d) { return covJob(CovJobDot,a,b,c,d); }
        CovFilteredJob covScalarJob(float a, float b, bool root) { return covJob(root ? CovJobRoot : CovJobDiv,vec3(a,0.0,0.0),vec3(b,0.0,0.0),vec3(0.0),vec3(0.0)); }
        CovFilteredJob covRoundJob(CovFiltered value, int power) {
            CovFilteredJob job = covJob(CovJobRound,vec3(0.0),vec3(0.0),vec3(0.0),vec3(0.0));
            job.wide = value; job.power = power; return job;
        }
        void covFilterTerm(CovFilteredJob job, int term, out float x, out float y) {
            bool negative;
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
            x = covF(x); y = covF(y);
            if (negative) x = -x;
        }
        CovFilteredResult covFilteredWorker(CovFilteredJob job) {
            CovFilteredResult result = covFilteredResultZero();
            if (job.kind <= CovJobDot) {
                if (!covPredicateFinite(job.a) || !covPredicateFinite(job.b) || !covPredicateFinite(job.c)
                    || job.kind == CovJobDot && !covPredicateFinite(job.d)) {
                    result.predicate.value = uintBitsToFloat(0x7fc00000u); return result;
                }
                int count = job.kind == CovJobEdge ? 6 : 8;
                if (CovFilterSignOnly) {
                    precise float sum = 0.0;
                    float magnitude = 0.0;
                    [[dont_unroll]] for (int term = 0; term < count; term++) {
                        float x, y; covFilterTerm(job,term,x,y);
                        if (!covFilterProductSafe(x,y)) { CovFilterRefused = true; return result; }
                        precise float product = x*y;
                        sum = sum+product;
                        magnitude = covFilterAddUp(magnitude,covFilterAbs(product));
                    }
                    // 1.1e-6 exceeds gamma(17,2^-24), including product-sum inflation.
                    // The additive normal term covers any inclusive-flush error conservatively.
                    precise float error = magnitude*0.0000011;
                    error = covFilterAddUp(covFilterUp(error),uintBitsToFloat(0x03000000u));
                    if (covFilterAbs(sum) > error && covFinite(sum) && covFinite(error)) {
                        result.predicate = CovPredicate(sum < 0.0 ? -1 : 1,sum < 0.0 ? -1.0 : 1.0); return result;
                    }
                }
                [[dont_unroll]] for (int term = 0; term < count; term++) {
                    float x, y; covFilterTerm(job,term,x,y);
                    if (!covFilterProductSafe(x,y)) { CovFilterRefused = true; return result; }
                    precise vec2 product = covFilterProduct(x,y);
                    result.exact = covFilterAdd(result.exact,product.x,product.y);
                }
                int sign = covFilterSign(result.exact);
                if (sign == 2) { CovFilterRefused = true; return result; }
                result.exact.sign = sign; result.predicate.sign = sign;
            } else if (job.kind == CovJobRound) {
                result.exact = job.wide; result.predicate.sign = job.wide.sign;
            }
            if (job.kind <= CovJobRound) {
                if (job.convert && !CovFilterSignOnly) result.rounded = covFilterRound(result.exact,job.power+298);
                else result.rounded = floatBitsToUint(float(result.predicate.sign));
            } else result.rounded = covRoundOperation(job.a.x,job.b.x,job.kind == CovJobRoot,false,0u,result.bisections);
            result.predicate.value = covRead(result.rounded);
            return result;
        }
        """;
}
