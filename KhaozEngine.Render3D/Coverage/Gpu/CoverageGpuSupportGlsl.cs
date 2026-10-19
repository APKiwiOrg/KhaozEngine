namespace KhaozEngine.Render3D;

/// <summary>Source-ordered grid, boundary and analytic overlay queries on cold packed supports.</summary>
internal static class CoverageGpuSupportGlsl
{
    // The composing kernel declares a readonly uint CovStaticWords[] storage buffer.
    // Header table offsets are relative to support. Source-triangle records retain all eight ints.
    internal static string Source => """

        struct CovTriangle { vec3 a; vec3 b; vec3 c; };
        struct CovSides { int a; int b; int c; };
        struct CovTriangleSides { CovSides a; CovSides b; CovSides c; };
        // Select whole scalar/struct fields, never dynamically index a vector or a private array.
        vec3 covTriangleCorner(CovTriangle t, int index) { if (index == 0) return t.a; if (index == 1) return t.b; return t.c; }
        CovSides covTriangleSidesAt(CovTriangleSides t, int index) { if (index == 0) return t.a; if (index == 1) return t.b; return t.c; }
        int covSide(CovSides s, int index) { if (index == 0) return s.a; if (index == 1) return s.b; return s.c; }
        void covTriangleSidesStore(inout CovTriangleSides t, int index, CovSides sides) {
            if (index == 0) t.a = sides; else if (index == 1) t.b = sides; else t.c = sides;
        }
        uint covSupportTable(uint support, uint field) { return support + CovStaticWords[support+field]; }
        vec3 covSupportPosition(uint support, uint index) {
            uint p = covSupportTable(support,7u) + index*3u;
            return vec3(covRead(CovStaticWords[p]),covRead(CovStaticWords[p+1u]),covRead(CovStaticWords[p+2u]));
        }
        CovTriangle covSupportTriangle(uint support, uint index) {
            uint t = covSupportTable(support,9u) + index*8u;
            return CovTriangle(covSupportPosition(support,CovStaticWords[t+5u]),
                covSupportPosition(support,CovStaticWords[t+6u]),covSupportPosition(support,CovStaticWords[t+7u]));
        }
        void covSupportBounds(CovTriangle t, out vec2 minimum, out vec2 maximum) {
            minimum = vec2(covMin(t.a.x,covMin(t.b.x,t.c.x)),covMin(t.a.y,covMin(t.b.y,t.c.y)));
            maximum = vec2(covMax(t.a.x,covMax(t.b.x,t.c.x)),covMax(t.a.y,covMax(t.b.y,t.c.y)));
        }
        float covEdgeHeight(vec3 a, vec3 b, vec2 p) {
            if (covGreater(a.x,b.x) || covEqual(a.x,b.x) && (covGreater(a.y,b.y) || covEqual(a.y,b.y) && covGreater(a.z,b.z))) {
                vec3 temporary = a; a = b; b = temporary;
            }
            precise float dx = covSub(b.x,a.x), dy = covSub(b.y,a.y);
            precise float t = covGreaterOrEqual(covAbs(dx),covAbs(dy)) ? covDiv(covSub(p.x,a.x),dx) : covDiv(covSub(p.y,a.y),dy);
            return covAdd(covMul(covSub(1.0,t),a.z),covMul(t,b.z));
        }
        float covTriangleHeight(CovTriangle t, vec2 p) {
            if (covEqual(p.x,t.a.x) && covEqual(p.y,t.a.y)) return covF(t.a.z);
            if (covEqual(p.x,t.b.x) && covEqual(p.y,t.b.y)) return covF(t.b.z);
            if (covEqual(p.x,t.c.x) && covEqual(p.y,t.c.y)) return covF(t.c.z);
            vec3 query = vec3(p,0.0);
            CovPredicate u = covEdge(t.b,t.c,query), v = covEdge(t.c,t.a,query), w = covEdge(t.a,t.b,query);
            if (u.sign == 0) return covEdgeHeight(t.b,t.c,p);
            if (v.sign == 0) return covEdgeHeight(t.c,t.a,p);
            if (w.sign == 0) return covEdgeHeight(t.a,t.b,p);
            return covDiv(covAdd(covAdd(covMul(u.value,t.a.z),covMul(v.value,t.b.z)),covMul(w.value,t.c.z)),covEdge(t.a,t.b,t.c).value);
        }
        bool covHeight(uint support, vec2 point, out float height, out int sourceTriangle) {
            height = 0.0; sourceTriangle = -1;
            vec2 minimum = vec2(covRead(CovStaticWords[support+1u]),covRead(CovStaticWords[support+2u]));
            vec2 maximum = vec2(covRead(CovStaticWords[support+3u]),covRead(CovStaticWords[support+4u]));
            if (!covFinite(point.x) || !covFinite(point.y) || covLess(point.x,minimum.x) || covGreater(point.x,maximum.x)
                || covLess(point.y,minimum.y) || covGreater(point.y,maximum.y)) return false;
            uint cell = uint(covCell(point.y,minimum.y,maximum.y)*16 + covCell(point.x,minimum.x,maximum.x));
            uint offsets = covSupportTable(support,11u), candidates = covSupportTable(support,12u);
            for (uint i = CovStaticWords[offsets+cell]; i < CovStaticWords[offsets+cell+1u]; i++) {
                uint index = CovStaticWords[candidates+i];
                CovTriangle t = covSupportTriangle(support,index);
                vec2 lo, hi; covSupportBounds(t,lo,hi);
                if (covLess(point.x,lo.x) || covGreater(point.x,hi.x) || covLess(point.y,lo.y) || covGreater(point.y,hi.y)) continue;
                vec3 p = vec3(point,0.0);
                CovPredicate area = covEdge(t.a,t.b,t.c), u = covEdge(t.b,t.c,p), v = covEdge(t.c,t.a,p), w = covEdge(t.a,t.b,p);
                if (!covFinite(area.value) || area.sign == 0 || !covFinite(u.value) || !covFinite(v.value) || !covFinite(w.value)) return false;
                if (u.sign*area.sign < 0 || v.sign*area.sign < 0 || w.sign*area.sign < 0) continue;
                height = covTriangleHeight(t,point);
                if (!covFinite(height)) return false;
                sourceTriangle = int(CovStaticWords[covSupportTable(support,8u)+index]);
                return true;
            }
            return false;
        }
        bool covBoundary(uint support, vec2 point, out float height, out float distance) {
            height = 0.0; distance = 0.0;
            if (!covFinite(point.x) || !covFinite(point.y)) return false;
            precise float best = uintBitsToFloat(0x7f800000u);
            uint count = CovStaticWords[support+6u], boundary = covSupportTable(support,10u);
            for (uint edge = 0u; edge < count; edge++) {
                vec3 a = covSupportPosition(support,CovStaticWords[boundary+edge]);
                vec3 b = covSupportPosition(support,CovStaticWords[boundary+(edge+1u)%count]);
                vec2 delta = vec2(covSub(b.x,a.x),covSub(b.y,a.y));
                CovPredicate length = covDot(a,b,a,b), dot = covDot(a,vec3(point,0.0),a,b);
                if (length.sign <= 0 || !covFinite(length.value) || !covFinite(dot.value)) return false;
                precise float projection = covDiv(dot.value,length.value);
                if (!covFinite(projection)) return false;
                precise float t = covClamp(projection,0.0,1.0);
                vec2 nearest = vec2(covAdd(a.x,covMul(t,delta.x)),covAdd(a.y,covMul(t,delta.y)));
                vec2 remaining = vec2(covSub(point.x,nearest.x),covSub(point.y,nearest.y));
                precise float squared = covDot2(remaining,remaining);
                precise float candidate = covAdd(a.z,covMul(t,covSub(b.z,a.z)));
                if (!covFinite(squared) || !covFinite(candidate)) return false;
                if (covLess(squared,best)) { best = squared; height = candidate; }
            }
            distance = covSqrt(best);
            return covFinite(distance);
        }
        bool covOverlayInside(CovTriangle t, vec3 p, int area, out CovSides sides, out bool inside) {
            CovPredicate a = covEdge(t.a,t.b,p), b = covEdge(t.b,t.c,p), c = covEdge(t.c,t.a,p);
            sides = CovSides(a.sign,b.sign,c.sign);
            inside = sides.a*area >= 0 && sides.b*area >= 0 && sides.c*area >= 0;
            return covFinite(a.value) && covFinite(b.value) && covFinite(c.value);
        }
        bool covOverlayAccumulate(CovTriangle host, CovTriangle sheet, vec3 p, inout float maximum) {
            precise float h = covTriangleHeight(host,p.xy), s = covTriangleHeight(sheet,p.xy), difference = covSub(h,s);
            if (!covFinite(h) || !covFinite(s) || !covFinite(difference)) return false;
            maximum = covMax(maximum,difference);
            return true;
        }
        bool covOverlayIntersection(CovTriangle host, CovTriangle sheet, vec3 a, vec3 b, vec3 s, vec3 t,
            int ca, int cb, int ac, int ad, inout float maximum) {
            if (ca*cb > 0 || ac*ad > 0) return true;
            if (ca == 0 && cb == 0) return true;
            vec3 p;
            if (ca == 0) p = a;
            else if (cb == 0) p = b;
            else {
                precise float quotient = covCrossing(s,t,a,b);
                if (!covFinite(quotient)) return false;
                precise float u = covClamp(quotient,0.0,1.0);
                p = vec3(covAdd(a.x,covMul(u,covSub(b.x,a.x))),covAdd(a.y,covMul(u,covSub(b.y,a.y))),covAdd(a.z,covMul(u,covSub(b.z,a.z))));
            }
            return covFinite3(p) && covOverlayAccumulate(host,sheet,p,maximum);
        }
        bool covOverlayPair(CovTriangle host, CovTriangle sheet, CovPredicate hostArea, inout float maximum) {
            if (!covFinite3(sheet.a) || !covFinite3(sheet.b) || !covFinite3(sheet.c)) return false;
            CovPredicate sheetArea = covEdge(sheet.a,sheet.b,sheet.c);
            if (sheetArea.sign == 0 || !covFinite(sheetArea.value)) return false;
            CovSides zero = CovSides(0,0,0);
            CovTriangleSides hostSides = CovTriangleSides(zero,zero,zero), sheetSides = CovTriangleSides(zero,zero,zero);
            for (int i = 0; i < 3; i++) {
                bool inside; CovSides sides;
                vec3 h = covTriangleCorner(host,i), s = covTriangleCorner(sheet,i);
                if (!covOverlayInside(sheet,h,sheetArea.sign,sides,inside)) return false;
                covTriangleSidesStore(hostSides,i,sides);
                if (inside && !covOverlayAccumulate(host,sheet,h,maximum)) return false;
                if (!covOverlayInside(host,s,hostArea.sign,sides,inside)) return false;
                covTriangleSidesStore(sheetSides,i,sides);
                if (inside && !covOverlayAccumulate(host,sheet,s,maximum)) return false;
            }
            for (int i = 0; i < 3; i++) {
                int nextI = (i+1)%3;
                CovSides h0 = covTriangleSidesAt(hostSides,i), h1 = covTriangleSidesAt(hostSides,nextI);
                for (int j = 0; j < 3; j++) {
                    int nextJ = (j+1)%3;
                    CovSides s0 = covTriangleSidesAt(sheetSides,j), s1 = covTriangleSidesAt(sheetSides,nextJ);
                    if (!covOverlayIntersection(host,sheet,covTriangleCorner(host,i),covTriangleCorner(host,nextI),
                        covTriangleCorner(sheet,j),covTriangleCorner(sheet,nextJ),covSide(h0,j),covSide(h1,j),covSide(s0,i),covSide(s1,i),maximum)) return false;
                }
            }
            return true;
        }
        bool covOverlay(uint support, vec3 a, vec3 b, vec3 c, out float maximum) {
            maximum = uintBitsToFloat(0xff800000u);
            if (!covFinite3(a) || !covFinite3(b) || !covFinite3(c)) return false;
            CovPredicate area = covEdge(a,b,c);
            if (area.sign == 0 || !covFinite(area.value)) return false;
            CovTriangle host = CovTriangle(a,b,c);
            for (uint i = 0u; i < CovStaticWords[support+5u]; i++) {
                if (!covOverlayPair(host,covSupportTriangle(support,i),area,maximum)) return false;
            }
            return true;
        }

        """;

}
