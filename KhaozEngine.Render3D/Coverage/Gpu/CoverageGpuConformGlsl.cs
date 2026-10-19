namespace KhaozEngine.Render3D;

/// <summary>One representative owns each point query and its exact-worker continuation.</summary>
internal static class CoverageGpuConformGlsl
{
    internal const string Helpers = """
        uint covCommands(uint f) { return f+16u+CovFrameWords[f+11u]*16u; }
        uint covCommand(uint f, uint slot) { return covCommands(f)+slot*80u; }
        int covRegionSlot(uint f, int region) {
            return region < 0 ? -1 : int(CovFrameWords[covCommands(f)+324u+uint(region)]);
        }
        uint covVertex(uint h, uint v) { return covTable(h,12u)+v*10u; }
        mat4 covFrameMatrix(uint start) {
            return mat4(vec4(covRead(CovFrameWords[start]),covRead(CovFrameWords[start+1u]),covRead(CovFrameWords[start+2u]),covRead(CovFrameWords[start+3u])),
                vec4(covRead(CovFrameWords[start+4u]),covRead(CovFrameWords[start+5u]),covRead(CovFrameWords[start+6u]),covRead(CovFrameWords[start+7u])),
                vec4(covRead(CovFrameWords[start+8u]),covRead(CovFrameWords[start+9u]),covRead(CovFrameWords[start+10u]),covRead(CovFrameWords[start+11u])),
                vec4(covRead(CovFrameWords[start+12u]),covRead(CovFrameWords[start+13u]),covRead(CovFrameWords[start+14u]),covRead(CovFrameWords[start+15u])));
        }
        vec3 covRecess(uint command) {
            return vec3(covRead(CovFrameWords[command+48u]),covRead(CovFrameWords[command+49u]),covRead(CovFrameWords[command+50u]));
        }
        vec3 covMove(vec3 before, float full, vec3 recess) {
            return vec3(covSub(before.x,covMul(full,recess.x)),covSub(before.y,covMul(full,recess.y)),covSub(before.z,covMul(full,recess.z)));
        }
        """;

