namespace KhaozEngine.Render3D;

/// <summary>Immutable pointwise faces precede ordered group gathers and full-target validation.</summary>
internal static class CoverageGpuCorrectionGlsl
{
    internal const string CorrectFaces = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], t = gl_GlobalInvocationID.x;
            if (t >= CovFrameWords[f+7u] || covCompletedFailure(w)) return;
            uint h = CovFrameWords[f], face = covTable(h,14u)+t*3u, indices = covTable(h,20u)+t*3u;
            CovWorkWords[covRow(w,20u)+t] = 0u;
            CovWorkWords[covRow(w,36u)+t*12u+11u] = 0u;
            [[dont_unroll]] for (uint k = 0u; k < 3u; k++) covNote(w,t*3u+k,0u,-1,t,k,0u);
            int slot = covRegionSlot(f,int(CovStaticWords[face+1u]));
            if (CovStaticWords[face+2u] != 1u || slot < 0) return;
            uint command = covCommand(f,uint(slot)), support = CovFrameWords[command+67u];
            float rate = covRead(CovFrameWords[command+55u]), clearance = covRead(CovFrameWords[command+54u]);
            if (!covGreater(rate,0.0) || !covFinite(rate) || !covFinite(clearance) || covLess(clearance,0.0)) {
                covNote(w,t*3u,4u,slot,t,0u,1u); return;
            }
            CovTriangle host = CovTriangle(vec3(0.0),vec3(0.0),vec3(0.0));
            CovHeightGraph height = covHeightBegin(support,vec2(0.0));
            CovExactResult result = covExactResultZero();
            vec3 actual = vec3(0.0), nominal = vec3(0.0);
            float discrepancy = 0.0;
            int phase = 0, corner = 0, pass = 0;
            bool valid = true;
            [[dont_unroll]] while (phase < 4) {
                CovExactJob job = covScalarJob(0.0,1.0,false); bool request = false;
                if (phase == 0) {
                    uint vertex = CovStaticWords[indices+uint(corner)];
                    vec3 position = covWork3(covRow(w,6u)+vertex*16u);
                    [[dont_unroll]] for (uint transform = 0u; transform < 2u; transform++) {
                        vec3 q = covPoint(position,covFrameMatrix(command+(transform == 0u ? 16u : 0u)));
                        if (transform == 0u) actual = q; else nominal = q;
                    }
                    if (!covFinite3(actual) || !covFinite3(nominal)) {
                        covNote(w,t*3u+uint(corner),4u,slot,t,uint(corner),1u); valid = false; phase = 3;
                    } else { pass = 0; height = covHeightBegin(support,actual.xy); phase = 1; }
                } else if (phase == 1) {
                    request = covHeightStep(height,result,job);
                    if (height.phase == CovHDone) {
                        if (!height.ok) { covNote(w,t*3u+uint(corner),5u,slot,t,uint(corner),1u); valid = false; phase = 3; }
                        else if (pass == 0) { pass = 1; height = covHeightBegin(support,nominal.xy); }
                        else {
                            if (corner == 0) host.a = nominal; else if (corner == 1) host.b = nominal; else host.c = nominal;
                            float x = covSub(actual.x,nominal.x), y = covSub(actual.y,nominal.y);
                            job = covScalarJob(covAdd(covMul(x,x),covMul(y,y)),0.0,true); request = true; phase = 2;
                        }
                    }
                } else if (phase == 2) {
                    float loss = covAdd(covAbs(covSub(actual.z,nominal.z)),covMul(covRead(CovStaticWords[support]),result.predicate.value));
                    if (!covFinite(loss)) { covNote(w,t*3u+uint(corner),4u,slot,t,uint(corner),1u); valid = false; }
                    else discrepancy = covMax(discrepancy,loss);
                    phase = 3;
                } else if (phase == 3) {
                    corner++;
                    if (corner < 3) phase = 0;
                    else phase = 4;
                }
                if (request) result = covExactWorker(job);
            }
            if (valid) covFaceWorkStore(covRow(w,36u)+t*12u,CovFaceWork(host,discrepancy,0.0,1u));
        }
        """;

    internal const string CorrectFacesFinish = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], t = gl_GlobalInvocationID.x;
            if (t >= CovFrameWords[f+7u] || covCompletedFailure(w)) return;
            uint continuation = covRow(w,36u)+t*12u;
            if (CovWorkWords[continuation+11u] != 2u) return;
            CovFaceWork next = covFaceWorkRead(continuation);
            uint h = CovFrameWords[f], face = covTable(h,14u)+t*3u, indices = covTable(h,20u)+t*3u;
            int slot = covRegionSlot(f,int(CovStaticWords[face+1u])); uint command = covCommand(f,uint(slot));
            float deficit = covAdd(covAdd(next.maximum,covRead(CovFrameWords[command+54u])),next.discrepancy);
            if (!covFinite(deficit)) { covNote(w,t*3u,4u,slot,t,0u,1u); return; }
            if (covLessOrEqual(deficit,0.0)) return;
            CovExactJob job = covScalarJob(covAdd(deficit,0.0),covRead(CovFrameWords[command+55u]),false);
            CovExactResult result = covExactResultZero(); int phase = 0;
            [[dont_unroll]] while (phase < 1) { result = covExactWorker(job); phase++; }
            float correction = result.predicate.value; bool valid = true;
            if (!covFinite(correction)) { covNote(w,t*3u,4u,slot,t,0u,1u); return; }
            [[dont_unroll]] for (uint k = 0u; k < 3u; k++) {
                uint vertex = CovStaticWords[indices+k];
                if (covNotEqual(covRead(CovStaticWords[covVertex(h,vertex)+9u]),1.0)) {
                    covNote(w,t*3u+k,8u,slot,t,k,1u); valid = false;
                }
            }
            if (valid) CovWorkWords[covRow(w,20u)+t] = floatBitsToUint(correction);
        }
        """;

    internal const string GatherCorrections = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], g = gl_GlobalInvocationID.x;
            if (g >= CovFrameWords[f+8u] || covCompletedFailure(w)) return;
            uint v = CovWorkWords[covRow(w,18u)+g];
            if (v == 0xffffffffu) return;
            uint h = CovFrameWords[f], offsets = covTable(h,32u), faces = covTable(h,34u);
            float correction = 0.0;
            [[dont_unroll]] for (uint i = CovStaticWords[offsets+g]; i < CovStaticWords[offsets+g+1u]; i++)
                correction = covMax(correction,covRead(CovWorkWords[covRow(w,20u)+CovStaticWords[faces+i]]));
            CovWorkWords[covRow(w,16u)+g] = floatBitsToUint(correction);
            float initial = covRead(CovWorkWords[covRow(w,14u)+g]);
            float total = covEqual(correction,0.0) ? initial : covAdd(initial,correction);
            CovWorkWords[covRow(w,14u)+g] = floatBitsToUint(total);
            if (covNotEqual(correction,0.0)) {
                int slot = covRegionSlot(f,int(CovStaticWords[covVertex(h,v)+8u]));
                covWrite3(covRow(w,12u)+g*3u,covMove(covWork3(covRow(w,4u)+v*16u),total,covRecess(covCommand(f,uint(slot)))));
            }
        }
        """;

    internal const string CheckFullTargets = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], g = gl_GlobalInvocationID.x;
            if (g >= CovFrameWords[f+8u] || covCompletedFailure(w)) return;
            uint v = CovWorkWords[covRow(w,18u)+g];
            covNote(w,g*2u,0u,-1,v,0u,0u); covNote(w,g*2u+1u,0u,-1,v,1u,0u);
            if (v == 0xffffffffu) return;
            uint h = CovFrameWords[f]; int slot = covRegionSlot(f,int(CovStaticWords[covVertex(h,v)+8u]));
            uint command = covCommand(f,uint(slot));
            vec3 full = covWork3(covRow(w,12u)+g*3u), before = covWork3(covRow(w,4u)+v*16u);
            float total = covRead(CovWorkWords[covRow(w,14u)+g]);
            uint code = 0u;
            if (!covFinite(total) || !covFinite3(full)) code = 4u;
            else {
                vec3 delta = covSub3(full,before), world = covLinear(delta,covFrameMatrix(command+32u));
                CovExactJob job = covScalarJob(covDot3(world,world),0.0,true);
                CovExactResult result = covExactResultZero(); int phase = 0;
                [[dont_unroll]] while (phase < 1) { result = covExactWorker(job); phase++; }
                float distance = result.predicate.value;
                vec3 q = covPoint(full,covFrameMatrix(command));
                float lower = covAdd(covRead(CovFrameWords[command+57u]),covRead(CovFrameWords[command+52u]));
                float upper = covSub(covRead(CovFrameWords[command+58u]),covRead(CovFrameWords[command+52u]));
                if (!covFinite3(delta) || !covFinite3(world) || !covFinite(distance) || !covFinite3(q) || !covFinite(lower) || !covFinite(upper)) code = 4u;
                else {
                    if (covGreater(distance,covRead(CovFrameWords[command+61u]))) code = 7u;
                    if (covLess(q.z,lower) || covGreater(q.z,upper)) covNote(w,g*2u+1u,6u,slot,v,1u,1u);
                }
            }
            covNote(w,g*2u,code,slot,v,0u,code == 0u ? 0u : 1u);
        }
        """;

    internal const string CopyCorrectedAliases = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], v = gl_GlobalInvocationID.x;
            if (v >= CovFrameWords[f+6u] || covCompletedFailure(w)) return;
            uint h = CovFrameWords[f], g = CovStaticWords[covVertex(h,v)+6u];
            if (CovWorkWords[covRow(w,18u)+g] != 0xffffffffu) covWrite3(covRow(w,6u)+v*16u,covWork3(covRow(w,12u)+g*3u));
        }
        """;

    internal const string TransitionGroups = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], g = gl_GlobalInvocationID.x;
            if (g >= CovFrameWords[f+8u] || covCompletedFailure(w)) return;
            uint v = CovWorkWords[covRow(w,18u)+g];
            if (v == 0xffffffffu) return;
            uint h = CovFrameWords[f]; int slot = covRegionSlot(f,int(CovStaticWords[covVertex(h,v)+8u]));
            uint command = covCommand(f,uint(slot)); float weight = covRead(CovFrameWords[command+62u]);
            vec3 before = covWork3(covRow(w,4u)+v*16u);
            if (covEqual(weight,0.0)) covWrite3(covRow(w,12u)+g*3u,before);
            else if (covNotEqual(weight,1.0)) {
                float applied = covMul(weight,covRead(CovWorkWords[covRow(w,14u)+g]));
                covWrite3(covRow(w,12u)+g*3u,covMove(before,applied,covRecess(command)));
            }
        }
        """;

    internal const string StoreTransition = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], v = gl_GlobalInvocationID.x;
            if (v >= CovFrameWords[f+6u] || covCompletedFailure(w)) return;
            uint h = CovFrameWords[f], g = CovStaticWords[covVertex(h,v)+6u], representative = CovWorkWords[covRow(w,18u)+g];
            uint source = covRow(w,6u)+v*16u, target = covRow(w,8u)+v*16u;
            if (representative != 0xffffffffu) {
                int slot = covRegionSlot(f,int(CovStaticWords[covVertex(h,representative)+8u]));
                if (covEqual(covRead(CovFrameWords[covCommand(f,uint(slot))+62u]),0.0)) source = covRow(w,4u)+v*16u;
            }
            [[dont_unroll]] for (uint field = 0u; field < 16u; field++) CovWorkWords[target+field] = CovWorkWords[source+field];
            if (representative != 0xffffffffu && source != covRow(w,4u)+v*16u) covWrite3(target,covWork3(covRow(w,12u)+g*3u));
        }
        """;
}
