namespace KhaozEngine.Render3D;

/// <summary>Ordered normal and tangent gather with complete current-only publication and barrier-safe tight bounds.</summary>
internal static class CoverageCarryPublicationShader
{
    internal const string Source = """
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
        uint covNormalWork(uint w, uint triangle) { return covRow(w,42u)+triangle*46u; }
        bool covMoved(uint w, uint vertex) {
            return CovWorkWords[covRow(w,44u)+vertex] != 0u;
        }
        vec3 covCombine(vec3 a, float x, vec3 b, float y, vec3 c, float z) {
            return vec3(covAdd(covAdd(covMul(a.x,x),covMul(b.x,y)),covMul(c.x,z)),
                covAdd(covAdd(covMul(a.y,x),covMul(b.y,y)),covMul(c.y,z)),covAdd(covAdd(covMul(a.z,x),covMul(b.z,y)),covMul(c.z,z)));
        }
        void carryGather(out vec3 position) {
            position = vec3(0.0);
            uint f = covFrame(), w = CovFrameWords[f+1u], vertex = carryIndex();
            if (vertex >= CovFrameWords[f+6u]) return;
            uint h = CovFrameWords[f], before = covRow(w,4u), record = before+vertex*16u;
            position = covWork3(record);
            covEvent(w,vertex,0u);
            bool moved = covMoved(w,vertex) && !covCompletedFailure(w);
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
            uint current = CovFrameWords[f+2u]+vertex*16u;
            bool updateNormal = moved && !covEqual3(covWork3(record+3u),vec3(0.0));
            bool updateTangent = moved && !covEqual3(covWork3(record+12u),vec3(0.0));
            if (moved) position = covWork3(covRow(w,8u)+vertex*16u);
            [[dont_unroll]] for (uint field = 0u; field < 16u; field++) {
                uint word = CovWorkWords[record+field];
                if (moved && field < 3u) word = floatBitsToUint(covF(field == 0u ? position.x : (field == 1u ? position.y : position.z)));
                else if (updateNormal && field >= 3u && field < 6u) word = floatBitsToUint(covF(field == 3u ? normal.x : (field == 4u ? normal.y : normal.z)));
                else if (updateTangent && field >= 12u && field < 15u) word = floatBitsToUint(covF(field == 12u ? tangent.x : (field == 13u ? tangent.y : tangent.z)));
                    CovCurrentWords[current+field] = word;
            }
        }

        shared uint carryBounds[384];
        uint carryOrdered(float value) {
            uint word = floatBitsToUint(value);
            return (word & 0x80000000u) != 0u ? ~word : word ^ 0x80000000u;
        }
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], vertex = carryIndex(), lane = gl_LocalInvocationID.x;
            uint r = CovFrameWords[f+4u];
            bool live = vertex < CovFrameWords[f+6u];
            uvec3 ordered = uvec3(0u);
            if (live) {
                vec3 position; carryGather(position);
                ordered = uvec3(carryOrdered(position.x),carryOrdered(position.y),carryOrdered(position.z));
            }
            carryBounds[lane] = live ? ordered.x : 0xffffffffu;
            carryBounds[64u+lane] = live ? ordered.y : 0xffffffffu;
            carryBounds[128u+lane] = live ? ordered.z : 0xffffffffu;
            carryBounds[192u+lane] = live ? ordered.x : 0u;
            carryBounds[256u+lane] = live ? ordered.y : 0u;
            carryBounds[320u+lane] = live ? ordered.z : 0u;
            // All 64 lanes, including tails, execute every barrier. There is no return from this entry point.
            barrier();
            [[dont_unroll]] for (uint step = 32u; step > 0u; step >>= 1u) {
                if (lane < step) {
                    [[dont_unroll]] for (uint axis = 0u; axis < 6u; axis++) {
                        uint at = axis*64u+lane, other = carryBounds[at+step];
                        carryBounds[at] = axis < 3u ? min(carryBounds[at],other) : max(carryBounds[at],other);
                    }
                }
                barrier();
            }
            if (lane == 0u) {
                atomicMin(CovResultWords[r],carryBounds[0]); atomicMin(CovResultWords[r+1u],carryBounds[64]);
                atomicMin(CovResultWords[r+2u],carryBounds[128]); atomicMax(CovResultWords[r+3u],carryBounds[192]);
                atomicMax(CovResultWords[r+4u],carryBounds[256]); atomicMax(CovResultWords[r+5u],carryBounds[320]);
            }
            if (vertex == 0u) CovResultWords[r+7u] = CovFrameWords[f+13u];
        }
        """;
}
