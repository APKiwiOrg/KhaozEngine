namespace KhaozEngine.Render3D;

/// <summary>Boundary projection and distance with strict, first-edge ties.</summary>
internal static class CoverageGpuBoundaryQueryGlsl
{
    internal const string Source = """

        const int CovBBegin = 0, CovBEdge = 1, CovBLength = 2, CovBDot = 3, CovBProjection = 4, CovBRoot = 5, CovBDone = 6;
        struct CovBoundaryGraph { int phase; uint support; vec2 point; uint edge; uint count; vec3 a; vec3 b; CovPredicate squaredLength; float best; float height; float distance; bool ok; };
        CovBoundaryGraph covBoundaryBegin(uint support, vec2 point) {
            return CovBoundaryGraph(CovBBegin,support,point,0u,0u,vec3(0.0),vec3(0.0),CovPredicate(0,0.0),uintBitsToFloat(0x7f800000u),0.0,0.0,false);
        }
        bool covBoundaryStep(inout CovBoundaryGraph q, CovExactResult result, out CovExactJob job) {
            job = covScalarJob(0.0,1.0,false);
            if (q.phase == CovBBegin) {
                if (!covFinite(q.point.x) || !covFinite(q.point.y)) { q.phase = CovBDone; return false; }
                q.count = CovStaticWords[q.support+6u]; q.phase = CovBEdge;
            }
            if (q.phase == CovBEdge) {
                if (q.edge == q.count) { job = covScalarJob(q.best,0.0,true); q.phase = CovBRoot; return true; }
                uint boundary = covSupportTable(q.support,10u);
                q.a = covSupportPosition(q.support,CovStaticWords[boundary+q.edge]);
                q.b = covSupportPosition(q.support,CovStaticWords[boundary+(q.edge+1u)%q.count]);
                q.squaredLength = covSupportInvariant(q.support,CovStaticWords[q.support+5u]+q.edge);
                job = covDotJob(q.a,vec3(q.point,0.0),q.a,q.b); q.phase = CovBDot; return true;
            }
            if (q.phase == CovBDot) {
                if (q.squaredLength.sign <= 0 || !covFinite(q.squaredLength.value) || !covFinite(result.predicate.value)) { q.phase = CovBDone; return false; }
                job = covScalarJob(result.predicate.value,q.squaredLength.value,false); q.phase = CovBProjection; return true;
            }
            if (q.phase == CovBProjection) {
                if (!covFinite(result.predicate.value)) { q.phase = CovBDone; return false; }
                precise float t = covClamp(result.predicate.value,0.0,1.0);
                vec2 delta = vec2(covSub(q.b.x,q.a.x),covSub(q.b.y,q.a.y));
                vec2 nearest = vec2(covAdd(q.a.x,covMul(t,delta.x)),covAdd(q.a.y,covMul(t,delta.y)));
                vec2 remaining = vec2(covSub(q.point.x,nearest.x),covSub(q.point.y,nearest.y));
                precise float squared = covDot2(remaining,remaining), candidate = covAdd(q.a.z,covMul(t,covSub(q.b.z,q.a.z)));
                if (!covFinite(squared) || !covFinite(candidate)) { q.phase = CovBDone; return false; }
                if (covLess(squared,q.best)) { q.best = squared; q.height = candidate; }
                q.edge++; q.phase = CovBEdge; return false;
            }
            q.distance = result.predicate.value; q.ok = covFinite(q.distance); q.phase = CovBDone; return false;
        }
        bool covBoundary(uint support, vec2 point, out float height, out float distance) {
            CovBoundaryGraph q = covBoundaryBegin(support,point); CovExactResult result = covExactResultZero(); CovExactJob job;
            [[dont_unroll]] while (q.phase != CovBDone) if (covBoundaryStep(q,result,job)) result = covExactWorker(job);
            height = q.height; distance = q.distance; return q.ok;
        }

        """;
}
