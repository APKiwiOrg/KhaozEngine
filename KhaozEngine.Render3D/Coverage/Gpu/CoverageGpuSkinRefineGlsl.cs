using System;

namespace KhaozEngine.Render3D;

/// <summary>Separate precise modules for the skinning and refinement dependency chain.</summary>
internal static class CoverageGpuSkinRefineGlsl
{
    internal static bool IsReducer(CoverageGpuPhase phase) => phase is CoverageGpuPhase.ReduceSkin or CoverageGpuPhase.ReduceSource
        or CoverageGpuPhase.ReduceAliases or CoverageGpuPhase.ReduceGroups or CoverageGpuPhase.ReduceAttributes
        or CoverageGpuPhase.ReduceConform or CoverageGpuPhase.ReduceFaces or CoverageGpuPhase.ReduceFullTargets
        or CoverageGpuPhase.ReduceVertices or CoverageGpuPhase.ReduceFacesOrientation or CoverageGpuPhase.ReduceAttributesFinal;

    internal static string Name(CoverageGpuPhase phase) => "Coverage" + phase;

    internal static string Source(CoverageGpuPhase phase) => "#version 450\nlayout(local_size_x = " + (IsReducer(phase) ? "1" : "64") + ") in;\n"
        + CoverageGpuGuardGlsl.Bindings + CoverageGpuArithmeticGlsl.Source + CoverageGpuExactWordsGlsl.WorkerSource
        + CoverageGpuRoundingGlsl.Source + CoverageGpuPredicatesGlsl.Source + CoverageGpuSupportGlsl.Source
        + CoverageGpuGuardGlsl.Source + CoverageGpuConformGlsl.Helpers + CoverageGpuQueryWorkLayout.Source + (phase switch
        {
            CoverageGpuPhase.Reset => CoverageGpuGuardGlsl.Reset,
            CoverageGpuPhase.Skin => Unit + SkinHelpers + Skin,
            CoverageGpuPhase.ReduceSkin => CoverageGpuGuardGlsl.Reduce(5, 1),
            CoverageGpuPhase.ValidateSource => CoverageGpuGuardGlsl.ValidateSource,
            CoverageGpuPhase.ReduceSource => CoverageGpuGuardGlsl.Reduce(5, 2),
            CoverageGpuPhase.ValidateAliases => CoverageGpuGuardGlsl.ValidateAliases,
            CoverageGpuPhase.ReduceAliases => CoverageGpuGuardGlsl.Reduce(9, 3),
            CoverageGpuPhase.InterpolateGroups => InterpolateGroups,
            CoverageGpuPhase.ReduceGroups => CoverageGpuGuardGlsl.Reduce(8, 4),
            CoverageGpuPhase.ValidateAttributes => Unit + CoverageGpuAttributesGlsl.Source,
            CoverageGpuPhase.ReduceAttributes => CoverageGpuGuardGlsl.Reduce(6, 5),
            CoverageGpuPhase.FillBefore => CoverageGpuGuardGlsl.FillBefore,
            CoverageGpuPhase.ConformGroups => CoverageGpuConformGlsl.ConformGroups,
            CoverageGpuPhase.ConformBoundary => CoverageGpuConformGlsl.ConformBoundary,
            CoverageGpuPhase.ConformResume => CoverageGpuConformGlsl.ConformResume,
            CoverageGpuPhase.CopyPointAliases => CoverageGpuConformGlsl.CopyPointAliases,
            CoverageGpuPhase.ReduceConform => CoverageGpuGuardGlsl.ReduceTally(8, 1, 6, false),
            CoverageGpuPhase.CorrectFaces => CoverageGpuCorrectionGlsl.CorrectFaces,
            CoverageGpuPhase.CorrectFacesOverlay => CoverageGpuCorrectionGlsl.CorrectFacesOverlay,
            CoverageGpuPhase.CorrectFacesFinish => CoverageGpuCorrectionGlsl.CorrectFacesFinish,
            CoverageGpuPhase.ReduceFaces => CoverageGpuGuardGlsl.ReduceTally(7, 3, 7, true),
            CoverageGpuPhase.GatherCorrections => CoverageGpuCorrectionGlsl.GatherCorrections,
            CoverageGpuPhase.CheckFullTargets => CoverageGpuCorrectionGlsl.CheckFullTargets,
            CoverageGpuPhase.ReduceFullTargets => CoverageGpuGuardGlsl.ReduceTally(8, 2, 8, false),
            CoverageGpuPhase.CopyCorrectedAliases => CoverageGpuCorrectionGlsl.CopyCorrectedAliases,
            CoverageGpuPhase.TransitionGroups => CoverageGpuCorrectionGlsl.TransitionGroups,
            CoverageGpuPhase.StoreTransition => CoverageGpuCorrectionGlsl.StoreTransition,
            CoverageGpuPhase.ValidateVertices => CoverageGpuNormalsGlsl.ValidateVertices,
            CoverageGpuPhase.ReduceVertices => CoverageGpuGuardGlsl.Reduce(6, 9),
            CoverageGpuPhase.PrepareNormalBasis => CoverageGpuNormalWorkLayout.Source + CoverageGpuNormalBasisGlsl.Unit + CoverageGpuNormalBasisGlsl.Prepare,
            CoverageGpuPhase.FinishNormalBasis => CoverageGpuNormalWorkLayout.Source + CoverageGpuNormalBasisGlsl.Finish,
            CoverageGpuPhase.ValidateFaces => CoverageGpuNormalWorkLayout.Source + CoverageGpuNormalsGlsl.ValidateFaces,
            CoverageGpuPhase.ReduceFacesOrientation => CoverageGpuGuardGlsl.ReduceFacesOrientation,
            CoverageGpuPhase.GatherAttributes => CoverageGpuNormalWorkLayout.Source + CoverageGpuNormalBasisGlsl.Unit + CoverageGpuNormalGatherGlsl.Source,
            CoverageGpuPhase.ReduceAttributesFinal => CoverageGpuGuardGlsl.Reduce(6, 11),
            CoverageGpuPhase.Select => CoverageGpuSelectGlsl.Source,
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        });

