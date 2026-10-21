namespace KhaozEngine.Tests.Gpu;

/// <summary>One face owns its entire basis. The single worker pump preserves C8 without a second dispatch.</summary>
internal static class CoverageCarryBasisShader
{
    internal const string Source = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], triangle = carryIndex();
            if (triangle >= CovFrameWords[f+7u]) return;
            uint p = covNormalWork(w,triangle);
            CovWorkWords[p+44u] = 0u; CovWorkWords[p+45u] = 0u;
            uint h = CovFrameWords[f], indices = covTable(h,20u)+triangle*3u;
            uint ai = CovStaticWords[indices], bi = CovStaticWords[indices+1u], ci = CovStaticWords[indices+2u];
            if (!covMoved(w,ai) && !covMoved(w,bi) && !covMoved(w,ci)) return;
            uint before = covRow(w,4u), after = covRow(w,8u), a = ai*16u, b = bi*16u, c = ci*16u;
            vec3 u = covSub3(covWork3(before+b),covWork3(before+a)), v = covSub3(covWork3(before+c),covWork3(before+a));
            vec3 s = covSub3(covWork3(after+b),covWork3(after+a)), t = covSub3(covWork3(after+c),covWork3(after+a));
            vec3 normal = covCross(u,v), finalNormal = covCross(s,t), n = vec3(0.0), m = vec3(0.0);
            float alignment = 0.0, beforeDet = 0.0, afterDet = 0.0, beforeScale = 0.0, afterScale = 0.0;
            vec3 numerator = vec3(0.0), divided = vec3(0.0);
            int vectorIndex = 0, axis = 0; bool pending = false;
            uint code = 0u; int phase = 0;
            CovNormalUnit unit; covNormalUnitBegin(normal,unit);
            CovExactResult result = covExactResultZero();
            [[dont_unroll]] while (phase < 6 && code == 0u) {
                CovExactJob job = covScalarJob(0.0,1.0,false); bool request = false;
                if (phase < 2) {
                    request = covNormalUnitStep(unit,result,job);
                    if (unit.phase == CovNormalUnitDone) {
                        code = unit.code;
                        if (phase == 0) { n = unit.value; covNormalUnitBegin(finalNormal,unit); phase = 1; }
                        else { m = unit.value; phase = 2; }
                    }
                } else if (phase == 2) {
                    alignment = covDot3(normal,finalNormal);
                    beforeDet = covDot3(u,covCross(v,n)); afterDet = covDot3(s,covCross(t,m));
                    job = covScalarJob(covMul(covDot3(u,u),covDot3(v,v)),0.0,true); request = true; phase = 3;
                } else if (phase == 3) {
                    beforeScale = result.predicate.value;
                    job = covScalarJob(covMul(covDot3(s,s),covDot3(t,t)),0.0,true); request = true; phase = 4;
                } else if (phase == 4) {
                    afterScale = result.predicate.value;
                    if (!covFinite(alignment) || !covFinite(beforeDet) || !covFinite(afterDet) || !covFinite(beforeScale) || !covFinite(afterScale)) code = 4u;
                    else if (!covGreater(alignment,0.0) || !covGreater(beforeDet,0.0) || !covGreater(afterDet,0.0)
                        || covLessOrEqual(beforeDet,covMul(beforeScale,1e-12)) || covLessOrEqual(afterDet,covMul(afterScale,1e-12))) code = 8u;
                    phase = 5;
                } else {
                    if (pending) {
                        if (axis == 0) divided.x = result.predicate.value;
                        else if (axis == 1) divided.y = result.predicate.value;
                        else divided.z = result.predicate.value;
                        axis++; pending = false;
                        if (axis == 3) {
                            covWrite3(p+18u+uint(vectorIndex)*3u,divided);
                            if (!covFinite3(divided)) code = 4u;
                            vectorIndex++; axis = 0;
                            if (vectorIndex == 6) { phase = 6; continue; }
                        }
                    }
                    if (axis == 0) {
                        if (vectorIndex == 0) numerator = covCross(v,n);
                        else if (vectorIndex == 1) numerator = covCross(n,u);
                        else if (vectorIndex == 2) numerator = normal;
                        else if (vectorIndex == 3) numerator = covCross(t,m);
                        else if (vectorIndex == 4) numerator = covCross(m,s);
                        else numerator = finalNormal;
                    }
                    float value = axis == 0 ? numerator.x : (axis == 1 ? numerator.y : numerator.z);
                    job = covScalarJob(value,vectorIndex < 3 ? beforeDet : afterDet,false); request = true; pending = true;
                }
                if (request) result = covExactWorker(job);
            }
            if (code == 0u) {
                covWrite3(p,u); covWrite3(p+3u,v); covWrite3(p+6u,n);
                covWrite3(p+9u,s); covWrite3(p+12u,t); covWrite3(p+15u,m);
            }
            CovWorkWords[p+44u] = code; CovWorkWords[p+45u] = code == 0u ? 2u : 3u;
            covEvent(w,triangle,code);
        }
        """;
}
