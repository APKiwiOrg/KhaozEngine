namespace KhaozEngine.Gpu;

/// <summary>Optional cold resource-memory diagnostics on a device. <see cref="IGpuDevice"/> does not require
/// this interface. Read outside measured frames. Unsupported native memory fields remain null with a reason.</summary>
public interface IGpuResourceMemoryDiagnostics
{
    /// <summary>Reads owned logical payload and independently supported native allocation, residency and budget
    /// facts. A snapshot does not imply that dedicated VRAM is available.</summary>
    GpuResourceMemorySnapshot ReadMemorySnapshot();
}

/// <summary>Independent memory facts with source and units on every field. Logical payload and backing heaps
/// describe different accounting domains and must not be added together. History and scratch are categories
/// within logical payload, not extra copies of it. Metal unified memory and D3D11 unavailable residency are
/// never relabeled dedicated VRAM. A backend reports unavailable values explicitly.</summary>
/// <param name="LogicalBufferBytes">Requested live buffer payload.</param>
/// <param name="LogicalTextureBytes">Requested live texture payload, including mips, layers and samples.</param>
/// <param name="StagingCapacityBytes">Retained staging and ring capacity, with overlap identified in Source.</param>
/// <param name="HistoryPayloadBytes">History payload category.</param>
/// <param name="ScratchPayloadBytes">Scratch payload category.</param>
/// <param name="PeakLogicalPayloadBytes">Peak live logical payload, not an addition to current payload.</param>
/// <param name="LiveResourceCount">Live owned resource count.</param>
/// <param name="CommittedBytes">Backing native allocations, without counting suballocations twice.</param>
/// <param name="ResidentBytes">Native residency if supported, otherwise null with a reason.</param>
/// <param name="BudgetBytes">Native memory budget if supported, otherwise null with a reason.</param>
/// <param name="UnifiedMemory">True for unified memory, false for discrete memory, null when unknown.</param>
public sealed record GpuResourceMemorySnapshot(GpuDiagnosticMetric LogicalBufferBytes,
    GpuDiagnosticMetric LogicalTextureBytes, GpuDiagnosticMetric StagingCapacityBytes,
    GpuDiagnosticMetric HistoryPayloadBytes, GpuDiagnosticMetric ScratchPayloadBytes,
    GpuDiagnosticMetric PeakLogicalPayloadBytes, GpuDiagnosticMetric LiveResourceCount,
    GpuDiagnosticMetric CommittedBytes, GpuDiagnosticMetric ResidentBytes, GpuDiagnosticMetric BudgetBytes,
    bool? UnifiedMemory);
