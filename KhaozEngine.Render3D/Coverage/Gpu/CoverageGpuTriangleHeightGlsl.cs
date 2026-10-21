namespace KhaozEngine.Render3D;

/// <summary>The pinned corner, canonical edge and barycentric height continuations.</summary>
internal static class CoverageGpuTriangleHeightGlsl
{
    internal const string Source = """

        const int CovTHBegin = 0, CovTHU = 1, CovTHV = 2, CovTHW = 3, CovTHArea = 4, CovTHDiv = 5, CovTHDone = 6;
        struct CovTriangleHeightGraph { int phase; CovTriangle triangle; vec2 point; CovPredicate u; CovPredicate v; CovPredicate w; CovPredicate area; bool cachedSides; bool cachedArea; vec3 a; vec3 b; bool edge; float height; };
        CovTriangleHeightGraph covTriangleHeightBegin(CovTriangle triangle, vec2 point) {
            CovPredicate zero = CovPredicate(0,0.0);
            return CovTriangleHeightGraph(CovTHBegin,triangle,point,zero,zero,zero,zero,false,false,vec3(0.0),vec3(0.0),false,0.0);
        }
        bool covTriangleHeightStep(inout CovTriangleHeightGraph q, CovExactResult result, out CovExactJob job) {
            CovTriangle t = q.triangle; vec3 p = vec3(q.point,0.0);
            job = covEdgeJob(t.b,t.c,p);
            if (q.phase == CovTHBegin) {
                if (covEqual(p.x,t.a.x) && covEqual(p.y,t.a.y)) { q.height = covF(t.a.z); q.phase = CovTHDone; return false; }
                if (covEqual(p.x,t.b.x) && covEqual(p.y,t.b.y)) { q.height = covF(t.b.z); q.phase = CovTHDone; return false; }
                if (covEqual(p.x,t.c.x) && covEqual(p.y,t.c.y)) { q.height = covF(t.c.z); q.phase = CovTHDone; return false; }
                if (!q.cachedSides) { q.phase = CovTHU; return true; }
                result.predicate = q.w; q.phase = CovTHW;
            }
            if (q.phase == CovTHU) {
                q.u = result.predicate; job = covEdgeJob(t.c,t.a,p); q.phase = CovTHV; return true;
            }
            if (q.phase == CovTHV) {
                q.v = result.predicate; job = covEdgeJob(t.a,t.b,p); q.phase = CovTHW; return true;
            }
            if (q.phase == CovTHW) {
                q.w = result.predicate;
                if (q.u.sign == 0) { q.a = t.b; q.b = t.c; }
                else if (q.v.sign == 0) { q.a = t.c; q.b = t.a; }
                else if (q.w.sign == 0) { q.a = t.a; q.b = t.b; }
                else {
                    q.phase = CovTHArea;
                    if (!q.cachedArea) { job = covEdgeJob(t.a,t.b,t.c); return true; }
                    result.predicate = q.area;
                }
                if (q.phase != CovTHArea) {
                    if (covGreater(q.a.x,q.b.x) || covEqual(q.a.x,q.b.x) && (covGreater(q.a.y,q.b.y) || covEqual(q.a.y,q.b.y) && covGreater(q.a.z,q.b.z))) {
                        vec3 temporary = q.a; q.a = q.b; q.b = temporary;
                    }
                    precise float dx = covSub(q.b.x,q.a.x), dy = covSub(q.b.y,q.a.y);
                    bool x = covGreaterOrEqual(covAbs(dx),covAbs(dy));
                    job = covScalarJob(x ? covSub(p.x,q.a.x) : covSub(p.y,q.a.y),x ? dx : dy,false);
                    q.edge = true; q.phase = CovTHDiv; return true;
                }
            }
            if (q.phase == CovTHArea) {
                precise float numerator = covAdd(covAdd(covMul(q.u.value,t.a.z),covMul(q.v.value,t.b.z)),covMul(q.w.value,t.c.z));
                job = covScalarJob(numerator,result.predicate.value,false); q.phase = CovTHDiv; return true;
            } else {
                precise float ratio = result.predicate.value;
                q.height = q.edge ? covAdd(covMul(covSub(1.0,ratio),q.a.z),covMul(ratio,q.b.z)) : ratio;
                q.phase = CovTHDone; return false;
            }
        }
        float covTriangleHeight(CovTriangle triangle, vec2 point) {
            CovTriangleHeightGraph q = covTriangleHeightBegin(triangle,point); CovExactResult result = covExactResultZero(); CovExactJob job;
            [[dont_unroll]] while (q.phase != CovTHDone) if (covTriangleHeightStep(q,result,job)) result = covExactWorker(job);
            return q.height;
        }

        """;
}
