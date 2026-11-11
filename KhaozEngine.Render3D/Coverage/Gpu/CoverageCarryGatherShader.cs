namespace KhaozEngine.Render3D;

/// <summary>Exact position admission followed by independent local native direction gathers.</summary>
internal static class CoverageCarryGatherShader
{
    internal static string Source => CoverageCarryNormalShader.Source + "\n" + CoverageCarryPositionShader.Source + "\n" + """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], vertex = carryIndex();
            if (vertex >= CovFrameWords[f+6u]) return;
            covPositionFailures();
            if (CovFrameWords[f+15u] != 0u) return;
            uint h = CovFrameWords[f], before = covRow(w,4u), record = before+vertex*16u;
            uint candidate = covRow(w,10u)+vertex*16u;
            bool moved = covMoved(w,vertex);
            vec3 normal = covDirectionRead3(record+3u), tangent = covDirectionRead3(record+12u);
            [[dont_unroll]] for (int direction = 0; direction < 2; direction++) {
                bool isTangent = direction == 1;
                vec3 uncovered = isTangent ? tangent : normal, value = uncovered;
                bool valid = covDirectionFinite3(value);
                if (moved && !covDirectionZero(uncovered)) {
                    uint group = isTangent ? vertex : CovStaticWords[covVertex(h,vertex)+7u];
                    uint offsets = covTable(h,isTangent ? 36u : 40u), corners = covTable(h,isTangent ? 38u : 42u);
                    uint cursor = CovStaticWords[offsets+group], end = CovStaticWords[offsets+group+1u];
                    vec3 sum = vec3(0.0); valid = true;
                    [[dont_unroll]] while (cursor < end && valid) {
                        uint corner = CovStaticWords[corners+cursor], member = CovStaticWords[covTable(h,20u)+corner]; cursor++;
                        if (!covMoved(w,member)) continue;
                        vec3 original = covDirectionRead3(before+member*16u+(isTangent ? 12u : 3u));
                        if (covDirectionZero(original)) continue;
                        uint p = covNormalWork(w,corner/3u);
                        if (CovWorkWords[p+45u] != 2u) { valid = false; continue; }
                        vec3 transformed;
                        if (isTangent) transformed = covCombine(covDirectionRead3(p+9u),covDirectionDot3(covDirectionRead3(p+18u),original),
                            covDirectionRead3(p+12u),covDirectionDot3(covDirectionRead3(p+21u),original),covDirectionRead3(p+15u),covDirectionDot3(covDirectionRead3(p+24u),original));
                        else transformed = covCombine(covDirectionRead3(p+27u),covDirectionDot3(covDirectionRead3(p),original),
                            covDirectionRead3(p+30u),covDirectionDot3(covDirectionRead3(p+3u),original),covDirectionRead3(p+33u),covDirectionDot3(covDirectionRead3(p+6u),original));
                        vec3 unit; valid = covDirectionUnit(transformed,unit) == 0u;
                        if (valid) { precise vec3 next = sum+unit; sum = next; valid = covDirectionFinite3(sum); }
                    }
                    if (valid) valid = covDirectionUnit(sum,value) == 0u;
                }
                value = covRecoverDirection(value,valid,uncovered,covAuthoredDirection(h,vertex,isTangent),isTangent);
                if (isTangent) tangent = value; else normal = value;
            }
            vec3 position = moved ? covWork3(covRow(w,8u)+vertex*16u) : covWork3(record);
            [[dont_unroll]] for (uint field = 0u; field < 16u; field++) {
                uint word = CovWorkWords[record+field];
                if (moved && field < 3u) word = floatBitsToUint(covF(field == 0u ? position.x : (field == 1u ? position.y : position.z)));
                else if (field >= 3u && field < 6u) word = floatBitsToUint(field == 3u ? normal.x : (field == 4u ? normal.y : normal.z));
                else if (field >= 12u && field < 15u) word = floatBitsToUint(field == 12u ? tangent.x : (field == 13u ? tangent.y : tangent.z));
                else if (field == 15u) word = floatBitsToUint(covDirectionHandedness(uintBitsToFloat(word)));
                CovWorkWords[candidate+field] = word;
            }
        }
        """;
}
