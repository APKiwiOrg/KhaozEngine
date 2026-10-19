namespace KhaozEngine.Render3D;

/// <summary>Shared word ABI, first-failure reduction and reference-key tally attribution.</summary>
internal static class CoverageGpuGuardGlsl
{
    internal const string Bindings = """
        layout(std430, set = 0, binding = 0) readonly buffer CovStaticBuf { uint CovStaticWords[]; };
        layout(std430, set = 0, binding = 1) readonly buffer CovFrameBuf { uint CovFrameWords[]; };
        layout(std430, set = 0, binding = 2) buffer CovWorkBuf { uint CovWorkWords[]; };
        layout(std430, set = 0, binding = 3) buffer CovCurrentBuf { uint CovCurrentWords[]; };
        layout(std430, set = 0, binding = 4) buffer CovPreviousBuf { uint CovPreviousWords[]; };
        layout(std430, set = 0, binding = 5) buffer CovResultsBuf { uint CovResultWords[]; };
        """;

    internal const string Source = """
        uint covFrame() { return CovFrameWords[gl_GlobalInvocationID.y]; }
        uint covTable(uint host, uint pair) { return host + CovStaticWords[host+pair]; }
        uint covRow(uint work, uint pair) { return work + CovWorkWords[work+pair]; }
        vec3 covWork3(uint start) {
            return vec3(covRead(CovWorkWords[start]),covRead(CovWorkWords[start+1u]),covRead(CovWorkWords[start+2u]));
        }
        void covWrite3(uint start, vec3 value) {
            CovWorkWords[start] = floatBitsToUint(covF(value.x));
            CovWorkWords[start+1u] = floatBitsToUint(covF(value.y));
            CovWorkWords[start+2u] = floatBitsToUint(covF(value.z));
        }
        void covEvent(uint work, uint element, uint code) {
            uint event = covRow(work,24u)+element*6u;
            CovWorkWords[event] = code; CovWorkWords[event+1u] = 0xffffffffu;
            CovWorkWords[event+2u] = element; CovWorkWords[event+3u] = 0u;
            CovWorkWords[event+4u] = 0u; CovWorkWords[event+5u] = 0u;
            if (code != 0u) atomicOr(CovWorkWords[work+30u],1u);
        }
        bool covCompletedFailure(uint work) { return CovWorkWords[work+26u] != 0u; }
        void covNote(uint work, uint row, uint code, int slot, uint element, uint localOrder, uint count) {
            uint event = covRow(work,24u)+row*6u;
            CovWorkWords[event] = code; CovWorkWords[event+1u] = uint(slot);
            CovWorkWords[event+2u] = element; CovWorkWords[event+3u] = localOrder;
            CovWorkWords[event+4u] = count; CovWorkWords[event+5u] = 0u;
            if (code != 0u) atomicOr(CovWorkWords[work+30u],1u);
        }
        """;

    internal static string ReduceTally(int countWord, int notes, int stage, bool slotFirst) => $$"""
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u];
            if (covCompletedFailure(w)) return;
            uint events = covRow(w,24u), count = CovFrameWords[f+{{countWord}}u]*{{notes}}u;
            uint code = 0u, slot = 0xffffffffu, element = 0xffffffffu, localOrder = 0xffffffffu;
            [[dont_unroll]] for (uint i = 0u; i < count; i++) {
                uint e = events+i*6u, candidate = CovWorkWords[e], s = CovWorkWords[e+1u], v = CovWorkWords[e+2u], l = CovWorkWords[e+3u];
                if (candidate == 0u) continue;
                bool earlier = v < element || v == element && l < localOrder;
                {{(slotFirst ? "earlier = s < slot || s == slot && earlier;" : "")}}
                if (code == 0u || earlier) { code = candidate; slot = s; element = v; localOrder = l; }
            }
            if (code == 0u) return;
            uint matching = 0u;
            [[dont_unroll]] for (uint i = 0u; i < count; i++) {
                uint e = events+i*6u;
                if (CovWorkWords[e] == code && CovWorkWords[e+1u] == slot)
                    matching += min(CovWorkWords[e+4u],2147483647u-matching);
            }
            CovWorkWords[w+26u] = code; CovWorkWords[w+27u] = slot;
            CovWorkWords[w+28u] = matching; CovWorkWords[w+29u] = {{stage}}u;
            uint original = CovFrameWords[covCommands(f)+320u+slot], command = covCommand(f,slot);
            // Order is the original submission index. Find its packed frame before attributing the failure.
            [[dont_unroll]] for (uint i = 0u; i < CovFrameWords[f+12u]; i++) {
                uint c = covCommand(f,i);
                if (CovFrameWords[c+56u] == original) { command = c; break; }
            }
            uint r = CovFrameWords[f+4u];
            CovResultWords[r+8u] = code;
            CovResultWords[r+9u] = CovFrameWords[command+64u]; CovResultWords[r+10u] = CovFrameWords[command+65u];
            CovResultWords[r+11u] = CovFrameWords[command+63u]; CovResultWords[r+12u] = matching;
            CovResultWords[r+13u] = 0u; CovResultWords[r+14u] = CovWorkWords[w+30u];
        }
        """;