    internal const string ConformGroups = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], g = gl_GlobalInvocationID.x;
            if (g >= CovFrameWords[f+8u] || covCompletedFailure(w)) return;
            uint h = CovFrameWords[f], before = covRow(w,4u), group = covRow(w,12u)+g*3u;
            uint representative = CovStaticWords[covTable(h,30u)+g];
            covWrite3(group,covWork3(before+representative*16u));
            CovWorkWords[covRow(w,14u)+g] = 0u;
            CovWorkWords[covRow(w,18u)+g] = 0xffffffffu;
            covNote(w,g,0u,-1,0xffffffffu,0u,0u);
            uint offsets = covTable(h,44u), candidates = covTable(h,46u);
            covConformWorkStore(covRow(w,34u)+g*6u,CovConformWork(vec3(0.0),0.0,1.0,0u));
            int slot = -1;
            [[dont_unroll]] for (uint i = CovStaticWords[offsets+g]; i < CovStaticWords[offsets+g+1u]; i++) {
                uint v = CovStaticWords[candidates+i], record = covVertex(h,v);
                int candidateSlot = covRegionSlot(f,int(CovStaticWords[record+8u]));
                if (candidateSlot >= 0 && covGreater(covRead(CovStaticWords[record+9u]),0.0)) { representative = v; slot = candidateSlot; break; }
            }
            if (slot < 0) return;
            CovWorkWords[covRow(w,18u)+g] = representative;
            uint record = covVertex(h,representative), command = covCommand(f,uint(slot)), support = CovFrameWords[command+67u];
            vec3 position = covWork3(before+representative*16u);
            vec3 q = covPoint(position,covFrameMatrix(command));
            float lower = covAdd(covRead(CovFrameWords[command+57u]),covRead(CovFrameWords[command+52u]));
            float upper = covSub(covRead(CovFrameWords[command+58u]),covRead(CovFrameWords[command+52u]));
            uint code = 0u;
            if (!covFinite3(q) || !covFinite(lower) || !covFinite(upper)) code = 4u;
            else if (covLess(q.z,lower) || covGreater(q.z,upper)) code = 6u;
            uint coreOffsets = covTable(h,32u);
            bool core = CovStaticWords[coreOffsets+g] != CovStaticWords[coreOffsets+g+1u];
            uint continuation = covRow(w,34u)+g*6u;
            CovConformWork next = CovConformWork(q,0.0,1.0,0u);
            if (code == 0u) {
                CovHeightGraph height = covHeightBegin(support,q.xy);
                CovExactResult result = covExactResultZero(); CovExactJob job;
                [[dont_unroll]] while (height.phase != CovHDone) if (covHeightStep(height,result,job)) result = covExactWorker(job);
                if (height.ok) { next.height = height.height; next.state = 2u; }
                else if (core) code = 5u;
                else next.state = 1u;
            }
            covConformWorkStore(continuation,next);
            covWrite3(group,position);
            covNote(w,g,code,slot,representative,0u,code == 0u ? 0u : 1u);
        }
        """;

    internal const string ConformBoundary = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], g = gl_GlobalInvocationID.x;
            if (g >= CovFrameWords[f+8u] || covCompletedFailure(w)) return;
            uint continuation = covRow(w,34u)+g*6u;
            CovConformWork next = covConformWorkRead(continuation);
            if (next.state != 1u) return;
            uint h = CovFrameWords[f], v = CovWorkWords[covRow(w,18u)+g];
            int slot = covRegionSlot(f,int(CovStaticWords[covVertex(h,v)+8u]));
            uint command = covCommand(f,uint(slot));
            CovBoundaryGraph boundary = covBoundaryBegin(CovFrameWords[command+67u],next.query.xy);
            CovExactResult result = covExactResultZero(); int phase = 0;
            float collar = covRead(CovFrameWords[command+59u]); uint code = 0u;
            [[dont_unroll]] while (phase < 3) {
                CovExactJob job = covScalarJob(0.0,1.0,false); bool request = false;
                if (phase == 0) {
                    request = covBoundaryStep(boundary,result,job);
                    if (boundary.phase == CovBDone) {
                        if (!boundary.ok) { code = 3u; next.state = 0u; phase = 3; }
                        else if (covGreaterOrEqual(boundary.distance,collar)) { next.state = 0u; phase = 3; }
                        else phase = 1;
                    }
                } else if (phase == 1) {
                    job = covScalarJob(boundary.distance,collar,false); request = true; phase = 2;
                } else {
                    float u = covClamp(covSub(1.0,result.predicate.value),0.0,1.0);
                    next.spatial = covMul(covMul(u,u),covSub(3.0,covMul(2.0,u)));
                    next.height = boundary.height; next.state = 2u; phase = 3;
                }
                if (request) result = covExactWorker(job);
            }
            covConformWorkStore(continuation,next);
            covNote(w,g,code,slot,v,0u,code == 0u ? 0u : 1u);
        }
        """;

    internal const string ConformResume = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], g = gl_GlobalInvocationID.x;
            if (g >= CovFrameWords[f+8u] || covCompletedFailure(w)) return;
            CovConformWork next = covConformWorkRead(covRow(w,34u)+g*6u);
            if (next.state != 2u) return;
            uint h = CovFrameWords[f], v = CovWorkWords[covRow(w,18u)+g], record = covVertex(h,v);
            int slot = covRegionSlot(f,int(CovStaticWords[record+8u])); uint command = covCommand(f,uint(slot));
            uint coreOffsets = covTable(h,32u); bool core = CovStaticWords[coreOffsets+g] != CovStaticWords[coreOffsets+g+1u];
            vec3 position = covWork3(covRow(w,4u)+v*16u);
            float target = covSub(next.height,covRead(CovFrameWords[command+53u])), difference = covSub(next.query.z,target);
            float full = covMul(covMul(core ? 1.0 : covRead(CovStaticWords[record+9u]),next.spatial),covMax(0.0,difference));
            float world = covMul(full,covRead(CovFrameWords[command+51u])); uint code = 0u;
            if (!covFinite(next.height) || !covFinite(next.spatial) || !covFinite(target) || !covFinite(difference) || !covFinite(full) || !covFinite(world)) code = 4u;
            else if (covGreater(world,covRead(CovFrameWords[command+61u]))) code = 7u;
            else if (covNotEqual(full,0.0)) {
                vec3 moved = covMove(position,full,covRecess(command)), movedQ = covPoint(moved,covFrameMatrix(command));
                float lower = covAdd(covRead(CovFrameWords[command+57u]),covRead(CovFrameWords[command+52u]));
                float upper = covSub(covRead(CovFrameWords[command+58u]),covRead(CovFrameWords[command+52u]));
                if (!covFinite3(moved) || !covFinite3(movedQ)) code = 4u;
                else if (covLess(movedQ.z,lower) || covGreater(movedQ.z,upper)) code = 6u;
                else {
                    CovWorkWords[covRow(w,14u)+g] = floatBitsToUint(full);
                    if (!covEqual3(moved,position)) position = moved;
                }
            }
            covWrite3(covRow(w,12u)+g*3u,position);
            covNote(w,g,code,slot,v,0u,code == 0u ? 0u : 1u);
        }
        """;

    internal const string CopyPointAliases = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], v = gl_GlobalInvocationID.x;
            if (v >= CovFrameWords[f+6u] || covCompletedFailure(w)) return;
            uint h = CovFrameWords[f], g = CovStaticWords[covVertex(h,v)+6u];
            uint before = covRow(w,4u)+v*16u, after = covRow(w,6u)+v*16u;
            [[dont_unroll]] for (uint field = 0u; field < 16u; field++) CovWorkWords[after+field] = CovWorkWords[before+field];
            if (CovWorkWords[covRow(w,18u)+g] != 0xffffffffu) covWrite3(after,covWork3(covRow(w,12u)+g*3u));
        }
        """;
}
