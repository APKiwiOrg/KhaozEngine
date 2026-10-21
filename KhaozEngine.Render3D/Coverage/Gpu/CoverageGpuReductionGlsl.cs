namespace KhaozEngine.Render3D;

/// <summary>One workgroup per actor, with deterministic integer winner and saturated-count merges.</summary>
internal static class CoverageGpuReductionGlsl
{
    const string Helpers = """
        shared uint CovGuardRows[64];
        uint covGuardMin(uint value) {
            uint lane = gl_LocalInvocationID.x;
            CovGuardRows[lane] = value; barrier();
            [[dont_unroll]] for (uint stride = 32u; stride > 0u; stride >>= 1u) {
                if (lane < stride) CovGuardRows[lane] = min(CovGuardRows[lane],CovGuardRows[lane+stride]);
                barrier();
            }
            uint result = CovGuardRows[0];
            // Every lane consumes the result before another reduction can reuse shared storage.
            barrier(); return result;
        }
        uint covGuardEarlier(uint events, uint a, uint b, bool slotFirst) {
            if (a == 0xffffffffu) return b;
            if (b == 0xffffffffu) return a;
            uint x = events+a*6u, y = events+b*6u;
            if (slotFirst && CovWorkWords[x+1u] != CovWorkWords[y+1u])
                return CovWorkWords[x+1u] < CovWorkWords[y+1u] ? a : b;
            if (CovWorkWords[x+2u] != CovWorkWords[y+2u]) return CovWorkWords[x+2u] < CovWorkWords[y+2u] ? a : b;
            if (CovWorkWords[x+3u] != CovWorkWords[y+3u]) return CovWorkWords[x+3u] < CovWorkWords[y+3u] ? a : b;
            // Equal reference keys retain the first physical row, as in the serial scan.
            return min(a,b);
        }
        uint covGuardFirst(uint events, uint value, bool slotFirst) {
            uint lane = gl_LocalInvocationID.x;
            CovGuardRows[lane] = value; barrier();
            [[dont_unroll]] for (uint stride = 32u; stride > 0u; stride >>= 1u) {
                if (lane < stride) CovGuardRows[lane] = covGuardEarlier(events,CovGuardRows[lane],CovGuardRows[lane+stride],slotFirst);
                barrier();
            }
            uint result = CovGuardRows[0]; barrier(); return result;
        }
        uint covGuardSum(uint value) {
            uint lane = gl_LocalInvocationID.x;
            CovGuardRows[lane] = value; barrier();
            [[dont_unroll]] for (uint stride = 32u; stride > 0u; stride >>= 1u) {
                if (lane < stride) CovGuardRows[lane] += min(CovGuardRows[lane+stride],2147483647u-CovGuardRows[lane]);
                barrier();
            }
            uint result = CovGuardRows[0]; barrier(); return result;
        }
        void covGuardRefuse(uint f, uint w, uint code, uint stage) {
            CovWorkWords[w+26u] = code; CovWorkWords[w+27u] = 0xffffffffu;
            CovWorkWords[w+28u] = 0u; CovWorkWords[w+29u] = stage;
            uint r = CovFrameWords[f+4u]; CovResultWords[r+8u] = code;
            CovResultWords[r+9u] = 0u; CovResultWords[r+10u] = 0u; CovResultWords[r+11u] = 0u;
            CovResultWords[r+12u] = 0u; CovResultWords[r+13u] = 0u; CovResultWords[r+14u] = 1u;
        }
        """;

    internal static string Reduce(int countWord, int stage) => Helpers + $$"""
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], lane = gl_LocalInvocationID.x;
            if (covCompletedFailure(w)) return;
            uint events = covRow(w,24u), count = CovFrameWords[f+{{countWord}}u], first = 0xffffffffu;
            [[dont_unroll]] for (uint i = lane; i < count; i += 64u) {
                if (CovWorkWords[events+i*6u] != 0u) { first = i; break; }
            }
            first = covGuardMin(first);
            if (lane == 0u && first != 0xffffffffu) covGuardRefuse(f,w,CovWorkWords[events+first*6u],{{stage}}u);
        }
        """;

