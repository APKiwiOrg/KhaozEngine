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
            vec3 normal = covWork3(record+3u), tangent = covWork3(record+12u), original = vec3(0.0), sum = vec3(0.0);
            uint code = 0u, cursor = 0u, end = 0u, corners = 0u;
            int direction = moved ? 0 : 2, phase = 0;
            CovNormalUnit unit; covNormalUnitBegin(vec3(0.0),unit);
            CovExactResult result = covExactResultZero();
            [[dont_unroll]] while (direction < 2 && code == 0u) {
                CovExactJob job = covScalarJob(0.0,1.0,false); bool request = false;
                if (phase == 0) {
                    vec3 authored = direction == 0 ? normal : tangent;
                    if (covEqual3(authored,vec3(0.0))) { direction++; continue; }
                    uint group = direction == 0 ? CovStaticWords[covVertex(h,vertex)+7u] : vertex;
                    uint offsets = covTable(h,direction == 0 ? 40u : 36u);
                    corners = covTable(h,direction == 0 ? 42u : 38u);
                    cursor = CovStaticWords[offsets+group]; end = CovStaticWords[offsets+group+1u];
                    sum = vec3(0.0); phase = 1;
                } else if (phase == 1) {
                    if (cursor == end) { covNormalUnitBegin(sum,unit); phase = 4; continue; }
                    uint corner = CovStaticWords[corners+cursor], member = CovStaticWords[covTable(h,20u)+corner];
                    if (!covMoved(w,member)) { cursor++; continue; }
                    original = covWork3(before+member*16u+(direction == 0 ? 3u : 12u));
                    if (covEqual3(original,vec3(0.0))) { cursor++; continue; }
                    uint p = covNormalWork(w,corner/3u);
                    if (CovWorkWords[p+45u] != 2u) { code = 4u; continue; }
                    // The immutable basis words equal pinned recomputation for every consumed corner.
                    vec3 transformed;
                    if (direction == 1) transformed = covCombine(covWork3(p+9u),covDot3(covWork3(p+18u),original),
                        covWork3(p+12u),covDot3(covWork3(p+21u),original),covWork3(p+15u),covDot3(covWork3(p+24u),original));
                    else transformed = covCombine(covWork3(p+27u),covDot3(covWork3(p),original),
                        covWork3(p+30u),covDot3(covWork3(p+3u),original),covWork3(p+33u),covDot3(covWork3(p+6u),original));
                    covNormalUnitBegin(transformed,unit); phase = 3;
                } else {
                    request = covNormalUnitStep(unit,result,job);
                    if (unit.phase == CovNormalUnitDone) {
                        code = unit.code;
                        if (code == 0u && phase == 3) {
                            sum = vec3(covAdd(sum.x,unit.value.x),covAdd(sum.y,unit.value.y),covAdd(sum.z,unit.value.z));
                            if (!covFinite3(sum)) code = 4u;
                            cursor++; phase = 1;
                        } else if (code == 0u) {
                            if (direction == 0) normal = unit.value; else tangent = unit.value;
                            direction++; phase = 0;
                        }
                    }
                }
                if (request) result = covExactWorker(job);
            }
            if (code != 0u) { covEvent(w,vertex,code); moved = false; }

            bool updateNormal = moved && !covEqual3(covWork3(record+3u),vec3(0.0));
            bool updateTangent = moved && !covEqual3(covWork3(record+12u),vec3(0.0));
            if (moved) position = covWork3(covRow(w,8u)+vertex*16u);
            [[dont_unroll]] for (uint field = 0u; field < 16u; field++) {
                uint word = CovWorkWords[record+field];
                if (moved && field < 3u) word = floatBitsToUint(covF(field == 0u ? position.x : (field == 1u ? position.y : position.z)));
                else if (updateNormal && field >= 3u && field < 6u) word = floatBitsToUint(covF(field == 3u ? normal.x : (field == 4u ? normal.y : normal.z)));
                else if (updateTangent && field >= 12u && field < 15u) word = floatBitsToUint(covF(field == 12u ? tangent.x : (field == 13u ? tangent.y : tangent.z)));
                    CovWorkWords[candidate+field] = word;
            }
        }
        """;
}
