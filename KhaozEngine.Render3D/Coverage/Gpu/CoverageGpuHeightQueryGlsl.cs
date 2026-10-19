namespace KhaozEngine.Render3D;

/// <summary>Inclusive, cell-only height traversal with first bad-candidate abort.</summary>
internal static class CoverageGpuHeightQueryGlsl
{
    internal const string Source = """

        const int CovHBegin = 0, CovHCellY = 1, CovHCellX = 2, CovHCandidate = 3, CovHArea = 4, CovHU = 5, CovHV = 6, CovHW = 7, CovHHeight = 8, CovHDone = 9;
        struct CovHeightGraph { int phase; uint support; vec2 point; vec2 minimum; vec2 maximum; int cellRow; uint cursor; uint end; uint index; CovTriangle triangle; CovPredicate area; CovPredicate u; CovPredicate v; CovTriangleHeightGraph heightGraph; bool ok; float height; int sourceTriangle; };
        CovHeightGraph covHeightBegin(uint support, vec2 point) {
            CovTriangle t = CovTriangle(vec3(0.0),vec3(0.0),vec3(0.0)); CovPredicate zero = CovPredicate(0,0.0);
            return CovHeightGraph(CovHBegin,support,point,vec2(0.0),vec2(0.0),0,0u,0u,0u,t,zero,zero,zero,covTriangleHeightBegin(t,point),false,0.0,-1);
        }
        bool covHeightStep(inout CovHeightGraph q, CovExactResult result, out CovExactJob job) {
            job = covScalarJob(0.0,1.0,false);
            if (q.phase == CovHBegin) {
                q.minimum = vec2(covRead(CovStaticWords[q.support+1u]),covRead(CovStaticWords[q.support+2u]));
                q.maximum = vec2(covRead(CovStaticWords[q.support+3u]),covRead(CovStaticWords[q.support+4u]));
                if (!covFinite(q.point.x) || !covFinite(q.point.y) || covLess(q.point.x,q.minimum.x) || covGreater(q.point.x,q.maximum.x)
                    || covLess(q.point.y,q.minimum.y) || covGreater(q.point.y,q.maximum.y)) { q.phase = CovHDone; return false; }
                job = covScalarJob(covSub(q.point.y,q.minimum.y),covSub(q.maximum.y,q.minimum.y),false); q.phase = CovHCellY; return true;
            }
            if (q.phase == CovHCellY) {
                q.cellRow = covCellRatio(result.predicate.value)*16;
                job = covScalarJob(covSub(q.point.x,q.minimum.x),covSub(q.maximum.x,q.minimum.x),false); q.phase = CovHCellX; return true;
            }
            if (q.phase == CovHCellX) {
                uint cell = uint(q.cellRow+covCellRatio(result.predicate.value)), offsets = covSupportTable(q.support,11u);
                q.cursor = CovStaticWords[offsets+cell]; q.end = CovStaticWords[offsets+cell+1u]; q.phase = CovHCandidate;
            }
            if (q.phase == CovHCandidate) {
                [[dont_unroll]] while (q.cursor < q.end) {
                    q.index = CovStaticWords[covSupportTable(q.support,12u)+q.cursor];
                    q.triangle = covSupportTriangle(q.support,q.index);
                    vec2 lo, hi; covSupportBounds(q.triangle,lo,hi);
                    if (covLess(q.point.x,lo.x) || covGreater(q.point.x,hi.x) || covLess(q.point.y,lo.y) || covGreater(q.point.y,hi.y)) { q.cursor++; continue; }
                    job = covEdgeJob(q.triangle.a,q.triangle.b,q.triangle.c); q.phase = CovHArea; return true;
                }
                q.phase = CovHDone; return false;
            }
            CovTriangle t = q.triangle; vec3 p = vec3(q.point,0.0);
            if (q.phase == CovHArea) { q.area = result.predicate; job = covEdgeJob(t.b,t.c,p); q.phase = CovHU; return true; }
            if (q.phase == CovHU) { q.u = result.predicate; job = covEdgeJob(t.c,t.a,p); q.phase = CovHV; return true; }
            if (q.phase == CovHV) { q.v = result.predicate; job = covEdgeJob(t.a,t.b,p); q.phase = CovHW; return true; }
            if (q.phase == CovHW) {
                CovPredicate w = result.predicate;
                if (!covFinite(q.area.value) || q.area.sign == 0 || !covFinite(q.u.value) || !covFinite(q.v.value) || !covFinite(w.value)) { q.phase = CovHDone; return false; }
                if (q.u.sign*q.area.sign < 0 || q.v.sign*q.area.sign < 0 || w.sign*q.area.sign < 0) { q.cursor++; q.phase = CovHCandidate; return false; }
                q.heightGraph = covTriangleHeightBegin(t,q.point); q.phase = CovHHeight;
            }
            bool pending = covTriangleHeightStep(q.heightGraph,result,job);
            if (q.heightGraph.phase == CovTHDone) {
                q.height = q.heightGraph.height;
                q.ok = covFinite(q.height);
                if (q.ok) q.sourceTriangle = int(CovStaticWords[covSupportTable(q.support,8u)+q.index]);
                q.phase = CovHDone;
            }
            return pending;
        }
        bool covHeight(uint support, vec2 point, out float height, out int sourceTriangle) {
            CovHeightGraph q = covHeightBegin(support,point); CovExactResult result = covExactResultZero(); CovExactJob job;
            [[dont_unroll]] while (q.phase != CovHDone) if (covHeightStep(q,result,job)) result = covExactWorker(job);
            height = q.height; sourceTriangle = q.sourceTriangle; return q.ok;
        }

        """;
}
