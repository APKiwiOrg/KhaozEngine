using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Research kernels reuse the pinned numerical graphs. No production pipeline selects these programs.</summary>
internal static class CoverageCarryShaders
{
    internal static IEnumerable<ShippedComputeKernel> Kernels()
    {
        foreach (var kernel in ExactKernels())
            yield return kernel with { ComputeGlsl = kernel.ComputeGlsl.ReplaceLineEndings("\n") };
        foreach (var kernel in ExactKernels())
            if (kernel.Name != "CoverageCarryOrdinarySkin")
                yield return new(kernel.Name.Replace("CoverageCarry", "CoverageCarryNative", StringComparison.Ordinal),
                    CoverageCarryNativeShader.Adapt(kernel.ComputeGlsl, kernel.Name));
    }

    internal static GpuShaderCompileOptions Options(string name) => new(PreciseMath:
        !name.StartsWith("CoverageCarryNative", StringComparison.Ordinal) && name != "CoverageCarryOrdinarySkin");

    internal static int[] BufferIndices(string name) => name switch
    {
        "CoverageCarryNativeSkin" or "CoverageCarryNativeBasis" => [0, 1, 2, 5],
        "CoverageCarryNativeTransport" => [0, 1, 2, 5, 6],
        "CoverageCarryTransport" => [0, 1, 2, 3, 4, 5, 6],
        _ => [0, 1, 2, 3, 4, 5],
    };

    static IEnumerable<ShippedComputeKernel> ExactKernels()
    {
        yield return new("CoverageCarrySkin", Adapt(CoverageGpuSkinRefineGlsl.Source(CoverageGpuPhase.Skin), 1));
        yield return new("CoverageCarryTransport", Prefix(3) + CoverageGpuSkinRefineGlsl.Unit
            + AdaptRefinement() + Transport);
        yield return new("CoverageCarryBasis", Prefix(4) + CoverageGpuNormalBasisGlsl.Unit + CoverageGpuNormalWorkLayout.Source
            + CoverageCarryBasisShader.Source);
        string gather = CoverageGpuSkinRefineGlsl.Source(CoverageGpuPhase.GatherAttributes)
            .Replace("void main()", "void carryGather()", StringComparison.Ordinal);
        yield return new("CoverageCarryPublish", Adapt(gather + CoverageCarryPublicationShader.Source, 3));
        yield return new("CoverageCarryOrdinarySkin", Prefix(1) + CoverageCarryOrdinaryShader.Source
            + CoverageCarryPublicationShader.Source.Replace("carryGather();", "carryOrdinary();", StringComparison.Ordinal)
                .Replace("CovFrameWords[f+6u]", "CovFrameWords[f+5u]", StringComparison.Ordinal));
    }

    static string AdaptRefinement() => CoverageCarryRefinementShader.Source
        .Replace("= gl_GlobalInvocationID.x", "= carryIndex()", StringComparison.Ordinal);

    static string Prefix(int stride) => Adapt(CoverageGpuSkinRefineGlsl.Source(CoverageGpuPhase.Select)
        [..CoverageGpuSkinRefineGlsl.Source(CoverageGpuPhase.Select).IndexOf(CoverageGpuSelectGlsl.Source, StringComparison.Ordinal)], stride);

    static string Adapt(string source, int stride)
    {
        // The leading frame table describes actual topology extents, rounded to 64, and live actor frame offsets.
        string guard = CoverageGpuGuardGlsl.Source.Replace("uint covFrame() { return CovFrameWords[gl_GlobalInvocationID.y]; }",
            $"uint carryStride() {{ return CovFrameWords[{stride}u]; }}\n"
            + "uint carryActor() { return gl_GlobalInvocationID.x/carryStride(); }\n"
            + "uint carryIndex() { return gl_GlobalInvocationID.x%carryStride(); }\n"
            + "uint covFrame() { return CovFrameWords[5u+carryActor()]; }", StringComparison.Ordinal);
        int start = guard.IndexOf("void covEvent(", StringComparison.Ordinal);
        int stop = guard.IndexOf("bool covCompletedFailure", start, StringComparison.Ordinal);
        guard = guard[..start] + "void covEvent(uint work, uint element, uint code) {\n"
            + "if (code != 0u) atomicOr(CovResultWords[CovFrameWords[covFrame()+4u]+6u],code);\n}\n" + guard[stop..];
        // Carry requests only corrected division and square root. Keep the same residual graph while
        // excluding unreachable geometric predicate branches from the physical worker and its C8 call tree.
        string jobs = CoverageGpuExactJobsGlsl.Source;
        int worker = jobs.IndexOf("CovExactResult covExactWorker(", StringComparison.Ordinal);
        string scalarJobs = jobs[..worker] + ScalarWorker;
        return source.Replace(jobs, scalarJobs, StringComparison.Ordinal)
            .Replace(CoverageGpuGuardGlsl.Source, guard, StringComparison.Ordinal)
            .Replace("= gl_GlobalInvocationID.x", "= carryIndex()", StringComparison.Ordinal)
            + "\n";
    }

    const string ScalarWorker = """
        CovExactResult covExactWorker(CovExactJob job) {
            CovExactResult result = covExactResultZero();
            result.rounded = covRoundOperation(job.a.x,job.b.x,job.kind == CovJobRoot,false,0u,result.bisections);
            result.predicate.value = covRead(result.rounded);
            return result;
        }
        """;

    const string Transport = """
        layout(std430, set = 0, binding = 6) readonly buffer CarryAnchorBuf { uint CarryAnchorWords[]; };
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
            uint group = CovStaticWords[record+6u], anchor = CovFrameWords[f+14u]+group*2u;
            int representative = int(CarryAnchorWords[anchor+1u]);
            float full = covRead(CarryAnchorWords[anchor]);
            if (representative >= 0 && !covEqual(full,0.0)) {
                uint rep = covVertex(h,uint(representative));
                int slot = covRegionSlot(f,int(CovStaticWords[rep+8u]));
                if (slot >= 0) {
                    uint command = covCommand(f,uint(slot));
                    float weight = covRead(CovFrameWords[command+62u]);
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
        }
        """;
}