    // The continuation requests all scalar operations through the physical kernel's one worker pump.
    internal const string Unit = """
        const int CovUnitDone = 5;
        struct CovUnit { int phase; vec3 inputValue; vec3 value; vec3 fallback; float minimum; float divisor; };
        void covUnitBegin(vec3 value, vec3 fallback, float minimum, out CovUnit q) {
            q = CovUnit(0,value,vec3(0.0),fallback,minimum,0.0);
        }
        bool covUnitStep(inout CovUnit q, CovExactResult result, out CovExactJob job) {
            job = covScalarJob(0.0,1.0,false);
            if (q.phase == 0) { q.phase = 1; job = covScalarJob(covDot3(q.inputValue,q.inputValue),0.0,true); return true; }
            if (q.phase == 1) {
                q.divisor = result.predicate.value;
                if (q.minimum >= 0.0 && !covGreater(q.divisor,q.minimum)) { q.value = q.fallback; q.phase = CovUnitDone; return false; }
                q.phase = 2; job = covScalarJob(q.inputValue.x,q.divisor,false); return true;
            }
            if (q.phase == 2) { q.value.x = result.predicate.value; q.phase = 3; job = covScalarJob(q.inputValue.y,q.divisor,false); return true; }
            if (q.phase == 3) { q.value.y = result.predicate.value; q.phase = 4; job = covScalarJob(q.inputValue.z,q.divisor,false); return true; }
            if (q.phase == 4) { q.value.z = result.predicate.value; q.phase = CovUnitDone; }
            return false;
        }
        """;

