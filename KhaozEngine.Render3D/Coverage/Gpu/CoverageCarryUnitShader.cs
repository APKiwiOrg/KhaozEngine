namespace KhaozEngine.Render3D;

/// <summary>Shared skin and refinement normalization continuation.</summary>
internal static class CoverageCarryUnitShader
{
    internal const string Source = """
        const int CovUnitDone = 5;
        struct CovUnit { int phase; vec3 inputValue; vec3 value; vec3 fallback; float minimum; float divisor; };
        void covUnitBegin(vec3 value, vec3 fallback, float minimum, out CovUnit q) {
            q = CovUnit(0,value,vec3(0.0),fallback,minimum,0.0);
        }
        bool covUnitStep(inout CovUnit q, CovExactResult result, out CovExactJob job) {
            job = covScalarJob(0.0,1.0,false);
            if (q.phase == 0) { q.phase = 1; job = covScalarJob(covDot3(q.inputValue,q.inputValue),0.0,true); return true; }
            if (q.phase == 1) {
                q.divisor = result.predicate.value;
                if (q.minimum >= 0.0 && !covGreater(q.divisor,q.minimum)) { q.value = q.fallback; q.phase = CovUnitDone; return false; }
                q.phase = 2; job = covScalarJob(q.inputValue.x,q.divisor,false); return true;
            }
            if (q.phase == 2) { q.value.x = result.predicate.value; q.phase = 3; job = covScalarJob(q.inputValue.y,q.divisor,false); return true; }
            if (q.phase == 3) { q.value.y = result.predicate.value; q.phase = 4; job = covScalarJob(q.inputValue.z,q.divisor,false); return true; }
            if (q.phase == 4) { q.value.z = result.predicate.value; q.phase = CovUnitDone; }
            return false;
        }
        """;
}
