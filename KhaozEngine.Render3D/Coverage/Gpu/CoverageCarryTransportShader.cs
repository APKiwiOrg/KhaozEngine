namespace KhaozEngine.Render3D;

/// <summary>Complete uncovered refinement before reporting failure, followed by selected-table transport.</summary>
internal static class CoverageCarryTransportShader
{
    internal static string Source => CoverageCarryUnitShader.Source + "\n" + """
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
            bool valid = true, pending = false, initial = true;
            CovUnit q; covUnitBegin(vec3(0.0),vec3(0.0),0.0,q);
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
                    if (initial) {
                        uint offset = direction == 0 ? 3u : 12u;
                        bool accepted = covDirectionBegin(covWork3(a+offset),covWork3(b+offset),covWork3(c+offset),weights,q);
                        if (!accepted) { valid = false; q.value = vec3(0.0); q.phase = CovUnitDone; }
                        initial = false;
                    } else request = covUnitStep(q,result,job);
                    if (q.phase == CovUnitDone) {
                        if (!covFinite3(q.value)) { valid = false; q.value = vec3(0.0); }
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
