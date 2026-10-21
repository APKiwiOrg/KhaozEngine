namespace KhaozEngine.Render3D;

/// <summary>Independent contiguous support producers followed by one source-ordered face replay.</summary>
internal static class CoverageGpuOverlayGlsl
{
    internal const string Produce = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], t = gl_GlobalInvocationID.x, stripe = gl_GlobalInvocationID.z;
            if (t >= CovFrameWords[f+7u] || covCompletedFailure(w)) return;
            uint continuation = covRow(w,36u)+t*12u;
            if (CovWorkWords[continuation+11u] != 1u) return;
            CovFaceWork next = covFaceWorkRead(continuation);
            uint h = CovFrameWords[f], face = covTable(h,14u)+t*3u;
            int slot = covRegionSlot(f,int(CovStaticWords[face+1u])); uint command = covCommand(f,uint(slot));
            uint support = CovFrameWords[command+67u], count = CovStaticWords[support+5u];
            uint guard = covRow(w,46u)+t*4u+stripe;
            CovWorkWords[guard] = 0u;
            if (count > CovWorkWords[w+48u]) return;
            uint first = covOverlayStripeStart(stripe,count), end = covOverlayStripeStart(stripe+1u,count);
            [[dont_unroll]] for (uint pair = first; pair < end; pair++) {
                uint p = covOverlayPairWork(w,t,pair);
                CovWorkWords[p+15u] = 0u; CovWorkWords[p+16u] = 0u;
            }
            CovOverlayGraph overlay = covOverlayBegin(support,next.host.a,next.host.b,next.host.c);
            overlay.cursor = first; overlay.limit = end;
            CovExactResult result = covExactResultZero(); CovExactJob job;
            [[dont_unroll]] while (overlay.phase != CovODone) {
                uint pair = overlay.cursor;
                bool request = covOverlayStep(overlay,result,job);
                if (overlay.sampled) {
                    uint p = covOverlayPairWork(w,t,pair);
                    CovWorkWords[p+uint(overlay.ordinal)] = floatBitsToUint(overlay.sampleValue);
                    CovWorkWords[p+15u] |= 1u << uint(overlay.ordinal);
                }
                if (overlay.cursor != pair) CovWorkWords[covOverlayPairWork(w,t,pair)+16u] = 1u;
                if (request) result = covExactWorker(job);
            }
            CovWorkWords[guard] = overlay.hostArea.sign == 0 ? 0u : 1u;
        }
        """;

    internal const string Replay = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], t = gl_GlobalInvocationID.x;
            if (t >= CovFrameWords[f+7u] || covCompletedFailure(w)) return;
            uint continuation = covRow(w,36u)+t*12u;
            if (CovWorkWords[continuation+11u] != 1u) return;
            uint h = CovFrameWords[f], face = covTable(h,14u)+t*3u;
            int slot = covRegionSlot(f,int(CovStaticWords[face+1u])); uint command = covCommand(f,uint(slot));
            uint support = CovFrameWords[command+67u], count = CovStaticWords[support+5u];
            bool ok = count <= CovWorkWords[w+48u];
            [[dont_unroll]] for (uint stripe = 0u; stripe < 4u; stripe++)
                if (CovWorkWords[covRow(w,46u)+t*4u+stripe] != 1u) ok = false;
            float maximum = uintBitsToFloat(0xff800000u);
            [[dont_unroll]] for (uint pair = 0u; pair < count && ok; pair++) {
                uint p = covOverlayPairWork(w,t,pair), mask = CovWorkWords[p+15u];
                [[dont_unroll]] for (uint candidate = 0u; candidate < 15u; candidate++)
                    if ((mask & (1u << candidate)) != 0u) maximum = covMax(maximum,covRead(CovWorkWords[p+candidate]));
                ok = CovWorkWords[p+16u] == 1u;
            }
            if (!ok) covNote(w,t*3u,4u,slot,t,0u,1u);
            CovWorkWords[continuation+10u] = floatBitsToUint(maximum);
            CovWorkWords[continuation+11u] = ok ? 2u : 0u;
        }
        """;
}