    internal const string Reset = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], i = gl_GlobalInvocationID.x;
            if (i < 6u) CovWorkWords[w+26u+i] = i == 1u ? 0xffffffffu : 0u;
            if (i < 8u) {
                CovResultWords[CovFrameWords[f+4u]+8u+i] = 0u;
                CovCurrentWords[CovFrameWords[f+2u]+8u+i] = 0u;
            }
            if (i < CovWorkWords[w+25u]) {
                uint e = covRow(w,24u)+i*6u;
                [[dont_unroll]] for (uint k = 0u; k < 6u; k++) CovWorkWords[e+k] = 0u;
            }
            if (i < CovFrameWords[f+8u]) {
                covWrite3(covRow(w,12u)+i*3u,vec3(0.0));
                CovWorkWords[covRow(w,14u)+i] = 0u;
                CovWorkWords[covRow(w,16u)+i] = 0u;
                CovWorkWords[covRow(w,18u)+i] = 0xffffffffu;
            }
            if (i < CovFrameWords[f+7u]) {
                CovWorkWords[covRow(w,20u)+i] = 0u;
                CovWorkWords[covRow(w,22u)+i*2u] = 0u;
                CovWorkWords[covRow(w,22u)+i*2u+1u] = 0u;
            }
        }
        """;

    internal static string Reduce(int countWord, int stage) => $$"""
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u];
            if (covCompletedFailure(w)) return;
            uint events = covRow(w,24u), count = CovFrameWords[f+{{countWord}}u];
            [[dont_unroll]] for (uint i = 0u; i < count; i++) {
                uint code = CovWorkWords[events+i*6u];
                if (code == 0u) continue;
                CovWorkWords[w+26u] = code; CovWorkWords[w+27u] = 0xffffffffu;
                CovWorkWords[w+28u] = 0u; CovWorkWords[w+29u] = {{stage}}u;
                uint r = CovFrameWords[f+4u];
                CovResultWords[r+8u] = code;
                CovResultWords[r+9u] = 0u; CovResultWords[r+10u] = 0u;
                CovResultWords[r+11u] = 0u; CovResultWords[r+12u] = 0u; CovResultWords[r+13u] = 0u;
                CovResultWords[r+14u] = 1u;
                return;
            }
        }
        """;

    internal const string ValidateSource = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], i = gl_GlobalInvocationID.x;
            if (i >= CovFrameWords[f+5u] || covCompletedFailure(w)) return;
            uint source = covRow(w,0u)+i*16u, target = covRow(w,2u)+i*16u;
            bool valid = true;
            [[dont_unroll]] for (uint field = 0u; field < 16u; field++) {
                float value = covRead(CovWorkWords[source+field]);
                CovWorkWords[target+field] = floatBitsToUint(value);
                if (!covFinite(value)) valid = false;
            }
            covEvent(w,i,valid ? 0u : 4u);
        }
        """;

    internal const string ValidateAliases = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], i = gl_GlobalInvocationID.x;
            if (i >= CovFrameWords[f+9u] || covCompletedFailure(w)) return;
            uint h = CovFrameWords[f], a = covTable(h,18u)+i*2u, source = covRow(w,2u);
            vec3 p = covWork3(source+CovStaticWords[a]*16u), r = covWork3(source+CovStaticWords[a+1u]*16u);
            covEvent(w,i,covEqual3(p,r) ? 0u : 8u);
        }
        """;

    internal const string FillBefore = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], i = gl_GlobalInvocationID.x;
            if (i >= CovFrameWords[f+6u] || covCompletedFailure(w)) return;
            uint h = CovFrameWords[f], record = covTable(h,12u)+i*10u;
            int original = int(CovStaticWords[record+1u]);
            uint source = original >= 0 ? covRow(w,2u)+uint(original)*16u : covRow(w,32u)+i*16u;
            uint target = covRow(w,4u)+i*16u;
            [[dont_unroll]] for (uint field = 0u; field < 16u; field++) CovWorkWords[target+field] = CovWorkWords[source+field];
            if (i == 0u) CovWorkWords[w+31u] = 1u;
        }
        """;
}
