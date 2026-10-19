namespace KhaozEngine.Render3D;

/// <summary>Generated attributes are validated privately before the single Before publication phase.</summary>
internal static class CoverageGpuAttributesGlsl
{
    internal const string Source = """
        float covWeighted(float a, float b, float c, vec3 weights) {
            return covAdd(covAdd(covMul(a,weights.x),covMul(b,weights.y)),covMul(c,weights.z));
        }
        float covMagnitude(float a, float b, float c, vec3 weights) {
            return covAdd(covAdd(covAbs(covMul(a,weights.x)),covAbs(covMul(b,weights.y))),covAbs(covMul(c,weights.z)));
        }
        bool covDirectionBegin(vec3 a, vec3 b, vec3 c, vec3 weights, out CovUnit q) {
            vec3 sum = vec3(covWeighted(a.x,b.x,c.x,weights),covWeighted(a.y,b.y,c.y,weights),covWeighted(a.z,b.z,c.z,weights));
            vec3 magnitude = vec3(covMagnitude(a.x,b.x,c.x,weights),covMagnitude(a.y,b.y,c.y,weights),covMagnitude(a.z,b.z,c.z,weights));
            covUnitBegin(sum,vec3(0.0),-1.0,q);
            if (!covFinite3(sum) || !covFinite3(magnitude)) return false;
            if (covEqual3(magnitude,vec3(0.0))) { q.value = sum; q.phase = CovUnitDone; return true; }
            float squared = covDot3(sum,sum), magnitudeSquared = covDot3(magnitude,magnitude);
            return covFinite(squared) && covFinite(magnitudeSquared)
                && covGreater(squared,covMul(magnitudeSquared,uintBitsToFloat(0x26800000u)));
        }
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], i = gl_GlobalInvocationID.x;
            if (i >= CovFrameWords[f+6u] || covCompletedFailure(w)) return;
            uint h = CovFrameWords[f], r = covTable(h,12u)+i*10u;
            covEvent(w,i,0u);
            if (int(CovStaticWords[r+1u]) >= 0) return;
            uint tri = covTable(h,16u)+CovStaticWords[r]*8u, source = covRow(w,2u), weightsAt = covTable(h,26u)+i*3u;
            uint a = source+CovStaticWords[tri+5u]*16u, b = source+CovStaticWords[tri+6u]*16u, c = source+CovStaticWords[tri+7u]*16u;
            vec3 weights = vec3(covRead(CovStaticWords[weightsAt]),covRead(CovStaticWords[weightsAt+1u]),covRead(CovStaticWords[weightsAt+2u]));
            uint target = covRow(w,32u)+i*16u, group = covRow(w,12u)+CovStaticWords[r+6u]*3u;
            covWrite3(target,covWork3(group));
            int direction = 0;
            uint field = 6u;
            bool valid = true, pending = false, initial = true;
            CovUnit q; covUnitBegin(vec3(0.0),vec3(0.0),0.0,q);
            CovExactResult result = covExactResultZero();
            [[dont_unroll]] while (valid && field < 12u) {
                CovExactJob job = covScalarJob(0.0,1.0,false);
                bool request = false;
                if (direction < 2) {
                    if (initial) {
                        uint offset = direction == 0 ? 3u : 12u;
                        valid = covDirectionBegin(covWork3(a+offset),covWork3(b+offset),covWork3(c+offset),weights,q);
                        initial = false;
                    } else request = covUnitStep(q,result,job);
                    if (q.phase == CovUnitDone) {
                        if (!covFinite3(q.value)) valid = false;
                        if (direction == 0) covWrite3(target+3u,q.value);
                        else {
                            vec3 tangent = q.value;
                            float handedness = 0.0;
                            if (!covEqual3(tangent,vec3(0.0))) {
                                handedness = covWeighted(covRead(CovWorkWords[a+15u]),covRead(CovWorkWords[b+15u]),covRead(CovWorkWords[c+15u]),weights);
                                if (!covFinite(handedness)) valid = false;
                                handedness = covLess(handedness,0.0) ? -1.0 : 1.0;
                            } else tangent = vec3(0.0);
                            covWrite3(target+12u,tangent);
                            CovWorkWords[target+15u] = floatBitsToUint(handedness);
                        }
                        direction++; initial = true;
                    }
                } else {
                    if (pending) {
                        float value = result.predicate.value;
                        CovWorkWords[target+field] = floatBitsToUint(value);
                        if (!covFinite(value)) valid = false;
                        field++; pending = false;
                    }
                    if (field < 12u) {
                        float sum = covWeighted(covRead(CovWorkWords[a+field]),covRead(CovWorkWords[b+field]),covRead(CovWorkWords[c+field]),weights);
                        job = covScalarJob(sum,12.0,false); pending = true; request = true;
                    }
                }
                if (request) result = covExactWorker(job);
            }
            if (!valid) covEvent(w,i,4u);
        }
        """;
}
