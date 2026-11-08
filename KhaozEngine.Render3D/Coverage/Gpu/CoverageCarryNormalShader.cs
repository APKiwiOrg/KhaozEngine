namespace KhaozEngine.Render3D;

/// <summary>Shared strict normal continuation, immutable basis access and vector combination.</summary>
internal static class CoverageCarryNormalShader
{
    internal const string Source = """
        const int CovNormalUnitDone = 5;
        struct CovNormalUnit { int phase; uint code; vec3 inputValue; vec3 value; float divisor; };
        void covNormalUnitBegin(vec3 value, out CovNormalUnit q) {
            q = CovNormalUnit(0,0u,value,vec3(0.0),0.0);
        }
        bool covNormalUnitStep(inout CovNormalUnit q, CovExactResult result, out CovExactJob job) {
            job = covScalarJob(0.0,1.0,false);
            if (q.phase == 0) { q.phase = 1; job = covScalarJob(covDot3(q.inputValue,q.inputValue),0.0,true); return true; }
            if (q.phase == 1) {
                q.divisor = result.predicate.value;
                if (!covFinite3(q.inputValue) || !covFinite(q.divisor)) { q.code = 4u; q.phase = CovNormalUnitDone; return false; }
                if (!covGreater(q.divisor,0.0)) { q.code = 8u; q.phase = CovNormalUnitDone; return false; }
                q.phase = 2; job = covScalarJob(q.inputValue.x,q.divisor,false); return true;
            }
            if (q.phase == 2) { q.value.x = result.predicate.value; q.phase = 3; job = covScalarJob(q.inputValue.y,q.divisor,false); return true; }
            if (q.phase == 3) { q.value.y = result.predicate.value; q.phase = 4; job = covScalarJob(q.inputValue.z,q.divisor,false); return true; }
            if (q.phase == 4) {
                q.value.z = result.predicate.value; q.phase = CovNormalUnitDone;
                if (!covFinite3(q.value)) q.code = 4u;
            }
            return false;
        }
        uint covNormalWork(uint w, uint triangle) { return covRow(w,42u)+triangle*46u; }
        bool covMoved(uint w, uint vertex) {
            return CovWorkWords[covRow(w,44u)+vertex] != 0u;
        }
        vec3 covCombine(vec3 a, float x, vec3 b, float y, vec3 c, float z) {
            return vec3(covAdd(covAdd(covMul(a.x,x),covMul(b.x,y)),covMul(c.x,z)),
                covAdd(covAdd(covMul(a.y,x),covMul(b.y,y)),covMul(c.y,z)),covAdd(covAdd(covMul(a.z,x),covMul(b.z,y)),covMul(c.z,z)));
        }
        """;
}
