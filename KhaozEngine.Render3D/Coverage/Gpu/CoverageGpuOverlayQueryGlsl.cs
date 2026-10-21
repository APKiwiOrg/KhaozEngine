namespace KhaozEngine.Render3D;

/// <summary>The finite corner, intersection, B30 and dual-height overlay graph.</summary>
internal static class CoverageGpuOverlayQueryGlsl
{
    internal const string Source = """

        const int CovOBegin = 0, CovOHostArea = 1, CovOPair = 2, CovOSheetArea = 3, CovOInside = 4, CovOSide = 5,
            CovONextCorner = 6, CovOIntersection = 7, CovONextIntersection = 8, CovOCrossing = 9, CovOHeightBegin = 10, CovOHeight = 11, CovODone = 12;
        struct CovOverlayGraph {
            int phase; uint support; CovTriangle host; CovTriangle sheet; CovPredicate hostArea; CovPredicate sheetArea; uint cursor; uint limit;
            int corner; int which; int side; CovSides sides; CovTriangleSides hostSides; CovTriangleSides sheetSides; bool inside;
            int i; int j; vec3 point; int resume; int heightPass; float hostHeight; CovTriangleHeightGraph heightGraph;
            CovCrossingGraph crossing; float maximum; bool ok; bool sampled; int ordinal; float sampleValue;
        };
        CovOverlayGraph covOverlayBegin(uint support, vec3 a, vec3 b, vec3 c) {
            CovOverlayGraph q;
            q.phase = CovOBegin; q.support = support; q.host = CovTriangle(a,b,c); q.sheet = q.host; q.cursor = 0u; q.limit = CovStaticWords[support+5u];
            q.hostArea = CovPredicate(0,0.0); q.sheetArea = q.hostArea; q.corner = 0; q.which = 0; q.side = 0; q.sides = CovSides(0,0,0);
            q.hostSides = CovTriangleSides(q.sides,q.sides,q.sides); q.sheetSides = q.hostSides; q.inside = false;
            q.i = 0; q.j = 0; q.point = vec3(0.0); q.resume = CovODone; q.heightPass = 0; q.hostHeight = 0.0;
            q.heightGraph = covTriangleHeightBegin(q.host,vec2(0.0));
            q.crossing = covCrossingBegin(vec3(0.0),vec3(0.0),vec3(0.0),vec3(0.0));
            q.maximum = uintBitsToFloat(0xff800000u); q.ok = false; q.sampled = false; q.ordinal = 0; q.sampleValue = 0.0;
            return q;
        }
        bool covOverlayStep(inout CovOverlayGraph q, CovExactResult result, out CovExactJob job) {
            q.sampled = false;
            job = covScalarJob(0.0,1.0,false);
            if (q.phase == CovOBegin) {
                if (!covFinite3(q.host.a) || !covFinite3(q.host.b) || !covFinite3(q.host.c)) { q.phase = CovODone; return false; }
                job = covEdgeJob(q.host.a,q.host.b,q.host.c); q.phase = CovOHostArea; return true;
            }
            if (q.phase == CovOHostArea) {
                if (result.predicate.sign == 0 || !covFinite(result.predicate.value)) { q.phase = CovODone; return false; }
                q.hostArea = result.predicate; q.phase = CovOPair;
            }
            if (q.phase == CovOPair) {
                if (q.cursor == q.limit) { q.ok = true; q.phase = CovODone; return false; }
                q.sheet = covSupportTriangle(q.support,q.cursor);
                if (!covFinite3(q.sheet.a) || !covFinite3(q.sheet.b) || !covFinite3(q.sheet.c)) { q.phase = CovODone; return false; }
                q.sheetArea = covSupportInvariant(q.support,q.cursor); q.phase = CovOSheetArea;
            }
            if (q.phase == CovOSheetArea) {
                if (q.sheetArea.sign == 0 || !covFinite(q.sheetArea.value)) { q.phase = CovODone; return false; }
                q.corner = 0; q.which = 0; q.side = 0; q.phase = CovOInside;
            }
            if (q.phase == CovOInside) {
                if (q.side == 0) { q.sides = CovSides(0,0,0); q.inside = true; }
                CovTriangle triangle; vec3 point;
                if (q.which == 0) { triangle = q.sheet; point = covTriangleCorner(q.host,q.corner); }
                else { triangle = q.host; point = covTriangleCorner(q.sheet,q.corner); }
                job = covEdgeJob(covTriangleCorner(triangle,q.side),covTriangleCorner(triangle,(q.side+1)%3),point);
                q.phase = CovOSide; return true;
            }
            if (q.phase == CovOSide) {
                if (!covFinite(result.predicate.value)) { q.phase = CovODone; return false; }
                int sign = result.predicate.sign;
                if (q.side == 0) q.sides.a = sign; else if (q.side == 1) q.sides.b = sign; else q.sides.c = sign;
                if (sign*(q.which == 0 ? q.sheetArea.sign : q.hostArea.sign) < 0) q.inside = false;
                q.side++;
                if (q.side < 3) { q.phase = CovOInside; return false; }
                if (q.which == 0) covTriangleSidesStore(q.hostSides,q.corner,q.sides);
                else covTriangleSidesStore(q.sheetSides,q.corner,q.sides);
                q.phase = CovONextCorner;
                if (q.inside) {
                    q.point = q.which == 0 ? covTriangleCorner(q.host,q.corner) : covTriangleCorner(q.sheet,q.corner);
                    q.resume = CovONextCorner; q.phase = CovOHeightBegin;
                }
            }
            if (q.phase == CovONextCorner) {
                q.side = 0;
                if (q.which == 0) { q.which = 1; q.phase = CovOInside; }
                else { q.which = 0; q.corner++; q.phase = q.corner < 3 ? CovOInside : CovOIntersection; q.i = 0; q.j = 0; }
                return false;
            }
            if (q.phase == CovONextIntersection) {
                q.j++;
                if (q.j == 3) { q.j = 0; q.i++; }
                if (q.i == 3) { q.cursor++; q.phase = CovOPair; return false; }
                q.phase = CovOIntersection;
            }
            if (q.phase == CovOIntersection) {
                int nextI = (q.i+1)%3, nextJ = (q.j+1)%3;
                int ca = covSide(covTriangleSidesAt(q.hostSides,q.i),q.j), cb = covSide(covTriangleSidesAt(q.hostSides,nextI),q.j);
                int ac = covSide(covTriangleSidesAt(q.sheetSides,q.j),q.i), ad = covSide(covTriangleSidesAt(q.sheetSides,nextJ),q.i);
                if (ca*cb > 0 || ac*ad > 0 || ca == 0 && cb == 0) { q.phase = CovONextIntersection; return false; }
                vec3 a = covTriangleCorner(q.host,q.i), b = covTriangleCorner(q.host,nextI);
                q.resume = CovONextIntersection;
                if (ca == 0) q.point = a;
                else if (cb == 0) q.point = b;
                else {
                    q.crossing = covCrossingBegin(covTriangleCorner(q.sheet,q.j),covTriangleCorner(q.sheet,nextJ),a,b);
                    q.phase = CovOCrossing;
                }
                if (q.phase != CovOCrossing) {
                    if (!covFinite3(q.point)) { q.phase = CovODone; return false; }
                    q.phase = CovOHeightBegin;
                }
            }
            if (q.phase == CovOCrossing) {
                bool pending = covCrossingStep(q.crossing,result,job);
                if (q.crossing.phase != CovCrossDone) return pending;
                if (!covFinite(q.crossing.value)) { q.phase = CovODone; return false; }
                precise float u = covClamp(q.crossing.value,0.0,1.0);
                vec3 a = q.crossing.a, b = q.crossing.b;
                q.point = vec3(covAdd(a.x,covMul(u,covSub(b.x,a.x))),covAdd(a.y,covMul(u,covSub(b.y,a.y))),covAdd(a.z,covMul(u,covSub(b.z,a.z))));
                if (!covFinite3(q.point)) { q.phase = CovODone; return false; }
                q.phase = CovOHeightBegin;
            }
            if (q.phase == CovOHeightBegin) {
                q.heightPass = 0; q.heightGraph = covTriangleHeightBegin(q.host,q.point.xy);
                q.heightGraph.area = q.hostArea; q.heightGraph.cachedArea = true; q.phase = CovOHeight;
            }
            if (q.phase == CovOHeight) {
                bool pending = covTriangleHeightStep(q.heightGraph,result,job);
                if (q.heightGraph.phase != CovTHDone) return pending;
                if (q.heightPass == 0) {
                    q.hostHeight = q.heightGraph.height; q.heightPass = 1;
                    q.heightGraph = covTriangleHeightBegin(q.sheet,q.point.xy);
                    q.heightGraph.area = q.sheetArea; q.heightGraph.cachedArea = true; return false;
                }
                precise float difference = covSub(q.hostHeight,q.heightGraph.height);
                if (!covFinite(q.hostHeight) || !covFinite(q.heightGraph.height) || !covFinite(difference)) { q.phase = CovODone; return false; }
                q.sampled = true; q.sampleValue = difference;
                q.ordinal = q.resume == CovONextCorner ? q.corner*2+q.which : 6+q.i*3+q.j;
                q.maximum = covMax(q.maximum,difference); q.phase = q.resume;
            }
            return false;
        }
        bool covOverlay(uint support, vec3 a, vec3 b, vec3 c, out float maximum) {
            CovOverlayGraph q = covOverlayBegin(support,a,b,c); CovExactResult result = covExactResultZero(); CovExactJob job;
            [[dont_unroll]] while (q.phase != CovODone) if (covOverlayStep(q,result,job)) result = covExactWorker(job);
            maximum = q.maximum; return q.ok;
        }

        """;
}
