namespace KhaozEngine.Render3D;

/// <summary>Cold support access and entry points for resumable, source-ordered queries.</summary>
internal static class CoverageGpuSupportGlsl
{
    internal static string Source => DataSource + CoverageGpuTriangleHeightGlsl.Source
        + CoverageGpuHeightQueryGlsl.Source + CoverageGpuBoundaryQueryGlsl.Source + CoverageGpuOverlayQueryGlsl.Source;

    internal const string DataSource = """

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
        // Cold exact triangle areas, then boundary squared lengths, follow the candidate table as sign and F word pairs.
        // The grid's final offset is the candidate count. Existing tables and reserved words stay unchanged.
        CovPredicate covSupportInvariant(uint support, uint index) {
            uint end = CovStaticWords[covSupportTable(support,11u)+256u];
            uint p = covSupportTable(support,12u)+end+index*2u;
            return CovPredicate(int(CovStaticWords[p]),covRead(CovStaticWords[p+1u]));
        }
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

        """;
}