    const string Skin = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], i = gl_GlobalInvocationID.x;
            if (i >= CovFrameWords[f+5u]) return;
            uint h = CovFrameWords[f], s = covTable(h,8u)+i*20u, indices = covTable(h,10u)+i*4u, palette = f+16u;
            uint target = covRow(w,0u)+i*16u, fallback = covRow(w,2u)+i*16u;
            vec4 weights = vec4(covRead(CovStaticWords[s+16u]),covRead(CovStaticWords[s+17u]),
                covRead(CovStaticWords[s+18u]),covRead(CovStaticWords[s+19u]));
            float total = covAdd(covAdd(covAdd(weights.x,weights.y),weights.z),weights.w);
            vec3 p = covSkin3(s), originalNormal = covSkin3(s+3u), originalTangent = covSkin3(s+12u);
            bool hasTangent = covGreater(covDot3(originalTangent,originalTangent),1e-12);
            // Dynamic buffer words are invocation-owned. Vector and matrix components remain constant-indexed.
            [[dont_unroll]] for (uint axis = 0u; axis < 3u; axis++) {
                float m0 = axis == 0u ? 1.0 : 0.0, m1 = axis == 1u ? 1.0 : 0.0, m2 = axis == 2u ? 1.0 : 0.0, m3 = 0.0;
                if (!covLess(total,1e-8)) {
                    m0 = covBlend(palette,indices,weights,axis); m1 = covBlend(palette,indices,weights,4u+axis);
                    m2 = covBlend(palette,indices,weights,8u+axis); m3 = covBlend(palette,indices,weights,12u+axis);
                }
                CovWorkWords[target+axis] = floatBitsToUint(covAdd(covAdd(covAdd(covMul(p.x,m0),covMul(p.y,m1)),covMul(p.z,m2)),m3));
                CovWorkWords[target+3u+axis] = floatBitsToUint(covSkinLinear(originalNormal,m0,m1,m2));
                CovWorkWords[target+12u+axis] = floatBitsToUint(covSkinLinear(originalTangent,m0,m1,m2));
            }
            vec3 n = covWork3(target+3u), t = vec3(0.0), transformedTangent = covWork3(target+12u);
            CovUnit q; covUnitBegin(n,originalNormal,1e-8,q);
            CovExactResult result = covExactResultZero();
            int direction = 0;
            [[dont_unroll]] while (direction < 2) {
                CovExactJob job;
                bool request = covUnitStep(q,result,job);
                if (q.phase == CovUnitDone) {
                    if (direction == 0) {
                        n = q.value; direction = 1;
                        if (!hasTangent) break;
                        covUnitBegin(transformedTangent,originalTangent,1e-8,q);
                    } else { t = q.value; direction = 2; }
                }
                if (request) result = covExactWorker(job);
            }
            covWrite3(target+3u,n); covWrite3(target+12u,t);
            [[dont_unroll]] for (uint field = 6u; field < 12u; field++) CovWorkWords[target+field] = floatBitsToUint(covRead(CovStaticWords[s+field]));
            CovWorkWords[target+15u] = hasTangent ? floatBitsToUint(covRead(CovStaticWords[s+15u])) : 0u;
            bool valid = true;
            [[dont_unroll]] for (uint field = 0u; field < 16u; field++) {
                uint value = CovWorkWords[target+field]; CovWorkWords[fallback+field] = value;
                if (!covFinite(covRead(value))) valid = false;
            }
            covEvent(w,i,valid ? 0u : 4u);
        }
        """;

    const string SkinHelpers = """
        float covSkinLinear(vec3 p, float a, float b, float c) {
            return covAdd(covAdd(covMul(p.x,a),covMul(p.y,b)),covMul(p.z,c));
        }
        vec3 covSkin3(uint start) {
            return vec3(covRead(CovStaticWords[start]),covRead(CovStaticWords[start+1u]),covRead(CovStaticWords[start+2u]));
        }
        float covBlend(uint palette, uint indices, vec4 w, uint field) {
            float a = covRead(CovFrameWords[palette+CovStaticWords[indices]*16u+field]);
            float b = covRead(CovFrameWords[palette+CovStaticWords[indices+1u]*16u+field]);
            float c = covRead(CovFrameWords[palette+CovStaticWords[indices+2u]*16u+field]);
            float d = covRead(CovFrameWords[palette+CovStaticWords[indices+3u]*16u+field]);
            return covAdd(covAdd(covAdd(covMul(a,w.x),covMul(b,w.y)),covMul(c,w.z)),covMul(d,w.w));
        }
        """;

    const string InterpolateGroups = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], i = gl_GlobalInvocationID.x;
            if (i >= CovFrameWords[f+8u] || covCompletedFailure(w)) return;
            uint h = CovFrameWords[f], r = covTable(h,22u)+i*7u, source = covRow(w,2u);
            uint a = CovStaticWords[r], b = CovStaticWords[r+1u], c = CovStaticWords[r+2u];
            uint wa = CovStaticWords[r+3u], wb = CovStaticWords[r+4u], wc = CovStaticWords[r+5u];
            uint weights = covTable(h,24u)+i*3u, target = covRow(w,12u)+i*3u;
            bool original = wa == 12u || wb == 12u || wc == 12u;
            bool edge = !original && (wa == 0u || wb == 0u || wc == 0u);
            if (original) a = wa == 12u ? a : (wb == 12u ? b : c);
            if (edge) {
                if (wb == 0u) b = c;
                else if (wa == 0u) { a = b; b = c; }
                if (a > b) { uint swap = a; a = b; b = swap; }
            }
            float t = covRead(CovStaticWords[covTable(h,28u)+i]);
            bool valid = true;
            [[dont_unroll]] for (uint axis = 0u; axis < 3u; axis++) {
                float x = covRead(CovWorkWords[source+a*16u+axis]), value;
                if (original) value = x;
                else if (edge) value = covAdd(covMul(x,covSub(1.0,t)),covMul(covRead(CovWorkWords[source+b*16u+axis]),t));
                else {
                    float sum = covAdd(covAdd(covMul(x,covRead(CovStaticWords[weights])),
                        covMul(covRead(CovWorkWords[source+b*16u+axis]),covRead(CovStaticWords[weights+1u]))),
                        covMul(covRead(CovWorkWords[source+c*16u+axis]),covRead(CovStaticWords[weights+2u])));
                    CovExactResult result = covExactWorker(covScalarJob(sum,12.0,false));
                    value = result.predicate.value;
                }
                CovWorkWords[target+axis] = floatBitsToUint(value);
                if (!covFinite(value)) valid = false;
            }
            covEvent(w,i,valid ? 0u : 4u);
        }
        """;
}
