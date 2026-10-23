using System;
using System.Collections.Immutable;
using System.Threading;

namespace KhaozEngine.Gpu;

/// <summary>Frozen count and text admission limits, enforced before native output materialization.</summary>
public sealed record GpuPipelineCompilerReadRequest(int MaximumExecutables, int MaximumStatisticsPerExecutable,
    int MaximumTotalStatistics, int MaximumTextUtf8Bytes, int MaximumCompilerFlagsUtf8Bytes, int MaximumEnumerationAttempts);
/// <summary>Retained counts, known native totals and the first refused native index/count. Null totals are unknown or not applicable.</summary>
public sealed record GpuPipelineCompilerReadCounts(bool EnumerationRequired, uint? ExecutableCount, ulong? TotalStatistics,
    int RetainedExecutables, int RetainedStatistics, uint? OmittedExecutables, ulong? OmittedStatistics,
    bool CountsComplete, uint? RefusedExecutableIndex, uint? RefusedCount);
/// <summary>Partial evidence and actual ordered failures. Failures are in-memory transport, never wire evidence.</summary>
public sealed record GpuPipelineCompilerReadResult(GpuPipelineCompilerReadRequest Request, GpuPipelineCompilerReport Report,
    bool Complete, GpuPipelineCompilerReadCounts Counts, string? RefusalReason, ImmutableArray<GpuDiagnosticFailure> Failures);
/// <summary>Optional cold read which enforces the frozen limits. The callback only checks cancellation/deadline at safe boundaries.</summary>
public interface IGpuBudgetedPipelineDiagnostics
{
    GpuPipelineCompilerReadResult ReadCompilerReport(GpuPipelineCompilerReadRequest request,
        Action<string> checkOperation, CancellationToken cancellation);
}
