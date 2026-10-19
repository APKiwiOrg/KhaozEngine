namespace KhaozEngine.Render3D;

/// <summary>Pinned basis and strict Unit continuations. The caller owns their single exact-worker pump.</summary>
internal static class CoverageGpuNormalBasisGlsl
{
    internal const string Unit = """
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
        """;

    internal const string Prepare = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], triangle = gl_GlobalInvocationID.x;
            if (triangle >= CovFrameWords[f+7u] || covCompletedFailure(w)) return;
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
            uint code = 0u; int phase = 0;
            CovNormalUnit unit; covNormalUnitBegin(normal,unit);
            CovExactResult result = covExactResultZero();
            [[dont_unroll]] while (phase < 5 && code == 0u) {
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
                } else {
                    afterScale = result.predicate.value;
                    if (!covFinite(alignment) || !covFinite(beforeDet) || !covFinite(afterDet) || !covFinite(beforeScale) || !covFinite(afterScale)) code = 4u;
                    else if (!covGreater(alignment,0.0) || !covGreater(beforeDet,0.0) || !covGreater(afterDet,0.0)
                        || covLessOrEqual(beforeDet,covMul(beforeScale,1e-12)) || covLessOrEqual(afterDet,covMul(afterScale,1e-12))) code = 8u;
                    phase = 5;
                }
                if (request) result = covExactWorker(job);
            }
            if (code == 0u) {
                covWrite3(p,u); covWrite3(p+3u,v); covWrite3(p+6u,n); covWrite3(p+9u,s); covWrite3(p+12u,t); covWrite3(p+15u,m);
                covWrite3(p+36u,normal); covWrite3(p+39u,finalNormal);
                CovWorkWords[p+42u] = floatBitsToUint(beforeDet); CovWorkWords[p+43u] = floatBitsToUint(afterDet);
            }
            CovWorkWords[p+44u] = code; CovWorkWords[p+45u] = code == 0u ? 1u : 3u;
        }
        """;

    internal const string Finish = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], triangle = gl_GlobalInvocationID.x;
            if (triangle >= CovFrameWords[f+7u] || covCompletedFailure(w)) return;
            uint p = covNormalWork(w,triangle);
            if (CovWorkWords[p+45u] != 1u) return;
            vec3 u = covWork3(p), v = covWork3(p+3u), n = covWork3(p+6u);
            vec3 s = covWork3(p+9u), t = covWork3(p+12u), m = covWork3(p+15u);
            float beforeDet = covRead(CovWorkWords[p+42u]), afterDet = covRead(CovWorkWords[p+43u]);
            vec3 numerator = vec3(0.0), divided = vec3(0.0);
            int vectorIndex = 0, axis = 0; bool pending = false, valid = true;
            CovExactResult result = covExactResultZero();
            [[dont_unroll]] while (vectorIndex < 6) {
                if (pending) {
                    if (axis == 0) divided.x = result.predicate.value;
                    else if (axis == 1) divided.y = result.predicate.value;
                    else divided.z = result.predicate.value;
                    axis++; pending = false;
                    if (axis == 3) {
                        covWrite3(p+18u+uint(vectorIndex)*3u,divided);
                        if (!covFinite3(divided)) valid = false;
                        vectorIndex++; axis = 0;
                        if (vectorIndex == 6) break;
                    }
                }
                if (axis == 0) {
                    if (vectorIndex == 0) numerator = covCross(v,n);
                    else if (vectorIndex == 1) numerator = covCross(n,u);
                    else if (vectorIndex == 2) numerator = covWork3(p+36u);
                    else if (vectorIndex == 3) numerator = covCross(t,m);
                    else if (vectorIndex == 4) numerator = covCross(m,s);
                    else numerator = covWork3(p+39u);
                }
                float value = axis == 0 ? numerator.x : (axis == 1 ? numerator.y : numerator.z);
                result = covExactWorker(covScalarJob(value,vectorIndex < 3 ? beforeDet : afterDet,false)); pending = true;
            }
            CovWorkWords[p+44u] = valid ? 0u : 4u; CovWorkWords[p+45u] = valid ? 2u : 3u;
        }
        """;
}
