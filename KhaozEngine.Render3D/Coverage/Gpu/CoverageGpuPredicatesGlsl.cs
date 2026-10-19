namespace KhaozEngine.Render3D;

/// <summary>Predicate entry points and the resumable, unrounded B30 crossing graph.</summary>
internal static class CoverageGpuPredicatesGlsl
{
    internal static string Source => CoverageGpuExactJobsGlsl.Source + """

        CovPredicate covEdge(vec3 a, vec3 b, vec3 p) { return covExactWorker(covEdgeJob(a,b,p)).predicate; }
        CovPredicate covDot(vec3 a, vec3 b, vec3 c, vec3 d) { return covExactWorker(covDotJob(a,b,c,d)).predicate; }
        const int CovCrossBegin = 0, CovCrossA = 1, CovCrossB = 2, CovCrossRoundA = 3, CovCrossRoundB = 4, CovCrossDiv = 5, CovCrossDone = 6;
        struct CovCrossingGraph { int phase; vec3 s; vec3 t; vec3 a; vec3 b; CovExact ca; CovExact cb; int power; float x; float value; };
        CovCrossingGraph covCrossingBegin(vec3 s, vec3 t, vec3 a, vec3 b) {
            return CovCrossingGraph(CovCrossBegin,s,t,a,b,covExactZero(),covExactZero(),0,0.0,uintBitsToFloat(0x7fc00000u));
        }
        // A step either requests one job or completes. Both exact sides stay owned here
        // until the common scale is chosen, before either RN32 conversion.
        bool covCrossingStep(inout CovCrossingGraph q, CovExactResult result, out CovExactJob job) {
            job = covEdgeJob(q.s,q.t,q.a);
            if (q.phase == CovCrossBegin) {
                if (!covPredicateFinite(q.s) || !covPredicateFinite(q.t) || !covPredicateFinite(q.a) || !covPredicateFinite(q.b)) { q.phase = CovCrossDone; return false; }
                job.convert = false; q.phase = CovCrossA;
            } else if (q.phase == CovCrossA) {
                q.ca = result.exact; job = covEdgeJob(q.s,q.t,q.b); job.convert = false; q.phase = CovCrossB;
            } else if (q.phase == CovCrossB) {
                q.cb = result.exact;
                if (q.ca.sign == 0 || q.cb.sign == 0 || q.ca.sign == q.cb.sign) { q.phase = CovCrossDone; return false; }
                q.power = 1-max(covExactBitLength(q.ca),covExactBitLength(q.cb));
                job = covRoundJob(q.ca,q.power); q.phase = CovCrossRoundA;
            } else if (q.phase == CovCrossRoundA) {
                q.x = result.predicate.value; job = covRoundJob(q.cb,q.power); q.phase = CovCrossRoundB;
            } else if (q.phase == CovCrossRoundB) {
                job = covScalarJob(q.x,covSub(q.x,result.predicate.value),false); q.phase = CovCrossDiv;
            } else { q.value = result.predicate.value; q.phase = CovCrossDone; return false; }
            return true;
        }
        float covCrossing(vec3 s, vec3 t, vec3 a, vec3 b) {
            CovCrossingGraph q = covCrossingBegin(s,t,a,b);
            CovExactResult result = covExactResultZero(); CovExactJob job;
            [[dont_unroll]] while (q.phase != CovCrossDone) if (covCrossingStep(q,result,job)) result = covExactWorker(job);
            return q.value;
        }
        int covCellRatio(float ratio) {
            precise float x = covMul(ratio,16.0);
            int cell = 0;
            [[dont_unroll]] for (int threshold = 1; threshold <= 15; threshold++) if (covGreaterOrEqual(x,float(threshold))) cell++;
            return cell;
        }
        int covCell(float value, float minimum, float maximum) { return covCellRatio(covDiv(covSub(value,minimum),covSub(maximum,minimum))); }

        """;
}
