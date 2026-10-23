using System.Collections.Immutable;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal enum RendererPerfCollectionState { Complete, Incomplete, Invalid, Cancelled, DeadlineExceeded }
internal enum RendererPerfPipelineKind { Graphics, Compute }
internal enum RendererPerfConstructionStage { Cold, Warmup, Measured }
internal sealed record RendererPerfKindCounts(int Graphics, int Compute);
internal sealed record RendererPerfCompilerCounts(int FactoryAttempts, RendererPerfKindCounts PipelineAttempts,
    RendererPerfKindCounts SuccessfulCreations, RendererPerfKindCounts Occurrences, RendererPerfKindCounts ReplayAttempts,
    RendererPerfKindCounts Reports, int UniqueRecipes, int ReplayEligibleRecipes, int BoundOccurrences, int Executables,
    int Statistics, int OmittedFactoryEvents);
internal sealed record RendererPerfCompilerReadEvidence(GpuPipelineCompilerReadRequest Request,
    GpuPipelineCompilerReadCounts? Counts, bool Complete, string? RefusalReason);
internal sealed record RendererPerfCompilerResult(int RecipeId, RendererPerfCollectionState State, string? Reason,
    int? PipelineIndex, ImmutableArray<int> OccurrenceIds, RendererPerfCompilerReadEvidence? Read);
internal sealed record RendererPerfFailureTextEvidence(bool Complete, int OmittedFailures, long RequiredUtf8Bytes);
internal sealed record RendererPerfCompilerEvidence(RendererPerfCollectionState State, string? Reason,
    RendererPerfCompilerCounts Counts, GpuEffectiveDiagnosticConfiguration? Production, GpuEffectiveDiagnosticConfiguration? Diagnostic,
    ImmutableArray<RendererPerfCompilerResult> Results, string? InventoryPath, ImmutableArray<RendererPerfCpuSpan> Stages,
    ImmutableArray<string> Errors)
{
    internal static RendererPerfCompilerEvidence Pending { get; } = new(RendererPerfCollectionState.Incomplete,
        "Construction capture and cold collection are not connected.", new(0, new(0, 0), new(0, 0), new(0, 0), new(0, 0),
            new(0, 0), 0, 0, 0, 0, 0, 0), null, null, [], null, [], []);
}
