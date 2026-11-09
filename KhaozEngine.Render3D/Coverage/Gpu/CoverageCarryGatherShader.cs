namespace KhaozEngine.Render3D;

/// <summary>Complete private candidates and actor error flags before any final-surface publication.</summary>
internal static class CoverageCarryGatherShader
{
    internal static string Source => CoverageCarryNormalShader.Source + "\n" + """
        void main() {
            vec3 position = vec3(0.0);
            uint f = covFrame(), w = CovFrameWords[f+1u], vertex = carryIndex();
            if (vertex >= CovFrameWords[f+6u]) return;
            // Raw publication always selects Before. No candidate word is consumed on a table hole.
            if (CovFrameWords[f+15u] != 0u) return;
            uint h = CovFrameWords[f], before = covRow(w,4u), record = before+vertex*16u;
            position = covWork3(record);
            uint candidate = covRow(w,10u)+vertex*16u;
            covEvent(w,vertex,0u);
            bool moved = covMoved(w,vertex);
            vec3 normal = covDirectionRead3(record+3u), tangent = covDirectionRead3(record+12u);
            uint code = 0u;
            int direction = moved ? 0 : 2;
            [[dont_unroll]] while (direction < 2 && code == 0u) {
                vec3 authored = direction == 0 ? normal : tangent;
                if (covDirectionZero(authored)) { direction++; continue; }
                uint group = direction == 0 ? CovStaticWords[covVertex(h,vertex)+7u] : vertex;
                uint offsets = covTable(h,direction == 0 ? 40u : 36u);
                uint corners = covTable(h,direction == 0 ? 42u : 38u);
                uint cursor = CovStaticWords[offsets+group], end = CovStaticWords[offsets+group+1u];
                vec3 sum = vec3(0.0);
                [[dont_unroll]] while (cursor < end && code == 0u) {
                    uint corner = CovStaticWords[corners+cursor], member = CovStaticWords[covTable(h,20u)+corner];
                    cursor++;
                    if (!covMoved(w,member)) continue;
                    vec3 original = covDirectionRead3(before+member*16u+(direction == 0 ? 3u : 12u));
                    if (covDirectionZero(original)) continue;
                    uint p = covNormalWork(w,corner/3u);
                    if (CovWorkWords[p+45u] != 2u) { code = 4u; continue; }
                    vec3 transformed;
                    if (direction == 1) transformed = covCombine(covDirectionRead3(p+9u),dot(covDirectionRead3(p+18u),original),
                        covDirectionRead3(p+12u),dot(covDirectionRead3(p+21u),original),covDirectionRead3(p+15u),dot(covDirectionRead3(p+24u),original));
                    else transformed = covCombine(covDirectionRead3(p+27u),dot(covDirectionRead3(p),original),
                        covDirectionRead3(p+30u),dot(covDirectionRead3(p+3u),original),covDirectionRead3(p+33u),dot(covDirectionRead3(p+6u),original));
                    vec3 unit;
                    code = covDirectionUnit(transformed,unit);
                    if (code == 0u) {
                        sum += unit;
                        if (!covDirectionFinite3(sum)) code = 4u;
                    }
                }
                if (code == 0u) {
                    vec3 unit;
                    code = covDirectionUnit(sum,unit);
                    if (direction == 0) normal = unit; else tangent = unit;
                }
                direction++;
            }
            if (code != 0u) { covEvent(w,vertex,code); moved = false; }

            bool updateNormal = moved && !covDirectionZero(covDirectionRead3(record+3u));
            bool updateTangent = moved && !covDirectionZero(covDirectionRead3(record+12u));
            if (moved) position = covWork3(covRow(w,8u)+vertex*16u);
            [[dont_unroll]] for (uint field = 0u; field < 16u; field++) {
                uint word = CovWorkWords[record+field];
                if (moved && field < 3u) word = floatBitsToUint(covF(field == 0u ? position.x : (field == 1u ? position.y : position.z)));
                else if (updateNormal && field >= 3u && field < 6u) word = floatBitsToUint(field == 3u ? normal.x : (field == 4u ? normal.y : normal.z));
                else if (updateTangent && field >= 12u && field < 15u) word = floatBitsToUint(field == 12u ? tangent.x : (field == 13u ? tangent.y : tangent.z));
                    CovWorkWords[candidate+field] = word;
            }
        }
        """;
}
