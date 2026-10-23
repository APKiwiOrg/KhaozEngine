using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

internal enum CoverageGpuFilteredStage { Faces, CompactFaces, ExactFaces, Pairs, CompactPairs, ExactPairs, Replay }

/// <summary>Original query continuations with a narrow certified worker, plus immutable exact replay modules.</summary>
internal static class CoverageGpuFilteredSources
{
    internal static string Name(CoverageGpuFilteredStage stage, CoverageGpuFilterPath path) =>
        "CoverageFiltered" + (IsProducer(stage) ? path == CoverageGpuFilterPath.FilteredFlush ? "EftFlush" : "EftPreserve" : "") + stage;

    internal static bool IsProducer(CoverageGpuFilteredStage stage) => stage is CoverageGpuFilteredStage.Faces or CoverageGpuFilteredStage.Pairs;

    internal static GpuShaderCompileOptions Options(CoverageGpuFilteredStage stage, CoverageGpuFilterPath path) =>
        new(PreciseMath: true, RequireFloat32Eft: IsProducer(stage),
            FlushFloat32Denorms: IsProducer(stage) && path == CoverageGpuFilterPath.FilteredFlush);

    internal static string Source(CoverageGpuFilteredStage stage)
    {
        if (stage == CoverageGpuFilteredStage.ExactFaces)
            return CoverageGpuSkinRefineGlsl.Source(CoverageGpuPhase.CorrectFaces).Replace(
                "t = gl_GlobalInvocationID.x;", "t = 0u;\nif (gl_GlobalInvocationID.x >= CovWorkWords[w+52u]) return;\n"
                + "t = CovWorkWords[covRow(w,48u)+gl_GlobalInvocationID.x];", StringComparison.Ordinal);
        if (stage is CoverageGpuFilteredStage.CompactFaces or CoverageGpuFilteredStage.CompactPairs or CoverageGpuFilteredStage.Replay)
            return Header + CoverageGpuGuardGlsl.Bindings + CoverageGpuArithmeticGlsl.Source + "struct CovPredicate { int sign; float value; };\n"
                + CoverageGpuSupportGlsl.DataSource + CoverageGpuGuardGlsl.Source + CoverageGpuConformGlsl.Helpers
                + CoverageGpuQueryWorkLayout.Source + (stage switch
                {
                    CoverageGpuFilteredStage.CompactFaces => CompactFaces,
                    CoverageGpuFilteredStage.CompactPairs => CompactPairs,
                    _ => Replay
                });
        if (stage == CoverageGpuFilteredStage.ExactPairs)
            return Header + "uint CovFilterPairEnd = 0u;\n" + CoverageGpuGuardGlsl.Bindings + CoverageGpuArithmeticGlsl.Source
                + CoverageGpuExactWordsGlsl.WorkerSource + CoverageGpuRoundingGlsl.Source + CoverageGpuPredicatesGlsl.Source
                + CoverageGpuSupportGlsl.DataSource + CoverageGpuTriangleHeightGlsl.Source + CoverageGpuOverlayStateGlsl.Source
                + PairQuery(CoverageGpuOverlayQueryGlsl.Source) + CoverageGpuGuardGlsl.Source + CoverageGpuConformGlsl.Helpers
                + CoverageGpuQueryWorkLayout.Source + PairMain(exact: true);
        string crossing = CoverageGpuPredicatesGlsl.Source[CoverageGpuExactJobsGlsl.Source.Length..];
        string cursors = CoverageGpuOverlayStateGlsl.Source;
        cursors = cursors[cursors.IndexOf("struct CovOverlayCrossingGraph", StringComparison.Ordinal)..];
        string queries = crossing + CoverageGpuSupportGlsl.DataSource + CoverageGpuTriangleHeightGlsl.Source
            + CoverageGpuHeightQueryGlsl.Source + SavedBall + cursors + PairQuery(CoverageGpuOverlayQueryGlsl.Source);
        return Header + "uint CovFilterPairEnd = 0u;\n" + CoverageGpuGuardGlsl.Bindings + CoverageGpuArithmeticGlsl.Source
            + CoverageGpuRoundingGlsl.Source + CoverageGpuFilterBallGlsl.Source + CoverageGpuFilterJobsGlsl.Source
            + Narrow(queries) + CoverageGpuGuardGlsl.Source + CoverageGpuConformGlsl.Helpers
            + CoverageGpuQueryWorkLayout.Source + (stage == CoverageGpuFilteredStage.Faces ? Faces() : PairMain(exact: false));
    }

    const string Header = "#version 450\n#extension GL_EXT_control_flow_attributes : require\nlayout(local_size_x = 64) in;\n";
    static string Narrow(string source) => source.Replace("CovExact", "CovFiltered", StringComparison.Ordinal)
        .Replace("covExact", "covFiltered", StringComparison.Ordinal);
    static string PairQuery(string source) => source.Replace("q.cursor == CovStaticWords[q.support+5u]", "q.cursor == CovFilterPairEnd", StringComparison.Ordinal);

    static string Faces() => Narrow(CoverageGpuCorrectionGlsl.CorrectFaces)
        .Replace("if (t >= CovFrameWords[f+7u] || covCompletedFailure(w)) return;",
            "if (t >= CovFrameWords[f+7u]) return;\nif (CovWorkWords[w+56u] > 0u) CovWorkWords[covRow(w,44u)+t] = 0u;\nif (covCompletedFailure(w)) return;", StringComparison.Ordinal)
        .Replace("int slot = covRegionSlot", "if (CovWorkWords[w+56u] == 0u) return;\nint slot = covRegionSlot", StringComparison.Ordinal)
        .Replace("CovTriangle host =", "CovWorkWords[covRow(w,44u)+t] = 2u;\nCovTriangle host =", StringComparison.Ordinal)
        .Replace("while (phase < 4)", "while (phase < 4 && !CovFilterRefused)", StringComparison.Ordinal)
        .Replace("if (valid) covFaceWorkStore", "if (CovFilterRefused) CovWorkWords[covRow(w,44u)+t] = 1u;\nelse if (valid) covFaceWorkStore", StringComparison.Ordinal);

