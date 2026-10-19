namespace KhaozEngine.Render3D;

/// <summary>The pinned corner, canonical edge and barycentric height continuations.</summary>
internal static class CoverageGpuTriangleHeightGlsl
{
    internal const string Source = """

        const int CovTHBegin = 0, CovTHU = 1, CovTHV = 2, CovTHW = 3, CovTHArea = 4, CovTHDiv = 5, CovTHDone = 6;
        struct CovTriangleHeightGraph { int phase; CovTriangle triangle; vec2 point; CovPredicate u; CovPredicate v; CovPredicate w; vec3 a; vec3 b; bool edge; float height; };
        CovTriangleHeightGraph covTriangleHeightBegin(CovTriangle triangle, vec2 point) {
            CovPredicate zero = CovPredicate(0,0.0);
            return CovTriangleHeightGraph(CovTHBegin,triangle,point,zero,zero,zero,vec3(0.0),vec3(0.0),false,0.0);
        }
        bool covTriangleHeightStep(inout CovTriangleHeightGraph q, CovExactResult result, out CovExactJob job) {
            CovTriangle t = q.triangle; vec3 p = vec3(q.point,0.0);
            job = covEdgeJob(t.b,t.c,p);
            if (q.phase == CovTHBegin) {
                if (covEqual(p.x,t.a.x) && covEqual(p.y,t.a.y)) { q.height = covF(t.a.z); q.phase = CovTHDone; return false; }
                if (covEqual(p.x,t.b.x) && covEqual(p.y,t.b.y)) { q.height = covF(t.b.z); q.phase = CovTHDone; return false; }
                if (covEqual(p.x,t.c.x) && covEqual(p.y,t.c.y)) { q.height = covF(t.c.z); q.phase = CovTHDone; return false; }
                q.phase = CovTHU;
            } else if (q.phase == CovTHU) {
                q.u = result.predicate; job = covEdgeJob(t.c,t.a,p); q.phase = CovTHV;
            } else if (q.phase == CovTHV) {
                q.v = result.predicate; job = covEdgeJob(t.a,t.b,p); q.phase = CovTHW;
            } else if (q.phase == CovTHW) {
                q.w = result.predicate;
                if (q.u.sign == 0) { q.a = t.b; q.b = t.c; }
                else if (q.v.sign == 0) { q.a = t.c; q.b = t.a; }
                else if (q.w.sign == 0) { q.a = t.a; q.b = t.b; }
                else { job = covEdgeJob(t.a,t.b,t.c); q.phase = CovTHArea; return true; }
                if (covGreater(q.a.x,q.b.x) || covEqual(q.a.x,q.b.x) && (covGreater(q.a.y,q.b.y) || covEqual(q.a.y,q.b.y) && covGreater(q.a.z,q.b.z))) {
                    vec3 temporary = q.a; q.a = q.b; q.b = temporary;
                }
                precise float dx = covSub(q.b.x,q.a.x), dy = covSub(q.b.y,q.a.y);
                bool x = covGreaterOrEqual(covAbs(dx),covAbs(dy));
                job = covScalarJob(x ? covSub(p.x,q.a.x) : covSub(p.y,q.a.y),x ? dx : dy,false);
                q.edge = true; q.phase = CovTHDiv;
            } else if (q.phase == CovTHArea) {
                precise float numerator = covAdd(covAdd(covMul(q.u.value,t.a.z),covMul(q.v.value,t.b.z)),covMul(q.w.value,t.c.z));
                job = covScalarJob(numerator,result.predicate.value,false); q.phase = CovTHDiv;
            } else {
                precise float ratio = result.predicate.value;
                q.height = q.edge ? covAdd(covMul(covSub(1.0,ratio),q.a.z),covMul(ratio,q.b.z)) : ratio;
                q.phase = CovTHDone; return false;
            }
            return true;
        }
        float covTriangleHeight(CovTriangle triangle, vec2 point) {
            CovTriangleHeightGraph q = covTriangleHeightBegin(triangle,point); CovExactResult result = covExactResultZero(); CovExactJob job;
            [[dont_unroll]] while (q.phase != CovTHDone) if (covTriangleHeightStep(q,result,job)) result = covExactWorker(job);
            return q.height;
        }

        """;
}
