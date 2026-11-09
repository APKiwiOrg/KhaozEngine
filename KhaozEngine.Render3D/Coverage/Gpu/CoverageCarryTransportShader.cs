namespace KhaozEngine.Render3D;

/// <summary>Complete uncovered refinement before reporting failure, followed by selected-table transport.</summary>
internal static class CoverageCarryTransportShader
{
    internal static string Source => CoverageCarryUnitShader.Source + "\n" + """
        float covWeighted(float a, float b, float c, vec3 weights) {
            return covAdd(covAdd(covMul(a,weights.x),covMul(b,weights.y)),covMul(c,weights.z));
        }
        bool covDirection(vec3 a, vec3 b, vec3 c, vec3 weights, out vec3 unit) {
            vec3 x = a*weights.x, y = b*weights.y, z = c*weights.z;
            vec3 sum = (x+y)+z, magnitude = (abs(x)+abs(y))+abs(z);
            unit = vec3(0.0);
            if (!covDirectionFinite3(sum) || !covDirectionFinite3(magnitude)) return false;
            if (covDirectionZero(magnitude)) { unit = sum; return true; }
            float squared = dot(sum,sum), magnitudeSquared = dot(magnitude,magnitude);
            if (!covDirectionFinite(squared) || !covDirectionFinite(magnitudeSquared)
                || !(squared > magnitudeSquared*uintBitsToFloat(0x26800000u))) return false;
            return covDirectionUnit(sum,unit) == 0u;
        }
        void carryAttributes() {
            uint f = covFrame(), w = CovFrameWords[f+1u], i = carryIndex();
            if (i >= CovFrameWords[f+6u]) return;
            uint h = CovFrameWords[f], r = covTable(h,12u)+i*10u;
            covEvent(w,i,0u);
            if (int(CovStaticWords[r+1u]) >= 0) return;
            uint tri = covTable(h,16u)+CovStaticWords[r]*8u, source = covRow(w,2u), weightsAt = covTable(h,26u)+i*3u;
            uint a = source+CovStaticWords[tri+5u]*16u, b = source+CovStaticWords[tri+6u]*16u, c = source+CovStaticWords[tri+7u]*16u;
            vec3 weights = vec3(covRead(CovStaticWords[weightsAt]),covRead(CovStaticWords[weightsAt+1u]),covRead(CovStaticWords[weightsAt+2u]));
            uint target = covRow(w,4u)+i*16u, positionGroup = CovStaticWords[r+6u];
            [[dont_unroll]] for (uint initialize = 0u; initialize < 16u; initialize++) CovWorkWords[target+initialize] = 0u;
        uint recipe = covTable(h,22u)+positionGroup*7u, positionWeights = covTable(h,24u)+positionGroup*3u;
        uint pa = CovStaticWords[recipe], pb = CovStaticWords[recipe+1u], pc = CovStaticWords[recipe+2u];
        uint wa = CovStaticWords[recipe+3u], wb = CovStaticWords[recipe+4u], wc = CovStaticWords[recipe+5u];
        bool positionOriginal = wa == 12u || wb == 12u || wc == 12u;
        bool positionEdge = !positionOriginal && (wa == 0u || wb == 0u || wc == 0u);
        if (positionOriginal) pa = wa == 12u ? pa : (wb == 12u ? pb : pc);
        if (positionEdge) {
            if (wb == 0u) pb = pc;
            else if (wa == 0u) { pa = pb; pb = pc; }
            if (pa > pb) { uint swap = pa; pa = pb; pb = swap; }
        }
        float positionT = covRead(CovStaticWords[covTable(h,28u)+positionGroup]);
        uint positionAxis = 0u; bool positionPending = false;
            int direction = 0;
            uint field = 6u;
            bool valid = true, pending = false;
            CovExactResult result = covExactResultZero();
            [[dont_unroll]] while (positionAxis < 3u || field < 12u) {
                CovExactJob job = covScalarJob(0.0,1.0,false);
                bool request = false;
                if (positionAxis < 3u) {
            if (positionPending) {
                float value = result.predicate.value;
                CovWorkWords[target+positionAxis] = floatBitsToUint(value);
                if (!covFinite(value)) valid = false;
                positionAxis++; positionPending = false;
            }
            if (positionAxis < 3u) {
                float x = covRead(CovWorkWords[source+pa*16u+positionAxis]), value = x;
                if (positionEdge) value = covAdd(covMul(x,covSub(1.0,positionT)),
                    covMul(covRead(CovWorkWords[source+pb*16u+positionAxis]),positionT));
                else if (!positionOriginal) {
                    float sum = covAdd(covAdd(covMul(x,covRead(CovStaticWords[positionWeights])),
                        covMul(covRead(CovWorkWords[source+pb*16u+positionAxis]),covRead(CovStaticWords[positionWeights+1u]))),
                        covMul(covRead(CovWorkWords[source+pc*16u+positionAxis]),covRead(CovStaticWords[positionWeights+2u])));
                    job = covScalarJob(sum,12.0,false); positionPending = true; request = true;
                }
                if (!request) {
                    CovWorkWords[target+positionAxis] = floatBitsToUint(value);
                    if (!covFinite(value)) valid = false;
                    positionAxis++;
                }
            }
        } else if (direction < 2) {
                    uint offset = direction == 0 ? 3u : 12u;
                    vec3 value;
                    bool accepted = covDirection(covDirectionRead3(a+offset),covDirectionRead3(b+offset),covDirectionRead3(c+offset),weights,value);
                    if (!accepted) { valid = false; value = vec3(0.0); }
                    if (direction == 0) covDirectionWrite3(target+3u,value);
                    else {
                        float handedness = 0.0;
                        if (!covDirectionZero(value)) {
                            handedness = covWeighted(covRead(CovWorkWords[a+15u]),covRead(CovWorkWords[b+15u]),covRead(CovWorkWords[c+15u]),weights);
                            if (!covFinite(handedness)) valid = false;
                            handedness = covLess(handedness,0.0) ? -1.0 : 1.0;
                        } else value = vec3(0.0);
                        covDirectionWrite3(target+12u,value);
                        CovWorkWords[target+15u] = floatBitsToUint(handedness);
                    }
                    direction++;
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
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], i = carryIndex();
            if (i >= CovFrameWords[f+6u]) return;
            uint h = CovFrameWords[f], record = covVertex(h,i), before = covRow(w,4u)+i*16u;
            int original = int(CovStaticWords[record+1u]);
            if (original >= 0) {
                uint source = covRow(w,2u)+uint(original)*16u;
                [[dont_unroll]] for (uint field = 0u; field < 16u; field++) CovWorkWords[before+field] = CovWorkWords[source+field];
            } else carryAttributes();
            vec3 position = covWork3(before);
            uint group = CovStaticWords[record+6u];
            int representative = int(CarryTableWords[group]);
            float full = CovFrameWords[f+15u] != 0u ? 0.0 : covRead(CarryTableWords[CovFrameWords[f+14u]+group]);
            if (CovFrameWords[f+15u] == 0u && CovStaticWords[record+9u] != 0u && representative >= 0 && !covEqual(full,0.0)) {
                uint rep = covVertex(h,uint(representative));
                int slot = covRegionSlot(f,int(CovStaticWords[rep+8u]));
                if (slot >= 0) {
                    uint command = covCommand(f,uint(slot));
                    float weight = covRead(CovFrameWords[command+3u]);
                    if (!covEqual(weight,0.0)) {
                        int repSource = int(CovStaticWords[rep+1u]);
                        vec3 canonical = repSource >= 0 ? covWork3(covRow(w,2u)+uint(repSource)*16u)
                            : covWork3(before);
                        float applied = covEqual(weight,1.0) ? full : covMul(weight,full);
                        position = covMove(canonical,applied,covRecess(command));
                    }
                }
            }
            covWrite3(covRow(w,8u)+i*16u,position);
            CovWorkWords[covRow(w,44u)+i] = covEqual3(position,covWork3(before)) ? 0u : 1u;
        }
        """;
}