    internal static string ReduceTally(int countWord, int notes, int stage, bool slotFirst) => Helpers + $$"""
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], lane = gl_LocalInvocationID.x;
            if (covCompletedFailure(w)) return;
            uint events = covRow(w,24u), count = CovFrameWords[f+{{countWord}}u]*{{notes}}u, first = 0xffffffffu;
            [[dont_unroll]] for (uint i = lane; i < count; i += 64u) {
                if (CovWorkWords[events+i*6u] != 0u) first = covGuardEarlier(events,first,i,{{(slotFirst ? "true" : "false")}});
            }
            first = covGuardFirst(events,first,{{(slotFirst ? "true" : "false")}});
            if (first == 0xffffffffu) return;
            uint code = CovWorkWords[events+first*6u], slot = CovWorkWords[events+first*6u+1u], matching = 0u;
            [[dont_unroll]] for (uint i = lane; i < count; i += 64u) {
                uint e = events+i*6u;
                if (CovWorkWords[e] == code && CovWorkWords[e+1u] == slot)
                    matching += min(CovWorkWords[e+4u],2147483647u-matching);
            }
            matching = covGuardSum(matching);
            if (lane != 0u) return;
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

    internal static string ReduceFacesOrientation => Helpers + """
        shared uint CovFirstSigns[256];
        int covGuardSign(uint faces, uint row) { return row == 0xffffffffu ? 0 : int(CovWorkWords[faces+row*2u+1u]); }
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], lane = gl_LocalInvocationID.x;
            if (covCompletedFailure(w)) return;
            uint events = covRow(w,24u), faces = covRow(w,22u), count = CovFrameWords[f+7u];
            uint first0 = 0xffffffffu, first1 = 0xffffffffu, first2 = 0xffffffffu, first3 = 0xffffffffu;
            [[dont_unroll]] for (uint t = lane; t < count; t += 64u) {
                if (CovWorkWords[events+t*6u] != 0u) continue;
                int slot = int(CovWorkWords[faces+t*2u]);
                if (slot < 0) continue;
                if (slot == 0) first0 = min(first0,t); else if (slot == 1) first1 = min(first1,t);
                else if (slot == 2) first2 = min(first2,t); else first3 = min(first3,t);
            }
            CovFirstSigns[lane] = first0; CovFirstSigns[lane+64u] = first1;
            CovFirstSigns[lane+128u] = first2; CovFirstSigns[lane+192u] = first3;
            barrier();
            [[dont_unroll]] for (uint stride = 32u; stride > 0u; stride >>= 1u) {
                if (lane < stride) {
                    CovFirstSigns[lane] = min(CovFirstSigns[lane],CovFirstSigns[lane+stride]);
                    CovFirstSigns[lane+64u] = min(CovFirstSigns[lane+64u],CovFirstSigns[lane+64u+stride]);
                    CovFirstSigns[lane+128u] = min(CovFirstSigns[lane+128u],CovFirstSigns[lane+128u+stride]);
                    CovFirstSigns[lane+192u] = min(CovFirstSigns[lane+192u],CovFirstSigns[lane+192u+stride]);
                }
                barrier();
            }
            int sign0 = covGuardSign(faces,CovFirstSigns[0]), sign1 = covGuardSign(faces,CovFirstSigns[64]);
            int sign2 = covGuardSign(faces,CovFirstSigns[128]), sign3 = covGuardSign(faces,CovFirstSigns[192]);
            uint first = 0xffffffffu;
            [[dont_unroll]] for (uint t = lane; t < count; t += 64u) {
                uint code = CovWorkWords[events+t*6u];
                int slot = int(CovWorkWords[faces+t*2u]), sign = int(CovWorkWords[faces+t*2u+1u]);
                if (code == 0u && slot >= 0) {
                    int initial = slot == 0 ? sign0 : (slot == 1 ? sign1 : (slot == 2 ? sign2 : sign3));
                    if (sign == 0 || sign != initial) code = 8u;
                }
                if (code != 0u) { first = t; break; }
            }
            first = covGuardMin(first);
            if (lane != 0u || first == 0xffffffffu) return;
            uint code = CovWorkWords[events+first*6u];
            atomicOr(CovWorkWords[w+30u],1u);
            covGuardRefuse(f,w,code == 0u ? 8u : code,10u);
        }
        """;
}
