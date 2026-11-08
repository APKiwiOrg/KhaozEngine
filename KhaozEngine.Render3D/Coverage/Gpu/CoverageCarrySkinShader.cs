namespace KhaozEngine.Render3D;

/// <summary>Current-pose scalar skinning, preserving authored absence and zero signs.</summary>
internal static class CoverageCarrySkinShader
{
    internal static string Source => CoverageCarryUnitShader.Source + "\n" + """
        float covSkinLinear(vec3 p, float a, float b, float c) {
            return covAdd(covAdd(covMul(p.x,a),covMul(p.y,b)),covMul(p.z,c));
        }
        vec3 covSkin3(uint start) {
            return vec3(covRead(CovStaticWords[start]),covRead(CovStaticWords[start+1u]),covRead(CovStaticWords[start+2u]));
        }
        float covBlend(uint palette, uint indices, vec4 w, uint field) {
            float a = covRead(CovFrameWords[palette+CovStaticWords[indices]*16u+field]);
            float b = covRead(CovFrameWords[palette+CovStaticWords[indices+1u]*16u+field]);
            float c = covRead(CovFrameWords[palette+CovStaticWords[indices+2u]*16u+field]);
            float d = covRead(CovFrameWords[palette+CovStaticWords[indices+3u]*16u+field]);
            return covAdd(covAdd(covAdd(covMul(a,w.x),covMul(b,w.y)),covMul(c,w.z)),covMul(d,w.w));
        }
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], i = carryIndex();
            if (i >= CovFrameWords[f+5u]) return;
            uint h = CovFrameWords[f], s = covTable(h,8u)+i*20u, indices = covTable(h,10u)+i*4u, palette = f+16u;
            uint target = covRow(w,0u)+i*16u, fallback = covRow(w,2u)+i*16u;
            vec4 weights = vec4(covRead(CovStaticWords[s+16u]),covRead(CovStaticWords[s+17u]),
                covRead(CovStaticWords[s+18u]),covRead(CovStaticWords[s+19u]));
            float total = covAdd(covAdd(covAdd(weights.x,weights.y),weights.z),weights.w);
            vec3 p = covSkin3(s), originalNormal = covSkin3(s+3u), originalTangent = covSkin3(s+12u);
            bool hasTangent = covGreater(covDot3(originalTangent,originalTangent),1e-12);
            // Dynamic buffer words are invocation-owned. Vector and matrix components remain constant-indexed.
            [[dont_unroll]] for (uint axis = 0u; axis < 3u; axis++) {
                float m0 = axis == 0u ? 1.0 : 0.0, m1 = axis == 1u ? 1.0 : 0.0, m2 = axis == 2u ? 1.0 : 0.0, m3 = 0.0;
                if (!covLess(total,1e-8)) {
                    m0 = covBlend(palette,indices,weights,axis); m1 = covBlend(palette,indices,weights,4u+axis);
                    m2 = covBlend(palette,indices,weights,8u+axis); m3 = covBlend(palette,indices,weights,12u+axis);
                }
                CovWorkWords[target+axis] = floatBitsToUint(covAdd(covAdd(covAdd(covMul(p.x,m0),covMul(p.y,m1)),covMul(p.z,m2)),m3));
                CovWorkWords[target+3u+axis] = floatBitsToUint(covSkinLinear(originalNormal,m0,m1,m2));
                CovWorkWords[target+12u+axis] = floatBitsToUint(covSkinLinear(originalTangent,m0,m1,m2));
            }
            bool absentTangent = covEqual3(originalTangent,vec3(0.0));
            vec3 n = covWork3(target+3u), t = absentTangent ? originalTangent : vec3(0.0), transformedTangent = covWork3(target+12u);
            CovUnit q; covUnitBegin(n,originalNormal,1e-8,q);
            CovExactResult result = covExactResultZero();
            int direction = 0;
            [[dont_unroll]] while (direction < 2) {
                CovExactJob job;
                bool request = covUnitStep(q,result,job);
                if (q.phase == CovUnitDone) {
                    if (direction == 0) {
                        n = q.value; direction = 1;
                        if (!hasTangent) break;
                        covUnitBegin(transformedTangent,originalTangent,1e-8,q);
                    } else { t = q.value; direction = 2; }
                }
                if (request) result = covExactWorker(job);
            }
            covWrite3(target+3u,n); covWrite3(target+12u,t);
            [[dont_unroll]] for (uint field = 6u; field < 12u; field++) CovWorkWords[target+field] = floatBitsToUint(covRead(CovStaticWords[s+field]));
            CovWorkWords[target+15u] = hasTangent || absentTangent ? floatBitsToUint(covRead(CovStaticWords[s+15u])) : 0u;
            bool valid = true;
            [[dont_unroll]] for (uint field = 0u; field < 16u; field++) {
                uint value = CovWorkWords[target+field]; CovWorkWords[fallback+field] = value;
                if (!covFinite(covRead(value))) valid = false;
            }
            covEvent(w,i,valid ? 0u : 4u);
        }
        """;
}