    // The original compact crossing cursor saves one value across requests. A ball needs four scalar words.
    const string SavedBall = """
        CovExact CovFilterSaved;
        CovExact covOverlayExactRead() { return CovFilterSaved; }
        void covOverlayExactStore(CovExact value) { CovFilterSaved = value; }
        """;

    static string PairMain(bool exact) => """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], item = gl_GlobalInvocationID.x+gl_WorkGroupID.z*4194240u;
        """ + (exact ? """
            if (item >= CovWorkWords[w+53u]) return;
            item = CovWorkWords[covRow(w,50u)+item];
        """ : """
            if (item >= CovWorkWords[w+47u]) return;
            uint record = covRow(w,46u)+item*3u;
            CovWorkWords[record] = 0u;
        """) + """
            uint capacity = CovWorkWords[w+56u];
            if (capacity == 0u) return;
            uint t = item/capacity, pair = item%capacity;
            uint continuation = covRow(w,36u)+t*12u;
            if (CovWorkWords[continuation+11u] != 1u || covCompletedFailure(w)) return;
            uint h = CovFrameWords[f], face = covTable(h,14u)+t*3u;
            int slot = covRegionSlot(f,int(CovStaticWords[face+1u])); uint command = covCommand(f,uint(slot));
            uint support = CovFrameWords[command+67u];
            if (pair >= CovStaticWords[support+5u]) return;
            CovFaceWork next = covFaceWorkRead(continuation);
            CovOverlayGraph overlay = covOverlayBegin(support,next.host.a,next.host.b,next.host.c);
            overlay.cursor = pair; CovFilterPairEnd = pair+1u;
        """ + (exact ? """
            CovExactResult result = covExactResultZero(); CovExactJob job;
            [[dont_unroll]] while (overlay.phase != CovODone)
                if (covOverlayStep(overlay,result,job)) result = covExactWorker(job);
            uint record = covRow(w,46u)+item*3u;
            CovWorkWords[record] = 2u;
        """ : """
            CovFilteredResult result = covFilteredResultZero(); CovFilteredJob job;
            [[dont_unroll]] while (overlay.phase != CovODone && !CovFilterRefused) {
                if (covOverlayStep(overlay,result,job)) {
                    CovFilterSignOnly = overlay.phase == CovOSide;
                    result = covFilteredWorker(job);
                }
            }
            CovWorkWords[record] = CovFilterRefused ? 1u : 2u;
        """) + """
            CovWorkWords[record+1u] = floatBitsToUint(overlay.maximum);
            CovWorkWords[record+2u] = overlay.ok ? 1u : 0u;
        }
        """;

    const string CompactFaces = """
        void main() {
            if (gl_GlobalInvocationID.x != 0u) return;
            uint f = covFrame(), w = CovFrameWords[f+1u], count = 0u, enabled = 0u;
            [[dont_unroll]] for (uint t = 0u; t < CovWorkWords[w+45u]; t++) {
                uint state = CovWorkWords[covRow(w,44u)+t];
                if (state != 0u) enabled++;
                if (state == 1u) CovWorkWords[covRow(w,48u)+count++] = t;
            }
            CovWorkWords[w+52u] = count; CovWorkWords[w+54u] = enabled;
        }
        """;

    const string CompactPairs = """
        void main() {
            if (gl_GlobalInvocationID.x != 0u) return;
            uint f = covFrame(), w = CovFrameWords[f+1u], count = 0u, enabled = 0u;
            [[dont_unroll]] for (uint item = 0u; item < CovWorkWords[w+47u]; item++) {
                uint state = CovWorkWords[covRow(w,46u)+item*3u];
                if (state != 0u) enabled++;
                if (state == 1u) CovWorkWords[covRow(w,50u)+count++] = item;
            }
            CovWorkWords[w+53u] = count; CovWorkWords[w+55u] = enabled;
        }
        """;

    const string Replay = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], t = gl_GlobalInvocationID.x;
            if (t >= CovFrameWords[f+7u] || covCompletedFailure(w)) return;
            uint continuation = covRow(w,36u)+t*12u;
            if (CovWorkWords[continuation+11u] != 1u) return;
            uint h = CovFrameWords[f], face = covTable(h,14u)+t*3u;
            int slot = covRegionSlot(f,int(CovStaticWords[face+1u])); uint command = covCommand(f,uint(slot));
            uint support = CovFrameWords[command+67u], capacity = CovWorkWords[w+56u];
            float maximum = uintBitsToFloat(0xff800000u); bool ok = true;
            [[dont_unroll]] for (uint pair = 0u; pair < CovStaticWords[support+5u]; pair++) {
                uint record = covRow(w,46u)+(t*capacity+pair)*3u;
                maximum = covMax(maximum,covRead(CovWorkWords[record+1u]));
                if (CovWorkWords[record+2u] == 0u) { ok = false; break; }
            }
            if (!ok) covNote(w,t*3u,4u,slot,t,0u,1u);
            CovWorkWords[continuation+10u] = floatBitsToUint(maximum);
            CovWorkWords[continuation+11u] = ok ? 2u : 0u;
        }
        """;
}
