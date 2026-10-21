namespace KhaozEngine.Tests.Gpu;

/// <summary>The ordinary renderer's raw four-bone blend and native length/divide, in a compute storage control.</summary>
internal static class CoverageCarryOrdinaryShader
{
    internal const string Source = """
        vec3 carryRaw3(uint s) {
            return vec3(uintBitsToFloat(CovStaticWords[s]),uintBitsToFloat(CovStaticWords[s+1u]),uintBitsToFloat(CovStaticWords[s+2u]));
        }
        void carryOrdinary() {
            uint f = covFrame(), w = CovFrameWords[f+1u], i = carryIndex(), h = CovFrameWords[f];
            uint s = covTable(h,8u)+i*20u, indices = covTable(h,10u)+i*4u;
            vec4 weights = vec4(uintBitsToFloat(CovStaticWords[s+16u]),uintBitsToFloat(CovStaticWords[s+17u]),
                uintBitsToFloat(CovStaticWords[s+18u]),uintBitsToFloat(CovStaticWords[s+19u]));
            float total = weights.x+weights.y+weights.z+weights.w;
            mat4 skin = mat4(1.0);
            if (total >= 1e-8) skin = covFrameMatrix(f+16u+CovStaticWords[indices]*16u)*weights.x
                + covFrameMatrix(f+16u+CovStaticWords[indices+1u]*16u)*weights.y
                + covFrameMatrix(f+16u+CovStaticWords[indices+2u]*16u)*weights.z
                + covFrameMatrix(f+16u+CovStaticWords[indices+3u]*16u)*weights.w;
            vec3 position = (skin*vec4(carryRaw3(s),1.0)).xyz;
            vec3 originalNormal = carryRaw3(s+3u), normal = mat3(skin)*originalNormal;
            float nl = length(normal);
            normal = nl > 1e-8 ? normal/nl : originalNormal;
            vec3 originalTangent = carryRaw3(s+12u), tangent = vec3(0.0);
            float handedness = 0.0;
            if (dot(originalTangent,originalTangent) > 1e-12) {
                tangent = mat3(skin)*originalTangent;
                float tl = length(tangent);
                tangent = tl > 1e-8 ? tangent/tl : originalTangent;
                handedness = uintBitsToFloat(CovStaticWords[s+15u]);
            }
            uint target = covRow(w,10u)+i*16u;
            covWrite3(target,position); covWrite3(target+3u,normal); covWrite3(target+12u,tangent);
            [[dont_unroll]] for (uint field = 6u; field < 12u; field++) CovWorkWords[target+field] = CovStaticWords[s+field];
            CovWorkWords[target+15u] = floatBitsToUint(handedness);
        }
        """;
}
